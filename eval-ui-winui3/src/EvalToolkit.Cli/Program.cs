using System.CommandLine;
using EvalToolkit.Cli.Commands;
using EvalToolkit.Core;

namespace EvalToolkit.Cli;

/// <summary>
/// Entry point for the native CLI shims (<c>eval-gen-native.exe</c> and
/// <c>eval-score-native.exe</c>).
///
/// <para>
/// A single binary (<c>EvalToolkit.Cli.exe</c>) is published once and copied
/// at packaging time to the two shim names. At runtime we inspect the
/// invoked binary name and route to the corresponding subcommand so that
/// <c>eval-gen-native --file ... --description ...</c> is equivalent to
/// <c>EvalToolkit.Cli eval-gen --file ... --description ...</c>.
/// </para>
///
/// <para>This mirrors the deployment model the existing
/// <c>install-tools.cmd</c> uses for the Node-side <c>eval-gen</c> and
/// <c>eval-score</c> bins, while keeping a single build artifact.</para>
/// </summary>
internal static class Program
{
    private const string GenerateBinaryName = "eval-gen-native";
    private const string ScoreBinaryName = "eval-score-native";

    private static async Task<int> Main(string[] args)
    {
        string invokedName = ResolveInvokedBinaryName();

        // Name-based dispatch: prepend the synthetic subcommand so the same
        // System.CommandLine root handles both the unified and per-shim entry
        // points without duplicating option definitions.
        string[] effectiveArgs = invokedName switch
        {
            GenerateBinaryName => Prepend(args, "eval-gen"),
            ScoreBinaryName => Prepend(args, "eval-score"),
            _ => args,
        };

        var root = BuildRootCommand();
        var parse = root.Parse(effectiveArgs);
        return await parse.InvokeAsync().ConfigureAwait(false);
    }

    private static RootCommand BuildRootCommand()
    {
        var root = new RootCommand(
            $"{CoreInfo.ProductName} v{CoreInfo.Version} — native CLI shims " +
            "(eval-gen-native, eval-score-native).");
        root.Subcommands.Add(GenerateCommand.Build());
        root.Subcommands.Add(ScoreCommand.Build());
        return root;
    }

    private static string ResolveInvokedBinaryName()
    {
        try
        {
            // Environment.ProcessPath returns the actual executable launched
            // (the apphost shim, e.g. C:\path\eval-gen-native.exe), whereas
            // Environment.GetCommandLineArgs()[0] returns the managed DLL path
            // when launched through the apphost. We need the shim name to do
            // name-based subcommand dispatch.
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                return Path.GetFileNameWithoutExtension(exePath) ?? string.Empty;
            }
            string[] commandLine = Environment.GetCommandLineArgs();
            if (commandLine.Length == 0) return string.Empty;
            return Path.GetFileNameWithoutExtension(commandLine[0]) ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string[] Prepend(string[] args, string head)
    {
        var combined = new string[args.Length + 1];
        combined[0] = head;
        Array.Copy(args, 0, combined, 1, args.Length);
        return combined;
    }
}
