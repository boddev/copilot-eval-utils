using EvalToolkit.Core;
using EvalToolkit.EvalGen.Diagnostics;

namespace EvalToolkit.EvalGen.Tests.Diagnostics;

public sealed class ConnectorDiagnosticsTests
{
    private static DatasetProfile MakeProfile(params string[] columnNames) => new()
    {
        FileName = "test.csv",
        Format = InputFormat.Csv,
        RowCount = 3,
        Columns = columnNames.Select(n => new ColumnProfile
        {
            Name = n,
            DataType = "string",
            NullCount = 0,
            UniqueCount = 3,
            TotalCount = 3,
            SampleValues = new object?[] { "a", "b", "c" },
        }).ToList(),
        SampleRecords = Array.Empty<IReadOnlyDictionary<string, object?>>(),
        CandidateKeyColumns = Array.Empty<string>(),
        CandidateTitleColumns = Array.Empty<string>(),
    };

    private static GeneratedEvalItem MakeItem(string prompt, params string[] supportingFacts) => new()
    {
        Id = "id-1",
        Prompt = prompt,
        ExpectedAnswer = "yes",
        SourceLocation = ":row 1",
        Assertions = Array.Empty<Assertion>(),
        Category = QuestionCategory.SingleRecordLookup,
        Difficulty = Difficulty.Easy,
        SupportingFacts = supportingFacts,
        GroundingConfidence = GroundingConfidence.High,
    };

    [Fact]
    public void LoadSchema_ThrowsWhenFileMissing()
    {
        var ex = Assert.Throws<FileNotFoundException>(() =>
            ConnectorDiagnostics.LoadSchema(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json")));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadSchema_ThrowsWhenContentFieldsMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"schema-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"name":"bad"}""");
        try
        {
            Assert.Throws<InvalidDataException>(() => ConnectorDiagnostics.LoadSchema(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadSchema_ParsesValidFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"schema-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"name":"good","contentFields":["title","body"],"hasSummaryItems":true}""");
        try
        {
            var schema = ConnectorDiagnostics.LoadSchema(path);
            Assert.Equal("good", schema.Name);
            Assert.Equal(new[] { "title", "body" }, schema.ContentFields);
            Assert.True(schema.HasSummaryItems);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Round-2: TS treats `contentFields: []` as valid (just 0% coverage),
    // not an error. Regression for GPT-5.5 finding #2.
    [Fact]
    public void LoadSchema_AcceptsEmptyContentFieldsArray()
    {
        var path = Path.Combine(Path.GetTempPath(), $"schema-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"name":"empty","contentFields":[]}""");
        try
        {
            var schema = ConnectorDiagnostics.LoadSchema(path);
            Assert.NotNull(schema.ContentFields);
            Assert.Empty(schema.ContentFields!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Round-2: TS uses literal property names; C# should not silently
    // accept PascalCase / SCREAMING. Regression for GPT-5.5 finding #3.
    [Fact]
    public void LoadSchema_RejectsWrongCasedPropertyNames()
    {
        var path = Path.Combine(Path.GetTempPath(), $"schema-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"name":"bad","ContentFields":["title"]}""");
        try
        {
            // PascalCase "ContentFields" should NOT be picked up; the missing
            // lowercase contentFields then triggers the validation error.
            Assert.Throws<InvalidDataException>(() => ConnectorDiagnostics.LoadSchema(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnalyzeFieldCoverage_ComputesIndexedUnindexedAndPercent()
    {
        var profile = MakeProfile("Title", "Body", "Secret");
        var schema = new ConnectorSchema { Name = "x", ContentFields = new[] { "title", "body" } };

        var coverage = ConnectorDiagnostics.AnalyzeFieldCoverage(profile, schema);

        Assert.Equal(new[] { "title", "body" }, coverage.IndexedFields);
        Assert.Equal(new[] { "secret" }, coverage.UnindexedFields);
        Assert.Equal(67, coverage.CoveragePercentage); // 2/3 = 66.666… rounds to 67
    }

    [Fact]
    public void AnalyzeFieldCoverage_ZeroColumnsYieldsZeroPercent()
    {
        var profile = MakeProfile();
        var schema = new ConnectorSchema { ContentFields = new[] { "anything" } };
        var coverage = ConnectorDiagnostics.AnalyzeFieldCoverage(profile, schema);
        Assert.Equal(0, coverage.CoveragePercentage);
    }

    [Fact]
    public void RunDiagnostics_FlagsUnindexedFieldReferencesAsErrors()
    {
        var profile = MakeProfile("title", "secret");
        var schema = new ConnectorSchema { Name = "X", ContentFields = new[] { "title" } };
        var items = new[]
        {
            MakeItem("What is the title?", "title=hello"),
            MakeItem("What is the secret?", "secret=foo"),
        };

        var report = ConnectorDiagnostics.RunDiagnostics(items, profile, schema);

        Assert.Equal(2, report.TotalItems);
        Assert.Equal(DiagnosticSeverity.Ok, report.ItemDiagnostics[0].Severity);
        Assert.Equal(DiagnosticSeverity.Error, report.ItemDiagnostics[1].Severity);
        Assert.Equal(1, report.FieldCoverage.QuestionsTargetingUnindexed);
    }

    [Fact]
    public void RunDiagnostics_FlagsAggregationWithoutSummaryItems()
    {
        var profile = MakeProfile("title");
        var schema = new ConnectorSchema { Name = "X", ContentFields = new[] { "title" }, HasSummaryItems = false };
        var items = new[]
        {
            MakeItem("How many records exist?"),
        };

        var report = ConnectorDiagnostics.RunDiagnostics(items, profile, schema);

        Assert.Single(report.AggregationWarnings);
        Assert.Equal(DiagnosticSeverity.Warning, report.ItemDiagnostics[0].Severity);
    }

    [Fact]
    public void RunDiagnostics_AggregationOkWhenSummaryItemsTrue()
    {
        var profile = MakeProfile("title");
        var schema = new ConnectorSchema { Name = "X", ContentFields = new[] { "title" }, HasSummaryItems = true };
        var items = new[]
        {
            MakeItem("How many records exist?"),
        };

        var report = ConnectorDiagnostics.RunDiagnostics(items, profile, schema);

        Assert.Empty(report.AggregationWarnings);
    }

    [Fact]
    public void RunDiagnostics_FlagsBroadQuestionsAsWarning()
    {
        var profile = MakeProfile("title");
        var schema = new ConnectorSchema { Name = "X", ContentFields = new[] { "title" } };
        var items = new[]
        {
            MakeItem("List all of the records."),
        };

        var report = ConnectorDiagnostics.RunDiagnostics(items, profile, schema);
        Assert.Equal(DiagnosticSeverity.Warning, report.ItemDiagnostics[0].Severity);
    }

    [Fact]
    public void FormatDiagnosticReport_ProducesMarkdown()
    {
        var profile = MakeProfile("title", "secret");
        var schema = new ConnectorSchema { Name = "ConnectorA", ContentFields = new[] { "title" } };
        var items = new[]
        {
            MakeItem("What is the title?", "title=hi"),
            MakeItem("What is the secret?", "secret=foo"),
        };

        var report = ConnectorDiagnostics.RunDiagnostics(items, profile, schema);
        var md = ConnectorDiagnostics.FormatDiagnosticReport(report);

        Assert.Contains("# Connector Diagnostics Report", md, StringComparison.Ordinal);
        Assert.Contains("ConnectorA", md, StringComparison.Ordinal);
        Assert.Contains("✅ title", md, StringComparison.Ordinal);
        Assert.Contains("❌ secret", md, StringComparison.Ordinal);
        Assert.Contains("🔴", md, StringComparison.Ordinal); // error
    }
}
