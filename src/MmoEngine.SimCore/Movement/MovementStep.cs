using System;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Math;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Movement;

/// <summary>
/// The movement step (FR-001 to FR-003, FR-005, specs/015-shared-simulation-core/data-model.md#movement-step,
/// research R8): a kinematic collide-and-slide controller that moves the velocity toward a target velocity.
/// It is a pure function of the character, one validated (or neutral) input, the compiled configuration
/// and the collision world; the candidate buffer is scratch whose contents never carry from one call to the
/// next. It allocates nothing, reads no clock, and bounds every loop: <c>maxSubMoves</c> sub-moves, each
/// with at most 4 push-outs, <c>maxSlideIterations</c> slide sweeps and one step-up of 3 sweeps plus
/// <c>maxSlideIterations</c> slide sweeps, then one ground probe; 8 iterations per triangle sweep; the
/// candidate buffer's capacity per query.
/// </summary>
public static class MovementStep
{
    /// <summary>The literal a diagonal wish direction is scaled by.</summary>
    public const float DiagonalFactor = 0.70710677f;

    /// <summary>The push-out iterations per sub-move; still overlapping after them reports <see cref="MovementOutcome.Stuck"/>.</summary>
    public const int MaxPushOutIterations = 4;

    /// <summary>A crease between two slide planes whose |q × p|² is below this has no direction: the velocity stops.</summary>
    internal const float CreaseThreshold = 1e-6f;

    /// <summary>A contact normal whose horizontal part has a squared length at most this has no horizontal direction.</summary>
    internal const float HorizontalThreshold = 1e-12f;

    /// <summary>The most slide planes one slide keeps: <c>movement.maxSlideIterations</c>' upper bound.</summary>
    private const int MaxSlidePlanes = 8;

    /// <summary>
    /// Sub-step 3: the target horizontal velocity the buttons give, <c>(F − B)·(sinψ, 0, cosψ) +
    /// (R − L)·(−cosψ, 0, sinψ)</c> evaluated in float component by component, times
    /// <see cref="DiagonalFactor"/> when both net axes are non-zero, times <c>crouchSpeed</c> when crouched,
    /// else <c>walkSpeed</c> when Walk is held, else <c>runSpeed</c>. Opposite buttons cancel.
    /// </summary>
    public static Vec3 WishVelocity(Buttons buttons, Stance stance, float sinYaw, float cosYaw, CompiledConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var forward = Held(buttons, Buttons.Forward) - Held(buttons, Buttons.Back);
        var right = Held(buttons, Buttons.Right) - Held(buttons, Buttons.Left);
        var wish = ((float)forward * new Vec3(sinYaw, 0f, cosYaw)) + ((float)right * new Vec3(-cosYaw, 0f, sinYaw));
        if (forward != 0 && right != 0)
        {
            wish *= DiagonalFactor;
        }

        var speed = stance == Stance.Crouched ? config.CrouchSpeed : (buttons & Buttons.Walk) != 0 ? config.WalkSpeed : config.RunSpeed;
        return wish * speed;
    }

    /// <summary>
    /// Sub-step 7's sub-move count: <c>clamp(⌈distance / radius⌉, 1, maxSubMoves)</c>, so no sub-move is
    /// longer than the radius while the cap holds (relation R09 keeps it holding).
    /// </summary>
    public static int SubMoveCount(float distance, float radius, int maxSubMoves) =>
        System.Math.Clamp(SimMath.TruncateToInt(System.MathF.Ceiling(distance / radius)), 1, System.Math.Max(1, maxSubMoves));

    /// <summary>
    /// Steps <paramref name="character"/> once with <paramref name="input"/>, through sub-steps 1 to 8 of
    /// data-model.md#movement-step: apply the input, settle the stance, take the wish velocity, move the
    /// horizontal velocity toward it, set the vertical velocity (jump and gravity), project onto the ground,
    /// run the sub-moves (push-out, slide, step-up), clamp to the playable area, and probe for ground. On a
    /// collision query that overflows <paramref name="candidates"/> it returns
    /// <see cref="StepFaultCode.QueryCapacityExceeded"/> and leaves <paramref name="character"/> unchanged.
    /// </summary>
    public static MovementResult Step(ref CharacterState character, in InputCommand input, CompiledConfig config, CollisionWorld world, Span<int> candidates)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(world);
        var mover = new Mover(config, world, candidates);
        var next = character;

        // 1. Apply the input.
        next.AimYaw = input.AimYaw;
        next.AimPitch = input.AimPitch;
        next.LastSequence = input.Sequence;
        SimMath.SinCosBinaryAngle(next.AimYaw, out var sinYaw, out var cosYaw);
        var buttons = input.Buttons;

        // 2. Stance: crouching always succeeds; standing up needs the headroom the standing capsule adds.
        if ((buttons & Buttons.Crouch) != 0)
        {
            next.Stance = Stance.Crouched;
        }
        else if (next.Stance == Stance.Crouched)
        {
            if (mover.HeadroomBlocked(next.Position))
            {
                mover.Outcome |= MovementOutcome.HeadroomBlocked;
            }
            else
            {
                next.Stance = Stance.Standing;
            }

            if (mover.Faulted)
            {
                return mover.Fault();
            }
        }

        // 3. The wish velocity, and 4. the horizontal velocity toward it by at most rate·dt.
        var wish = WishVelocity(buttons, next.Stance, sinYaw, cosYaw, config);
        var hasWish = Held(buttons, Buttons.Forward) != Held(buttons, Buttons.Back) || Held(buttons, Buttons.Right) != Held(buttons, Buttons.Left);
        var groundedAtStart = (next.Flags & CharacterFlags.Grounded) != 0;
        var rate = groundedAtStart
            ? (hasWish ? config.GroundAccelPerStep : config.GroundDecelPerStep)
            : (hasWish ? config.AirAccelPerStep : 0f);
        var (vx, vz) = Approach(next.Velocity.X, next.Velocity.Z, wish.X, wish.Z, rate);

        // 5. The vertical velocity: grounded it is 0, or jumpSpeed when Jump is held; airborne, including on
        // the jump step, gravity, clamped at −maxFallSpeed.
        var grounded = groundedAtStart;
        var jumped = false;
        var vy = next.Velocity.Y;
        if (grounded)
        {
            vy = 0f;
            if ((buttons & Buttons.Jump) != 0)
            {
                vy = config.JumpSpeed;
                grounded = false;
                jumped = true;
            }
        }

        if (!grounded)
        {
            vy -= config.GravityPerStep;
            if (vy < 0f - config.MaxFallSpeed)
            {
                vy = 0f - config.MaxFallSpeed;
            }
        }

        // 6. Ground projection keeps the horizontal speed on a slope; n.y ≥ CosSlopeLimit > 0.
        if (grounded)
        {
            vy = ProjectedY(next.GroundNormal, vx, vz);
        }

        // 7. The sub-moves.
        var velocity = new Vec3(vx, vy, vz);
        var position = next.Position;
        var subMoves = SubMoveCount(Vec3.Length(velocity * config.Dt), config.CapsuleRadius, config.MaxSubMoves);
        var subDt = config.Dt / (float)subMoves;
        var axisTop = config.AxisTop[next.Stance == Stance.Crouched ? CompiledConfig.Crouched : CompiledConfig.Standing];
        mover.AxisTop = axisTop;
        for (var k = 0; k < subMoves; k++)
        {
            mover.SubMoves++;
            mover.SubMove(ref position, ref velocity, grounded, subDt);
            if (mover.Faulted)
            {
                return mover.Fault();
            }
        }

        // The safety clamp keeps the feet inside the playable area.
        var heightfield = world.Heightfield;
        var clampedX = SimMath.Clamp(position.X, heightfield.MinX, heightfield.MaxX);
        var clampedZ = SimMath.Clamp(position.Z, heightfield.MinZ, heightfield.MaxZ);
        if (clampedX != position.X || clampedZ != position.Z)
        {
            position = new Vec3(clampedX, position.Y, clampedZ);
            mover.Outcome |= MovementOutcome.ClampedToBounds;
        }

        // 8. The ground probe: max(snapDistance, 2·contactSkin) when grounded at the start and not jumped,
        // so a grounded probe always reaches the contact band, else 2·contactSkin while not rising. Its
        // contact is the nearest touching support contact, else the nearest contact.
        var contactBand = 2f * config.ContactSkin;
        var probe = groundedAtStart && !jumped ? SimMath.Max(config.SnapDistance, contactBand) : (velocity.Y <= 0f ? contactBand : 0f);
        var groundNormal = Up;
        grounded = false;
        if (probe > 0f)
        {
            var down = new Vec3(0f, 0f - probe, 0f);
            var hit = mover.GroundSweep(position, down);
            if (mover.Faulted)
            {
                return mover.Fault();
            }

            if (hit.Hit && mover.IsSupport(hit, out var supportNormal))
            {
                position = new Vec3(position.X, position.Y + (down.Y * hit.T), position.Z);
                grounded = true;
                groundNormal = supportNormal;
                velocity = new Vec3(velocity.X, 0f, velocity.Z);
            }
        }

        next.Position = position;
        next.Velocity = velocity;
        next.GroundNormal = groundNormal;
        var flags = grounded ? CharacterFlags.Grounded : default;
        if ((mover.Outcome & MovementOutcome.ClampedToBounds) != 0)
        {
            flags |= CharacterFlags.ClampedToBounds;
        }

        next.Flags = flags;
        character = next;
        return new MovementResult(mover.Outcome, mover.SubMoves, mover.Sweeps, mover.SlideCapHits, StepFaultCode.None);
    }

    private static Vec3 Up => new(0f, 1f, 0f);

    private static Vec3 Zero => new(0f, 0f, 0f);

    private static int Held(Buttons buttons, Buttons button) => (buttons & button) != 0 ? 1 : 0;

    /// <summary>
    /// Sub-step 4: (x, z) moved toward (targetX, targetZ) by at most <paramref name="rate"/>, taking the
    /// target exactly when it is within the rate.
    /// </summary>
    private static (float X, float Z) Approach(float x, float z, float targetX, float targetZ, float rate)
    {
        var dx = targetX - x;
        var dz = targetZ - z;
        var gap = SimMath.Sqrt((dx * dx) + (dz * dz));
        if (gap <= rate)
        {
            return (targetX, targetZ);
        }

        var scale = rate / gap;
        return (x + (dx * scale), z + (dz * scale));
    }

    /// <summary>The vertical velocity that keeps (vx, vz) on the plane of <paramref name="normal"/>: −(n.x·vx + n.z·vz)/n.y.</summary>
    private static float ProjectedY(Vec3 normal, float vx, float vz) => 0f - (((normal.X * vx) + (normal.Z * vz)) / normal.Y);

    /// <summary><paramref name="v"/> clipped against plane normal <paramref name="p"/>: v − (v·p)·p when v·p &lt; 0.</summary>
    private static Vec3 Clip(Vec3 v, Vec3 p)
    {
        var into = Vec3.Dot(v, p);
        return into < 0f ? v - (p * into) : v;
    }

    /// <summary>The normal flattened to horizontal and normalised; the normal itself when it has no horizontal part.</summary>
    private static Vec3 Horizontal(Vec3 normal)
    {
        var lengthSquared = (normal.X * normal.X) + (normal.Z * normal.Z);
        if (lengthSquared <= HorizontalThreshold)
        {
            return normal;
        }

        var length = SimMath.Sqrt(lengthSquared);
        return new Vec3(normal.X / length, 0f, normal.Z / length);
    }

    private static bool IsZero(Vec3 v) => v.X == 0f && v.Y == 0f && v.Z == 0f;

    /// <summary>How far <paramref name="to"/> is from <paramref name="from"/> along the horizontal vector <paramref name="along"/>, scaled by its length.</summary>
    private static float Progress(Vec3 from, Vec3 to, Vec3 along) => ((to.X - from.X) * along.X) + ((to.Z - from.Z) * along.Z);

    /// <summary>One step's working state: the context, the counters and outcome, and whether a query overflowed.</summary>
    private ref struct Mover
    {
        private readonly CompiledConfig _config;
        private readonly CollisionWorld _world;
        private readonly Span<int> _candidates;

        public Mover(CompiledConfig config, CollisionWorld world, Span<int> candidates)
        {
            _config = config;
            _world = world;
            _candidates = candidates;
            AxisTop = config.AxisTop[CompiledConfig.Standing];
        }

        /// <summary>The capsule axis's top above the feet for the step's stance.</summary>
        public float AxisTop { get; set; }

        public MovementOutcome Outcome { get; set; }

        public int SubMoves { get; set; }

        public int Sweeps { get; private set; }

        public int SlideCapHits { get; private set; }

        public bool Faulted { get; private set; }

        /// <summary>The result of a step refused because a query overflowed the candidate buffer.</summary>
        public readonly MovementResult Fault() =>
            new(Outcome, SubMoves, Sweeps, SlideCapHits, StepFaultCode.QueryCapacityExceeded);

        /// <summary>
        /// Sub-step 2's headroom test: whether any triangle is within r + contactSkin/2 of the part of the
        /// axis the standing capsule adds, from crouchHeight − r to standHeight − r above the feet.
        /// </summary>
        public bool HeadroomBlocked(Vec3 feet)
        {
            var low = new Vec3(feet.X, feet.Y + _config.AxisTop[CompiledConfig.Crouched], feet.Z);
            var high = new Vec3(feet.X, feet.Y + _config.AxisTop[CompiledConfig.Standing], feet.Z);
            var clearance = _config.HeadroomClearance;
            var found = CapsuleSweep.Nearest(_world, low, high, clearance, _candidates, out var queryCount, out var distance, out _, out _);
            if (queryCount < 0)
            {
                Faulted = true;
                return false;
            }

            return found && distance <= clearance;
        }

        /// <summary>The capsule at <paramref name="feet"/> swept by <paramref name="displacement"/>, counted.</summary>
        public SweepHit Sweep(Vec3 feet, Vec3 displacement) => Sweep(feet, displacement, preferSupport: false);

        /// <summary>
        /// A sweep that looks for ground, counted: <see cref="CapsuleSweep.SweepPreferringSupport"/>, whose
        /// contact is the nearest support contact touching the capsule at the sweep's time, else the nearest
        /// contact. The ground probe and the step-up's two down-sweeps use it.
        /// </summary>
        public SweepHit GroundSweep(Vec3 feet, Vec3 displacement) => Sweep(feet, displacement, preferSupport: true);

        /// <summary>
        /// The support rule (<see cref="CapsuleSweep.IsSupport"/>): a contact normal with y ≥ CosSlopeLimit
        /// (the support normal is then the contact normal), or an edge or vertex contact on a face whose
        /// normal has y ≥ CosSlopeLimit with the contact normal's y ≥ <c>ledgeSupportMinNormalY</c> (the
        /// support normal is then the face normal).
        /// </summary>
        public readonly bool IsSupport(in SweepHit hit, out Vec3 supportNormal)
        {
            if (!CapsuleSweep.IsSupport(hit.Normal, hit.FaceNormal, hit.IsFaceContact, _config.CosSlopeLimit, _config.LedgeSupportMinNormalY))
            {
                supportNormal = Up;
                return false;
            }

            supportNormal = hit.Normal.Y >= _config.CosSlopeLimit ? hit.Normal : hit.FaceNormal;
            return true;
        }

        /// <summary>
        /// One sub-move: push-out, the slide, then the step-up when grounded and the slide hit a wall.
        /// </summary>
        public void SubMove(ref Vec3 position, ref Vec3 velocity, bool grounded, float subDt)
        {
            PushOut(ref position);
            if (Faulted)
            {
                return;
            }

            var start = position;
            var startVelocity = velocity;
            var slide = Slide(ref position, ref velocity, grounded, subDt, wallsOnly: false);
            if (Faulted)
            {
                return;
            }

            var outcome = slide.Outcome;
            if (grounded && slide.HitWall)
            {
                var stepPosition = position;
                var stepVelocity = velocity;
                if (TryStepUp(start, startVelocity, subDt, ref stepPosition, ref stepVelocity, out var stepOutcome))
                {
                    position = stepPosition;
                    velocity = stepVelocity;
                    outcome = stepOutcome;
                }

                if (Faulted)
                {
                    return;
                }
            }

            Outcome |= outcome;
            if ((outcome & MovementOutcome.SlideCapReached) != 0)
            {
                SlideCapHits++;
            }
        }

        /// <summary>
        /// Push-out: while a triangle is closer to the axis than <c>PushOutThreshold</c>, the nearest (ties to
        /// the lowest ID) pushes the capsule out to r + contactSkin, at most 4 times; still overlapping after
        /// them reports <see cref="MovementOutcome.Stuck"/>.
        /// </summary>
        private void PushOut(ref Vec3 position)
        {
            var threshold = _config.PushOutThreshold;
            for (var iteration = 0; ; iteration++)
            {
                var bottom = new Vec3(position.X, position.Y + _config.CapsuleRadius, position.Z);
                var top = new Vec3(position.X, position.Y + AxisTop, position.Z);
                var found = CapsuleSweep.Nearest(_world, bottom, top, threshold, _candidates, out var queryCount, out var distance, out _, out var direction);
                if (queryCount < 0)
                {
                    Faulted = true;
                    return;
                }

                if (!found || !(distance < threshold))
                {
                    return;
                }

                if (iteration == MaxPushOutIterations)
                {
                    Outcome |= MovementOutcome.Stuck;
                    return;
                }

                position += direction * (_config.SweepTarget - distance);
            }
        }

        /// <summary>
        /// The slide: at most <c>maxSlideIterations</c> sweeps of d = v·subDt·τ, each moving to the time of
        /// impact and keeping the rest of the motion (τ ·= 1 − t), with one clip plane per hit (see
        /// data-model.md#movement-step), the crease and three-plane stop when the clipped velocity would move
        /// back into an earlier plane of this slide, and the no-reversal rule. In walls-only mode every hit
        /// is a wall.
        /// </summary>
        private SlideResult Slide(ref Vec3 position, ref Vec3 velocity, bool grounded, float subDt, bool wallsOnly)
        {
            Span<Vec3> planes = stackalloc Vec3[MaxSlidePlanes];
            var planeCount = 0;
            var startVelocity = velocity;
            var remaining = 1f;
            var hitWall = false;
            var outcome = MovementOutcome.None;
            var capped = true;
            for (var iteration = 0; iteration < _config.MaxSlideIterations; iteration++)
            {
                var displacement = velocity * (subDt * remaining);
                if (IsZero(displacement))
                {
                    capped = false;
                    break;
                }

                var hit = Sweep(position, displacement);
                if (Faulted)
                {
                    return default;
                }

                if (!hit.Hit)
                {
                    position += displacement;
                    capped = false;
                    break;
                }

                position += displacement * hit.T;
                remaining *= 1f - hit.T;
                if (hit.TriangleId < 0)
                {
                    outcome |= MovementOutcome.ClampedToBounds;
                }

                var normal = hit.Normal;
                Vec3 plane;
                if (!wallsOnly && normal.Y >= _config.CosSlopeLimit)
                {
                    // Walkable: grounded, re-project onto the support plane; airborne, clip (the probe decides
                    // whether it landed).
                    plane = normal;
                    velocity = grounded ? new Vec3(velocity.X, ProjectedY(normal, velocity.X, velocity.Z), velocity.Z) : Clip(velocity, plane);
                }
                else
                {
                    hitWall = true;
                    if (grounded || wallsOnly || velocity.Y > 0f)
                    {
                        // Steep while grounded, rising, or in walls-only mode: clip against the horizontal normal and
                        // never climb the face.
                        plane = Horizontal(normal);
                        velocity = Clip(velocity, plane);
                        if (Vec3.Dot(velocity, normal) < 0f)
                        {
                            velocity = new Vec3(velocity.X, 0f, velocity.Z);
                        }
                    }
                    else
                    {
                        // Steep while falling: clip against the true normal, so the character slides down the face.
                        plane = normal;
                        velocity = Clip(velocity, plane);
                    }
                }

                velocity = Crease(velocity, plane, planes[..planeCount]);
                if (planeCount < MaxSlidePlanes)
                {
                    planes[planeCount] = plane;
                    planeCount++;
                }

                // No reversal: a velocity against the slide's starting velocity stops, so corners never oscillate.
                if (Vec3.Dot(velocity, startVelocity) < 0f)
                {
                    velocity = Zero;
                }

                if (IsZero(velocity) || !(remaining > 0f))
                {
                    capped = false;
                    break;
                }
            }

            if (capped)
            {
                outcome |= MovementOutcome.SlideCapReached;
            }

            return new SlideResult(hitWall, outcome);
        }

        /// <summary>
        /// The step-up: from the sub-move's start, a 2·contactSkin down-sweep finds the ground contact, an
        /// up-sweep by stepHeight (refused if it rises no more than the skin), a walls-only slide with the
        /// horizontal velocity, and a down-sweep by the rise plus 2·contactSkin. Accepted only when it lands
        /// on support, the landing contact is at most stepHeight above the starting one, and it ends further
        /// along the horizontal velocity than the plain slide. Both down-sweeps are ground sweeps
        /// (<see cref="GroundSweep"/>), so their contact is support whenever a support contact touches.
        /// </summary>
        private bool TryStepUp(Vec3 start, Vec3 startVelocity, float subDt, ref Vec3 position, ref Vec3 velocity, out MovementOutcome outcome)
        {
            outcome = MovementOutcome.None;
            var skin = _config.ContactSkin;
            var ground = GroundSweep(start, new Vec3(0f, 0f - (2f * skin), 0f));
            if (Faulted || !ground.Hit)
            {
                return false;
            }

            var stepHeight = _config.StepHeight;
            var up = Sweep(start, new Vec3(0f, stepHeight, 0f));
            if (Faulted)
            {
                return false;
            }

            var rise = up.Hit ? stepHeight * up.T : stepHeight;
            if (!(rise > skin))
            {
                return false;
            }

            var raised = new Vec3(start.X, start.Y + rise, start.Z);
            var horizontal = new Vec3(startVelocity.X, 0f, startVelocity.Z);
            var moved = raised;
            var movedVelocity = horizontal;
            var slide = Slide(ref moved, ref movedVelocity, grounded: true, subDt, wallsOnly: true);
            if (Faulted)
            {
                return false;
            }

            var drop = 0f - (rise + (2f * skin));
            var land = GroundSweep(moved, new Vec3(0f, drop, 0f));
            if (Faulted || !land.Hit || !IsSupport(land, out _) || land.ContactPoint.Y - ground.ContactPoint.Y > stepHeight)
            {
                return false;
            }

            var landed = new Vec3(moved.X, moved.Y + (drop * land.T), moved.Z);
            if (!(Progress(start, landed, horizontal) > Progress(start, position, horizontal)))
            {
                return false;
            }

            position = landed;
            velocity = movedVelocity;
            outcome = slide.Outcome;
            return true;
        }

        /// <summary>The counted sweep behind <see cref="Sweep(Vec3, Vec3)"/> and <see cref="GroundSweep"/>; a query overflow faults the step.</summary>
        private SweepHit Sweep(Vec3 feet, Vec3 displacement, bool preferSupport)
        {
            Sweeps++;
            var bottom = new Vec3(feet.X, feet.Y + _config.CapsuleRadius, feet.Z);
            var top = new Vec3(feet.X, feet.Y + AxisTop, feet.Z);
            var radius = _config.CapsuleRadius;
            var skin = _config.ContactSkin;
            int queryCount;
            var hit = preferSupport
                ? CapsuleSweep.SweepPreferringSupport(_world, bottom, top, radius, skin, displacement, _config.CosSlopeLimit, _config.LedgeSupportMinNormalY, _candidates, out queryCount)
                : CapsuleSweep.Sweep(_world, bottom, top, radius, skin, displacement, _candidates, out queryCount);
            if (queryCount < 0)
            {
                Faulted = true;
                return default;
            }

            return hit;
        }

        /// <summary>
        /// The crease rule: when <paramref name="velocity"/> moves into an earlier plane q of the slide (the first
        /// in hit order with v·q &lt; 0), it is projected onto the crease (q × p)/|q × p|, or stopped when
        /// |q × p|² is below <see cref="CreaseThreshold"/>; if it then still moves into a third plane, it stops.
        /// </summary>
        private static Vec3 Crease(Vec3 velocity, Vec3 plane, ReadOnlySpan<Vec3> earlier)
        {
            for (var k = 0; k < earlier.Length; k++)
            {
                var q = earlier[k];
                if (!(Vec3.Dot(velocity, q) < 0f))
                {
                    continue;
                }

                var crease = Vec3.Cross(q, plane);
                var lengthSquared = Vec3.Dot(crease, crease);
                if (lengthSquared < CreaseThreshold)
                {
                    return Zero;
                }

                var length = SimMath.Sqrt(lengthSquared);
                var direction = new Vec3(crease.X / length, crease.Y / length, crease.Z / length);
                var creased = direction * Vec3.Dot(velocity, direction);
                for (var m = 0; m < earlier.Length; m++)
                {
                    if (m != k && Vec3.Dot(creased, earlier[m]) < 0f)
                    {
                        return Zero;
                    }
                }

                return creased;
            }

            return velocity;
        }
    }

    /// <summary>What a slide reports: whether it hit a wall (a steep or walls-only hit) and its outcome flags.</summary>
    private readonly struct SlideResult(bool hitWall, MovementOutcome outcome)
    {
        public bool HitWall { get; } = hitWall;

        public MovementOutcome Outcome { get; } = outcome;
    }
}
