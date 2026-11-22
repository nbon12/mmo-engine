namespace MmoEngine.SimCore.Formats;

/// <summary>Why a format decoder refused its input (data-model.md#reason-and-outcome-codes).</summary>
public enum FormatErrorCode : byte
{
    /// <summary>No error.</summary>
    None = 0,

    /// <summary>The format identifier is wrong.</summary>
    UnknownMagic = 1,

    /// <summary>The version is not one this reader supports.</summary>
    UnsupportedVersion = 2,

    /// <summary>Fewer bytes than declared or needed.</summary>
    Truncated = 3,

    /// <summary>More bytes than declared, or a count or length above its limit.</summary>
    Oversized = 4,

    /// <summary>Fields that disagree: lengths, ordering, or an unknown, missing or repeated config field ID.</summary>
    Inconsistent = 5,

    /// <summary>A value outside its documented range.</summary>
    OutOfRange = 6,

    /// <summary>A NaN or infinite float.</summary>
    NonFinite = 7,

    /// <summary>The XxHash64 trailer does not match.</summary>
    ChecksumMismatch = 8,

    /// <summary>A stream names a collision checksum that was not supplied.</summary>
    CollisionDataMissing = 9,

    /// <summary>A stream's rules revision differs from the core's; reported as not comparable.</summary>
    RulesRevisionMismatch = 10,
}
