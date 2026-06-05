using EvalToolkit.EvalScore.Preflight;

namespace EvalToolkit.EvalScore.Tests.Preflight;

[Collection("EulaService")]
public class EulaServiceTests : IDisposable
{
    private readonly string _originalPath;
    private readonly string _tempPath;

    public EulaServiceTests()
    {
        _originalPath = EulaService.MarkerFilePath;
        _tempPath = Path.Combine(Path.GetTempPath(), $"eula-test-{Guid.NewGuid():N}");
        EulaService.MarkerFilePath = _tempPath;
    }

    public void Dispose()
    {
        EulaService.MarkerFilePath = _originalPath;
        try { File.Delete(_tempPath); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void IsEulaAccepted_returns_false_when_marker_absent()
    {
        Assert.False(EulaService.IsEulaAccepted());
    }

    [Fact]
    public void RecordEulaAcceptance_writes_marker_with_url()
    {
        EulaService.RecordEulaAcceptance();
        Assert.True(File.Exists(_tempPath));
        string content = File.ReadAllText(_tempPath);
        Assert.Contains("Accepted on", content);
        Assert.Contains(EulaService.EulaUrl, content);
        Assert.EndsWith("\n", content);
    }

    [Fact]
    public void IsEulaAccepted_returns_true_after_record()
    {
        EulaService.RecordEulaAcceptance();
        Assert.True(EulaService.IsEulaAccepted());
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("YES")]
    [InlineData("y")]
    [InlineData("  yes  ")]
    public async Task ApproveEulaAsync_accepts_yes_responses(string answer)
    {
        var writes = new List<string>();
        bool result = await EulaService.ApproveEulaAsync(
            reader: _ => Task.FromResult<string?>(answer),
            writer: writes.Add);
        Assert.True(result);
        Assert.True(EulaService.IsEulaAccepted());
        Assert.Contains(writes, w => w.Contains("EULA accepted"));
    }

    [Theory]
    [InlineData("no")]
    [InlineData("n")]
    [InlineData("")]
    [InlineData("maybe")]
    public async Task ApproveEulaAsync_rejects_non_yes_responses(string answer)
    {
        var writes = new List<string>();
        bool result = await EulaService.ApproveEulaAsync(
            reader: _ => Task.FromResult<string?>(answer),
            writer: writes.Add);
        Assert.False(result);
        Assert.False(EulaService.IsEulaAccepted());
    }
}
