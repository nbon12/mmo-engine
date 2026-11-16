using MmoEngine.SimCore.Geometry;

namespace MmoEngine.SimCore.Hitboxes;

/// <summary>
/// One posed hitbox capsule in world space: the body part, the segment's endpoints and its radius
/// (specs/015-shared-simulation-core/data-model.md#hitbox-layout-and-hitboxes). <c>HitboxBuilder</c>
/// writes a character's three into the world's scratch each step, from the post-movement position, aim
/// yaw and stance. Hitboxes are derived, not part of the hashed state.
/// </summary>
public readonly record struct Hitbox(HitboxPart Part, Vec3 A, Vec3 B, float Radius);

/// <summary>
/// A character's bounding sphere in world space: the feet plus <c>(0, BoundCenterUp, 0)</c>, and the
/// layout's <c>BoundRadius</c>. It encloses the character's three hitboxes whatever the yaw.
/// </summary>
public readonly record struct HitboxBounds(Vec3 Centre, float Radius);
