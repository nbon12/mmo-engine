using MmoEngine.SimCore.Projectiles;

namespace MmoEngine.SimCore.Collision;

/// <summary>
/// The first triangle a segment meets (<see cref="CollisionWorld.SegmentQuery"/>,
/// specs/015-shared-simulation-core/data-model.md#collisionworld): the fraction <see cref="T"/> of the
/// segment, in [0, 1], at which it meets triangle <see cref="TriangleId"/>, and whether that is a
/// heightfield triangle (<see cref="HitKind.Terrain"/>) or a mesh triangle (<see cref="HitKind.Mesh"/>).
/// The default value means nothing; the query's return value says whether it hit.
/// </summary>
/// <param name="T">The fraction of the segment before the triangle, in [0, 1]; never −0.</param>
/// <param name="TriangleId">A heightfield or mesh triangle ID (contracts/collision-data-format.md#triangle-ids); never a boundary plane's.</param>
/// <param name="Kind">Terrain for a heightfield triangle ID, Mesh otherwise.</param>
public readonly record struct SegmentHit(float T, int TriangleId, HitKind Kind);
