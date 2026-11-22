using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Wasm.Tests.Support;

/// <summary>
/// One committed recorded stream, <c>simcore/streams/&lt;name&gt;.mmrs</c>, with
/// <c>simcore/content/test-world.mmcd</c>, both opened read-only through <see cref="RepoPaths"/>, and the
/// inputs the host's exports take, derived with core APIs only: the stream is decoded with
/// <see cref="RecordedStream.Decode"/>. The derived bytes are the ones <c>agreement prepare</c> writes under
/// <c>batch/&lt;stream&gt;/</c> (contracts/simcore-cli.md#agreement-work-directory). Every <see cref="Load"/>
/// reads the files again and returns new arrays, so a test may change what it gets.
/// </summary>
public sealed class CommittedStreamInputs
{
    private CommittedStreamInputs(string name, byte[] stream, byte[] collision, RecordedStream decoded)
    {
        Name = name;
        Stream = stream;
        Collision = collision;
        ConfigBlock = MmoEngine.SimCore.Config.ConfigBlock.Encode(decoded.Config);
        InitialState = decoded.InitialState.ToArray();
        MatchSalt = decoded.MatchSalt;
        StepCount = decoded.Steps.Count;
        Commands = IsBatchable(decoded) ? EncodeCommands(decoded) : null;
    }

    /// <summary>The stream's name, its file name without <c>.mmrs</c>.</summary>
    public string Name { get; }

    /// <summary>The stream file's bytes.</summary>
    public byte[] Stream { get; }

    /// <summary>The bytes of <c>simcore/content/test-world.mmcd</c>, the collision file the stream names.</summary>
    public byte[] Collision { get; }

    /// <summary>The stream's embedded configuration re-encoded as a config block (<c>batch/&lt;stream&gt;/config.bin</c>).</summary>
    public byte[] ConfigBlock { get; }

    /// <summary>The stream's initial state: canonical, with zero events (<c>batch/&lt;stream&gt;/initial-state.bin</c>).</summary>
    public byte[] InitialState { get; }

    /// <summary>The match salt from the stream's header.</summary>
    public ulong MatchSalt { get; }

    /// <summary>The number of step entries in the stream.</summary>
    public int StepCount { get; }

    /// <summary>
    /// For a batchable stream (every step carries exactly one command, for character index 0), its commands
    /// in step order, each encoded in 17 bytes with <see cref="InputCodec.TryEncode"/>
    /// (<c>batch/&lt;stream&gt;/commands.bin</c>); null for any other stream.
    /// </summary>
    public byte[]? Commands { get; }

    /// <summary>Reads and decodes <c>simcore/streams/<paramref name="name"/>.mmrs</c> and the test world.</summary>
    /// <exception cref="CoreFormatException">The stream does not decode.</exception>
    /// <exception cref="InvalidOperationException">The stream names a collision file other than the test world.</exception>
    public static CommittedStreamInputs Load(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var stream = RepoPaths.ReadAllBytes("simcore", "streams", name + ".mmrs");
        var collision = RepoPaths.ReadAllBytes("simcore", "content", "test-world.mmcd");
        var decoded = RecordedStream.Decode(stream);
        var checksum = CollisionFile.Decode(collision).Checksum;
        if (decoded.CollisionChecksum != checksum)
        {
            throw new InvalidOperationException(
                $"simcore/streams/{name}.mmrs names collision checksum {decoded.CollisionChecksum:x16}; simcore/content/test-world.mmcd is {checksum:x16}.");
        }

        return new CommittedStreamInputs(name, stream, collision, decoded);
    }

    /// <summary>The batchable rule of contracts/simcore-cli.md#agreement-work-directory.</summary>
    private static bool IsBatchable(RecordedStream stream) =>
        stream.Steps.All(step => step.Commands.Length == 1 && step.Commands[0].CharacterIndex == 0);

    private static byte[] EncodeCommands(RecordedStream stream)
    {
        var commands = new byte[stream.Steps.Count * InputCodec.EncodedSize];
        for (var s = 0; s < stream.Steps.Count; s++)
        {
            var command = stream.Steps[s].Commands[0].Command;
            if (!InputCodec.TryEncode(in command, commands.AsSpan(s * InputCodec.EncodedSize, InputCodec.EncodedSize)))
            {
                throw new InvalidOperationException($"InputCodec.TryEncode refused step entry {s}'s command.");
            }
        }

        return commands;
    }
}
