namespace MmoEngine.SimCore.Hitboxes;

/// <summary>The body part a hit landed on (data-model.md#reason-and-outcome-codes).</summary>
public enum HitboxPart : byte
{
    /// <summary>The head capsule.</summary>
    Head = 0,

    /// <summary>The torso capsule.</summary>
    Torso = 1,

    /// <summary>The legs capsule.</summary>
    Legs = 2,

    /// <summary>No body part: a terrain or mesh hit.</summary>
    None = 255,
}
