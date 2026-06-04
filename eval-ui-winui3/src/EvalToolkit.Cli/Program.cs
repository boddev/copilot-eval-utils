using EvalToolkit.Core;

namespace EvalToolkit.Cli;

/// <summary>
/// Entry point for the native CLI shims. The single binary will dispatch
/// to either <c>eval-gen-native</c> or <c>eval-score-native</c> based on
/// its invoked name (managed via copies / symlinks at install time) or
/// the first positional argument when run as <c>EvalToolkit.Cli.exe</c>.
///
/// Replaced with the real System.CommandLine pipeline in the
/// <c>cli-shims</c> phase A todo.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.Error.WriteLine($"{CoreInfo.Name} v{CoreInfo.Version} — CLI shim not yet implemented.");
        Console.Error.WriteLine("Phase A todo `cli-shims` adds System.CommandLine dispatch for");
        Console.Error.WriteLine("`eval-gen-native` and `eval-score-native`.");
        if (args.Length > 0)
        {
            Console.Error.WriteLine($"Received {args.Length} argument(s): {string.Join(' ', args)}");
        }
        return 1;
    }
}
