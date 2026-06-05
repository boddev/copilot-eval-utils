using EvalToolkit.EvalScore.Preflight;

namespace EvalToolkit.EvalScore.Tests.Preflight;

#pragma warning disable CA1711
[CollectionDefinition("EulaService", DisableParallelization = true)]
public class EulaServiceCollection { }
#pragma warning restore CA1711

[Collection("EulaService")]
public class PreflightTests : IDisposable
{
    private readonly string _originalPath;
    private readonly string _tempPath;

    public PreflightTests()
    {
        _originalPath = EulaService.MarkerFilePath;
        _tempPath = Path.Combine(Path.GetTempPath(), $"preflight-test-{Guid.NewGuid():N}");
        EulaService.MarkerFilePath = _tempPath;
    }

    public void Dispose()
    {
        EulaService.MarkerFilePath = _originalPath;
        try { File.Delete(_tempPath); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Default_skips_connectivity_test()
    {
        EulaService.RecordEulaAcceptance();
        var result = await EvalToolkit.EvalScore.Preflight.Preflight.RunAsync();
        Assert.True(result.Passed);
        Assert.Equal(2, result.Checks.Count);
        Assert.Equal("Skipped", result.Checks[1].Message);
    }

    [Fact]
    public async Task EULA_callback_invoked_when_marker_absent()
    {
        int callCount = 0;
        var options = new PreflightOptions
        {
            ApproveEulaAsync = _ => { callCount++; return Task.FromResult(true); },
        };
        var result = await EvalToolkit.EvalScore.Preflight.Preflight.RunAsync(options);
        Assert.Equal(1, callCount);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task EULA_decline_fails_preflight()
    {
        var options = new PreflightOptions
        {
            ApproveEulaAsync = _ => Task.FromResult(false),
        };
        var result = await EvalToolkit.EvalScore.Preflight.Preflight.RunAsync(options);
        Assert.False(result.Passed);
        Assert.False(result.Checks[0].Passed);
        Assert.Equal("EULA declined", result.Checks[0].Message);
    }

    [Fact]
    public async Task Connectivity_test_runs_when_not_skipped()
    {
        EulaService.RecordEulaAcceptance();
        var options = new PreflightOptions
        {
            SkipConnectivityTest = false,
            AskClient = (prompt, _) => Task.FromResult($"OK: {prompt}"),
        };
        var result = await EvalToolkit.EvalScore.Preflight.Preflight.RunAsync(options);
        Assert.True(result.Passed);
        Assert.True(result.Checks[1].Passed);
        Assert.Contains("WorkIQ responded", result.Checks[1].Message);
    }

    [Fact]
    public async Task Connectivity_test_fails_on_exception()
    {
        EulaService.RecordEulaAcceptance();
        var options = new PreflightOptions
        {
            SkipConnectivityTest = false,
            TenantId = "tenant-abc",
            AskClient = (_, _) => throw new InvalidOperationException("boom"),
        };
        var result = await EvalToolkit.EvalScore.Preflight.Preflight.RunAsync(options);
        Assert.False(result.Passed);
        Assert.False(result.Checks[1].Passed);
        Assert.Contains("boom", result.Checks[1].Message);
        Assert.Contains("-t tenant-abc", result.Checks[1].Message);
    }

    [Fact]
    public async Task Connectivity_test_without_client_returns_unconfigured()
    {
        var conn = await EvalToolkit.EvalScore.Preflight.Preflight.TestConnectivityAsync(null, null);
        Assert.False(conn.Connected);
        Assert.Contains("No WorkIQ client", conn.Message);
    }

    [Fact]
    public void PrintResults_handles_skipped_and_failed()
    {
        var writes = new List<string>();
        var pr = new PreflightResult(false, new[]
        {
            new PreflightCheck("EULA", true, "EULA accepted"),
            new PreflightCheck("Connectivity", true, "Skipped"),
            new PreflightCheck("Other", false, "boom"),
        });
        EvalToolkit.EvalScore.Preflight.Preflight.PrintResults(pr, writes.Add);
        Assert.Contains(writes, w => w.Contains('⏭'));
        Assert.Contains(writes, w => w.Contains('❌') && w.Contains("Other"));
        Assert.Contains(writes, w => w.Contains("preflight checks failed"));
    }
}
