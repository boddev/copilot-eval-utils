using EvalToolkit.WorkIQ;
using Microsoft.Identity.Client;
using System.Reflection;

namespace EvalToolkit.WorkIQ.Tests;

/// <summary>
/// Tests for <see cref="MsalA2ATokenProvider"/>'s in-memory cache,
/// in-flight de-dupe, silent-first fallback ordering, and error
/// message parity with the TS implementation.
///
/// <para>Tests bypass real MSAL by injecting a stub
/// <see cref="IMsalTokenAcquirer"/> via the internal constructor
/// (InternalsVisibleTo wires this through). Per Opus-4.8 plan-stage
/// review (T1): the seam is at the AAD-call boundary, so the
/// provider's orchestration logic (cache check, account lookup,
/// silent attempt, allowDeviceCode branch, saveResult) runs for real
/// — only the network calls are faked.</para>
/// </summary>
public class MsalA2ATokenProviderTests
{
    private static MsalA2ATokenProviderConfig CreateConfig(bool allowDeviceCode = false)
    {
        return new MsalA2ATokenProviderConfig(
            ClientId: "11111111-1111-1111-1111-111111111111",
            TenantId: "22222222-2222-2222-2222-222222222222",
            Scopes: new[] { "https://example.invalid/.default" },
            CachePath: Path.Combine(Path.GetTempPath(), $"auth-port-test-cache-{Guid.NewGuid():N}.json"),
            AllowDeviceCode: allowDeviceCode);
    }

    // ── Authority URL ────────────────────────────────────────────────

    [Theory]
    [InlineData("22222222-2222-2222-2222-222222222222", "https://login.microsoftonline.com/22222222-2222-2222-2222-222222222222")]
    [InlineData("contoso.onmicrosoft.com", "https://login.microsoftonline.com/contoso.onmicrosoft.com")]
    [InlineData("organizations", "https://login.microsoftonline.com/organizations")]
    [InlineData("common", "https://login.microsoftonline.com/common")]
    public void Authority_GuidAndDomainTenantsBuildExpectedUrl(string tenant, string expected)
    {
        // Per GPT-5.5 plan-stage residual: explicitly test both
        // GUID and domain tenant shapes since the TS encodes via
        // encodeURIComponent — neither shape requires escaping.
        var config = CreateConfig() with { TenantId = tenant };
        var stub = new StubAcquirer();
        var provider = new MsalA2ATokenProvider(config, stub);
        Assert.Equal(expected, provider.Authority);
    }

    // ── In-memory cache + skew window ────────────────────────────────

    [Fact]
    public async Task GetToken_CachedTokenBeyondSkew_ReturnsCachedWithoutAcquirerCall()
    {
        var config = CreateConfig();
        var stub = new StubAcquirer();
        var provider = new MsalA2ATokenProvider(config, stub);
        SetCachedToken(provider, "cached-token", DateTimeOffset.UtcNow.AddHours(1));

        string token = await provider.GetTokenAsync(forceRefresh: false, CancellationToken.None);

        Assert.Equal("cached-token", token);
        Assert.Equal(0, stub.SilentCalls);
        Assert.Equal(0, stub.DeviceCodeCalls);
    }

    [Fact]
    public async Task GetToken_CachedTokenInsideSkew_RefreshesThroughAcquirer()
    {
        var config = CreateConfig(allowDeviceCode: true);
        var stub = new StubAcquirer { SilentResult = MakeAuthResult("fresh-token", DateTimeOffset.UtcNow.AddHours(1)) };
        var provider = new MsalA2ATokenProvider(config, stub);
        // 2 minutes from now is well INSIDE the 5-minute skew window.
        SetCachedToken(provider, "stale-token", DateTimeOffset.UtcNow.AddMinutes(2));
        stub.Account = new StubAccount();

        string token = await provider.GetTokenAsync(forceRefresh: false, CancellationToken.None);

        Assert.Equal("fresh-token", token);
        Assert.Equal(1, stub.SilentCalls);
    }

    [Fact]
    public async Task GetToken_SkewBoundary_TreatedAsExpired()
    {
        // Per Opus-4.8 plan-stage review (T2): TS uses strict
        // `expiresInMs > 5 * 60 * 1000`, so an entry expiring at
        // EXACTLY 5 minutes is refreshed, not returned.
        var config = CreateConfig(allowDeviceCode: true);
        var stub = new StubAcquirer { SilentResult = MakeAuthResult("fresh-token", DateTimeOffset.UtcNow.AddHours(1)) };
        var provider = new MsalA2ATokenProvider(config, stub);
        SetCachedToken(provider, "stale-token", DateTimeOffset.UtcNow + MsalA2ATokenProvider.ExpirationSkew);
        stub.Account = new StubAccount();

        string token = await provider.GetTokenAsync(forceRefresh: false, CancellationToken.None);

        Assert.Equal("fresh-token", token);
    }

    // ── Force refresh ────────────────────────────────────────────────

    [Fact]
    public async Task GetToken_ForceRefresh_BypassesInMemoryCacheAndFlowsForceFlagToSilent()
    {
        // Per Opus-4.8 plan-stage review (M3): forceRefresh must
        // reach AcquireTokenSilent's force flag, not just bypass the
        // in-memory cache. The A2A 401-retry path depends on this.
        var config = CreateConfig(allowDeviceCode: true);
        var stub = new StubAcquirer
        {
            Account = new StubAccount(),
            SilentResult = MakeAuthResult("forced-token", DateTimeOffset.UtcNow.AddHours(1)),
        };
        var provider = new MsalA2ATokenProvider(config, stub);
        SetCachedToken(provider, "cached-token", DateTimeOffset.UtcNow.AddHours(1));

        string token = await provider.GetTokenAsync(forceRefresh: true, CancellationToken.None);

        Assert.Equal("forced-token", token);
        Assert.Equal(1, stub.SilentCalls);
        Assert.True(stub.LastSilentForce);
    }

    [Fact]
    public async Task GetToken_TrueParallelNonForceCalls_ShareSingleAcquisition()
    {
        // Per round-2 reviewer feedback (Opus B1 + GPT-5.5 #2): the
        // simpler sequential dedupe test above passes even when the
        // publication-to-_inFlight is non-atomic, because the first
        // call's synchronous prelude wins under sequential
        // single-thread invocation. To truly verify the lock-based
        // claim, launch many parallel callers via Task.Run with a
        // Barrier so they all hit GetTokenAsync at essentially the
        // same instant. Under the buggy old code, SilentCalls would
        // be > 1; under the TCS-under-lock fix, it stays at exactly 1.
        var config = CreateConfig(allowDeviceCode: true);
        var releaseGate = new TaskCompletionSource();
        var stub = new StubAcquirer
        {
            Account = new StubAccount(),
            SilentResultFactory = async ct =>
            {
                await releaseGate.Task.ConfigureAwait(false);
                return MakeAuthResult("shared-parallel-token", DateTimeOffset.UtcNow.AddHours(1));
            },
        };
        var provider = new MsalA2ATokenProvider(config, stub);

        const int parallelCount = 32;
        var barrier = new Barrier(parallelCount);
        var tasks = new Task<string>[parallelCount];
        for (int i = 0; i < parallelCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                return provider.GetTokenAsync(forceRefresh: false, CancellationToken.None);
            });
        }

        // Give the threads a moment to all reach SignalAndWait and
        // queue up at the lock before we release the silent flow.
        await Task.Delay(50);
        releaseGate.SetResult();

        string[] results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal("shared-parallel-token", r));
        Assert.Equal(1, stub.SilentCalls);
    }

    // ── Concurrent de-dupe ───────────────────────────────────────────

    [Fact]
    public async Task GetToken_ConcurrentNonForceCalls_ShareSingleAcquisition()
    {
        var config = CreateConfig(allowDeviceCode: true);
        var releaseGate = new TaskCompletionSource();
        var stub = new StubAcquirer
        {
            Account = new StubAccount(),
            SilentResultFactory = async ct =>
            {
                await releaseGate.Task.ConfigureAwait(false);
                return MakeAuthResult("shared-token", DateTimeOffset.UtcNow.AddHours(1));
            },
        };
        var provider = new MsalA2ATokenProvider(config, stub);

        Task<string> first = provider.GetTokenAsync(forceRefresh: false, CancellationToken.None);
        Task<string> second = provider.GetTokenAsync(forceRefresh: false, CancellationToken.None);

        releaseGate.SetResult();
        string[] results = await Task.WhenAll(first, second);

        Assert.Equal("shared-token", results[0]);
        Assert.Equal("shared-token", results[1]);
        Assert.Equal(1, stub.SilentCalls);
    }

    [Fact]
    public async Task GetToken_ConcurrentForceCalls_DoNotDedupe()
    {
        // Per Opus-4.8 plan-stage review (T2): force calls each get
        // their own acquisition (TS only stashes inFlight when
        // !forceRefresh).
        var config = CreateConfig(allowDeviceCode: true);
        int call = 0;
        var stub = new StubAcquirer
        {
            Account = new StubAccount(),
            SilentResultFactory = ct =>
            {
                int index = Interlocked.Increment(ref call);
                return Task.FromResult(MakeAuthResult($"force-token-{index}", DateTimeOffset.UtcNow.AddHours(1)));
            },
        };
        var provider = new MsalA2ATokenProvider(config, stub);

        string[] results = await Task.WhenAll(
            provider.GetTokenAsync(forceRefresh: true, CancellationToken.None),
            provider.GetTokenAsync(forceRefresh: true, CancellationToken.None));

        Assert.Equal(2, stub.SilentCalls);
        Assert.Contains("force-token-1", results);
        Assert.Contains("force-token-2", results);
    }

    // ── Silent first, then fallback ──────────────────────────────────

    [Fact]
    public async Task GetToken_NoAccountAndNoBroker_ThrowsExactTsMessageWithNoSilentReasonClause()
    {
        // Per Opus-4.8 plan-stage review (M1) variant 1: no cached
        // account → reason is empty → message has no middle clause.
        var config = CreateConfig(allowDeviceCode: false);
        var stub = new StubAcquirer { Account = null };
        var provider = new MsalA2ATokenProvider(config, stub);

        WorkIQException ex = await Assert.ThrowsAsync<WorkIQException>(
            () => provider.GetTokenAsync(forceRefresh: false, CancellationToken.None));

        Assert.Equal(
            "MSAL A2A auth requires an interactive device-code sign-in, but this process is non-interactive." +
            " Run eval-score once in an interactive terminal, or configure WORK_IQ_A2A_TOKEN_COMMAND instead.",
            ex.Message);
    }

    [Fact]
    public async Task GetToken_SilentFailsAndDeviceCodeDisallowed_ThrowsExactTsMessageIncludingReason()
    {
        // Per Opus-4.8 plan-stage review (M1) variant 2: cached
        // account exists, silent threw → reason clause is interpolated
        // between prefix and suffix.
        var config = CreateConfig(allowDeviceCode: false);
        var stub = new StubAcquirer
        {
            Account = new StubAccount(),
            SilentResultFactory = _ => throw new MsalUiRequiredException("interaction_required", "AAD said no"),
        };
        var provider = new MsalA2ATokenProvider(config, stub);

        WorkIQException ex = await Assert.ThrowsAsync<WorkIQException>(
            () => provider.GetTokenAsync(forceRefresh: false, CancellationToken.None));

        Assert.StartsWith(
            "MSAL A2A auth requires an interactive device-code sign-in, but this process is non-interactive.",
            ex.Message);
        Assert.Contains(" Silent token acquisition failed: ", ex.Message);
        Assert.Contains("AAD said no", ex.Message);
        Assert.EndsWith(
            " Run eval-score once in an interactive terminal, or configure WORK_IQ_A2A_TOKEN_COMMAND instead.",
            ex.Message);
    }

    [Fact]
    public async Task GetToken_SilentFailsAndDeviceCodeAllowed_FallsBackToDeviceCode()
    {
        var config = CreateConfig(allowDeviceCode: true);
        var stub = new StubAcquirer
        {
            Account = new StubAccount(),
            SilentResultFactory = _ => throw new MsalUiRequiredException("interaction_required", "no cache"),
            DeviceCodeResult = MakeAuthResult("device-token", DateTimeOffset.UtcNow.AddHours(1)),
        };
        var provider = new MsalA2ATokenProvider(config, stub);

        string token = await provider.GetTokenAsync(forceRefresh: false, CancellationToken.None);

        Assert.Equal("device-token", token);
        Assert.Equal(1, stub.SilentCalls);
        Assert.Equal(1, stub.DeviceCodeCalls);
    }

    [Fact]
    public async Task GetToken_InteractiveBrokerWins_OverDeviceCode()
    {
        var config = CreateConfig(allowDeviceCode: true);
        var stub = new StubAcquirer
        {
            Account = null,
            DeviceCodeResult = MakeAuthResult("device-token", DateTimeOffset.UtcNow.AddHours(1)),
        };
        var broker = new StubBroker
        {
            Result = MakeAuthResult("broker-token", DateTimeOffset.UtcNow.AddHours(1)),
        };
        var provider = new MsalA2ATokenProvider(config, stub, broker);

        string token = await provider.GetTokenAsync(forceRefresh: false, CancellationToken.None);

        Assert.Equal("broker-token", token);
        Assert.Equal(0, stub.DeviceCodeCalls);
        Assert.Equal(1, broker.Calls);
    }

    [Fact]
    public async Task GetToken_AcquirerReturnsNullToken_ThrowsParityWithTsSaveResult()
    {
        // Per Opus-4.8 plan-stage review (M2): TS throws
        // "MSAL A2A auth did not return an access token." when
        // saveResult sees an empty token. Mirror verbatim.
        var config = CreateConfig(allowDeviceCode: true);
        var stub = new StubAcquirer
        {
            Account = new StubAccount(),
            SilentResultFactory = _ => Task.FromResult(MakeAuthResult(string.Empty, DateTimeOffset.UtcNow.AddHours(1))),
        };
        var provider = new MsalA2ATokenProvider(config, stub);

        WorkIQException ex = await Assert.ThrowsAsync<WorkIQException>(
            () => provider.GetTokenAsync(forceRefresh: false, CancellationToken.None));
        Assert.Equal("MSAL A2A auth did not return an access token.", ex.Message);
    }

    // ── Construction validation ──────────────────────────────────────

    [Fact]
    public void Constructor_RejectsEmptyClientId()
    {
        var config = CreateConfig() with { ClientId = string.Empty };
        Assert.Throws<ArgumentException>(() => new MsalA2ATokenProvider(config));
    }

    [Fact]
    public void Constructor_RejectsEmptyTenantId()
    {
        var config = CreateConfig() with { TenantId = string.Empty };
        Assert.Throws<ArgumentException>(() => new MsalA2ATokenProvider(config));
    }

    [Fact]
    public void Constructor_RejectsEmptyScopes()
    {
        var config = CreateConfig() with { Scopes = Array.Empty<string>() };
        Assert.Throws<ArgumentException>(() => new MsalA2ATokenProvider(config));
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static void SetCachedToken(MsalA2ATokenProvider provider, string token, DateTimeOffset expiresOn)
    {
        // Tests poke private state to short-circuit the cache layer
        // without round-tripping through MSAL. Justified because the
        // cache logic itself is the unit under test.
        Type type = typeof(MsalA2ATokenProvider);
        FieldInfo tokenField = type.GetField("_cachedAccessToken", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo expiryField = type.GetField("_cachedAccessTokenExpiresOn", BindingFlags.NonPublic | BindingFlags.Instance)!;
        tokenField.SetValue(provider, token);
        expiryField.SetValue(provider, (DateTimeOffset?)expiresOn);
    }

    private static AuthenticationResult MakeAuthResult(string accessToken, DateTimeOffset expiresOn)
    {
        // AuthenticationResult has a public ctor for testing.
        return new AuthenticationResult(
            accessToken: accessToken,
            isExtendedLifeTimeToken: false,
            uniqueId: "unique",
            expiresOn: expiresOn,
            extendedExpiresOn: expiresOn,
            tenantId: "tenant",
            account: new StubAccount(),
            idToken: null,
            scopes: new[] { "scope-a" },
            correlationId: Guid.NewGuid());
    }

    private sealed class StubAccount : IAccount
    {
        public string Username => "user@example.com";
        public string Environment => "login.microsoftonline.com";
        public AccountId HomeAccountId => new("oid.tid", "oid", "tid");
    }

    private sealed class StubAcquirer : IMsalTokenAcquirer
    {
        public IAccount? Account { get; set; }
        public AuthenticationResult? SilentResult { get; set; }
        public Func<CancellationToken, Task<AuthenticationResult>>? SilentResultFactory { get; set; }
        public AuthenticationResult? DeviceCodeResult { get; set; }

        public int SilentCalls;
        public int DeviceCodeCalls;
        public bool LastSilentForce;

        public Task<IAccount?> GetCachedAccountAsync(CancellationToken cancellationToken) => Task.FromResult(Account);

        public Task<AuthenticationResult> AcquireTokenSilentAsync(IAccount account, IReadOnlyList<string> scopes, bool forceRefresh, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref SilentCalls);
            LastSilentForce = forceRefresh;
            if (SilentResultFactory is not null)
            {
                return SilentResultFactory(cancellationToken);
            }
            if (SilentResult is null)
            {
                throw new MsalUiRequiredException("no_account", "no result configured");
            }
            return Task.FromResult(SilentResult);
        }

        public Task<AuthenticationResult> AcquireTokenByDeviceCodeAsync(IReadOnlyList<string> scopes, Func<DeviceCodeResult, Task> deviceCodeCallback, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref DeviceCodeCalls);
            if (DeviceCodeResult is null)
            {
                throw new InvalidOperationException("test did not configure DeviceCodeResult");
            }
            return Task.FromResult(DeviceCodeResult);
        }
    }

    private sealed class StubBroker : IInteractiveAuthBroker
    {
        public AuthenticationResult? Result { get; set; }
        public int Calls;

        public Task<AuthenticationResult> AcquireTokenAsync(InteractiveAuthRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (Result is null)
            {
                throw new InvalidOperationException("test did not configure broker Result");
            }
            return Task.FromResult(Result);
        }
    }
}
