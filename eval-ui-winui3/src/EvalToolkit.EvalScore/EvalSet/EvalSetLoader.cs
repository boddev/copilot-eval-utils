using System.Text.Json;
using System.Text.Json.Serialization;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.EvalSet;

/// <summary>
/// Loader for the EvalGen <c>*.evalgen.json</c> EvalSet format. Mirrors
/// TS <c>loadEvalSet</c> in <c>eval-score/node/src/evalset-loader.ts</c>.
///
/// <para>Behavior preserved:
/// <list type="bullet">
///   <item>Path is resolved to absolute before existence check.</item>
///   <item>Missing file throws <see cref="FileNotFoundException"/>.</item>
///   <item>Invalid JSON throws <see cref="InvalidOperationException"/>
///     with the absolute path in the message.</item>
///   <item>Missing or non-array <c>items</c> property throws
///     <see cref="InvalidOperationException"/>.</item>
///   <item>Version not starting with <c>"1."</c> emits a warning via
///     <see cref="OnVersionWarning"/> (the TS code writes to stderr).</item>
///   <item>Each item maps to a fresh <see cref="EvalRow"/> with empty
///     <see cref="EvalRow.ActualAnswer"/>; assertions, category,
///     difficulty, grounding_confidence, and id are copied through.</item>
///   <item>Metadata (description, source_file, generated_at, model,
///     evalgen_version) is collected into a string→string dict for
///     reporters to use.</item>
/// </list></para>
/// </summary>
public static class EvalSetLoader
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>Optional sink for version-mismatch warnings (defaults to <see cref="Console.Error"/>).</summary>
    public static Action<string>? OnVersionWarning { get; set; }

    public static LoadResult Load(string evalsetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evalsetPath);
        string absPath = Path.GetFullPath(evalsetPath);
        if (!File.Exists(absPath))
        {
            throw new FileNotFoundException($"EvalSet file not found: {absPath}", absPath);
        }

        string content = File.ReadAllText(absPath);
        EvalSetJson evalSet;
        try
        {
            evalSet = JsonSerializer.Deserialize<EvalSetJson>(content, s_jsonOptions)
                ?? throw new InvalidOperationException($"Invalid JSON in EvalSet file: {absPath}");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Invalid JSON in EvalSet file: {absPath}");
        }

        if (evalSet.Items is null)
        {
            throw new InvalidOperationException("Invalid EvalSet format: missing \"items\" array");
        }

        if (!string.IsNullOrEmpty(evalSet.Version) && !evalSet.Version.StartsWith("1.", StringComparison.Ordinal))
        {
            string warning = $"  ⚠️  EvalSet version {evalSet.Version} may not be compatible";
            if (OnVersionWarning is not null)
            {
                OnVersionWarning(warning);
            }
            else
            {
                Console.Error.WriteLine(warning);
            }
        }

        var rows = new List<EvalRow>(evalSet.Items.Count);
        foreach (EvalSetItemJson item in evalSet.Items)
        {
            rows.Add(new EvalRow
            {
                Prompt = item.Prompt,
                ExpectedAnswer = item.ExpectedAnswer,
                SourceLocation = item.SourceLocation,
                ActualAnswer = string.Empty,
                Assertions = item.Assertions,
                // TS evalset-loader stores item.id as (row as any)._id (a
                // separate non-`id` field that no consumer ever reads after
                // assignment). Mirroring that behavior here keeps checkpoint
                // output free of an unexpected extensions.evalscore.item_id
                // for EvalSet-loaded rows. Category/Difficulty/
                // GroundingConfidence DO have downstream readers (reporter
                // per-category grouping) and are preserved.
                Category = item.Category,
                Difficulty = item.Difficulty,
                GroundingConfidence = item.GroundingConfidence,
            });
        }

        var metadata = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(evalSet.Description)) metadata["description"] = evalSet.Description;
        if (!string.IsNullOrEmpty(evalSet.SourceFile)) metadata["source_file"] = evalSet.SourceFile;
        if (!string.IsNullOrEmpty(evalSet.GeneratedAt)) metadata["generated_at"] = evalSet.GeneratedAt;
        if (!string.IsNullOrEmpty(evalSet.Metadata?.Model)) metadata["model"] = evalSet.Metadata.Model;
        if (!string.IsNullOrEmpty(evalSet.Metadata?.EvalGenVersion)) metadata["evalgen_version"] = evalSet.Metadata.EvalGenVersion;

        return new LoadResult(rows, evalSet.Warnings ?? new List<string>(), metadata);
    }

    public sealed record LoadResult(IList<EvalRow> Rows, IList<string> Warnings, IDictionary<string, string> Metadata);

    private sealed class EvalSetJson
    {
        public string? Version { get; set; }
        public string? GeneratedAt { get; set; }
        public string? Description { get; set; }
        public string? SourceFile { get; set; }
        public int ItemCount { get; set; }
        public List<EvalSetItemJson>? Items { get; set; }
        public List<string>? Warnings { get; set; }
        public EvalSetMetadataJson? Metadata { get; set; }
    }

    private sealed class EvalSetItemJson
    {
        public string? Id { get; set; }
        public string Prompt { get; set; } = string.Empty;
        public string ExpectedAnswer { get; set; } = string.Empty;
        public string SourceLocation { get; set; } = string.Empty;
        public List<Assertion>? Assertions { get; set; }
        public string? Category { get; set; }
        public string? Difficulty { get; set; }
        public string? GroundingConfidence { get; set; }
    }

    private sealed class EvalSetMetadataJson
    {
        public string? Model { get; set; }
        [JsonPropertyName("evalgen_version")]
        public string? EvalGenVersion { get; set; }
    }
}
