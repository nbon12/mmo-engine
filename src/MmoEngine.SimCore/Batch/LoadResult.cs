namespace MmoEngine.SimCore.Batch;

/// <summary>
/// What a <see cref="BatchStepper"/> load call returns
/// (specs/015-shared-simulation-core/contracts/wasm-api.md#return-codes). Anything but <see cref="Ok"/>
/// leaves the reason in the report buffer: a <c>u32</c> text length, then that many bytes of UTF-8.
/// </summary>
public enum LoadResult : byte
{
    /// <summary>Loaded; the report's text length is 0.</summary>
    Ok = 0,

    /// <summary>The bytes failed their decoder; the report holds the format error's message.</summary>
    FormatRefused = 1,

    /// <summary>The config block decoded but failed the guard; the report holds every error, one <c>path: message</c> line each.</summary>
    ConfigInvalid = 2,

    /// <summary><c>Reset</c> ran before both the collision world and the configuration were loaded; the report's text length is 0.</summary>
    NotReady = 3,
}
