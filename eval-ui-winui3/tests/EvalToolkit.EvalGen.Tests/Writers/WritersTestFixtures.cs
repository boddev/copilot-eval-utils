using System.Reflection;
using System.Text.Json;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Tests.Writers;

/// <summary>
/// Shared test fixtures + probe-results loader for the writer parity
/// tests. The JS probe (<c>writers-probe/writers-probe.js</c>) captured
/// byte-exact output for 42 scenarios from the live TS writers with a
/// pinned clock of <c>2024-01-15T12:34:56.789Z</c>; the JSON file is
/// embedded as <c>WritersProbeResults.json</c> and these fixtures
/// reproduce the JS input items exactly so each test can call its
/// writer and assert the bytes match.
/// </summary>
internal static class WritersTestFixtures
{
    /// <summary>Pinned clock matching the JS probe's monkey-patched <c>Date</c>.</summary>
    public static readonly DateTimeOffset PinnedNow =
        DateTimeOffset.Parse("2024-01-15T12:34:56.789Z",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    /// <summary>
    /// Equivalent to JS <c>baseItem()</c>: a minimal item with one
    /// <c>must_contain</c> assertion. Use <c>with</c> expressions to
    /// produce variants.
    /// </summary>
    public static GeneratedEvalItem BaseItem() => new()
    {
        Id = "item-1",
        Prompt = "Who owns Acme Corp?",
        ExpectedAnswer = "Jane Smith owns Acme Corp.",
        SourceLocation = "suppliers.csv:row 1",
        Assertions = [new MustContainAssertion { Value = "Jane Smith" }],
        Category = QuestionCategory.SingleRecordLookup,
        Difficulty = Difficulty.Easy,
        SupportingFacts = ["owner=Jane Smith"],
        GroundingConfidence = GroundingConfidence.High,
    };

    /// <summary>Equivalent to JS <c>secondItem()</c>.</summary>
    public static GeneratedEvalItem SecondItem() => BaseItem() with
    {
        Id = "item-2",
        Prompt = "What is Acme Corp status?",
        ExpectedAnswer = "Acme Corp is active.",
        SourceLocation = "suppliers.csv:row 2",
        Assertions = [new MustContainAssertion { Value = "active" }],
        SupportingFacts = ["status=active"],
    };

    /// <summary>Equivalent to JS <c>richItem()</c>.</summary>
    public static GeneratedEvalItem RichItem() => BaseItem() with
    {
        Id = "item-rich",
        ReferencedRows = ["rowA", "rowB"],
        Assertions =
        [
            new MustContainAssertion { Value = "Jane" },
            new MustContainAssertion { Value = "Smith", WholeWord = true },
            new MustContainAnyAssertion { Values = ["Smith", "Doe"] },
            new MustNotContainAssertion { Value = "fictional" },
        ],
        Difficulty = Difficulty.Hard,
        GroundingConfidence = GroundingConfidence.Medium,
        SupportingFacts = ["owner=Jane Smith", "verified by HR 2024-01-01"],
    };

    /// <summary>Equivalent to JS <c>csvEdgeItems()</c>.</summary>
    public static IReadOnlyList<GeneratedEvalItem> CsvEdgeItems() =>
    [
        BaseItem() with
        {
            Id = "edge-comma",
            Prompt = "value, with comma",
            ExpectedAnswer = "answer, with comma",
            SourceLocation = "file.csv:row 9",
        },
        BaseItem() with
        {
            Id = "edge-quote",
            Prompt = "value with \"embedded\" quote",
            ExpectedAnswer = "answer with \"embedded\" quote",
            SourceLocation = "file.csv:row 10",
        },
        BaseItem() with
        {
            Id = "edge-newline",
            Prompt = "value with\nembedded newline",
            ExpectedAnswer = "answer with\nembedded newline",
            SourceLocation = "file.csv:row 11",
        },
        BaseItem() with
        {
            Id = "edge-crlf",
            Prompt = "value with\r\nCRLF",
            ExpectedAnswer = "answer with\r\nCRLF",
            SourceLocation = "file.csv:row 12",
        },
        BaseItem() with
        {
            Id = "edge-unicode",
            Prompt = "café \u00e9 emoji 🚀",
            ExpectedAnswer = "answer with unicode \u00e9 and surrogate 🚀",
            SourceLocation = "file.csv:row 13",
        },
        BaseItem() with
        {
            Id = "edge-control",
            Prompt = "control \u0001 \u0007 \u001f end",
            ExpectedAnswer = "answer with controls \u0008 \u000b \u000c",
            SourceLocation = "file.csv:row 14",
        },
    ];
}

/// <summary>
/// Single scenario loaded from <c>WritersProbeResults.json</c>. Field
/// names match the JS probe's recorded shape so the JSON binds 1:1.
/// </summary>
internal sealed record WritersProbeScenario(
    string Scenario,
    string WrittenPath,
    int ByteLength,
    string Sha256,
    string Text,
    bool Bom,
    bool EndsWithNewline,
    string? InputPath,
    int? BodyLength);

/// <summary>
/// Loads + caches the 42-scenario probe ground truth from the embedded
/// <c>WritersProbeResults.json</c> resource.
/// </summary>
internal static class WritersProbeData
{
    private const string ResourceName =
        "EvalToolkit.EvalGen.Tests.Writers.WritersProbeResults.json";

    private static readonly Lazy<IReadOnlyDictionary<string, WritersProbeScenario>> s_scenarios =
        new(LoadScenarios);

    public static IReadOnlyDictionary<string, WritersProbeScenario> All => s_scenarios.Value;

    public static WritersProbeScenario Get(string scenarioName)
    {
        if (!All.TryGetValue(scenarioName, out var s))
        {
            throw new InvalidOperationException(
                $"Writers probe scenario not found: '{scenarioName}'. " +
                $"Available: {string.Join(", ", All.Keys.OrderBy(k => k, StringComparer.Ordinal))}");
        }
        return s;
    }

    private static Dictionary<string, WritersProbeScenario> LoadScenarios()
    {
        var asm = typeof(WritersProbeData).Assembly;
        using Stream? stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' not found. " +
                $"Available: {string.Join(", ", asm.GetManifestResourceNames())}");

        using var doc = JsonDocument.Parse(stream);
        var dict = new Dictionary<string, WritersProbeScenario>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            string name = prop.Name;
            JsonElement v = prop.Value;
            dict[name] = new WritersProbeScenario(
                Scenario: name,
                WrittenPath: v.GetProperty("written_path").GetString() ?? string.Empty,
                ByteLength: v.GetProperty("byte_length").GetInt32(),
                Sha256: v.GetProperty("sha256").GetString() ?? string.Empty,
                Text: v.GetProperty("text").GetString() ?? string.Empty,
                Bom: v.GetProperty("bom").GetBoolean(),
                EndsWithNewline: v.GetProperty("ends_with_newline").GetBoolean(),
                InputPath: v.TryGetProperty("input_path", out var ip) ? ip.GetString() : null,
                BodyLength: v.TryGetProperty("body_length", out var bl) ? bl.GetInt32() : null);
        }
        return dict;
    }
}

/// <summary>
/// Helper utilities used across writer tests.
/// </summary>
internal static class WritersTestUtil
{
    /// <summary>
    /// Allocate a unique temp file path inside the per-test temp dir.
    /// The file is NOT created — callers pass the path to a writer.
    /// </summary>
    public static string TempPath(string fileName)
    {
        string dir = Path.Combine(
            Path.GetTempPath(),
            "EvalToolkit.EvalGen.Tests.Writers",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileName);
    }

    /// <summary>
    /// Read the bytes of <paramref name="path"/> as UTF-8 (no BOM
    /// stripping). Matches the JS probe's <c>fs.readFileSync(p).toString('utf-8')</c>.
    /// </summary>
    public static string ReadAllUtf8(string path) =>
        System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path));
}
