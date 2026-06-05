using System.Text;
using System.Text.Json;
using EvalToolkit.EvalScore.EvalDocument;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Checkpoint;

/// <summary>
/// Per-row checkpoint metadata. Mirrors the TS <c>writeCheckpoint</c>
/// metadata parameter in <c>eval-score/node/src/index.ts</c>.
/// </summary>
public sealed record CheckpointMetadata
{
    public required string InputFile { get; init; }
    public EvaluationTarget? Target { get; init; }
    public JudgeProvider? JudgeProvider { get; init; }
    public IReadOnlyList<EvaluatorName>? Evaluators { get; init; }
}

/// <summary>
/// Serialize an in-progress run to disk as the canonical
/// <see cref="EvalDocument"/> JSON. Used as the
/// <c>onRowComplete</c> hook in the eval loop so a crash mid-run
/// leaves a resumable artifact behind.
///
/// <para>TS parity: direct overwrite (not atomic write). Matches the
/// Node implementation which uses <c>fs.promises.writeFile</c>
/// directly. High-frequency invocations (per-row) make atomic writes
/// unnecessary churn for this slice; resumability comes from the
/// engine skipping rows that already have scores, not from
/// crash-tolerant snapshots.</para>
/// </summary>
public static class CheckpointWriter
{
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WriteAsync(
        string checkpointFile,
        IReadOnlyList<EvalRow> rows,
        CheckpointMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(checkpointFile);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(metadata);

        // TS `path.dirname("checkpoint.json")` returns `"."`. C#
        // `Path.GetDirectoryName("checkpoint.json")` returns `""`.
        // Only create the directory when the path has one.
        string? directory = Path.GetDirectoryName(checkpointFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = EvalDocumentBuilder.RowsToEvalDocument(rows, new EvalDocumentBuilder.BuildOptions
        {
            InputFile = metadata.InputFile,
            Target = metadata.Target,
            JudgeProvider = metadata.JudgeProvider,
            RunEvaluators = metadata.Evaluators,
        });

        string json = JsonSerializer.Serialize(document, PrettyJson);
        // UTF-8 no BOM — TS `fs.promises.writeFile(... 'utf-8')` writes no BOM.
        await File.WriteAllTextAsync(
            checkpointFile,
            json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }
}
