namespace EvalToolkit.EvalGen.LlmClients;

/// <summary>
/// Resolves the standalone GitHub Copilot CLI executable (<c>copilot</c>).
///
/// <para>The CLI ships two ways on Windows: a real <c>copilot.exe</c> (e.g.
/// installed via WinGet) and an npm shim (<c>copilot.cmd</c> /
/// <c>copilot.ps1</c> that forwards to <c>node npm-loader.js</c>). A .NET
/// <see cref="System.Diagnostics.Process"/> launched with
/// <c>UseShellExecute=false</c> can only start a real executable, not the npm
/// shim (it fails with "%1 is not a valid Win32 application"). So on Windows we
/// deliberately resolve <c>copilot.exe</c> and never the <c>.cmd</c>/<c>.ps1</c>
/// shims.</para>
///
/// <para>Resolution order: (1) the <c>COPILOT_CLI_PATH</c> environment
/// override, (2) a real <c>copilot.exe</c>/<c>copilot</c> found on
/// <c>PATH</c>, (3) the bare command name as a last resort.</para>
/// </summary>
internal static class CopilotCliLocator
{
    private const string OverrideEnvVar = "COPILOT_CLI_PATH";

    private static readonly object s_lock = new();
    private static string? s_cached;

    /// <summary>Resolve and cache the executable path/name.</summary>
    public static string Resolve()
    {
        if (s_cached is not null) return s_cached;
        lock (s_lock)
        {
            s_cached ??= ResolveCore();
            return s_cached;
        }
    }

    private static string ResolveCore()
    {
        string? overridePath = Environment.GetEnvironmentVariable(OverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            string trimmed = overridePath.Trim().Trim('"');
            if (File.Exists(trimmed) && IsLaunchable(trimmed))
            {
                return trimmed;
            }

            throw new InvalidOperationException(
                $"{OverrideEnvVar} is set to '{overridePath}', which is not a launchable executable. " +
                (OperatingSystem.IsWindows()
                    ? "On Windows it must point at a real copilot.exe (npm .cmd/.ps1 shims cannot be launched directly)."
                    : "It must point at an executable file."));
        }

        return SearchPath() ?? (OperatingSystem.IsWindows() ? "copilot.exe" : "copilot");
    }

    private static bool IsLaunchable(string path)
    {
        if (!OperatingSystem.IsWindows()) return true;
        string ext = Path.GetExtension(path);
        return string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string? SearchPath()
    {
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;

        string[] names = OperatingSystem.IsWindows()
            ? new[] { "copilot.exe" }
            : new[] { "copilot" };

        foreach (string rawDir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(rawDir)) continue;

            string dir = rawDir.Trim().Trim('"');
            foreach (string name in names)
            {
                string candidate;
                try { candidate = Path.Combine(dir, name); }
                catch (ArgumentException) { continue; }

                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }
}
