using System.Reflection;

namespace EvalToolkit.Parity.Harness;

/// <summary>
/// Locates the sibling <c>eval-gen</c> directory from a running
/// assembly. The harness lives at
/// <c>eval-ui-winui3/src/EvalToolkit.Parity.Harness/</c> and needs to
/// find <c>eval-gen/dist/parity-entrypoint.js</c> at
/// <c>eval-ui-winui3/../eval-gen/dist/parity-entrypoint.js</c>.
///
/// Lookup strategy (checked in order):
/// <list type="number">
///   <item><c>EVALTOOLKIT_EVAL_GEN_DIR</c> env var (escape hatch for
///     CI / customer-fixture debugging where the source tree layout
///     differs).</item>
///   <item>Walk up from <see cref="AppContext.BaseDirectory"/>
///     looking for a sibling <c>eval-gen</c> dir that contains
///     <c>package.json</c>. This handles
///     <c>bin/Debug/net10.0</c>-style outputs in the standard repo
///     layout.</item>
/// </list>
/// Throws if neither succeeds — failing loudly here is better than
/// proceeding with a wrong path and producing misleading parity-diff
/// output that blames an algorithm bug instead of a setup bug.
/// </summary>
public static class EvalGenLocator
{
    /// <summary>Env var that lets CI / dev override the discovered <c>eval-gen</c> root.</summary>
    public const string OverrideEnvVar = "EVALTOOLKIT_EVAL_GEN_DIR";

    /// <summary>
    /// Returns the absolute path to the <c>eval-gen</c> directory.
    /// Does NOT verify the parity entrypoint is built — call
    /// <see cref="GetParityEntrypointPath"/> for that.
    /// </summary>
    public static string GetEvalGenRoot()
    {
        string? overrideDir = Environment.GetEnvironmentVariable(OverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            string resolved = Path.GetFullPath(overrideDir.Trim());
            if (!Directory.Exists(resolved))
            {
                throw new DirectoryNotFoundException(
                    $"{OverrideEnvVar} pointed at '{resolved}' but that directory does not exist.");
            }
            return resolved;
        }

        DirectoryInfo? cursor = new(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            string candidate = Path.Combine(cursor.FullName, "eval-gen");
            if (Directory.Exists(candidate) &&
                File.Exists(Path.Combine(candidate, "package.json")))
            {
                return candidate;
            }
            cursor = cursor.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate sibling eval-gen/ directory by walking up from " +
            $"'{AppContext.BaseDirectory}'. Set {OverrideEnvVar} to override.");
    }

    /// <summary>
    /// Returns the absolute path to <c>dist/parity-entrypoint.js</c>
    /// for use as the <c>node</c> entry argument. Throws with a
    /// build-the-TS-side hint if the file is missing.
    /// </summary>
    public static string GetParityEntrypointPath()
    {
        string root = GetEvalGenRoot();
        string entry = Path.Combine(root, "dist", "parity-entrypoint.js");
        if (!File.Exists(entry))
        {
            throw new FileNotFoundException(
                $"Parity entrypoint not found at '{entry}'. " +
                $"Run `npm run build` in '{root}' to produce dist/parity-entrypoint.js " +
                $"before invoking the parity harness.",
                entry);
        }
        return entry;
    }

    /// <summary>
    /// Lightweight existence probe — for tests that should be skipped
    /// (e.g. via <c>Skip.IfNot(...)</c> patterns) when the TS side
    /// hasn't been built. Returns true only if both the directory and
    /// the compiled entrypoint exist.
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            _ = GetParityEntrypointPath();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the assembly version of this harness. Stamped into
    /// parity-diff envelopes so a regressing build can be correlated
    /// with a specific harness rev.
    /// </summary>
    public static string GetHarnessVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
}
