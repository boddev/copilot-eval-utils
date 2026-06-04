using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace EvalToolkit.WorkIQ;

/// <summary>
/// Wires a <c>Microsoft.Identity.Client.Extensions.Msal</c>
/// <see cref="MsalCacheHelper"/> to a PublicClientApplication's user
/// token cache. Cache storage uses DPAPI on Windows
/// (libsecret / Keychain on Linux/macOS); the cache file lives at
/// <see cref="DpapiCachePath"/>, NOT at the legacy plaintext-JSON
/// path that the Node tool uses.
///
/// <para>
/// On first run, if a legacy <c>~/.evalscore/msal-a2a-cache.json</c>
/// file exists AND the DPAPI cache does not, the JSON is read with
/// <see cref="ITokenCacheSerializer.DeserializeMsalV3(byte[], bool)"/>
/// and persisted via DPAPI. This is one-way — subsequent token
/// refreshes write to DPAPI only. Documented in the slice plan.
/// </para>
///
/// <para>Per GPT-5.5 plan-stage review (auth-port): legacy import is
/// "best effort" — if the JSON is unreadable or the deserialize
/// throws, the import is silently skipped and the user is treated as
/// uncached. The provider's first silent acquire will fail and the
/// device-code / interactive path will take over.</para>
/// </summary>
public sealed class MsalA2ATokenCachePlugin
{
    /// <summary>Default cache file location (<c>%LOCALAPPDATA%\EvalToolkit\msal-a2a-cache.bin</c>).</summary>
    public string DpapiCachePath { get; }

    /// <summary>Legacy plaintext-JSON path imported on first run (best effort).</summary>
    public string LegacyJsonCachePath { get; }

    private readonly StorageCreationProperties _storageProperties;
    private readonly string _clientId;
    private MsalCacheHelper? _helper;
    private bool _legacyImportAttempted;
    private bool _importedFromLegacy;

    public MsalA2ATokenCachePlugin(string clientId, string? dpapiCachePath = null, string? legacyJsonCachePath = null)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("MSAL client id is required for the cache plugin.", nameof(clientId));
        }
        _clientId = clientId;
        DpapiCachePath = dpapiCachePath ?? MsalA2ATokenCachePaths.DefaultDpapiCachePath();
        LegacyJsonCachePath = legacyJsonCachePath ?? MsalA2ATokenProviderConfig.DefaultLegacyCachePath();

        string cacheDir = Path.GetDirectoryName(DpapiCachePath)
            ?? throw new ArgumentException("DPAPI cache path must include a directory.", nameof(dpapiCachePath));
        string cacheFile = Path.GetFileName(DpapiCachePath);

        var builder = new StorageCreationPropertiesBuilder(cacheFile, cacheDir);
        // On Linux, the helper requires an unprotected fallback path to
        // be configured explicitly (libsecret may not be available in
        // headless environments such as CI). Configure it but keep the
        // file mode-0600 so it's not world-readable. On Windows this
        // call is a no-op because DPAPI is always available.
        builder.WithUnprotectedFile();
        _storageProperties = builder.Build();
    }

    /// <summary>
    /// Register the cache with the PCA's user-token cache. Performs
    /// the one-time legacy JSON import if applicable. Thread-safe per
    /// MsalCacheHelper's documented contract.
    /// </summary>
    public async Task RegisterAsync(IPublicClientApplication app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (_helper is null)
        {
            _helper = await MsalCacheHelper.CreateAsync(_storageProperties).ConfigureAwait(false);
        }

        // Per Opus-4.8 plan-stage review (M5): do the legacy import
        // BEFORE registering the helper. Otherwise BeforeAccess fires
        // on the first cache op and loads the (empty) DPAPI bin,
        // clobbering anything we deserialized. SaveUnencryptedTokenCache
        // writes V3 bytes through the helper's encrypted storage; the
        // subsequent RegisterCache + GetAccountsAsync round-trip then
        // loads the imported data via BeforeAccess.
        if (!_legacyImportAttempted)
        {
            _legacyImportAttempted = true;
            TryImportLegacyCache();
        }

        _helper.RegisterCache(app.UserTokenCache);

        // Per Opus-4.8 plan-stage review (M5): if the import didn't
        // actually produce any usable accounts (corrupted bytes,
        // wrong client/tenant), wipe the DPAPI cache so we don't
        // carry junk forward — the next call falls through to
        // device-code / interactive. Use file-level delete rather
        // than MsalCacheHelper.Clear() (which is obsolete) since we
        // know the cache is functionally empty.
        if (_importedFromLegacy)
        {
            IEnumerable<IAccount> accounts = await app.GetAccountsAsync().ConfigureAwait(false);
            if (!accounts.Any())
            {
                try
                {
                    if (File.Exists(DpapiCachePath))
                    {
                        File.Delete(DpapiCachePath);
                    }
                }
                catch
                {
                    // Best-effort delete.
                }
                _importedFromLegacy = false;
            }
        }
    }

    /// <summary>
    /// Read the legacy plaintext JSON cache (if any) and persist it
    /// via the helper's encrypted storage. Swallows ALL errors
    /// (corruption, missing, permission). Per the plan: "first-run
    /// import ... (best effort)".
    /// </summary>
    private void TryImportLegacyCache()
    {
        try
        {
            // Skip if DPAPI cache already exists (we've already imported, or there's a real session).
            if (File.Exists(DpapiCachePath))
            {
                return;
            }
            if (!File.Exists(LegacyJsonCachePath))
            {
                return;
            }

            byte[] legacyBytes = File.ReadAllBytes(LegacyJsonCachePath);
            if (legacyBytes.Length == 0)
            {
                return;
            }

            // Validate the bytes parse as MSAL V3 cache before
            // persisting — a separate temp cache lets us catch
            // malformed JSON / wrong-shape input without polluting
            // the helper's underlying storage. The Node tool emits
            // msal-node v3's unified-cache JSON which is the same
            // wire format DeserializeMsalV3 reads.
            try
            {
                var probe = new MsalCacheProbeApp(_clientId);
                ((ITokenCacheSerializer)probe.UserTokenCache).DeserializeMsalV3(legacyBytes, shouldClearExistingCache: true);
            }
            catch
            {
                // Malformed legacy file — silently skip.
                return;
            }

            _helper!.SaveUnencryptedTokenCache(legacyBytes);
            _importedFromLegacy = true;
        }
        catch
        {
            // Best-effort: any failure (file unreadable, permission
            // denied, helper save failure) is silently absorbed. The
            // next silent acquire will fall through to device-code /
            // interactive naturally.
        }
    }

    /// <summary>
    /// Throwaway PCA used solely to validate that legacy cache bytes
    /// deserialize. We don't want to pollute the production PCA's
    /// cache during validation — Opus-4.8 review M5.
    /// </summary>
    private sealed class MsalCacheProbeApp
    {
        public ITokenCache UserTokenCache { get; }

        public MsalCacheProbeApp(string clientId)
        {
            IPublicClientApplication app = PublicClientApplicationBuilder
                .Create(clientId)
                .WithAuthority("https://login.microsoftonline.com/common", validateAuthority: false)
                .Build();
            UserTokenCache = app.UserTokenCache;
        }
    }
}
