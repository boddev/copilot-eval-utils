using System.Globalization;
using System.Text.RegularExpressions;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Pipeline;

/// <summary>
/// Port of <c>eval-gen/src/profiler.ts</c>. Analyzes dataset schema, column
/// types, value distributions, and selects representative sample records.
/// </summary>
public static class Profiler
{
    private static readonly Regex IsoDateRx = new(@"^\d{4}-\d{2}-\d{2}", RegexOptions.Compiled);
    private static readonly Regex SlashDateRx = new(@"^\d{1,2}/\d{1,2}/\d{2,4}", RegexOptions.Compiled);

    /// <summary>
    /// Profile a dataset: analyze schema, types, distributions, and select samples.
    /// Mirrors TS <c>profileDataset</c>.
    /// </summary>
    public static DatasetProfile ProfileDataset(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> records,
        string fileName,
        InputFormat format,
        Random? random = null)
    {
        if (records.Count == 0)
        {
            throw new InvalidOperationException("Cannot profile an empty dataset");
        }

        // Collect all column names across all records (skip internal metadata fields starting with _)
        var columnNames = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            foreach (var key in record.Keys)
            {
                if (!key.StartsWith('_') && seenNames.Add(key))
                {
                    columnNames.Add(key);
                }
            }
        }

        var columns = columnNames.Select(name => ProfileColumn(name, records)).ToList();
        var sampleRecords = SelectSampleRecords(records, columns, 20, random ?? Random.Shared);
        var candidateKeyColumns = FindCandidateKeys(columns, records.Count);
        var candidateTitleColumns = FindCandidateTitles(columns);

        return new DatasetProfile
        {
            FileName = fileName,
            Format = format,
            RowCount = records.Count,
            Columns = columns,
            SampleRecords = sampleRecords,
            CandidateKeyColumns = candidateKeyColumns,
            CandidateTitleColumns = candidateTitleColumns,
        };
    }

    private enum SimpleType { String, Number, Boolean, Date }

    private static SimpleType InferValueType(object? value)
    {
        if (value is bool) return SimpleType.Boolean;
        if (value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            return SimpleType.Number;
        }

        var str = ConvertToString(value);

        if (IsoDateRx.IsMatch(str) || SlashDateRx.IsMatch(str))
        {
            if (TryParseDate(str, out _)) return SimpleType.Date;
        }

        if (!string.IsNullOrWhiteSpace(str) && TryParseNumber(str, out _))
        {
            return SimpleType.Number;
        }

        return SimpleType.String;
    }

    private static string MergeTypes(HashSet<SimpleType> types)
    {
        if (types.Count == 0) return "null";
        if (types.Count == 1)
        {
            return types.First() switch
            {
                SimpleType.String => "string",
                SimpleType.Number => "number",
                SimpleType.Boolean => "boolean",
                SimpleType.Date => "date",
                _ => "string",
            };
        }
        return "mixed";
    }

    private static ColumnProfile ProfileColumn(string name, IReadOnlyList<IReadOnlyDictionary<string, object?>> records)
    {
        var types = new HashSet<SimpleType>();
        var uniqueValues = new HashSet<string>(StringComparer.Ordinal);
        var sampleValueStrings = new List<string>();
        Dictionary<string, int>? valueCounts = new(StringComparer.Ordinal);
        int nullCount = 0;
        double? numericMin = null;
        double? numericMax = null;
        long? minDateTicks = null;
        long? maxDateTicks = null;

        foreach (var record in records)
        {
            if (!record.TryGetValue(name, out var value)) value = null;

            if (value is null || (value is string s && s.Length == 0))
            {
                nullCount++;
                continue;
            }

            var key = ConvertToString(value);
            if (uniqueValues.Add(key))
            {
                if (sampleValueStrings.Count < 10)
                {
                    sampleValueStrings.Add(key);
                }
            }

            if (valueCounts is not null)
            {
                valueCounts.TryGetValue(key, out var c);
                valueCounts[key] = c + 1;
                if (valueCounts.Count > 20)
                {
                    valueCounts = null;
                }
            }

            var valueType = InferValueType(value);
            types.Add(valueType);

            if (TryParseNumber(key, out var numericValue))
            {
                if (numericMin is null || numericValue < numericMin) numericMin = numericValue;
                if (numericMax is null || numericValue > numericMax) numericMax = numericValue;
            }

            if (IsoDateRx.IsMatch(key) || SlashDateRx.IsMatch(key))
            {
                if (TryParseDate(key, out var dt))
                {
                    var ticks = dt.UtcDateTime.Ticks;
                    if (minDateTicks is null || ticks < minDateTicks) minDateTicks = ticks;
                    if (maxDateTicks is null || ticks > maxDateTicks) maxDateTicks = ticks;
                }
            }
        }

        var uniqueCount = uniqueValues.Count;
        var dataType = MergeTypes(types);

        var sampleValues = sampleValueStrings.Select<string, object?>(sv =>
            dataType == "number" && TryParseNumber(sv, out var n) ? n : sv).ToList();

        IReadOnlyDictionary<string, int>? finalValueCounts = null;
        if (uniqueCount <= 20 && dataType == "string" && valueCounts is not null)
        {
            finalValueCounts = valueCounts;
        }

        object? min = null;
        object? max = null;
        if (dataType == "number" && numericMin is not null && numericMax is not null)
        {
            min = numericMin.Value;
            max = numericMax.Value;
        }
        else if (dataType == "date" && minDateTicks is not null && maxDateTicks is not null)
        {
            min = new DateTimeOffset(minDateTicks.Value, TimeSpan.Zero).UtcDateTime
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            max = new DateTimeOffset(maxDateTicks.Value, TimeSpan.Zero).UtcDateTime
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        return new ColumnProfile
        {
            Name = name,
            DataType = dataType,
            NullCount = nullCount,
            UniqueCount = uniqueCount,
            TotalCount = records.Count,
            SampleValues = sampleValues,
            ValueCounts = finalValueCounts,
            Min = min,
            Max = max,
        };
    }

    private static List<string> FindCandidateKeys(IReadOnlyList<ColumnProfile> columns, int rowCount)
    {
        var result = new List<string>();
        foreach (var c in columns)
        {
            var nonNull = Math.Max(1, c.TotalCount - c.NullCount);
            var uniqueRatio = (double)c.UniqueCount / nonNull;
            if (uniqueRatio > 0.9 && c.NullCount < rowCount * 0.05)
            {
                result.Add(c.Name);
            }
        }
        return result;
    }

    private static readonly Regex TitlePatternsRx =
        new(@"^(name|title|label|description|display|subject|heading)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static List<string> FindCandidateTitles(IReadOnlyList<ColumnProfile> columns)
    {
        var result = new List<string>();
        foreach (var c in columns)
        {
            if (c.DataType == "string" && TitlePatternsRx.IsMatch(c.Name))
            {
                result.Add(c.Name);
            }
        }
        return result;
    }

    private static List<IReadOnlyDictionary<string, object?>> SelectSampleRecords(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> records,
        IReadOnlyList<ColumnProfile> columns,
        int count,
        Random random)
    {
        if (records.Count <= count) return records.ToList();

        var selected = new SortedSet<int>();

        // Always include first and last
        selected.Add(0);
        selected.Add(records.Count - 1);

        // Find a categorical column for stratification
        var categoricalCol = columns.FirstOrDefault(c => c.ValueCounts is not null && c.ValueCounts.Count > 1);
        if (categoricalCol?.ValueCounts is not null)
        {
            var categories = categoricalCol.ValueCounts.Keys.ToList();
            var perCategory = Math.Max(1, (count - 2) / categories.Count);

            foreach (var category in categories)
            {
                var matching = new List<int>();
                for (int i = 0; i < records.Count; i++)
                {
                    records[i].TryGetValue(categoricalCol.Name, out var v);
                    if (string.Equals(ConvertToString(v), category, StringComparison.Ordinal))
                    {
                        matching.Add(i);
                    }
                }

                for (int j = 0; j < Math.Min(perCategory, matching.Count); j++)
                {
                    var idx = (int)Math.Floor((double)j * matching.Count / perCategory);
                    selected.Add(matching[idx]);
                }
            }
        }

        // Fill remaining with evenly-spaced records
        int safetyLoops = 0;
        while (selected.Count < count && selected.Count < records.Count && safetyLoops++ < 10_000)
        {
            var deficit = count - selected.Count;
            var step = Math.Max(1, records.Count / deficit);
            for (int i = 0; i < records.Count && selected.Count < count; i += step)
            {
                selected.Add(i);
            }
            if (selected.Count < count)
            {
                var idx = random.Next(records.Count);
                selected.Add(idx);
            }
        }

        return selected.Select(i => records[i]).ToList();
    }

    /// <summary>
    /// Convert a value to its JS-equivalent <c>String(value)</c>. JS coerces
    /// numbers without trailing decimal zeros (e.g. 5 → "5", 5.5 → "5.5"),
    /// booleans to "true"/"false", null/undefined to "null"/"undefined" (TS
    /// only reaches this after a null-check, so we treat null as "null"
    /// defensively).
    /// </summary>
    internal static string ConvertToString(object? value)
    {
        if (value is null) return "null";
        if (value is bool b) return b ? "true" : "false";
        if (value is string s) return s;
        if (value is double d) return JsNumberToString(d);
        if (value is float f) return JsNumberToString(f);
        if (value is decimal m) return JsNumberToString((double)m);
        if (value is sbyte or byte or short or ushort or int or uint or long or ulong)
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }
        return value.ToString() ?? string.Empty;
    }

    private static string JsNumberToString(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        if (d == Math.Floor(d) && Math.Abs(d) < 1e16)
        {
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        }
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    internal static bool TryParseNumber(string s, out double value)
    {
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    internal static bool TryParseDate(string s, out DateTimeOffset dt)
    {
        // Try ISO 8601 first.
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
        {
            return true;
        }
        return false;
    }
}
