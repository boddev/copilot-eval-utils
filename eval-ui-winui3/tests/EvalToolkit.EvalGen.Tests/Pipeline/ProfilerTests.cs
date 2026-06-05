using System.Globalization;
using EvalToolkit.Core;
using EvalToolkit.EvalGen.Pipeline;

namespace EvalToolkit.EvalGen.Tests.Pipeline;

public class ProfilerTests
{
    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in pairs) d[p.Key] = p.Value;
        return d;
    }

    [Fact]
    public void ProfileDataset_EmptyRecords_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Profiler.ProfileDataset(Array.Empty<IReadOnlyDictionary<string, object?>>(), "x.csv", InputFormat.Csv));
    }

    [Fact]
    public void ProfileDataset_SkipsUnderscorePrefixedFields()
    {
        var records = new[]
        {
            Row(("id", "1"), ("_source_file", "a.csv"), ("name", "Alice")),
            Row(("id", "2"), ("_source_file", "a.csv"), ("name", "Bob")),
        };
        var p = Profiler.ProfileDataset(records, "a.csv", InputFormat.Csv);
        Assert.Equal(2, p.Columns.Count);
        Assert.DoesNotContain(p.Columns, c => c.Name == "_source_file");
        Assert.Contains(p.Columns, c => c.Name == "id");
        Assert.Contains(p.Columns, c => c.Name == "name");
    }

    [Fact]
    public void ProfileDataset_DetectsNumericColumn()
    {
        var records = new[]
        {
            Row(("price", "10")),
            Row(("price", "20")),
            Row(("price", "30")),
        };
        var p = Profiler.ProfileDataset(records, "f.csv", InputFormat.Csv);
        var price = p.Columns.Single(c => c.Name == "price");
        Assert.Equal("number", price.DataType);
        Assert.Equal(10.0, Convert.ToDouble(price.Min, CultureInfo.InvariantCulture));
        Assert.Equal(30.0, Convert.ToDouble(price.Max, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ProfileDataset_MixedTypes_ReportedAsMixed()
    {
        var records = new[]
        {
            Row(("v", "10")),
            Row(("v", "hello")),
            Row(("v", true)),
        };
        var p = Profiler.ProfileDataset(records, "x.csv", InputFormat.Csv);
        Assert.Equal("mixed", p.Columns.Single(c => c.Name == "v").DataType);
    }

    [Fact]
    public void ProfileDataset_DetectsDateColumn()
    {
        var records = new[]
        {
            Row(("when", "2024-01-15")),
            Row(("when", "2024-06-30")),
        };
        var p = Profiler.ProfileDataset(records, "x.csv", InputFormat.Csv);
        Assert.Equal("date", p.Columns.Single(c => c.Name == "when").DataType);
        Assert.NotNull(p.Columns.Single(c => c.Name == "when").Min);
    }

    [Fact]
    public void ProfileDataset_CountsNulls()
    {
        var records = new[]
        {
            Row(("x", "a")),
            Row(("x", null)),
            Row(("x", "")),
        };
        var p = Profiler.ProfileDataset(records, "x.csv", InputFormat.Csv);
        Assert.Equal(2, p.Columns.Single(c => c.Name == "x").NullCount);
    }

    [Fact]
    public void ProfileDataset_CandidateKeys_HighUniqueness()
    {
        var records = Enumerable.Range(1, 20).Select(i =>
            (IReadOnlyDictionary<string, object?>)Row(("id", i.ToString(CultureInfo.InvariantCulture)), ("status", "active"))).ToArray();
        var p = Profiler.ProfileDataset(records, "x.csv", InputFormat.Csv);
        Assert.Contains("id", p.CandidateKeyColumns);
        Assert.DoesNotContain("status", p.CandidateKeyColumns);
    }

    [Fact]
    public void ProfileDataset_CandidateTitles_MatchPattern()
    {
        var records = new[]
        {
            Row(("id", "1"), ("name", "Alice"), ("description", "first"), ("age", "30")),
            Row(("id", "2"), ("name", "Bob"), ("description", "second"), ("age", "25")),
        };
        var p = Profiler.ProfileDataset(records, "x.csv", InputFormat.Csv);
        Assert.Contains("name", p.CandidateTitleColumns);
        Assert.Contains("description", p.CandidateTitleColumns);
        Assert.DoesNotContain("id", p.CandidateTitleColumns);
        Assert.DoesNotContain("age", p.CandidateTitleColumns);
    }

    [Fact]
    public void ProfileDataset_ValueCounts_LowCardinalityStringsOnly()
    {
        var records = Enumerable.Range(0, 30).Select(i =>
            (IReadOnlyDictionary<string, object?>)Row(("status", i % 3 == 0 ? "open" : (i % 3 == 1 ? "closed" : "pending")))).ToArray();
        var p = Profiler.ProfileDataset(records, "x.csv", InputFormat.Csv);
        var status = p.Columns.Single(c => c.Name == "status");
        Assert.NotNull(status.ValueCounts);
        Assert.Equal(3, status.ValueCounts!.Count);
    }

    [Fact]
    public void ProfileDataset_HighCardinality_NoValueCounts()
    {
        var records = Enumerable.Range(0, 30).Select(i =>
            (IReadOnlyDictionary<string, object?>)Row(("uuid", Guid.NewGuid().ToString()))).ToArray();
        var p = Profiler.ProfileDataset(records, "x.csv", InputFormat.Csv);
        Assert.Null(p.Columns.Single(c => c.Name == "uuid").ValueCounts);
    }

    [Fact]
    public void ProfileDataset_SampleRecords_IncludeFirstAndLast()
    {
        var records = Enumerable.Range(0, 100).Select(i =>
            (IReadOnlyDictionary<string, object?>)Row(("id", i.ToString(CultureInfo.InvariantCulture)), ("category", i % 4 == 0 ? "A" : "B"))).ToArray();
        var p = Profiler.ProfileDataset(records, "x.csv", InputFormat.Csv, new Random(42));
        Assert.True(p.SampleRecords.Count <= 20);
        Assert.Contains(p.SampleRecords, r => r["id"]?.ToString() == "0");
        Assert.Contains(p.SampleRecords, r => r["id"]?.ToString() == "99");
    }

    [Fact]
    public void ProfileDataset_SampleRecords_StableWithSeededRandom()
    {
        var records = Enumerable.Range(0, 100).Select(i =>
            (IReadOnlyDictionary<string, object?>)Row(("id", i.ToString(CultureInfo.InvariantCulture)))).ToArray();
        var p1 = Profiler.ProfileDataset(records, "x.csv", InputFormat.Csv, new Random(123));
        var p2 = Profiler.ProfileDataset(records, "x.csv", InputFormat.Csv, new Random(123));
        Assert.Equal(p1.SampleRecords.Count, p2.SampleRecords.Count);
        for (int i = 0; i < p1.SampleRecords.Count; i++)
        {
            Assert.Equal(p1.SampleRecords[i]["id"], p2.SampleRecords[i]["id"]);
        }
    }

    [Fact]
    public void ProfileDataset_ReturnsCorrectRowCountAndFormat()
    {
        var records = new[] { Row(("a", "1")), Row(("a", "2")) };
        var p = Profiler.ProfileDataset(records, "data.json", InputFormat.Json);
        Assert.Equal(2, p.RowCount);
        Assert.Equal(InputFormat.Json, p.Format);
        Assert.Equal("data.json", p.FileName);
    }
}
