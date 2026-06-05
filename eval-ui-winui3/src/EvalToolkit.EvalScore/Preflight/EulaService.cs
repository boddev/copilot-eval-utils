namespace EvalToolkit.EvalScore.Preflight;

/// <summary>
/// Manages the WorkIQ End User License Agreement marker file. Mirrors
/// TS helpers <c>checkEulaAccepted</c> / <c>recordEulaAcceptance</c> /
/// <c>approveEula</c> in <c>eval-score/node/src/setup.ts</c>.
///
/// <para>The marker file lives at
/// <c>%USERPROFILE%\.workiq-eula-accepted</c> (TS uses the same path
/// via <c>process.env.USERPROFILE || HOME || '.'</c>). On Windows the
/// fallback "." case never triggers in practice; the C# port uses
/// <see cref="Environment.SpecialFolder.UserProfile"/>, which always
/// resolves on Windows.</para>
///
/// <para>The marker file content matches the TS exactly:
/// <c>"Accepted on {ISO8601 timestamp} for {EULA_URL}\n"</c>.</para>
/// </summary>
public static class EulaService
{
    public const string EulaUrl = "https://github.com/microsoft/work-iq-mcp";

    public static string MarkerFilePath { get; set; } = DefaultMarkerFilePath();

    public static string DefaultMarkerFilePath()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            home = ".";
        }
        return Path.Combine(home, ".workiq-eula-accepted");
    }

    public static bool IsEulaAccepted() => File.Exists(MarkerFilePath);

    public static void RecordEulaAcceptance()
    {
        string? dir = Path.GetDirectoryName(MarkerFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        string iso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(MarkerFilePath, $"Accepted on {iso} for {EulaUrl}\n");
    }

    /// <summary>
    /// Interactive yes/no prompt that mirrors the TS <c>approveEula</c>
    /// flow. Returns true when the user accepts. The <paramref name="reader"/>
    /// callback returns the user's typed answer (typically wired to
    /// <see cref="Console.ReadLine"/>); the <paramref name="writer"/>
    /// receives the prompt text (typically wired to
    /// <see cref="Console.Error"/>).
    /// </summary>
    public static async Task<bool> ApproveEulaAsync(
        Func<CancellationToken, Task<string?>> reader,
        Action<string> writer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        writer(string.Empty);
        writer("┌──────────────────────────────────────────────────┐");
        writer("│  WorkIQ End User License Agreement               │");
        writer("└──────────────────────────────────────────────────┘");
        writer(string.Empty);
        writer("  Before using WorkIQ, you must accept the EULA.");
        writer($"  Review the terms at: {EulaUrl}");
        writer(string.Empty);
        writer("  Do you accept the WorkIQ EULA? (yes/no): ");

        string? raw = await reader(cancellationToken).ConfigureAwait(false);
        string answer = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (answer is "y" or "yes")
        {
            RecordEulaAcceptance();
            writer("  ✅ EULA accepted.");
            return true;
        }

        writer("  ❌ EULA declined. WorkIQ cannot be used without accepting the EULA.");
        return false;
    }
}
