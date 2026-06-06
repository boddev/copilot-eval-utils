using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Production <see cref="IWebView2RuntimeService"/> backed by the real
/// <see cref="CoreWebView2Environment.GetAvailableBrowserVersionString(string)"/>
/// API and a launched-process wait on the bundled bootstrapper. The
/// detection delegate is injectable so tests can drive the boolean
/// flip without needing the actual WebView2 runtime.
/// </summary>
public sealed class WebView2RuntimeService : IWebView2RuntimeService
{
    private const string EvergreenBootstrapperFileName = "MicrosoftEdgeWebview2Setup.exe";
    private const string EvergreenFwlinkUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private readonly Func<string?> _versionProbe;
    private readonly Func<string, IProgress<string>?, CancellationToken, Task<int>> _runProcess;
    private readonly Func<string, Task> _openUrl;
    private readonly Func<string, bool> _fileExists;

    /// <summary>Production constructor — uses the real APIs.</summary>
    public WebView2RuntimeService()
        : this(
            versionProbe: DefaultVersionProbe,
            runProcess: DefaultRunProcessAsync,
            openUrl: DefaultOpenUrlAsync,
            fileExists: File.Exists)
    {
    }

    /// <summary>Test constructor — accepts injectable seams.</summary>
    public WebView2RuntimeService(
        Func<string?> versionProbe,
        Func<string, IProgress<string>?, CancellationToken, Task<int>> runProcess,
        Func<string, Task> openUrl,
        Func<string, bool> fileExists)
    {
        _versionProbe = versionProbe ?? throw new ArgumentNullException(nameof(versionProbe));
        _runProcess = runProcess ?? throw new ArgumentNullException(nameof(runProcess));
        _openUrl = openUrl ?? throw new ArgumentNullException(nameof(openUrl));
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
    }

    public string BundledInstallerPath { get; } = Path.Combine(
        AppContext.BaseDirectory, "Assets", "webview2", EvergreenBootstrapperFileName);

    public bool IsBundledInstallerAvailable => _fileExists(BundledInstallerPath);

    public string ManualInstallerUrl => EvergreenFwlinkUrl;

    public Task<bool> IsRuntimeAvailableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The version probe is synchronous on the API surface but we
        // expose it as Task<bool> so callers always await it (matches
        // the rest of the service and keeps room for a future async
        // implementation, e.g. probing the install registry).
        bool available;
        try
        {
            string? version = _versionProbe();
            available = !string.IsNullOrEmpty(version);
        }
        catch
        {
            // Any exception (loader, DLL-not-found, etc.) means the
            // runtime is not usable.
            available = false;
        }
        return Task.FromResult(available);
    }

    public async Task<bool> TryRunBundledBootstrapperAsync(
        IProgress<string>? log,
        CancellationToken cancellationToken = default)
    {
        if (!IsBundledInstallerAvailable)
        {
            throw new InvalidOperationException(
                $"Bundled Evergreen Bootstrapper not present at '{BundledInstallerPath}'. " +
                $"Call {nameof(OpenManualInstallerAsync)} instead.");
        }

        log?.Report($"Launching {EvergreenBootstrapperFileName}…");
        int exitCode = await _runProcess(BundledInstallerPath, log, cancellationToken)
            .ConfigureAwait(false);
        log?.Report($"Bootstrapper exited (code {exitCode}); re-checking runtime…");

        // The bootstrapper exit code is not always reliable (it may
        // return 0 even when the user cancelled the silent install
        // prompt), so always probe the runtime fresh rather than
        // trusting the code.
        return await IsRuntimeAvailableAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task OpenManualInstallerAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _openUrl(ManualInstallerUrl);
    }

    // -- default seams ------------------------------------------------

    private static string? DefaultVersionProbe()
    {
        // Static API — returns null/empty when runtime is missing.
        return CoreWebView2Environment.GetAvailableBrowserVersionString();
    }

    private static async Task<int> DefaultRunProcessAsync(
        string exePath,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true,   // bootstrapper handles its own UAC
            CreateNoWindow = false,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{exePath}'.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static Task DefaultOpenUrlAsync(string url)
    {
        var psi = new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        };
        Process.Start(psi);
        return Task.CompletedTask;
    }
}
