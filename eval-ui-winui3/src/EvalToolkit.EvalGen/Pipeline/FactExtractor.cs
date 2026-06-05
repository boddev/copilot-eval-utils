using System.Globalization;
using System.Text.Json;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Pipeline;

/// <summary>
/// Port of <c>eval-gen/src/fact-extractor.ts</c>. Extracts atomic facts from
/// dataset records using stratified sampling; provides helpers to group and
/// summarize facts for LLM context.
/// </summary>
public static class FactExtractor
{
    /// <summary>Options for <see cref="ExtractFacts"/>.</summary>
    public sealed record ExtractFactsOptions
    {
        /// <summary>Hard cap on total facts emitted (default 200).</summary>
        public int MaxFacts { get; init; } = 200;

        /// <summary>Target distinct records to sample (default scales with MaxFacts; floor 50).</summary>
        public int? TargetRecords { get; init; }

        /// <summary>Cap facts emitted per record (default unlimited).</summary>
        public int MaxFactsPerRecord { get; init; } = int.MaxValue;
    }

    /// <summary>
    /// Extract atomic facts from dataset records using stratified sampling.
    /// Mirrors TS <c>extractFacts</c>.
    /// </summary>
    public static IReadOnlyList<Fact> ExtractFacts(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> records,
        DatasetProfile profile,
        ExtractFactsOptions? options = null)
    {
        options ??= new ExtractFactsOptions();
        var maxFacts = options.MaxFacts;
        var maxFactsPerRecord = options.MaxFactsPerRecord;

        var facts = new List<Fact>();
        var selectedIndices = SelectStratifiedIndices(records, profile, maxFacts, options.TargetRecords);

        int factId = 0;
        foreach (var rowIndex in selectedIndices)
        {
            var record = records[rowIndex];
            string fileLabel = profile.FileName;
            if (record.TryGetValue("_source_file", out var sf) && sf is not null)
            {
                // Parity with TS truthiness: empty string falls back to profile.FileName.
                var sourceFileLabel = Profiler.ConvertToString(sf);
                if (!string.IsNullOrEmpty(sourceFileLabel))
                {
                    fileLabel = sourceFileLabel;
                }
            }
            var rowRef = $"{fileLabel}:row {rowIndex + 1}";

            int factsForThisRecord = 0;
            foreach (var col in profile.Columns)
            {
                if (factsForThisRecord >= maxFactsPerRecord) break;
                record.TryGetValue(col.Name, out var value);
                if (value is null || (value is string s && s.Length == 0)) continue;
                if (col.Name == "_source_file") continue;

                facts.Add(new Fact
                {
                    Id = $"f-{++factId}",
                    Field = col.Name,
                    Value = value,
                    RowReference = rowRef,
                    Record = CloneRecord(record),
                });
                factsForThisRecord++;
            }

            if (facts.Count >= maxFacts) break;
        }

        return facts.Count <= maxFacts ? facts : facts.Take(maxFacts).ToList();
    }

    private static Dictionary<string, object?> CloneRecord(IReadOnlyDictionary<string, object?> record)
    {
        var copy = new Dictionary<string, object?>(record.Count, StringComparer.Ordinal);
        foreach (var kv in record) copy[kv.Key] = kv.Value;
        return copy;
    }

    private static List<int> SelectStratifiedIndices(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> records,
        DatasetProfile profile,
        int maxFacts,
        int? explicitTargetRecords)
    {
        var defaultTarget = Math.Max(50, (int)Math.Ceiling(maxFacts / 5.0));
        var targetRecords = Math.Min(records.Count, explicitTargetRecords ?? defaultTarget);
        var selected = new SortedSet<int>();

        // 1. Records with extreme numeric values
        foreach (var col in profile.Columns)
        {
            if (col.DataType == "number" && col.Min is not null && col.Max is not null)
            {
                var minTarget = Convert.ToDouble(col.Min, CultureInfo.InvariantCulture);
                var maxTarget = Convert.ToDouble(col.Max, CultureInfo.InvariantCulture);
                int minIdx = FindIndex(records, r =>
                {
                    r.TryGetValue(col.Name, out var v);
                    return v is not null && Profiler.TryParseNumber(Profiler.ConvertToString(v), out var n) && n == minTarget;
                });
                int maxIdx = FindIndex(records, r =>
                {
                    r.TryGetValue(col.Name, out var v);
                    return v is not null && Profiler.TryParseNumber(Profiler.ConvertToString(v), out var n) && n == maxTarget;
                });
                if (minIdx >= 0) selected.Add(minIdx);
                if (maxIdx >= 0) selected.Add(maxIdx);
            }
        }

        // 2. Records from rare/most common categories
        foreach (var col in profile.Columns)
        {
            if (col.ValueCounts is not null)
            {
                var sorted = col.ValueCounts.OrderBy(kv => kv.Value).ToList();
                if (sorted.Count > 0)
                {
                    var rarest = sorted[0].Key;
                    int idx = FindIndex(records, r =>
                    {
                        r.TryGetValue(col.Name, out var v);
                        return string.Equals(Profiler.ConvertToString(v), rarest, StringComparison.Ordinal);
                    });
                    if (idx >= 0) selected.Add(idx);
                }
                if (sorted.Count > 1)
                {
                    var most = sorted[^1].Key;
                    int idx = FindIndex(records, r =>
                    {
                        r.TryGetValue(col.Name, out var v);
                        return string.Equals(Profiler.ConvertToString(v), most, StringComparison.Ordinal);
                    });
                    if (idx >= 0) selected.Add(idx);
                }
            }
        }

        // 3. Records with null/empty fields (up to 3 columns)
        var nullCols = profile.Columns.Where(c => c.NullCount > 0).Take(3).ToList();
        foreach (var col in nullCols)
        {
            int idx = FindIndex(records, r =>
            {
                r.TryGetValue(col.Name, out var v);
                return v is null || (v is string s && s.Length == 0);
            });
            if (idx >= 0) selected.Add(idx);
        }

        // 4. Evenly spaced fill
        var remaining = targetRecords - selected.Count;
        if (remaining > 0)
        {
            var step = Math.Max(1, records.Count / remaining);
            for (int i = 0; i < records.Count && selected.Count < targetRecords; i += step)
            {
                selected.Add(i);
            }
        }

        return selected.ToList();
    }

    private static int FindIndex<T>(IReadOnlyList<T> list, Func<T, bool> predicate)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (predicate(list[i])) return i;
        }
        return -1;
    }

    /// <summary>
    /// Group facts by row reference for easier question generation. Preserves
    /// insertion order via <c>Dictionary&lt;,&gt;</c> (.NET dictionaries
    /// preserve insertion order, matching JS Map semantics).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<Fact>> GroupFactsByRecord(IEnumerable<Fact> facts)
    {
        var groups = new Dictionary<string, List<Fact>>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            if (!groups.TryGetValue(fact.RowReference, out var list))
            {
                list = new List<Fact>();
                groups[fact.RowReference] = list;
            }
            list.Add(fact);
        }

        var result = new Dictionary<string, IReadOnlyList<Fact>>(groups.Count, StringComparer.Ordinal);
        foreach (var kv in groups) result[kv.Key] = kv.Value;
        return result;
    }

    /// <summary>
    /// Get a summary of facts for LLM context. Each fact line is prefixed with
    /// its stable [f-N] ID so the LLM can cite specific facts. Mirrors TS
    /// <c>summarizeFacts</c>.
    /// </summary>
    public static string SummarizeFacts(IEnumerable<Fact> facts, int maxRecords = 15)
    {
        var grouped = GroupFactsByRecord(facts);
        var lines = new List<string>();
        int count = 0;
        foreach (var (rowRef, recordFacts) in grouped)
        {
            if (count >= maxRecords) break;
            var fields = string.Join(", ", recordFacts.Select(f =>
                $"[{f.Id}] {f.Field}={JsonStringify(f.Value)}"));
            lines.Add($"[{rowRef}] {fields}");
            count++;
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// JS <c>JSON.stringify</c>-equivalent for primitives. Strings get double
    /// quotes with backslash escapes; numbers/bools/null get their wire form;
    /// objects/arrays get System.Text.Json serialization.
    /// </summary>
    internal static string JsonStringify(object? value)
    {
        if (value is null) return "null";
        if (value is bool b) return b ? "true" : "false";
        if (value is string s) return JsonSerializer.Serialize(s);
        if (value is double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
            return Profiler.ConvertToString(d);
        }
        if (value is float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) return "null";
            return Profiler.ConvertToString(f);
        }
        if (value is decimal m) return m.ToString(CultureInfo.InvariantCulture);
        if (value is sbyte or byte or short or ushort or int or uint or long or ulong)
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }
        return JsonSerializer.Serialize(value);
    }
}
