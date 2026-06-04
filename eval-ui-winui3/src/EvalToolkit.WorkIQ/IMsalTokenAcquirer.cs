using Microsoft.Identity.Client;

namespace EvalToolkit.WorkIQ;

/// <summary>
/// Payload for an interactive auth request. Carries the data WAM
/// needs (scopes, login hint, parent-window handle) so the
/// interactive seam can evolve without churning every call site.
/// </summary>
/// <param name="Scopes">Scopes to request.</param>
/// <param name="Account">
/// Previously-cached account (may be null on first run). The broker
/// can use this as a login hint.
/// </param>
/// <param name="LoginHint">Optional UPN / email login hint.</param>
/// <param name="ParentWindowHandle">
/// Optional parent-window handle for WAM. The WinUI shell sets this
/// to the main window's HWND.
/// </param>
public sealed record InteractiveAuthRequest(
    IReadOnlyList<string> Scopes,
    IAccount? Account,
    string? LoginHint = null,
    IntPtr? ParentWindowHandle = null);

/// <summary>
/// Window-handle / WAM / interactive broker seam that the WinUI shell
/// implements (deferred to the <c>winui-shell</c> slice). When no
/// implementation is provided, <see cref="MsalA2ATokenProvider"/>
/// falls back to MSAL device-code if
/// <see cref="MsalA2ATokenProviderConfig.AllowDeviceCode"/> is true,
/// or throws otherwise — matching TS exactly.
///
/// Per GPT-5.5 plan-stage review (auth-port): prefer a small
/// interface over a raw delegate so the broker can evolve to take a
/// window handle, account hint, telemetry, etc. without churning
/// every call site. Per Opus-4.8 plan-stage review (S1): the request
/// payload is a record so future shell additions don't require an
/// interface change.
/// </summary>
public interface IInteractiveAuthBroker
{
    /// <summary>
    /// Acquire a token interactively (WAM / web browser /
    /// embedded WebView2 — implementation's choice).
    /// </summary>
    Task<AuthenticationResult> AcquireTokenAsync(
        InteractiveAuthRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Narrow abstraction over the MSAL PublicClientApplication APIs the
/// provider needs. Tests inject a stub to exercise the silent-first
/// → device-code/interactive fallback ordering without a real AAD
/// tenant.
///
/// Per GPT-5.5 plan-stage review (auth-port): use a multi-method
/// abstraction rather than a single "acquire" delegate so tests can
/// prove silent-first ordering, force-refresh behavior, and
/// fallback semantics independently.
/// </summary>
internal interface IMsalTokenAcquirer
{
    /// <summary>Return the first cached account (TS does <c>accounts[0]</c>), or null.</summary>
    Task<IAccount?> GetCachedAccountAsync(CancellationToken cancellationToken);

    /// <summary>Acquire a token silently from cache / refresh token.</summary>
    Task<AuthenticationResult> AcquireTokenSilentAsync(
        IAccount account,
        IReadOnlyList<string> scopes,
        bool forceRefresh,
        CancellationToken cancellationToken);

    /// <summary>
    /// Acquire via device-code flow. The callback receives the device
    /// code message exactly as MSAL emits it (TS writes it to stderr).
    /// </summary>
    Task<AuthenticationResult> AcquireTokenByDeviceCodeAsync(
        IReadOnlyList<string> scopes,
        Func<DeviceCodeResult, Task> deviceCodeCallback,
        CancellationToken cancellationToken);
}
