using System.Globalization;
using EvalToolkit.Core;
using EvalToolkit.EvalGen.Pipeline;

namespace EvalToolkit.EvalGen.Tests.Pipeline;

public class FactExtractorTests
{
    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in pairs) d[p.Key] = p.Value;
        return d;
    }

    private static IReadOnlyDictionary<string, object?>[] MakeRecords(int count)
    {
        return Enumerable.Range(1, count).Select(i =>
            (IReadOnlyDictionary<string, object?>)Row(
                ("id", i.ToString(CultureInfo.InvariantCulture)),
                ("name", $"item-{i}"),
                ("price", (i * 10).ToString(CultureInfo.InvariantCulture)),
                ("status", i % 2 == 0 ? "active" : "inactive"))).ToArray();
    }

    [Fact]
    public void ExtractFacts_BuildsRowReferences()
    {
        var records = MakeRecords(5);
        var profile = Profiler.ProfileDataset(records, "items.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile);
        Assert.NotEmpty(facts);
        Assert.All(facts, f => Assert.Contains("items.csv:row ", f.RowReference));
    }

    [Fact]
    public void ExtractFacts_UsesSourceFileTagWhenPresent()
    {
        var records = new[]
        {
            (IReadOnlyDictionary<string, object?>)Row(("id", "1"), ("name", "a"), ("_source_file", "alt.csv")),
            (IReadOnlyDictionary<string, object?>)Row(("id", "2"), ("name", "b"), ("_source_file", "alt.csv")),
        };
        var profile = Profiler.ProfileDataset(records, "items.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile);
        Assert.All(facts, f => Assert.StartsWith("alt.csv:row", f.RowReference));
    }

    [Fact]
    public void ExtractFacts_SkipsNullsAndEmpties()
    {
        var records = new[]
        {
            (IReadOnlyDictionary<string, object?>)Row(("id", "1"), ("desc", null), ("notes", "")),
            (IReadOnlyDictionary<string, object?>)Row(("id", "2"), ("desc", "x"), ("notes", "n")),
        };
        var profile = Profiler.ProfileDataset(records, "f.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile);
        Assert.DoesNotContain(facts, f => f.Field == "desc" && f.RowReference.EndsWith("row 1", StringComparison.Ordinal));
        Assert.DoesNotContain(facts, f => f.Field == "notes" && f.RowReference.EndsWith("row 1", StringComparison.Ordinal));
        Assert.Contains(facts, f => f.Field == "desc" && f.RowReference.EndsWith("row 2", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractFacts_EmptySourceFile_FallsBackToProfileFileName()
    {
        // Round-2 fix: TS truthiness — empty _source_file falls back to profile.fileName.
        // Previously C# kept "" producing row refs like ":row 1".
        var records = new[]
        {
            (IReadOnlyDictionary<string, object?>)Row(("id", "1"), ("name", "a"), ("_source_file", "")),
        };
        var profile = Profiler.ProfileDataset(records, "items.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile);
        Assert.All(facts, f => Assert.StartsWith("items.csv:row", f.RowReference));
        Assert.DoesNotContain(facts, f => f.RowReference.StartsWith(":row", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractFacts_RespectsMaxFactsCap()
    {
        var records = MakeRecords(50);
        var profile = Profiler.ProfileDataset(records, "f.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile, new FactExtractor.ExtractFactsOptions { MaxFacts = 10 });
        Assert.True(facts.Count <= 10);
    }

    [Fact]
    public void ExtractFacts_RespectsMaxFactsPerRecord()
    {
        var records = MakeRecords(10);
        var profile = Profiler.ProfileDataset(records, "f.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile, new FactExtractor.ExtractFactsOptions
        {
            MaxFacts = 100,
            MaxFactsPerRecord = 2,
        });
        var byRow = facts.GroupBy(f => f.RowReference);
        Assert.All(byRow, g => Assert.True(g.Count() <= 2));
    }

    [Fact]
    public void ExtractFacts_AssignsSequentialIds()
    {
        var records = MakeRecords(5);
        var profile = Profiler.ProfileDataset(records, "f.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile);
        for (int i = 0; i < facts.Count; i++)
        {
            Assert.Equal($"f-{i + 1}", facts[i].Id);
        }
    }

    [Fact]
    public void GroupFactsByRecord_PreservesInsertionOrder()
    {
        var records = MakeRecords(3);
        var profile = Profiler.ProfileDataset(records, "f.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile);
        var grouped = FactExtractor.GroupFactsByRecord(facts);
        var prevIdx = -1;
        foreach (var rowRef in grouped.Keys)
        {
            var idx = facts.First(f => f.RowReference == rowRef).Id;
            int idNum = int.Parse(idx.AsSpan(2), CultureInfo.InvariantCulture);
            Assert.True(idNum > prevIdx);
            prevIdx = idNum;
        }
    }

    [Fact]
    public void SummarizeFacts_FormatsWithIds()
    {
        var records = MakeRecords(2);
        var profile = Profiler.ProfileDataset(records, "f.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile);
        var summary = FactExtractor.SummarizeFacts(facts);
        Assert.Contains("[f-1]", summary);
        Assert.Contains("[f.csv:row 1]", summary);
        Assert.Contains("id=", summary);
    }

    [Fact]
    public void SummarizeFacts_CapsByMaxRecords()
    {
        var records = MakeRecords(10);
        var profile = Profiler.ProfileDataset(records, "f.csv", InputFormat.Csv);
        var facts = FactExtractor.ExtractFacts(records, profile);
        var summary = FactExtractor.SummarizeFacts(facts, maxRecords: 2);
        var lineCount = summary.Split('\n').Length;
        Assert.Equal(2, lineCount);
    }
}
