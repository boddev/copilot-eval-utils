using System.Text.Json;
using EvalToolkit.EvalScore.Checkpoint;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Tests.Checkpoint;

public class CheckpointWriterTests : IDisposable
{
    private readonly string _tempDir;

    public CheckpointWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "evalscore-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static EvalRow Row(string prompt = "q") => new()
    {
        Prompt = prompt,
        ExpectedAnswer = "expected",
        SourceLocation = "/file",
        ActualAnswer = "actual",
        SimilarityScore = 80,
    };

    private static CheckpointMetadata Meta(string input = "in.csv") => new()
    {
        InputFile = input,
        Target = new EvaluationTarget { Type = TargetType.WorkIq },
        JudgeProvider = JudgeProvider.WorkIq,
        Evaluators = new[] { EvaluatorName.Similarity },
    };

    [Fact]
    public async Task WriteAsync_WritesValidJson()
    {
        string path = Path.Combine(_tempDir, "out.json");
        await CheckpointWriter.WriteAsync(path, new[] { Row() }, Meta());

        Assert.True(File.Exists(path));
        string json = await File.ReadAllTextAsync(path);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("1.4.0", doc.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task WriteAsync_CreatesMissingDirectory()
    {
        string path = Path.Combine(_tempDir, "nested", "deep", "out.json");
        await CheckpointWriter.WriteAsync(path, new[] { Row() }, Meta());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task WriteAsync_PathWithNoDirectory_DoesNotThrow()
    {
        // Reviewer B4: bare filename → Path.GetDirectoryName returns "",
        // CreateDirectory("") would throw. Run inside a working-dir scope.
        string previousCwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _tempDir;
        try
        {
            await CheckpointWriter.WriteAsync("bare-checkpoint.json", new[] { Row() }, Meta());
            Assert.True(File.Exists(Path.Combine(_tempDir, "bare-checkpoint.json")));
        }
        finally
        {
            Environment.CurrentDirectory = previousCwd;
        }
    }

    [Fact]
    public async Task WriteAsync_OverwritesExistingFile()
    {
        string path = Path.Combine(_tempDir, "out.json");
        await CheckpointWriter.WriteAsync(path, new[] { Row("v1") }, Meta());
        await CheckpointWriter.WriteAsync(path, new[] { Row("v2") }, Meta());

        string json = await File.ReadAllTextAsync(path);
        using var doc = JsonDocument.Parse(json);
        string prompt = doc.RootElement.GetProperty("items")[0].GetProperty("prompt").GetString()!;
        Assert.Equal("v2", prompt);
    }

    [Fact]
    public async Task WriteAsync_HonorsCancellation()
    {
        string path = Path.Combine(_tempDir, "out.json");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CheckpointWriter.WriteAsync(path, new[] { Row() }, Meta(), cts.Token));
    }

    [Fact]
    public async Task WriteAsync_WritesUtf8NoBom()
    {
        string path = Path.Combine(_tempDir, "out.json");
        await CheckpointWriter.WriteAsync(path, new[] { Row() }, Meta());

        byte[] bytes = await File.ReadAllBytesAsync(path);
        // BOM is 0xEF 0xBB 0xBF — must not be present.
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        // First non-whitespace char should be '{'.
        Assert.Equal((byte)'{', bytes[0]);
    }
}
