using System.Text.Json;
using System.Text.Json.Serialization;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.EvalSet;

/// <summary>
/// Attaches assertions from an EvalGen sidecar JSON to a list of rows.
/// Mirrors TS <c>loadAssertionsFromSidecar</c> in
/// <c>eval-score/node/src/assertion-checker.ts</c>.
///
/// <para>Sidecar matching uses <c>prompt.Trim().ToLowerInvariant()</c>
/// as the key. Only sidecar items with a non-empty prompt AND at least
/// one assertion contribute to the lookup map. Multiple sidecar items
/// sharing the same key are last-write-wins (the TS map does the same).</para>
/// </summary>
public static class SidecarLoader
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static IList<EvalRow> LoadAssertions(IList<EvalRow> rows, string sidecarPath)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentException.ThrowIfNullOrWhiteSpace(sidecarPath);

        string absPath = Path.GetFullPath(sidecarPath);
        if (!File.Exists(absPath))
        {
            throw new FileNotFoundException($"Sidecar file not found: {absPath}", absPath);
        }

        string content = File.ReadAllText(absPath);
        SidecarJson sidecar;
        try
        {
            sidecar = JsonSerializer.Deserialize<SidecarJson>(content, s_jsonOptions)
                ?? throw new InvalidOperationException($"Invalid JSON in sidecar file: {absPath}");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Invalid JSON in sidecar file: {absPath}");
        }

        if (sidecar.Items is null)
        {
            throw new InvalidOperationException("Invalid sidecar format: missing \"items\" array");
        }

        var assertionMap = new Dictionary<string, IReadOnlyList<Assertion>>(StringComparer.Ordinal);
        foreach (SidecarItemJson item in sidecar.Items)
        {
            if (string.IsNullOrEmpty(item.Prompt))
            {
                continue;
            }
            if (item.Assertions is null || item.Assertions.Count == 0)
            {
                continue;
            }
            string key = item.Prompt.Trim().ToLowerInvariant();
            assertionMap[key] = item.Assertions;
        }

        foreach (EvalRow row in rows)
        {
            string key = (row.Prompt ?? string.Empty).Trim().ToLowerInvariant();
            if (assertionMap.TryGetValue(key, out IReadOnlyList<Assertion>? assertions))
            {
                row.Assertions = assertions;
            }
        }

        return rows;
    }

    private sealed class SidecarJson
    {
        public List<SidecarItemJson>? Items { get; set; }
    }

    private sealed class SidecarItemJson
    {
        public string Prompt { get; set; } = string.Empty;
        public List<Assertion>? Assertions { get; set; }
    }
}
