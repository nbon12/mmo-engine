namespace MmoEngine.Maps;

/// <summary>
/// A map file could not be read or failed its schema checks (contracts/map-format.md). The portal
/// validator CLI exits with code 2 on it. The message names the field, for example
/// <c>layers[0].width: must be at least 1, was 0</c>.
/// </summary>
public sealed class MapFormatException : Exception
{
    public MapFormatException(string message)
        : base(message)
    {
    }

    public MapFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
