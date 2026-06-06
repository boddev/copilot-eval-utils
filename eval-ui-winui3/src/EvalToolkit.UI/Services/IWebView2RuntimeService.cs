namespace EvalToolkit.UI.Services;

/// <summary>
/// Detects and (when packaged) bootstraps the Microsoft Edge WebView2
/// Runtime. The WinUI app uses WebView2 only on the Step 5 report
/// viewer, so this service is consulted from <see cref="Views.WizardView"/>
/// on first render of a scored report, not at app launch.
///
/// <para>The Evergreen Bootstrapper (~1.7 MB) is intended to be bundled
/// at packaging time under <c>Assets/webview2/MicrosoftEdgeWebview2Setup.exe</c>.
/// The repo does not commit the binary; the production MSIX/ZIP pipeline
/// populates it from the public fwlink during build. On dev builds where
/// the binary is missing, the bundled-install path is unavailable and
/// only the manual-installer link works — both are exposed as separate
/// methods to keep UX semantics clear (per slice-27 GPT-5.5 review).</para>
/// </summary>
public interface IWebView2RuntimeService
{
    /// <summary>
    /// Returns true iff the WebView2 Runtime is currently usable on this
    /// machine. Uses <see cref="Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(string)"/>
    /// and treats a null/empty return or any thrown exception as "missing".
    /// </summary>
    Task<bool> IsRuntimeAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// True iff the Evergreen Bootstrapper EXE is present in the app
    /// bundle. Use this to decide whether the "Install" UI action is
    /// available — when false, surface the "Get installer" action instead.
    /// </summary>
    bool IsBundledInstallerAvailable { get; }

    /// <summary>
    /// Absolute path of the bundled Evergreen Bootstrapper (whether or
    /// not it exists). Exposed for diagnostics/logging.
    /// </summary>
    string BundledInstallerPath { get; }

    /// <summary>
    /// The public fwlink that downloads the current Evergreen
    /// Bootstrapper. Stable URL per
    /// <see href="https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution"/>.
    /// </summary>
    string ManualInstallerUrl { get; }

    /// <summary>
    /// Launch the bundled Evergreen Bootstrapper and wait for it to
    /// exit, then re-check <see cref="IsRuntimeAvailableAsync"/>. Returns
    /// true iff the runtime is now available. Throws
    /// <see cref="InvalidOperationException"/> when
    /// <see cref="IsBundledInstallerAvailable"/> is false.
    /// </summary>
    Task<bool> TryRunBundledBootstrapperAsync(
        IProgress<string>? log,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens <see cref="ManualInstallerUrl"/> in the user's default
    /// browser. Does not block on or verify installation — the caller
    /// is expected to retry detection later.
    /// </summary>
    Task OpenManualInstallerAsync(CancellationToken cancellationToken = default);
}
