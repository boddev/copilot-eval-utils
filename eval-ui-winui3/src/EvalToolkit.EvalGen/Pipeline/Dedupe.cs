using System.Text.Json;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Pipeline;

/// <summary>
/// Port of <c>eval-gen/src/dedupe.ts</c>. Prompt + assertion normalization,
/// near-duplicate detection, avoidance-set loading and filtering against
/// prior eval sidecar files.
/// </summary>
public static class Dedupe
{
    /// <summary>Lowercase, alphanumeric+space-only, single-spaced, trimmed.</summary>
    public static string NormalizePrompt(string prompt)
    {
        if (string.IsNullOrEmpty(prompt)) return string.Empty;

        var sb = new System.Text.StringBuilder(prompt.Length);
        foreach (var c in prompt.ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || char.IsWhiteSpace(c))
            {
                sb.Append(char.IsWhiteSpace(c) ? ' ' : c);
            }
        }

        // Collapse runs of whitespace to a single space and trim.
        var s = sb.ToString();
        var collapsed = new System.Text.StringBuilder(s.Length);
        bool prevSpace = false;
        foreach (var c in s)
        {
            if (c == ' ')
            {
                if (!prevSpace) collapsed.Append(' ');
                prevSpace = true;
            }
            else
            {
                collapsed.Append(c);
                prevSpace = false;
            }
        }
        return collapsed.ToString().Trim();
    }

    /// <summary>
    /// Two prompts are near-duplicates if normalized forms match, or one
    /// contains the other and the shorter is &gt;80% of the longer.
    /// </summary>
    public static bool IsNearDuplicatePrompt(string a, string b)
    {
        var normalizedA = NormalizePrompt(a);
        var normalizedB = NormalizePrompt(b);
        if (normalizedA.Length == 0 || normalizedB.Length == 0) return false;
        if (normalizedA == normalizedB) return true;

        var shorter = normalizedA.Length < normalizedB.Length ? normalizedA : normalizedB;
        var longer = normalizedA.Length < normalizedB.Length ? normalizedB : normalizedA;
        return longer.Contains(shorter, StringComparison.Ordinal)
            && (double)shorter.Length / longer.Length > 0.8;
    }

    /// <summary>Lowercased, single-spaced, trimmed assertion value.</summary>
    private static string NormalizeAssertionValue(string value)
    {
        var lower = value.ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lower.Length);
        bool prevSpace = false;
        foreach (var c in lower)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!prevSpace) sb.Append(' ');
                prevSpace = true;
            }
            else
            {
                sb.Append(c);
                prevSpace = false;
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>Canonical string form of a single assertion.</summary>
    public static string NormalizeAssertion(Assertion assertion) => assertion switch
    {
        MustContainAssertion mc =>
            $"must_contain:{NormalizeAssertionValue(mc.Value)}:{(mc.WholeWord ? "whole" : "partial")}",
        MustContainAnyAssertion mca =>
            $"must_contain_any:{string.Join('|', mca.Values.Select(NormalizeAssertionValue).OrderBy(v => v, StringComparer.Ordinal))}",
        MustNotContainAssertion mnc =>
            $"must_not_contain:{NormalizeAssertionValue(mnc.Value)}",
        _ => JsonSerializer.Serialize(assertion),
    };

    /// <summary>Stable signature for a list of assertions (order-independent).</summary>
    public static string AssertionSignature(IReadOnlyList<Assertion> assertions)
    {
        return string.Join("||", assertions.Select(NormalizeAssertion).OrderBy(v => v, StringComparer.Ordinal));
    }

    /// <summary>Prior eval item loaded from a sidecar file for avoidance-comparison.</summary>
    internal sealed record PriorEvalItem(
        string Prompt,
        string SourceLocation,
        string AssertionSignature,
        string SourceFile,
        string FilePath);

    /// <summary>Loaded avoidance set: prior items + sidecar files + warnings.</summary>
    public sealed record AvoidanceSet(
        IReadOnlyList<object> Items,
        IReadOnlyList<string> Files,
        IReadOnlyList<string> Warnings)
    {
        public static AvoidanceSet Empty { get; } = new(Array.Empty<object>(), Array.Empty<string>(), Array.Empty<string>());

        internal IReadOnlyList<PriorEvalItem> PriorItems => Items.OfType<PriorEvalItem>().ToList();
    }

    /// <summary>Result of filtering generated items against an avoidance set.</summary>
    public sealed record AvoidanceFilterResult(
        IReadOnlyList<GeneratedEvalItem> Items,
        int RemovedCount,
        int DuplicatePromptCount,
        int DuplicateSourceLocationCount,
        int AssertionOverlapCount,
        IReadOnlyList<string> Warnings);

    private static string SourceKey(string sourceFile, string sourceLocation)
        => $"{sourceFile}\u0000{sourceLocation}";

    private static IEnumerable<string> DiscoverSidecars(string inputPath)
    {
        if (File.Exists(inputPath))
        {
            if (inputPath.EndsWith(".evalgen.json", StringComparison.OrdinalIgnoreCase))
            {
                yield return inputPath;
            }
            yield break;
        }
        if (!Directory.Exists(inputPath)) yield break;

        foreach (var entry in Directory.EnumerateFileSystemEntries(inputPath))
        {
            if (Directory.Exists(entry))
            {
                foreach (var nested in DiscoverSidecars(entry)) yield return nested;
            }
            else if (File.Exists(entry) && entry.EndsWith(".evalgen.json", StringComparison.OrdinalIgnoreCase))
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// Load prior eval sets to use as an avoidance comparison set. Throws if
    /// any input path does not exist; collects warnings for individual files
    /// that fail to parse. Mirrors TS <c>loadAvoidanceSet</c>.
    /// </summary>
    public static AvoidanceSet LoadAvoidanceSet(
        IEnumerable<string>? inputs,
        IEnumerable<string>? excludePaths = null)
    {
        if (inputs is null) return AvoidanceSet.Empty;
        var inputList = inputs.ToList();
        if (inputList.Count == 0) return AvoidanceSet.Empty;

        var excluded = new HashSet<string>(
            (excludePaths ?? Enumerable.Empty<string>()).Select(p => Path.GetFullPath(p).ToLowerInvariant()),
            StringComparer.Ordinal);

        var sidecars = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var input in inputList)
        {
            var resolved = Path.GetFullPath(input);
            if (!File.Exists(resolved) && !Directory.Exists(resolved))
            {
                throw new InvalidOperationException($"Avoidance eval set path not found: {resolved}");
            }
            foreach (var sidecar in DiscoverSidecars(resolved))
            {
                var abs = Path.GetFullPath(sidecar);
                if (!excluded.Contains(abs.ToLowerInvariant()))
                {
                    sidecars.Add(abs);
                }
            }
        }

        var items = new List<object>();
        var warnings = new List<string>();
        var files = new List<string>();

        foreach (var filePath in sidecars)
        {
            JsonDocument? parsed = null;
            try
            {
                using var stream = File.OpenRead(filePath);
                parsed = JsonDocument.Parse(stream);
            }
            catch (Exception ex)
            {
                warnings.Add($"Skipped avoidance eval set {filePath}: {ex.Message}");
                continue;
            }

            using (parsed)
            {
                var root = parsed.RootElement;
                if (!root.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
                {
                    warnings.Add($"Skipped avoidance eval set {filePath}: missing items array");
                    continue;
                }

                files.Add(filePath);
                var sourceFile = root.TryGetProperty("source_file", out var sf) && sf.ValueKind == JsonValueKind.String
                    ? sf.GetString() ?? string.Empty
                    : string.Empty;

                foreach (var item in itemsEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("prompt", out var promptEl) || promptEl.ValueKind != JsonValueKind.String) continue;
                    var prompt = promptEl.GetString() ?? string.Empty;
                    var sourceLocation = item.TryGetProperty("source_location", out var slEl) && slEl.ValueKind == JsonValueKind.String
                        ? slEl.GetString() ?? string.Empty
                        : string.Empty;
                    IReadOnlyList<Assertion> assertions = Array.Empty<Assertion>();
                    if (item.TryGetProperty("assertions", out var aEl) && aEl.ValueKind == JsonValueKind.Array)
                    {
                        assertions = ReadAssertions(aEl);
                    }
                    items.Add(new PriorEvalItem(
                        prompt,
                        sourceLocation,
                        AssertionSignature(assertions),
                        sourceFile,
                        filePath));
                }
            }
        }

        return new AvoidanceSet(items, files, warnings);
    }

    private static List<Assertion> ReadAssertions(JsonElement array)
    {
        var result = new List<Assertion>();
        foreach (var el in array.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (!el.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String) continue;
            switch (typeEl.GetString())
            {
                case "must_contain":
                    if (el.TryGetProperty("value", out var mcv) && mcv.ValueKind == JsonValueKind.String)
                    {
                        var ww = el.TryGetProperty("wholeWord", out var wwEl) && wwEl.ValueKind == JsonValueKind.True;
                        result.Add(new MustContainAssertion { Value = mcv.GetString() ?? string.Empty, WholeWord = ww });
                    }
                    break;
                case "must_contain_any":
                    if (el.TryGetProperty("values", out var vsEl) && vsEl.ValueKind == JsonValueKind.Array)
                    {
                        var values = vsEl.EnumerateArray()
                            .Where(v => v.ValueKind == JsonValueKind.String)
                            .Select(v => v.GetString() ?? string.Empty)
                            .ToList();
                        result.Add(new MustContainAnyAssertion { Values = values });
                    }
                    break;
                case "must_not_contain":
                    if (el.TryGetProperty("value", out var mncv) && mncv.ValueKind == JsonValueKind.String)
                    {
                        result.Add(new MustNotContainAssertion { Value = mncv.GetString() ?? string.Empty });
                    }
                    break;
            }
        }
        return result;
    }

    /// <summary>
    /// Filter generated items against an avoidance set, removing exact +
    /// near-duplicate prompts and same source-row matches. Assertion overlaps
    /// are counted as a warning but not removed. Mirrors TS
    /// <c>filterAgainstAvoidance</c>.
    /// </summary>
    public static AvoidanceFilterResult FilterAgainstAvoidance(
        IReadOnlyList<GeneratedEvalItem> items,
        AvoidanceSet avoidance,
        string currentSourceFile)
    {
        var relevantPriorItems = avoidance.PriorItems
            .Where(item => string.IsNullOrEmpty(item.SourceFile) || item.SourceFile == currentSourceFile)
            .ToList();

        var promptSet = new HashSet<string>(
            relevantPriorItems.Select(i => NormalizePrompt(i.Prompt)).Where(p => p.Length > 0),
            StringComparer.Ordinal);
        var sourceLocationSet = new HashSet<string>(
            relevantPriorItems
                .Where(i => !string.IsNullOrEmpty(i.SourceLocation))
                .Select(i => SourceKey(string.IsNullOrEmpty(i.SourceFile) ? currentSourceFile : i.SourceFile, i.SourceLocation)),
            StringComparer.Ordinal);
        var assertionSignatureSet = new HashSet<string>(
            relevantPriorItems.Select(i => i.AssertionSignature).Where(s => s.Length > 0),
            StringComparer.Ordinal);
        var priorPrompts = relevantPriorItems.Select(i => i.Prompt).ToList();

        var kept = new List<GeneratedEvalItem>();
        var warnings = new List<string>(avoidance.Warnings);
        int duplicatePromptCount = 0;
        int duplicateSourceLocationCount = 0;
        int assertionOverlapCount = 0;

        foreach (var item in items)
        {
            var normalizedPrompt = NormalizePrompt(item.Prompt);
            var promptDuplicate = promptSet.Contains(normalizedPrompt)
                || priorPrompts.Any(p => IsNearDuplicatePrompt(item.Prompt, p));
            var sourceDuplicate = !string.IsNullOrEmpty(item.SourceLocation)
                && sourceLocationSet.Contains(SourceKey(currentSourceFile, item.SourceLocation));
            var signature = AssertionSignature(item.Assertions);
            var assertionOverlap = signature.Length > 0 && assertionSignatureSet.Contains(signature);

            if (assertionOverlap) assertionOverlapCount++;

            if (promptDuplicate || sourceDuplicate)
            {
                if (promptDuplicate) duplicatePromptCount++;
                if (sourceDuplicate) duplicateSourceLocationCount++;
                continue;
            }

            kept.Add(item);
            if (!string.IsNullOrEmpty(normalizedPrompt))
            {
                promptSet.Add(normalizedPrompt);
                priorPrompts.Add(item.Prompt);
            }
            if (!string.IsNullOrEmpty(item.SourceLocation))
            {
                sourceLocationSet.Add(SourceKey(currentSourceFile, item.SourceLocation));
            }
            if (signature.Length > 0)
            {
                assertionSignatureSet.Add(signature);
            }
        }

        if (assertionOverlapCount > 0)
        {
            warnings.Add(
                $"{assertionOverlapCount} generated item(s) reused an assertion signature from prior eval sets. " +
                "They were kept because assertion-only matches can be legitimate; review them if you require zero assertion overlap.");
        }

        return new AvoidanceFilterResult(
            kept,
            items.Count - kept.Count,
            duplicatePromptCount,
            duplicateSourceLocationCount,
            assertionOverlapCount,
            warnings);
    }
}
