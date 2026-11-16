using System.Reflection;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Scenarios;

/// <summary>
/// The generated test world holds every feature the movement, weapon and projectile scenarios need (spec
/// Assumptions "Test content"; plan.md "The test world's layout constraints are the cross-phase ones only";
/// contracts/simcore-cli.md#content-generate). Everything is checked on
/// <c>CollisionFile.Decode(CollisionFile.Encode(TestWorldGenerator.Generate()))</c>, and the geometry that
/// <see cref="TestWorldGenerator.Features"/> names is checked against the decoded triangles, so a named
/// feature cannot drift from the world.
/// </summary>
/// <remarks>
/// The members of <see cref="TestWorldFeatures"/> this test reads: <c>FlatRunStart</c>;
/// <c>WallShooterStart</c>, <c>WallTargetStart</c> and <c>WallMeshId</c> (the vertical wall between them);
/// <c>RidgeShotStart</c>, <c>RidgeShotYaw</c> and <c>RidgeCrest</c> (its <c>From</c> and <c>To</c> are two
/// points of the crest line, both at the crest height); <c>PlateauHeight</c>; <c>CrouchUnderLedgeStart</c>,
/// <c>CrouchUnderLedgeYaw</c>, <c>LedgeUnderside</c> (a height), and <c>LedgeNearEdge</c> and
/// <c>LedgeFarEdge</c> (where the walk line from the start crosses the ledge's near and far edges); and
/// <c>Corner</c> (a point on the vertical line where the two walls meet). A location is every public
/// <see cref="Vec3"/> property whose name ends in <c>Start</c>; a yaw is a binary angle, 65,536 per turn,
/// with forward (sin ψ, 0, cos ψ).
/// </remarks>
public sealed class TestWorldGeneratorTests
{
    /// <summary>The eye height above the feet the wall shots use, m (hitboxes.eyeHeightStanding).</summary>
    private const double EyeHeight = 1.62;

    /// <summary>How close a face normal's y must be to cos 30° or cos 50°.</summary>
    private const double SlopeTolerance = 1e-4;

    /// <summary>How close a named height or distance must be to the geometry, m.</summary>
    private const double Tolerance = 1e-3;

    /// <summary>How far inside and outside the ledge's named edges the underside is probed, m.</summary>
    private const double EdgeProbe = 0.05;

    [Fact]
    public void TheTestWorldHasEveryFeatureTheScenariosNeed()
    {
        var encoded = CollisionFile.Encode(TestWorldGenerator.Generate());
        var world = CollisionFile.Decode(encoded);
        var data = world.Data;
        var features = TestWorldGenerator.Features;
        var config = TestConfigs.Default();
        var movement = config.Movement!;
        var meshTriangles = MeshTriangles(world, data);
        var failures = new List<string>();

        if (world.DegenerateTriangleCount != 0)
        {
            failures.Add($"DegenerateTriangleCount is {world.DegenerateTriangleCount}, not 0");
        }

        CheckSlopesAndPlateau(world, features, failures);
        CheckRidgeAndCliff(world, features, config, failures);
        CheckWallPair(data, meshTriangles, features, movement, failures);
        CheckStairs(world, meshTriangles, movement, failures);
        CheckLedge(world, meshTriangles, features, movement, failures);
        CheckCorner(meshTriangles, features, failures);
        CheckSingleQuad(data, meshTriangles, failures);
        CheckLocations(world, meshTriangles, features, movement, failures);
        CheckFiringLane(world, data, features, failures);

        var again = CollisionFile.Encode(TestWorldGenerator.Generate());
        if (!encoded.AsSpan().SequenceEqual(again))
        {
            failures.Add($"Two calls encode differently: {encoded.Length} bytes then {again.Length} bytes");
        }

        Assert.True(failures.Count == 0, $"The test world misses {failures.Count} feature checks:\n{string.Join("\n", failures)}");
    }

    /// <summary>
    /// A walkable 30° slope rising to the plateau, the plateau, and a steep 50° slope rising from the
    /// plateau: heightfield triangles whose unit normal's y is within 1e-4 of cos 30° with their highest
    /// vertex at <c>PlateauHeight</c>, level ones with every vertex there, and ones within 1e-4 of cos 50°
    /// with their lowest vertex there.
    /// </summary>
    private static void CheckSlopesAndPlateau(CollisionWorld world, TestWorldFeatures features, List<string> failures)
    {
        var plateau = (double)features.PlateauHeight;
        var cos30 = System.Math.Cos(30.0 * System.Math.PI / 180.0);
        var cos50 = System.Math.Cos(50.0 * System.Math.PI / 180.0);
        bool walkable = false, level = false, steep = false;
        var heightfield = world.Heightfield;
        for (var id = 0; id < heightfield.TriangleCount; id++)
        {
            heightfield.GetTriangle(id, out var a, out var b, out var c);
            var normalY = UnitNormal(a, b, c).Y;
            var low = System.Math.Min(a.Y, System.Math.Min(b.Y, c.Y));
            var high = System.Math.Max(a.Y, System.Math.Max(b.Y, c.Y));
            walkable |= System.Math.Abs(normalY - cos30) <= SlopeTolerance && System.Math.Abs(high - plateau) <= Tolerance;
            level |= normalY >= 1.0 - 1e-9 && System.Math.Abs(low - plateau) <= Tolerance && System.Math.Abs(high - plateau) <= Tolerance;
            steep |= System.Math.Abs(normalY - cos50) <= SlopeTolerance && System.Math.Abs(low - plateau) <= Tolerance;
        }

        AddUnless(walkable, failures, $"no heightfield triangle is a 30° slope (normal y {cos30:F6}) reaching the plateau height {plateau}");
        AddUnless(level, failures, $"no heightfield triangle is level at the plateau height {plateau}");
        AddUnless(steep, failures, $"no heightfield triangle is a 50° slope (normal y {cos50:F6}) rising from the plateau height {plateau}");
    }

    /// <summary>
    /// The ridge and the cliff. <c>RidgeCrest</c> is level, at least 3 m above the ground at
    /// <c>RidgeShotStart</c>; the heading <c>RidgeShotYaw</c> from <c>RidgeShotStart</c> crosses the crest line
    /// between its two points, 20 m to 200 m away, where the terrain is at the crest height and falls at
    /// least 0.5 m within 10 m on either side along the heading (a ridge, not a step), and the crest point is
    /// inside both projectile classes' ranges from the eye. A cliff is a heightfield cell whose corner
    /// heights drop by at least 3 m.
    /// </summary>
    private static void CheckRidgeAndCliff(CollisionWorld world, TestWorldFeatures features, SimConfig config, List<string> failures)
    {
        var from = features.RidgeCrest.From;
        var to = features.RidgeCrest.To;
        var start = features.RidgeShotStart;
        var crest = (double)from.Y;
        AddUnless(System.Math.Abs(from.Y - to.Y) <= SlopeTolerance, failures, $"RidgeCrest is not level: {from} to {to}");

        var startGround = (double)world.HeightAt(start.X, start.Z);
        AddUnless(
            crest - startGround >= 3.0,
            failures,
            $"RidgeCrest at {crest} m is {crest - startGround:F3} m above the ground at RidgeShotStart {start}, less than 3 m");

        var (sin, cos) = Heading(features.RidgeShotYaw);
        var (ex, ez) = ((double)to.X - from.X, (double)to.Z - from.Z);
        var denominator = (sin * ez) - (cos * ex);
        if (System.Math.Abs(denominator) < 1e-9)
        {
            failures.Add($"The heading RidgeShotYaw {features.RidgeShotYaw} from {start} is parallel to RidgeCrest {from} to {to}");
        }
        else
        {
            var (wx, wz) = ((double)from.X - start.X, (double)from.Z - start.Z);
            var distance = ((wx * ez) - (wz * ex)) / denominator;
            var along = ((wx * cos) - (wz * sin)) / denominator;
            var (px, pz) = (start.X + (sin * distance), start.Z + (cos * distance));
            AddUnless(
                along >= 0.0 && along <= 1.0 && distance >= 20.0 && distance <= 200.0,
                failures,
                $"The heading RidgeShotYaw {features.RidgeShotYaw} from {start} meets the crest line {distance:F3} m away at {along:F3} of the way from {from} to {to}; it must cross the crest 20 m to 200 m away");

            var crestGround = world.HeightAt((float)px, (float)pz);
            AddUnless(
                System.Math.Abs(crestGround - crest) <= 0.05,
                failures,
                $"The terrain where the heading crosses the crest, ({px:F3}, {pz:F3}), is at {crestGround} m, not the crest height {crest}");
            double[] offsets = [-10.0, 10.0];
            foreach (var offset in offsets)
            {
                var side = world.HeightAt((float)(px + (sin * offset)), (float)(pz + (cos * offset)));
                AddUnless(
                    crest - side >= 0.5,
                    failures,
                    $"The terrain {offset} m along the heading from the crest crossing is at {side} m, not at least 0.5 m below the crest {crest}");
            }

            var eye = startGround + EyeHeight;
            var reach = System.Math.Sqrt((distance * distance) + ((crest - eye) * (crest - eye)));
            var classes = config.ProjectileClasses!;
            for (var k = 0; k < classes.Count; k++)
            {
                AddUnless(
                    reach < classes[k]!.MaxRangeM,
                    failures,
                    $"The crest crossing is {reach:F3} m from the eye, outside projectile class {k}'s range {classes[k]!.MaxRangeM} m");
            }
        }

        var heightfield = world.Heightfield;
        var cliff = false;
        for (var j = 0; j + 1 < heightfield.Rows && !cliff; j++)
        {
            for (var i = 0; i + 1 < heightfield.Columns && !cliff; i++)
            {
                var h00 = heightfield.Sample(i, j).Y;
                var h10 = heightfield.Sample(i + 1, j).Y;
                var h01 = heightfield.Sample(i, j + 1).Y;
                var h11 = heightfield.Sample(i + 1, j + 1).Y;
                var high = System.Math.Max(System.Math.Max(h00, h10), System.Math.Max(h01, h11));
                var low = System.Math.Min(System.Math.Min(h00, h10), System.Math.Min(h01, h11));
                cliff = (double)high - low >= 3.0;
            }
        }

        AddUnless(cliff, failures, "no heightfield cell's corner heights drop by 3 m or more (the cliff)");
    }

    /// <summary>
    /// The vertical wall and the wall pair: mesh <c>WallMeshId</c> is planar with every triangle's normal y
    /// exactly 0; <c>WallShooterStart</c> and <c>WallTargetStart</c> are 25 m apart within 1e-3 m, on opposite
    /// sides of its plane and each clear of it by capsuleRadius + contactSkin; and the line between their
    /// eye points, 1.62 m above each, crosses the plane inside the bounding box of the mesh's vertices.
    /// </summary>
    private static void CheckWallPair(
        CollisionData data,
        List<(uint MeshId, MeshTriangle Triangle)> meshTriangles,
        TestWorldFeatures features,
        MovementConfig movement,
        List<string> failures)
    {
        var wallId = features.WallMeshId;
        var wall = data.Meshes.FirstOrDefault(mesh => mesh.MeshId == wallId);
        var triangles = meshTriangles.Where(entry => entry.MeshId == wallId).Select(entry => entry.Triangle).ToList();
        if (wall is null || triangles.Count == 0)
        {
            failures.Add($"WallMeshId {wallId} names no mesh with a kept triangle");
            return;
        }

        AddUnless(
            triangles.All(triangle => triangle.Normal.Y == 0f),
            failures,
            $"mesh {wallId} is not a vertical wall: its normals' y are [{string.Join(", ", triangles.Select(triangle => triangle.Normal.Y))}]");

        var normal = triangles[0].Normal;
        var offset = Dot(normal, triangles[0].A);
        var vertices = Points(wall.Vertices);
        AddUnless(
            vertices.All(vertex => System.Math.Abs(Dot(normal, vertex) - offset) <= SlopeTolerance),
            failures,
            $"mesh {wallId} is not planar");

        var shooter = features.WallShooterStart;
        var target = features.WallTargetStart;
        var apart = Distance(shooter, target);
        AddUnless(System.Math.Abs(apart - 25.0) <= Tolerance, failures, $"WallShooterStart {shooter} and WallTargetStart {target} are {apart:F6} m apart, not 25 m");

        var shooterSide = Dot(normal, shooter) - offset;
        var targetSide = Dot(normal, target) - offset;
        var clearance = (double)movement.CapsuleRadius + movement.ContactSkin;
        AddUnless(
            System.Math.Sign(shooterSide) * System.Math.Sign(targetSide) < 0
                && System.Math.Abs(shooterSide) >= clearance
                && System.Math.Abs(targetSide) >= clearance,
            failures,
            $"WallShooterStart and WallTargetStart are {shooterSide:F3} m and {targetSide:F3} m from the wall plane; they must be on opposite sides, each at least {clearance} m from it");

        if (shooterSide != targetSide)
        {
            var t = shooterSide / (shooterSide - targetSide);
            var crossing = (
                X: shooter.X + (t * ((double)target.X - shooter.X)),
                Y: shooter.Y + EyeHeight + (t * ((double)target.Y - shooter.Y)),
                Z: shooter.Z + (t * ((double)target.Z - shooter.Z)));
            var inside =
                crossing.X >= vertices.Min(vertex => vertex.X) - Tolerance && crossing.X <= vertices.Max(vertex => vertex.X) + Tolerance
                && crossing.Y >= vertices.Min(vertex => vertex.Y) - Tolerance && crossing.Y <= vertices.Max(vertex => vertex.Y) + Tolerance
                && crossing.Z >= vertices.Min(vertex => vertex.Z) - Tolerance && crossing.Z <= vertices.Max(vertex => vertex.Z) + Tolerance;
            AddUnless(inside, failures, $"The eye line between the wall pair crosses the wall plane at {crossing}, outside the wall mesh's bounding box");
        }
    }

    /// <summary>
    /// A stair of rise 0.30 m and one of 0.50 m, either side of stepHeight: a mesh with a level top that rise
    /// above the ground under it, and a vertical face in the same mesh from the ground up to that top.
    /// </summary>
    private static void CheckStairs(CollisionWorld world, List<(uint MeshId, MeshTriangle Triangle)> meshTriangles, MovementConfig movement, List<string> failures)
    {
        AddUnless(
            0.30 < movement.StepHeight && movement.StepHeight < 0.50,
            failures,
            $"stepHeight {movement.StepHeight} must lie between the two stair rises, 0.30 and 0.50 m");
        double[] rises = [0.30, 0.50];
        foreach (var rise in rises)
        {
            var found = false;
            foreach (var group in meshTriangles.GroupBy(entry => entry.MeshId))
            {
                var faces = group.Select(entry => entry.Triangle).ToList();
                foreach (var top in faces.Where(IsLevel))
                {
                    var centroid = Centroid(top);
                    var topY = centroid.Y;
                    if (System.Math.Abs(topY - world.HeightAt((float)centroid.X, (float)centroid.Z) - rise) > Tolerance)
                    {
                        continue;
                    }

                    found |= faces.Any(face =>
                    {
                        if (face.Normal.Y != 0f)
                        {
                            return false;
                        }

                        var middle = Centroid(face);
                        return face.Min.Y <= world.HeightAt((float)middle.X, (float)middle.Z) + Tolerance
                            && System.Math.Abs(face.Max.Y - topY) <= Tolerance;
                    });
                }
            }

            AddUnless(found, failures, $"no mesh is a stair of rise {rise} m: a level top {rise} m above the ground with a vertical face up to it");
        }
    }

    /// <summary>
    /// The ledge: the <c>crouch-under-ledge</c> start is 2 m before <c>LedgeNearEdge</c> along its heading
    /// <c>CrouchUnderLedgeYaw</c>, and <c>LedgeFarEdge</c> is 4 m beyond it on the same heading (all within
    /// 1e-3 m horizontally); <c>LedgeUnderside</c> is 1.5 m above the floor, between crouchHeight and
    /// standHeight; just inside both edges and midway between them the lowest mesh above the floor is a level
    /// face at <c>LedgeUnderside</c>, and just outside both edges there is no mesh between the floor and the
    /// underside.
    /// </summary>
    private static void CheckLedge(
        CollisionWorld world,
        List<(uint MeshId, MeshTriangle Triangle)> meshTriangles,
        TestWorldFeatures features,
        MovementConfig movement,
        List<string> failures)
    {
        var start = features.CrouchUnderLedgeStart;
        var near = features.LedgeNearEdge;
        var far = features.LedgeFarEdge;
        var (sin, cos) = Heading(features.CrouchUnderLedgeYaw);
        AddUnless(
            Horizontal(start.X + (2.0 * sin) - near.X, start.Z + (2.0 * cos) - near.Z) <= Tolerance,
            failures,
            $"CrouchUnderLedgeStart {start} facing yaw {features.CrouchUnderLedgeYaw} is not 2 m before LedgeNearEdge {near}");
        AddUnless(
            Horizontal(near.X + (4.0 * sin) - far.X, near.Z + (4.0 * cos) - far.Z) <= Tolerance,
            failures,
            $"LedgeFarEdge {far} is not 4 m beyond LedgeNearEdge {near} along yaw {features.CrouchUnderLedgeYaw}");

        var underside = (double)features.LedgeUnderside;
        var middle = (X: (near.X + (double)far.X) / 2.0, Z: (near.Z + (double)far.Z) / 2.0);
        var clearance = underside - world.HeightAt((float)middle.X, (float)middle.Z);
        AddUnless(
            System.Math.Abs(clearance - 1.5) <= Tolerance && clearance > movement.CrouchHeight && clearance < movement.StandHeight,
            failures,
            $"LedgeUnderside {underside} is {clearance:F6} m above the floor; it must be 1.5 m, between crouchHeight {movement.CrouchHeight} and standHeight {movement.StandHeight}");

        (string Name, double Along)[] inside =
        [
            ("just inside the near edge", EdgeProbe),
            ("midway", 2.0),
            ("just inside the far edge", 4.0 - EdgeProbe),
        ];
        foreach (var (name, along) in inside)
        {
            var (x, z) = (near.X + (sin * along), near.Z + (cos * along));
            var floor = world.HeightAt((float)x, (float)z);
            var lowest = Crossings(meshTriangles, x, z)
                .Where(crossing => crossing.Y > floor + Tolerance)
                .OrderBy(crossing => crossing.Y)
                .FirstOrDefault();
            AddUnless(
                lowest.Level && System.Math.Abs(lowest.Y - underside) <= Tolerance,
                failures,
                $"{name} ({x:F3}, {z:F3}) the lowest mesh above the floor is at {lowest.Y} m (level {lowest.Level}), not a level underside at {underside}");
        }

        (string Name, double Along)[] outside =
        [
            ("just before the near edge", -EdgeProbe),
            ("just beyond the far edge", 4.0 + EdgeProbe),
        ];
        foreach (var (name, along) in outside)
        {
            var (x, z) = (near.X + (sin * along), near.Z + (cos * along));
            var floor = world.HeightAt((float)x, (float)z);
            var blocking = Crossings(meshTriangles, x, z)
                .Where(crossing => crossing.Y > floor + Tolerance && crossing.Y <= underside + Tolerance)
                .ToList();
            AddUnless(
                blocking.Count == 0,
                failures,
                $"{name} ({x:F3}, {z:F3}) a mesh lies between the floor and the underside at [{string.Join(", ", blocking.Select(crossing => crossing.Y))}], so the named edge is not the ledge's edge");
        }
    }

    /// <summary>Two walls meeting at 90 degrees: two vertical triangles of one mesh, with perpendicular normals, both touching the vertical line through <c>Corner</c>.</summary>
    private static void CheckCorner(List<(uint MeshId, MeshTriangle Triangle)> meshTriangles, TestWorldFeatures features, List<string> failures)
    {
        var corner = features.Corner;
        bool OnCornerLine(MeshTriangle triangle) =>
            new[] { triangle.A, triangle.B, triangle.C }.Any(vertex => Horizontal((double)vertex.X - corner.X, (double)vertex.Z - corner.Z) <= Tolerance);

        var found = meshTriangles
            .Where(entry => entry.Triangle.Normal.Y == 0f && OnCornerLine(entry.Triangle))
            .GroupBy(entry => entry.MeshId)
            .Any(group => group.Any(first => group.Any(second =>
                System.Math.Abs(Dot(first.Triangle.Normal, second.Triangle.Normal)) <= SlopeTolerance)));
        AddUnless(found, failures, $"no two vertical triangles of one mesh meet at 90 degrees at Corner {corner}");
    }

    /// <summary>A single zero-thickness quad: a mesh of exactly two kept, coplanar triangles that share an edge.</summary>
    private static void CheckSingleQuad(CollisionData data, List<(uint MeshId, MeshTriangle Triangle)> meshTriangles, List<string> failures)
    {
        var found = false;
        foreach (var mesh in data.Meshes.Where(mesh => mesh.Indices.Length == 6))
        {
            var pair = meshTriangles.Where(entry => entry.MeshId == mesh.MeshId).Select(entry => entry.Triangle).ToList();
            if (pair.Count != 2)
            {
                continue;
            }

            var normal = pair[0].Normal;
            var offset = Dot(normal, pair[0].A);
            var coplanar = System.Math.Abs(Dot(normal, pair[1].Normal)) >= 1.0 - 1e-6
                && Points(mesh.Vertices).All(vertex => System.Math.Abs(Dot(normal, vertex) - offset) <= SlopeTolerance);
            var shared = new[] { pair[0].A, pair[0].B, pair[0].C }.Count(vertex => vertex.Equals(pair[1].A) || vertex.Equals(pair[1].B) || vertex.Equals(pair[1].C));
            found |= coplanar && shared == 2;
        }

        AddUnless(found, failures, "no mesh is a single zero-thickness quad (two coplanar triangles sharing an edge)");
    }

    /// <summary>
    /// Every location (a <see cref="Vec3"/> property of <see cref="TestWorldFeatures"/> named <c>…Start</c>)
    /// lies inside the playable area at rest height over the highest surface under it (terrain or mesh), and
    /// <c>FlatRunStart.x</c> is at least 1 m.
    /// </summary>
    private static void CheckLocations(
        CollisionWorld world,
        List<(uint MeshId, MeshTriangle Triangle)> meshTriangles,
        TestWorldFeatures features,
        MovementConfig movement,
        List<string> failures)
    {
        var locations = typeof(TestWorldFeatures)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(Vec3) && property.Name.EndsWith("Start", StringComparison.Ordinal))
            .ToDictionary(property => property.Name, property => (Vec3)property.GetValue(features)!);
        string[] required = ["FlatRunStart", "WallShooterStart", "WallTargetStart", "RidgeShotStart", "CrouchUnderLedgeStart"];
        foreach (var name in required)
        {
            AddUnless(locations.ContainsKey(name), failures, $"TestWorldFeatures has no location {name}");
        }

        var heightfield = world.Heightfield;
        foreach (var (name, location) in locations)
        {
            var inside = location.X >= heightfield.MinX && location.X <= heightfield.MaxX
                && location.Z >= heightfield.MinZ && location.Z <= heightfield.MaxZ;
            AddUnless(inside, failures, $"{name} {location} is outside the playable area [{heightfield.MinX}, {heightfield.MaxX}] x [{heightfield.MinZ}, {heightfield.MaxZ}]");

            var surface = (double)world.HeightAt(location.X, location.Z);
            foreach (var crossing in Crossings(meshTriangles, location.X, location.Z))
            {
                if (crossing.Y <= location.Y + movement.ContactTolerance && crossing.Y > surface)
                {
                    surface = crossing.Y;
                }
            }

            var gap = location.Y - surface;
            AddUnless(
                gap >= -movement.ContactTolerance && gap <= (double)movement.ContactSkin + movement.ContactTolerance,
                failures,
                $"{name} {location} is {gap:F6} m above the surface under it, not at rest height ([-contactTolerance, contactSkin + contactTolerance])");
        }

        AddUnless(features.FlatRunStart.X >= 1f, failures, $"FlatRunStart.x is {features.FlatRunStart.X}, below 1 m");
    }

    /// <summary>
    /// The firing lane in front of <c>FlatRunStart</c>, on the decoded <see cref="CollisionData"/> alone: every
    /// mesh vertex has z &lt; <c>FlatRunStart.z</c> − 1, every heightfield sample with z ≥ <c>FlatRunStart.z</c>
    /// is within 0.5 m of the ground height at <c>FlatRunStart</c>, and the heightfield extends at least 100 m
    /// in +Z from it.
    /// </summary>
    private static void CheckFiringLane(CollisionWorld world, CollisionData data, TestWorldFeatures features, List<string> failures)
    {
        var start = features.FlatRunStart;
        var meshVertexZ = data.Meshes.SelectMany(mesh => Points(mesh.Vertices)).Select(vertex => vertex.Z).DefaultIfEmpty(float.NegativeInfinity).Max();
        AddUnless(
            meshVertexZ < start.Z - 1f,
            failures,
            $"a mesh vertex lies at z {meshVertexZ}, not below FlatRunStart.z - 1 = {start.Z - 1f}, in the firing lane");

        var ground = (double)world.HeightAt(start.X, start.Z);
        var worst = 0.0;
        var worstSample = (I: -1, J: -1);
        for (var j = 0; j < data.Rows; j++)
        {
            var z = data.OriginZ + ((float)j * data.CellSize);
            if (z < start.Z)
            {
                continue;
            }

            for (var i = 0; i < data.Columns; i++)
            {
                var off = System.Math.Abs(data.Heights[(j * data.Columns) + i] - ground);
                if (off > worst)
                {
                    worst = off;
                    worstSample = (i, j);
                }
            }
        }

        AddUnless(
            worst <= 0.5,
            failures,
            $"heightfield sample {worstSample} in the firing lane is {worst:F3} m from the ground height {ground} at FlatRunStart, more than 0.5 m");

        var maxZ = data.OriginZ + ((float)(data.Rows - 1) * data.CellSize);
        AddUnless(
            (double)maxZ - start.Z >= 100.0,
            failures,
            $"the heightfield ends at z {maxZ}, {(double)maxZ - start.Z:F3} m in +Z from FlatRunStart, less than 100 m");
    }

    /// <summary>
    /// Every kept mesh triangle with its mesh's ID, in triangle ID order: mesh triangle IDs follow the
    /// heightfield's in file order, mesh by mesh.
    /// </summary>
    private static List<(uint MeshId, MeshTriangle Triangle)> MeshTriangles(CollisionWorld world, CollisionData data)
    {
        var triangles = new List<(uint MeshId, MeshTriangle Triangle)>();
        var id = world.Meshes.FirstId;
        foreach (var mesh in data.Meshes)
        {
            for (var t = 0; t < mesh.Indices.Length / 3; t++, id++)
            {
                if (world.Meshes.TryGetMeshTriangle(id, out var triangle))
                {
                    triangles.Add((mesh.MeshId, triangle));
                }
            }
        }

        return triangles;
    }

    /// <summary>The heights, in double, at which the vertical line through (x, z) crosses each non-vertical mesh triangle, and whether that triangle is level.</summary>
    private static IEnumerable<(double Y, bool Level)> Crossings(List<(uint MeshId, MeshTriangle Triangle)> meshTriangles, double x, double z)
    {
        foreach (var (_, triangle) in meshTriangles)
        {
            var normal = UnitNormal(triangle.A, triangle.B, triangle.C);
            if (System.Math.Abs(normal.Y) < 1e-6 || !ContainsInXz(triangle, x, z))
            {
                continue;
            }

            var a = triangle.A;
            var y = a.Y - (((normal.X * (x - a.X)) + (normal.Z * (z - a.Z))) / normal.Y);
            yield return (y, IsLevel(triangle));
        }
    }

    private static bool IsLevel(MeshTriangle triangle) => System.Math.Abs(triangle.Normal.Y) >= 1f - 1e-6f;

    private static bool ContainsInXz(MeshTriangle triangle, double x, double z)
    {
        var (a, b, c) = (triangle.A, triangle.B, triangle.C);
        var e0 = (((double)b.X - a.X) * (z - a.Z)) - (((double)b.Z - a.Z) * (x - a.X));
        var e1 = (((double)c.X - b.X) * (z - b.Z)) - (((double)c.Z - b.Z) * (x - b.X));
        var e2 = (((double)a.X - c.X) * (z - c.Z)) - (((double)a.Z - c.Z) * (x - c.X));
        return (e0 >= 0.0 && e1 >= 0.0 && e2 >= 0.0) || (e0 <= 0.0 && e1 <= 0.0 && e2 <= 0.0);
    }

    /// <summary>The triangle's unit normal in double, turned to face up (y ≥ 0).</summary>
    private static (double X, double Y, double Z) UnitNormal(Vec3 a, Vec3 b, Vec3 c)
    {
        var (ux, uy, uz) = ((double)b.X - a.X, (double)b.Y - a.Y, (double)b.Z - a.Z);
        var (vx, vy, vz) = ((double)c.X - a.X, (double)c.Y - a.Y, (double)c.Z - a.Z);
        var (nx, ny, nz) = ((uy * vz) - (uz * vy), (uz * vx) - (ux * vz), (ux * vy) - (uy * vx));
        var scale = (ny < 0.0 ? -1.0 : 1.0) / System.Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        return (nx * scale, ny * scale, nz * scale);
    }

    private static (double X, double Y, double Z) Centroid(MeshTriangle triangle) => (
        ((double)triangle.A.X + triangle.B.X + triangle.C.X) / 3.0,
        ((double)triangle.A.Y + triangle.B.Y + triangle.C.Y) / 3.0,
        ((double)triangle.A.Z + triangle.B.Z + triangle.C.Z) / 3.0);

    /// <summary>A yaw's forward direction in X–Z: (sin ψ, cos ψ) with ψ = yaw · 2π / 65,536.</summary>
    private static (double Sin, double Cos) Heading(ushort yaw)
    {
        var angle = yaw * 2.0 * System.Math.PI / 65536.0;
        return (System.Math.Sin(angle), System.Math.Cos(angle));
    }

    private static List<Vec3> Points(float[] vertices)
    {
        var points = new List<Vec3>(vertices.Length / 3);
        for (var v = 0; v + 2 < vertices.Length; v += 3)
        {
            points.Add(new Vec3(vertices[v], vertices[v + 1], vertices[v + 2]));
        }

        return points;
    }

    private static double Dot(Vec3 a, Vec3 b) => ((double)a.X * b.X) + ((double)a.Y * b.Y) + ((double)a.Z * b.Z);

    private static double Distance(Vec3 a, Vec3 b)
    {
        var (dx, dy, dz) = ((double)a.X - b.X, (double)a.Y - b.Y, (double)a.Z - b.Z);
        return System.Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private static double Horizontal(double dx, double dz) => System.Math.Sqrt((dx * dx) + (dz * dz));

    private static void AddUnless(bool holds, List<string> failures, string failure)
    {
        if (!holds)
        {
            failures.Add(failure);
        }
    }
}
