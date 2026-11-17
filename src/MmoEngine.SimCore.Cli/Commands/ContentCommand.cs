using System.Buffers.Binary;
using System.CommandLine;
using System.Globalization;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Server;
using Serilog;

namespace MmoEngine.SimCore.Cli.Commands;

/// <summary>
/// <c>content generate [--out simcore/content/test-world.mmcd]</c>
/// (specs/015-shared-simulation-core/contracts/simcore-cli.md#content-generate): encodes
/// <see cref="TestWorldGenerator.Generate"/> as <c>MMCD</c> v1, writes it through <see cref="AtomicFile"/>,
/// and prints <c>&lt;path&gt;: &lt;bytes&gt; bytes, checksum &lt;x16&gt;</c>, the checksum being the file's
/// XxHash64 trailer. The bytes are the same on every run and every machine. When the file cannot be
/// written, it prints the reason on stderr, writes nothing and exits 2.
/// </summary>
internal static class ContentCommand
{
    /// <summary>Where the test world is written when <c>--out</c> is not given.</summary>
    public const string DefaultOut = "simcore/content/test-world.mmcd";

    /// <summary>The <c>content</c> command with its <c>generate</c> subcommand.</summary>
    public static Command Create(TextWriter stdout, TextWriter stderr, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(logger);
        var output = new Option<string>("--out")
        {
            Description = "The collision file to write.",
            DefaultValueFactory = _ => DefaultOut,
        };
        var generate = new Command("generate", "Writes the deterministic test world.") { output };
        generate.SetAction(parse => Generate(parse.GetValue(output)!, stdout, stderr, logger));
        return new Command("content", "Collision content.") { generate };
    }

    /// <summary>Writes the test world to <paramref name="path"/> and returns the exit code.</summary>
    internal static int Generate(string path, TextWriter stdout, TextWriter stderr, ILogger logger)
    {
        var bytes = CollisionFile.Encode(TestWorldGenerator.Generate());
        var checksum = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bytes.Length - sizeof(ulong)));
        try
        {
            AtomicFile.WriteAllBytes(path, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            stderr.Write($"content generate: {path}: the file cannot be written ({ex.Message})\n");
            return CliExitCodes.Usage;
        }

        stdout.Write(string.Create(CultureInfo.InvariantCulture, $"{path}: {bytes.Length} bytes, checksum {checksum:x16}\n"));
        logger.Information("Wrote the test world {Path}: {Length} bytes, checksum {Checksum:x16}", path, bytes.Length, checksum);
        return CliExitCodes.Success;
    }
}
