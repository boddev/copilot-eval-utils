using EvalToolkit.WorkIQ;
using Microsoft.Identity.Client;

namespace EvalToolkit.WorkIQ.Tests;

/// <summary>
/// Tests for <see cref="MsalA2ATokenCachePlugin"/>'s legacy-import
/// path. The plugin's actual DPAPI persistence is delegated to
/// <c>MsalCacheHelper</c> (and runs only on Windows); these tests
/// drive the import branch logic — best-effort skip on
/// missing/corrupt files, no-op when DPAPI cache already exists.
///
/// Per Opus-4.8 plan-stage review (M5): the import goes through
/// <c>SaveUnencryptedTokenCache</c> rather than <c>DeserializeMsalV3</c>
/// on a registered cache (which would be clobbered by the
/// BeforeAccess load). These tests verify the gating logic; the
/// happy-path "imported account is usable" assertion requires a real
/// MSAL v3 cache fixture which we can't synthesize without standing
/// up a tenant.
/// </summary>
[Collection("WorkIQTempCache")]
public class MsalA2ATokenCachePluginTests : IDisposable
{
    private readonly string _tempDir;

    public MsalA2ATokenCachePluginTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"auth-port-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RegisterAsync_NoLegacyFile_DoesNotCreateDpapiCache()
    {
        string dpapiPath = Path.Combine(_tempDir, "cache.bin");
        string legacyPath = Path.Combine(_tempDir, "missing.json");
        var plugin = new MsalA2ATokenCachePlugin(
            clientId: "11111111-1111-1111-1111-111111111111",
            dpapiCachePath: dpapiPath,
            legacyJsonCachePath: legacyPath);

        IPublicClientApplication app = PublicClientApplicationBuilder
            .Create("11111111-1111-1111-1111-111111111111")
            .WithAuthority("https://login.microsoftonline.com/22222222-2222-2222-2222-222222222222", validateAuthority: false)
            .Build();

        await plugin.RegisterAsync(app);

        Assert.False(File.Exists(dpapiPath));
    }

    [Fact]
    public async Task RegisterAsync_CorruptLegacyFile_SilentlySkipped()
    {
        string dpapiPath = Path.Combine(_tempDir, "cache.bin");
        string legacyPath = Path.Combine(_tempDir, "corrupt.json");
        await File.WriteAllTextAsync(legacyPath, "this is not msal v3 json");

        var plugin = new MsalA2ATokenCachePlugin(
            clientId: "11111111-1111-1111-1111-111111111111",
            dpapiCachePath: dpapiPath,
            legacyJsonCachePath: legacyPath);

        IPublicClientApplication app = PublicClientApplicationBuilder
            .Create("11111111-1111-1111-1111-111111111111")
            .WithAuthority("https://login.microsoftonline.com/22222222-2222-2222-2222-222222222222", validateAuthority: false)
            .Build();

        // Should not throw — best-effort import absorbs all errors.
        await plugin.RegisterAsync(app);

        // No DPAPI file was created from junk input.
        Assert.False(File.Exists(dpapiPath));
    }

    [Fact]
    public async Task RegisterAsync_EmptyLegacyFile_SilentlySkipped()
    {
        string dpapiPath = Path.Combine(_tempDir, "cache.bin");
        string legacyPath = Path.Combine(_tempDir, "empty.json");
        await File.WriteAllTextAsync(legacyPath, string.Empty);

        var plugin = new MsalA2ATokenCachePlugin(
            clientId: "11111111-1111-1111-1111-111111111111",
            dpapiCachePath: dpapiPath,
            legacyJsonCachePath: legacyPath);

        IPublicClientApplication app = PublicClientApplicationBuilder
            .Create("11111111-1111-1111-1111-111111111111")
            .WithAuthority("https://login.microsoftonline.com/22222222-2222-2222-2222-222222222222", validateAuthority: false)
            .Build();

        await plugin.RegisterAsync(app);

        Assert.False(File.Exists(dpapiPath));
    }

    [Fact]
    public async Task RegisterAsync_DpapiCacheAlreadyExists_DoesNotImportLegacy()
    {
        string dpapiPath = Path.Combine(_tempDir, "cache.bin");
        string legacyPath = Path.Combine(_tempDir, "legacy.json");
        // Make the DPAPI file appear pre-existing so the import is
        // skipped even though the legacy file would otherwise apply.
        await File.WriteAllBytesAsync(dpapiPath, new byte[] { 0x01, 0x02 });
        await File.WriteAllTextAsync(legacyPath, "{}");

        var plugin = new MsalA2ATokenCachePlugin(
            clientId: "11111111-1111-1111-1111-111111111111",
            dpapiCachePath: dpapiPath,
            legacyJsonCachePath: legacyPath);

        IPublicClientApplication app = PublicClientApplicationBuilder
            .Create("11111111-1111-1111-1111-111111111111")
            .WithAuthority("https://login.microsoftonline.com/22222222-2222-2222-2222-222222222222", validateAuthority: false)
            .Build();

        await plugin.RegisterAsync(app);

        // DPAPI file untouched (still our 2 sentinel bytes).
        byte[] postRegister = await File.ReadAllBytesAsync(dpapiPath);
        Assert.Equal(new byte[] { 0x01, 0x02 }, postRegister);
    }

    [Fact]
    public void Constructor_RejectsEmptyClientId()
    {
        Assert.Throws<ArgumentException>(() => new MsalA2ATokenCachePlugin(clientId: ""));
        Assert.Throws<ArgumentException>(() => new MsalA2ATokenCachePlugin(clientId: "   "));
    }
}

/// <summary>
/// Serializes the cache-plugin tests so concurrent runs don't fight
/// over the shared MSAL persistence helper's process-wide mutex.
/// </summary>
#pragma warning disable CA1711 // CollectionDefinition suffix is xUnit-required naming.
[CollectionDefinition("WorkIQTempCache", DisableParallelization = true)]
public class WorkIQTempCacheCollection
{
}
#pragma warning restore CA1711
