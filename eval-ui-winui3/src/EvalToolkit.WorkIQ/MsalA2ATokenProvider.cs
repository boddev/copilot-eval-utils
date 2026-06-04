using EvalToolkit.Core;
using Microsoft.Identity.Client;

namespace EvalToolkit.WorkIQ;

/// <summary>
/// MSAL-backed A2A access-token provider. Mirrors
/// <c>MsalA2ATokenProvider</c> in
/// <c>eval-score/node/src/workiq-client.ts</c>:
/// <list type="bullet">
///   <item>In-memory cache with a 5-minute skew window.</item>
///   <item>Single in-flight de-dupe for concurrent non-force callers.</item>
///   <item>Silent flow first using the first cached account.</item>
///   <item>If silent fails and device-code is disallowed → throws
///         with the exact TS error message format.</item>
///   <item>If an <see cref="IInteractiveAuthBroker"/> is registered
///         (the WinUI shell plugs WAM in here later), the interactive
///         flow takes precedence over device-code.</item>
///   <item>Otherwise device-code via MSAL with the device code
///         message written to <see cref="Console.Error"/> (TS does
///         <c>console.error(response.message)</c>).</item>
/// </list>
///
/// <para>
/// Thread safety: per GPT-5.5 plan-stage review (auth-port), all
/// cache field mutations and in-flight task assignments happen under
/// <c>_stateLock</c>. The C# port runs in a true multi-threaded
/// environment (unlike Node's single event loop) so without the lock
/// a forced refresh could race with a concurrent non-force call and
/// overwrite a fresh token with stale data.
/// </para>
/// </summary>
public sealed class MsalA2ATokenProvider : IA2ATokenProvider
{
    /// <summary>Skew window pulled forward exactly from the TS implementation (5 minutes).</summary>
    public static readonly TimeSpan ExpirationSkew = TimeSpan.FromMinutes(5);

    private readonly MsalA2ATokenProviderConfig _config;
    private readonly IInteractiveAuthBroker? _interactiveBroker;
    private readonly Lock _stateLock = new();
    private readonly Action<DeviceCodeResult> _deviceCodeOutput;

    private IMsalTokenAcquirer? _acquirer;
    private Func<IMsalTokenAcquirer>? _acquirerFactory;

    private string? _cachedAccessToken;
    private DateTimeOffset? _cachedAccessTokenExpiresOn;
    private Task<string>? _inFlight;

    /// <summary>
    /// Production constructor. The token acquirer is built lazily on
    /// first acquisition so MSAL initialization cost doesn't fall on
    /// the constructor of an unused provider (parallels TS lazy
    /// <c>getApplication()</c>).
    /// </summary>
    /// <param name="config">Validated MSAL configuration.</param>
    /// <param name="interactiveBroker">
    /// Optional WAM / interactive seam. The WinUI shell injects this
    /// to skip device-code in favor of a WAM prompt; when null and
    /// <see cref="MsalA2ATokenProviderConfig.AllowDeviceCode"/> is
    /// true, device-code is used.
    /// </param>
    /// <param name="cachePlugin">
    /// Optional cache plugin override. Defaults to a fresh
    /// <see cref="MsalA2ATokenCachePlugin"/> using
    /// <see cref="MsalA2ATokenCachePaths.DefaultDpapiCachePath"/> +
    /// the config's <c>CachePath</c> as the legacy-import source.
    /// </param>
    public MsalA2ATokenProvider(
        MsalA2ATokenProviderConfig config,
        IInteractiveAuthBroker? interactiveBroker = null,
        MsalA2ATokenCachePlugin? cachePlugin = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrWhiteSpace(_config.ClientId))
        {
            throw new ArgumentException("MsalA2ATokenProviderConfig.ClientId is required.", nameof(config));
        }
        if (string.IsNullOrWhiteSpace(_config.TenantId))
        {
            throw new ArgumentException("MsalA2ATokenProviderConfig.TenantId is required.", nameof(config));
        }
        if (_config.Scopes is null || _config.Scopes.Count == 0)
        {
            throw new ArgumentException("MsalA2ATokenProviderConfig.Scopes must be non-empty.", nameof(config));
        }

        _interactiveBroker = interactiveBroker;
        _deviceCodeOutput = WriteDeviceCodeMessageToStderr;
        MsalA2ATokenCachePlugin plugin = cachePlugin ?? new MsalA2ATokenCachePlugin(
            clientId: _config.ClientId,
            dpapiCachePath: null,
            legacyJsonCachePath: _config.CachePath);
        _acquirerFactory = () => CreateProductionAcquirer(plugin);
    }

    /// <summary>
    /// Internal test seam: inject a stub acquirer. Bypasses MSAL PCA
    /// construction entirely. <strong>Tests only.</strong>
    /// </summary>
    internal MsalA2ATokenProvider(
        MsalA2ATokenProviderConfig config,
        IMsalTokenAcquirer acquirer,
        IInteractiveAuthBroker? interactiveBroker = null,
        Action<DeviceCodeResult>? deviceCodeOutput = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _acquirer = acquirer ?? throw new ArgumentNullException(nameof(acquirer));
        _interactiveBroker = interactiveBroker;
        _deviceCodeOutput = deviceCodeOutput ?? WriteDeviceCodeMessageToStderr;
    }

    /// <summary>
    /// Authority URL exactly as MSAL.NET expects:
    /// <c>https://login.microsoftonline.com/{tenant}</c>. We use
    /// <see cref="Uri.EscapeDataString(string)"/> on the tenant
    /// segment to mirror TS's <c>encodeURIComponent</c>. Both GUID
    /// and verified-domain tenants pass through unchanged because
    /// neither contains characters that require percent-encoding;
    /// the escape only protects against malformed operator input
    /// (tested for both shapes).
    /// </summary>
    public string Authority => $"https://login.microsoftonline.com/{Uri.EscapeDataString(_config.TenantId)}";

    public Task<string> GetTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        // Force path: always acquire fresh; never publishes to or
        // observes _inFlight (matches TS where forceRefresh bypasses
        // the cached-promise dedupe entirely).
        if (forceRefresh)
        {
            return AcquireAndCacheAsync(forceRefresh: true, cancellationToken);
        }

        // Non-force path: hot-cache check → in-flight dedupe →
        // publish a new TaskCompletionSource as the in-flight task
        // *atomically inside the lock*, then fire the real
        // acquisition outside the lock and settle the TCS. This
        // closes the round-2 reviewer race window where two callers
        // could both pass the no-_inFlight check and start
        // concurrent acquisitions before either of them published a
        // task to _inFlight (B1 from GPT-5.5 + Opus-4.8 round 2).
        TaskCompletionSource<string> tcs;
        lock (_stateLock)
        {
            if (TryGetCachedTokenLocked(out string? hit))
            {
                return Task.FromResult(hit!);
            }
            if (_inFlight is not null)
            {
                return _inFlight;
            }
            tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight = tcs.Task;
        }

        // Run outside the lock so the async path doesn't run any of
        // its synchronous prelude under _stateLock.
        _ = RunAndSettleAsync(tcs, cancellationToken);
        return tcs.Task;
    }

    private async Task RunAndSettleAsync(TaskCompletionSource<string> tcs, CancellationToken cancellationToken)
    {
        try
        {
            string token = await AcquireAndCacheAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
            tcs.TrySetResult(token);
        }
        catch (OperationCanceledException oce)
        {
            tcs.TrySetCanceled(oce.CancellationToken);
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }

    private async Task<string> AcquireAndCacheAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        try
        {
            AuthenticationResult result = await AcquireTokenAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
            string accessToken = result.AccessToken;
            if (string.IsNullOrEmpty(accessToken))
            {
                throw new WorkIQException("MSAL A2A auth did not return an access token.");
            }

            lock (_stateLock)
            {
                // Only publish to cache if we don't already have a
                // newer entry — protects against a force call landing
                // a stale token after a concurrent silent succeeded.
                DateTimeOffset newExpiry = result.ExpiresOn;
                if (_cachedAccessTokenExpiresOn is null || newExpiry > _cachedAccessTokenExpiresOn.Value)
                {
                    _cachedAccessToken = accessToken;
                    _cachedAccessTokenExpiresOn = newExpiry;
                }
            }
            return accessToken;
        }
        finally
        {
            lock (_stateLock)
            {
                if (!forceRefresh)
                {
                    _inFlight = null;
                }
            }
        }
    }

    private async Task<AuthenticationResult> AcquireTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        IMsalTokenAcquirer acquirer = GetAcquirer();
        IAccount? account = await acquirer.GetCachedAccountAsync(cancellationToken).ConfigureAwait(false);

        Exception? silentError = null;
        if (account is not null)
        {
            try
            {
                return await acquirer.AcquireTokenSilentAsync(account, _config.Scopes, forceRefresh, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                silentError = ex;
            }
        }

        // Per GPT-5.5 plan-stage review: interactive broker (when
        // wired) is preferred over device-code in GUI hosts.
        if (_interactiveBroker is not null)
        {
            var request = new InteractiveAuthRequest(_config.Scopes, account);
            return await _interactiveBroker.AcquireTokenAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!_config.AllowDeviceCode)
        {
            string reason = silentError is null ? string.Empty : $" Silent token acquisition failed: {silentError.Message}";
            throw new WorkIQException(
                "MSAL A2A auth requires an interactive device-code sign-in, but this process is non-interactive." +
                reason +
                " Run eval-score once in an interactive terminal, or configure WORK_IQ_A2A_TOKEN_COMMAND instead.");
        }

        return await acquirer.AcquireTokenByDeviceCodeAsync(
            _config.Scopes,
            (DeviceCodeResult dcr) =>
            {
                _deviceCodeOutput(dcr);
                return Task.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private bool TryGetCachedTokenLocked(out string? hit)
    {
        if (_cachedAccessToken is null || _cachedAccessTokenExpiresOn is null)
        {
            hit = null;
            return false;
        }
        TimeSpan ttl = _cachedAccessTokenExpiresOn.Value - DateTimeOffset.UtcNow;
        if (ttl <= ExpirationSkew)
        {
            hit = null;
            return false;
        }
        hit = _cachedAccessToken;
        return true;
    }

    private IMsalTokenAcquirer GetAcquirer()
    {
        if (_acquirer is not null)
        {
            return _acquirer;
        }
        lock (_stateLock)
        {
            if (_acquirer is null)
            {
                if (_acquirerFactory is null)
                {
                    throw new InvalidOperationException("MSAL acquirer factory was not configured.");
                }
                _acquirer = _acquirerFactory();
                _acquirerFactory = null;
            }
            return _acquirer;
        }
    }

    private IMsalTokenAcquirer CreateProductionAcquirer(MsalA2ATokenCachePlugin cachePlugin)
    {
        var builder = PublicClientApplicationBuilder.Create(_config.ClientId)
            .WithAuthority(Authority, validateAuthority: true);
        IPublicClientApplication app = builder.Build();
        IMsalTokenAcquirer acquirer = new PcaTokenAcquirer(app, cachePlugin);
        return acquirer;
    }

    private static void WriteDeviceCodeMessageToStderr(DeviceCodeResult result)
    {
        Console.Error.WriteLine(result.Message);
    }

    /// <summary>Production acquirer wrapping a real PCA + cache plugin.</summary>
    private sealed class PcaTokenAcquirer : IMsalTokenAcquirer, IDisposable
    {
        private readonly IPublicClientApplication _app;
        private readonly MsalA2ATokenCachePlugin _cachePlugin;
        private readonly SemaphoreSlim _registrationGate = new(1, 1);
        private volatile bool _cacheRegistered;

        public PcaTokenAcquirer(IPublicClientApplication app, MsalA2ATokenCachePlugin cachePlugin)
        {
            _app = app;
            _cachePlugin = cachePlugin;
        }

        // Per round-2 reviewer feedback (R2): the prior implementation
        // checked _cacheRegistered without synchronization, allowing
        // concurrent token requests to invoke RegisterAsync more than
        // once. SemaphoreSlim with double-check ensures exactly-once
        // registration even under heavy concurrent load.
        private async Task EnsureCacheRegisteredAsync(CancellationToken cancellationToken)
        {
            if (_cacheRegistered)
            {
                return;
            }
            await _registrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_cacheRegistered)
                {
                    return;
                }
                await _cachePlugin.RegisterAsync(_app, cancellationToken).ConfigureAwait(false);
                _cacheRegistered = true;
            }
            finally
            {
                _registrationGate.Release();
            }
        }

        public async Task<IAccount?> GetCachedAccountAsync(CancellationToken cancellationToken)
        {
            await EnsureCacheRegisteredAsync(cancellationToken).ConfigureAwait(false);
            IEnumerable<IAccount> accounts = await _app.GetAccountsAsync().ConfigureAwait(false);
            return accounts.FirstOrDefault();
        }

        public async Task<AuthenticationResult> AcquireTokenSilentAsync(
            IAccount account,
            IReadOnlyList<string> scopes,
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            return await _app.AcquireTokenSilent(scopes, account)
                .WithForceRefresh(forceRefresh)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<AuthenticationResult> AcquireTokenByDeviceCodeAsync(
            IReadOnlyList<string> scopes,
            Func<DeviceCodeResult, Task> deviceCodeCallback,
            CancellationToken cancellationToken)
        {
            return await _app.AcquireTokenWithDeviceCode(scopes, deviceCodeCallback)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public void Dispose()
        {
            _registrationGate.Dispose();
        }
    }
}
