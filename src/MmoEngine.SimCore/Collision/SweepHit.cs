using MmoEngine.SimCore.Geometry;

namespace MmoEngine.SimCore.Collision;

/// <summary>
/// The first contact of a capsule sweep (<see cref="CapsuleSweep.Sweep"/>, research R7). The default value
/// is a miss: <see cref="Hit"/> false, and every other member then means nothing.
/// </summary>
public readonly struct SweepHit
{
    /// <summary>A hit at <paramref name="t"/> against <paramref name="triangleId"/>.</summary>
    internal SweepHit(float t, int triangleId, Vec3 normal, Vec3 faceNormal, bool isFaceContact, Vec3 contactPoint)
    {
        Hit = true;
        T = t;
        TriangleId = triangleId;
        Normal = normal;
        FaceNormal = faceNormal;
        IsFaceContact = isFaceContact;
        ContactPoint = contactPoint;
    }

    /// <summary>Whether the capsule touched anything within the displacement.</summary>
    public bool Hit { get; }

    /// <summary>
    /// The fraction of the displacement the capsule moves before the contact, in [0, 1]: never past the
    /// true time of impact, so the capsule stops between r + skin and r + 1.1·skin from what it touches.
    /// </summary>
    public float T { get; }

    /// <summary>The triangle touched: a heightfield or mesh triangle ID, or a boundary plane's, −4 to −1.</summary>
    public int TriangleId { get; }

    /// <summary>
    /// The unit contact normal, pointing from the contact toward the capsule: the face normal for a face
    /// contact (so a flat wall stays flat), otherwise the direction from the closest point on the triangle to
    /// the closest point on the axis.
    /// </summary>
    public Vec3 Normal { get; }

    /// <summary>The touched triangle's (or boundary plane's) unit normal, turned to face the capsule.</summary>
    public Vec3 FaceNormal { get; }

    /// <summary>Whether the closest point lies inside the triangle's face rather than on an edge or a vertex.</summary>
    public bool IsFaceContact { get; }

    /// <summary>The closest point on the triangle at <see cref="T"/>, in world coordinates.</summary>
    public Vec3 ContactPoint { get; }
}
