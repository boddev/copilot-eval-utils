using System.Text.Json;
using EvalToolkit.Parity.Harness;

namespace EvalToolkit.Parity.Tests;

/// <summary>
/// Direct unit tests for the harness components themselves —
/// independent of any actual TS invocation. These run on any box and
/// catch comparer / locator / option-shape regressions without
/// requiring the eval-gen TypeScript side to be built.
/// </summary>
[Collection("Parity")]
public class HarnessComponentTests
{
    // ── NormalizedJsonComparer ────────────────────────────────────────

    [Fact]
    public void Comparer_IdenticalObjects_ReturnsEmptyDiff()
    {
        JsonElement a = Parse("""{"format":"csv","records":[{"id":1,"name":"x"}]}""");
        JsonElement b = Parse("""{"format":"csv","records":[{"id":1,"name":"x"}]}""");
        NormalizedJsonComparer cmp = new();
        Assert.Empty(cmp.Compare(a, b));
    }

    [Fact]
    public void Comparer_IgnoresObjectKeyOrder()
    {
        // The TS sortedStringify and the C# WriteSortedJson both
        // produce sorted keys, but the underlying parse-tree comparer
        // shouldn't depend on serialized order — it recurses by name.
        JsonElement a = Parse("""{"a":1,"b":2,"c":3}""");
        JsonElement b = Parse("""{"c":3,"a":1,"b":2}""");
        NormalizedJsonComparer cmp = new();
        Assert.Empty(cmp.Compare(a, b));
    }

    [Fact]
    public void Comparer_DetectsStringValueMismatch_WithPath()
    {
        JsonElement a = Parse("""{"records":[{"name":"Acme"}]}""");
        JsonElement b = Parse("""{"records":[{"name":"Globex"}]}""");
        NormalizedJsonComparer cmp = new();
        IReadOnlyList<JsonDiff> diffs = cmp.Compare(a, b);
        JsonDiff diff = Assert.Single(diffs);
        Assert.Equal("/records/0/name", diff.Path);
        Assert.Contains("string", diff.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Comparer_DetectsMissingObjectKey_OnEachSide()
    {
        JsonElement a = Parse("""{"shared":1,"only_left":2}""");
        JsonElement b = Parse("""{"shared":1,"only_right":3}""");
        NormalizedJsonComparer cmp = new();
        IReadOnlyList<JsonDiff> diffs = cmp.Compare(a, b);
        Assert.Equal(2, diffs.Count);
        Assert.Contains(diffs, d => d.Path == "/only_left" && d.Reason.Contains("missing on right"));
        Assert.Contains(diffs, d => d.Path == "/only_right" && d.Reason.Contains("missing on left"));
    }

    [Fact]
    public void Comparer_RespectsIgnoredPaths()
    {
        // Real use: /version of the envelope differs between TS/C# but
        // is irrelevant to correctness — we ignore the field so
        // reviewers focus on data diffs.
        JsonElement a = Parse("""{"version":"1.0.0","records":[1]}""");
        JsonElement b = Parse("""{"version":"2.0.0","records":[1]}""");
        NormalizedJsonComparer cmp = new(new NormalizedJsonComparisonOptions
        {
            IgnoredPaths = new HashSet<string>(StringComparer.Ordinal) { "/version" },
        });
        Assert.Empty(cmp.Compare(a, b));
    }

    [Fact]
    public void Comparer_DetectsArrayLengthDifference()
    {
        JsonElement a = Parse("""[1,2,3]""");
        JsonElement b = Parse("""[1,2]""");
        NormalizedJsonComparer cmp = new();
        IReadOnlyList<JsonDiff> diffs = cmp.Compare(a, b);
        Assert.Contains(diffs, d => d.Reason.Contains("array length differs"));
    }

    [Fact]
    public void Comparer_NumericStringEqualsNumber_OptIn()
    {
        // Off by default: types differ -> diff.
        JsonElement a = Parse("""{"x":"42"}""");
        JsonElement b = Parse("""{"x":42}""");
        Assert.NotEmpty(new NormalizedJsonComparer().Compare(a, b));

        // Opt-in: parity holds because "42" decimal-parses to 42.
        NormalizedJsonComparer loose = new(new NormalizedJsonComparisonOptions
        {
            NumericStringEqualsNumber = true,
        });
        Assert.Empty(loose.Compare(a, b));
    }

    [Fact]
    public void Comparer_NumberKindTolerantOnIntegers()
    {
        // JSON parser treats 1 and 1.0 as different ValueKinds in some
        // versions, but our NumbersEqual ladder coerces through long /
        // decimal / double — the comparer must call them equal.
        JsonElement a = Parse("""{"x":1}""");
        JsonElement b = Parse("""{"x":1.0}""");
        Assert.Empty(new NormalizedJsonComparer().Compare(a, b));
    }

    [Fact]
    public void WriteSortedJson_EmitsKeysInOrdinalOrder()
    {
        // Lock the canonical wire shape: keys sorted, no indentation.
        // The TS stableStringify uses the same `keys().sort()` order.
        Dictionary<string, object> input = new()
        {
            ["zeta"] = 1,
            ["alpha"] = 2,
            ["mu"] = 3,
        };
        string json = NormalizedJsonComparer.WriteSortedJson(input);
        Assert.Equal("""{"alpha":2,"mu":3,"zeta":1}""", json);
    }

    [Fact]
    public void WriteSortedJson_RecursesIntoNestedObjects()
    {
        var input = new
        {
            outer = new { z = 1, a = 2 },
            beta = "x",
        };
        string json = NormalizedJsonComparer.WriteSortedJson(input);
        Assert.Equal("""{"beta":"x","outer":{"a":2,"z":1}}""", json);
    }

    // ── EvalGenLocator ────────────────────────────────────────────────

    [Fact]
    public void Locator_FindsSiblingEvalGenDirectoryFromTestBin()
    {
        // We're running from
        // eval-ui-winui3/tests/EvalToolkit.Parity.Tests/bin/Debug/netN/.
        // The locator should walk up until it finds the sibling
        // eval-gen/ that contains package.json.
        string root = EvalGenLocator.GetEvalGenRoot();
        Assert.True(Directory.Exists(root), $"Expected to discover eval-gen at '{root}'.");
        Assert.True(File.Exists(Path.Combine(root, "package.json")));
    }

    [Fact]
    public void Locator_RespectsEnvironmentOverride_WhenPointedAtRealDir()
    {
        string realRoot = EvalGenLocator.GetEvalGenRoot();
        string original = Environment.GetEnvironmentVariable(EvalGenLocator.OverrideEnvVar) ?? string.Empty;
        try
        {
            Environment.SetEnvironmentVariable(EvalGenLocator.OverrideEnvVar, realRoot);
            Assert.Equal(realRoot, EvalGenLocator.GetEvalGenRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EvalGenLocator.OverrideEnvVar,
                string.IsNullOrEmpty(original) ? null : original);
        }
    }

    [Fact]
    public void Locator_ThrowsWhenOverridePointsAtNonexistentDirectory()
    {
        string original = Environment.GetEnvironmentVariable(EvalGenLocator.OverrideEnvVar) ?? string.Empty;
        try
        {
            Environment.SetEnvironmentVariable(
                EvalGenLocator.OverrideEnvVar,
                Path.Combine(Path.GetTempPath(), $"__nonexistent_{Guid.NewGuid():N}"));
            Assert.Throws<DirectoryNotFoundException>(() => EvalGenLocator.GetEvalGenRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EvalGenLocator.OverrideEnvVar,
                string.IsNullOrEmpty(original) ? null : original);
        }
    }

    // ── ParityRunOptions / ParityFixture ─────────────────────────────

    [Fact]
    public void ParityFixture_BuildsRunOptionsRootedInEvalGenFixtures()
    {
        ParityFixture fx = new()
        {
            Id = "csv/suppliers",
            RelativePath = "suppliers.csv",
            Recursive = false,
            Extensions = new[] { "csv" },
        };
        ParityRunOptions opts = fx.BuildRunOptions();
        Assert.Equal("read", opts.Operation);
        Assert.EndsWith(Path.Combine("tests", "fixtures", "suppliers.csv"), opts.Fixture);
        Assert.False(opts.Recursive);
        Assert.Equal(new[] { "csv" }, opts.Extensions);
        Assert.True(File.Exists(opts.Fixture),
            $"Fixture path '{opts.Fixture}' should resolve to a real file in the repo.");
    }

    private static JsonElement Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ── Review-driven hardening tests (round-3 reviewers) ────────────

    /// <summary>
    /// Per Opus-4.8 round-3 review: <see cref="NormalizedJsonComparer.WriteSortedJson{T}(T)"/>
    /// must emit Latin-1+ non-ASCII content and HTML metacharacters
    /// literally (no <c>\u</c>-escape) so a byte-comparison against
    /// TS output doesn't false-fail on realistic document content with
    /// accents or angle brackets.
    ///
    /// Note: supplementary-plane characters (emoji, etc.) are escaped
    /// as surrogate-pair <c>\uXXXX\uXXXX</c> by both .NET's
    /// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> and
    /// by JS <c>JSON.stringify</c> when the surrogate is the only
    /// representation, so they aren't tested here. The parity
    /// comparer uses decoded-string equality (via JsonElement parse)
    /// so emoji parity works regardless of escape representation.
    /// </summary>
    [Fact]
    public void WriteSortedJson_PreservesNonAsciiAndHtmlMetacharsLiterally()
    {
        var value = new Dictionary<string, string>
        {
            ["accented"] = "café",
            ["html"] = "<a href=\"x\">a&b</a>",
        };
        string actual = NormalizedJsonComparer.WriteSortedJson(value);

        Assert.Contains("café", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("caf\\u00E9", actual, StringComparison.Ordinal);
        Assert.Contains("<a href=\\\"x\\\">a&b</a>", actual, StringComparison.Ordinal);
        // Keys must be sorted.
        Assert.True(actual.IndexOf("\"accented\"", StringComparison.Ordinal) <
                    actual.IndexOf("\"html\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// Per Opus-4.8 round-3 review: <c>NumericStringEqualsNumber</c>
    /// uses <see cref="System.Globalization.NumberStyles"/>.Float
    /// semantics — it must NOT treat thousands-separator, currency,
    /// or parenthesized-negative strings as equal to numbers (JS
    /// <c>Number(...)</c> returns NaN for those). Otherwise the
    /// opt-in masks exactly the cell-type drift it's supposed to
    /// surface.
    /// </summary>
    [Theory]
    [InlineData("1,000")]      // thousands separator: JS Number -> NaN
    [InlineData("$5")]         // currency: JS Number -> NaN
    [InlineData("(5)")]        // accountant negative: JS Number -> NaN
    [InlineData(" 5,000.00 ")] // combined: JS Number -> NaN
    public void Comparer_NumericStringMatch_RejectsNonJsNumberFormats(string suspect)
    {
        JsonElement left = Parse("""{"v":5000}""");
        JsonElement right = Parse($$"""{"v":"{{suspect}}"}""");

        NormalizedJsonComparer cmp = new(new NormalizedJsonComparisonOptions
        {
            NumericStringEqualsNumber = true,
        });

        var diffs = cmp.Compare(left, right);
        Assert.NotEmpty(diffs);
        Assert.Contains(diffs, d => d.Path == "/v");
    }

    /// <summary>
    /// Conversely: a string that JS <c>Number()</c> would parse — bare
    /// digits, leading sign, decimal, exponent, surrounding whitespace
    /// — IS treated as equal under the opt-in, so the same opt-in still
    /// covers the legitimate XLSX-style cell-type drift case.
    /// </summary>
    [Theory]
    [InlineData("5000")]
    [InlineData(" 5000 ")]
    [InlineData("+5000")]
    [InlineData("5.0e3")]
    public void Comparer_NumericStringMatch_AcceptsJsLikeFloatFormats(string equiv)
    {
        JsonElement left = Parse("""{"v":5000}""");
        JsonElement right = Parse($$"""{"v":"{{equiv}}"}""");

        NormalizedJsonComparer cmp = new(new NormalizedJsonComparisonOptions
        {
            NumericStringEqualsNumber = true,
        });

        Assert.Empty(cmp.Compare(left, right));
    }
}
