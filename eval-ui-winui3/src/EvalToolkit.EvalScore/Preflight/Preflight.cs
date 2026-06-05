namespace EvalToolkit.EvalScore.Preflight;

/// <summary>
/// Preflight orchestrator. Mirrors TS <c>runPreflight</c> +
/// <c>testConnectivity</c> + <c>printPreflightResults</c> in
/// <c>eval-score/node/src/setup.ts</c>.
///
/// <para>Behavior preserved:
/// <list type="bullet">
///   <item><see cref="PreflightOptions.SkipConnectivityTest"/> defaults
///     to <c>true</c> (matches TS).</item>
///   <item>EULA check fires first; if the marker file is absent, the
///     caller-supplied <see cref="PreflightOptions.ApproveEulaAsync"/>
///     callback is invoked (UI shells use a dialog; CLI shims wire to
///     <see cref="EulaService.ApproveEulaAsync"/>).</item>
///   <item>Connectivity test sends <c>"Reply with the word 'connected'
///     to confirm you are working."</c> via the caller-supplied
///     <see cref="PreflightOptions.AskClient"/> (the TS lazy import of
///     CliWorkIQClient is intentionally NOT ported here — the caller
///     supplies the WorkIQ client to avoid a hard dep on the CLI from
///     the engine library).</item>
///   <item>Skipped checks are reported with a <c>"Skipped"</c> message
///     so the printer can show the ⏭️  icon.</item>
/// </list></para>
/// </summary>
public static class Preflight
{
    private const string ConnectivityProbe = "Reply with the word 'connected' to confirm you are working.";

    public static async Task<PreflightResult> RunAsync(
        PreflightOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PreflightOptions();
        var checks = new List<PreflightCheck>();

        // 1. WorkIQ EULA
        bool eulaAccepted = EulaService.IsEulaAccepted();
        if (!eulaAccepted && options.ApproveEulaAsync is not null)
        {
            eulaAccepted = await options.ApproveEulaAsync(cancellationToken).ConfigureAwait(false);
        }
        checks.Add(new PreflightCheck(
            Name: "WorkIQ EULA",
            Passed: eulaAccepted,
            Message: eulaAccepted ? "EULA accepted" : "EULA declined"));

        // 2. Connectivity test (skipped by default)
        if (!options.SkipConnectivityTest)
        {
            ConnectivityResult conn = await TestConnectivityAsync(
                options.TenantId,
                options.AskClient,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new PreflightCheck(
                Name: "WorkIQ connectivity",
                Passed: conn.Connected,
                Message: conn.Message));
        }
        else
        {
            checks.Add(new PreflightCheck(
                Name: "WorkIQ connectivity",
                Passed: true,
                Message: "Skipped"));
        }

        bool allPassed = checks.TrueForAll(c => c.Passed);
        return new PreflightResult(allPassed, checks);
    }

    public static async Task<ConnectivityResult> TestConnectivityAsync(
        string? tenantId,
        Func<string, CancellationToken, Task<string>>? askClient,
        CancellationToken cancellationToken = default)
    {
        if (askClient is null)
        {
            return new ConnectivityResult(
                Connected: false,
                Message: "No WorkIQ client configured. Provide PreflightOptions.AskClient.",
                ResponseTimeMs: 0);
        }

        long start = Environment.TickCount64;
        try
        {
            await askClient(ConnectivityProbe, cancellationToken).ConfigureAwait(false);
            long responseTimeMs = Environment.TickCount64 - start;
            return new ConnectivityResult(
                Connected: true,
                Message: $"WorkIQ responded in {(responseTimeMs / 1000.0):F1}s",
                ResponseTimeMs: responseTimeMs);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            long responseTimeMs = Environment.TickCount64 - start;
            string tenantFragment = string.IsNullOrEmpty(tenantId) ? string.Empty : $" -t {tenantId}";
            return new ConnectivityResult(
                Connected: false,
                Message: $"WorkIQ connectivity test failed: {ex.Message}\n        " +
                         $"Verify workiq works by running: workiq{tenantFragment} ask -q \"Say hello\"",
                ResponseTimeMs: responseTimeMs);
        }
    }

    /// <summary>
    /// Print preflight results to the supplied writer (typically
    /// <see cref="Console.Error"/>). Mirrors TS
    /// <c>printPreflightResults</c>.
    /// </summary>
    public static void PrintResults(PreflightResult result, Action<string> writer)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(writer);

        writer(string.Empty);
        writer("╔══════════════════════════════════════════════╗");
        writer("║  Preflight Checks                            ║");
        writer("╚══════════════════════════════════════════════╝");
        writer(string.Empty);

        int displayIndex = 0;
        foreach (PreflightCheck check in result.Checks)
        {
            displayIndex++;
            if (check.Message == "Skipped")
            {
                writer($"  [{displayIndex}/{result.Checks.Count}] {check.Name}... ⏭️  Skipped");
                continue;
            }
            string icon = check.Passed ? "✅" : "❌";
            writer($"  [{displayIndex}/{result.Checks.Count}] {check.Name}... {icon} {check.Message}");
        }
        writer(string.Empty);

        if (!result.Passed)
        {
            writer("  ──────────────────────────────────────────");
            writer("  One or more preflight checks failed.");
            writer("  Fix the issues above and try again.");
            writer("  ──────────────────────────────────────────");
            writer(string.Empty);
        }
        else
        {
            writer("  All preflight checks passed.");
            writer(string.Empty);
        }
    }
}

public sealed record PreflightOptions
{
    public string? TenantId { get; init; }
    public bool SkipConnectivityTest { get; init; } = true;
    public Func<string, CancellationToken, Task<string>>? AskClient { get; init; }
    public Func<CancellationToken, Task<bool>>? ApproveEulaAsync { get; init; }
}

public sealed record PreflightCheck(string Name, bool Passed, string Message);

public sealed record PreflightResult(bool Passed, IReadOnlyList<PreflightCheck> Checks);

public sealed record ConnectivityResult(bool Connected, string Message, long ResponseTimeMs);
