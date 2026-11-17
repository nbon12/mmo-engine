using System.CommandLine;
using System.CommandLine.Parsing;

namespace MmoEngine.SimCore.Cli.Commands;

/// <summary>
/// Refuses an empty or whitespace-only path when the command line is parsed
/// (specs/015-shared-simulation-core/contracts/simcore-cli.md#settings-output-and-exit-codes): every path
/// option and argument of every command carries this validator, so a value such as <c>--content ""</c> is
/// a usage error that <see cref="Program"/> logs and exits 2 for before any command runs, rather than an
/// exception from a file API.
/// </summary>
internal static class PathOptions
{
    /// <summary>Adds the refusal to <paramref name="option"/> and returns it.</summary>
    public static Option<string> RequireNonEmpty(Option<string> option)
    {
        ArgumentNullException.ThrowIfNull(option);
        option.Validators.Add(result => Refuse(result, option.Name));
        return option;
    }

    /// <summary>Adds the refusal to <paramref name="argument"/> and returns it.</summary>
    public static Argument<string> RequireNonEmpty(Argument<string> argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        argument.Validators.Add(result => Refuse(result, $"<{argument.Name}>"));
        return argument;
    }

    /// <summary>The message a refused path gives, naming the option or argument.</summary>
    internal static string Message(string name) => $"{name}: the path must not be empty or whitespace";

    private static void Refuse(SymbolResult result, string name)
    {
        foreach (var token in result.Tokens)
        {
            if (string.IsNullOrWhiteSpace(token.Value))
            {
                result.AddError(Message(name));
                return;
            }
        }
    }
}
