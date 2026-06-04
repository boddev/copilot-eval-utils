namespace EvalToolkit.Parity.Harness;

/// <summary>
/// Describes a single fixture the parity harness should diff: an
/// input path + the args used to read it + optional comparison
/// tweaks. Test theories use this as their <c>MemberData</c> shape so
/// fixtures are declared once and shared across multiple suites.
/// </summary>
public sealed record ParityFixture
{
    /// <summary>
    /// Short, human-readable id used in xUnit test names. Should be
    /// unique within a theory; convention: <c>"format/scenario"</c>
    /// e.g. <c>"csv/suppliers"</c>, <c>"docx/multiline-title"</c>.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>Relative path under <c>eval-gen/tests/fixtures/</c>.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Op verb passed to both the TS and C# sides. Defaults to <c>"read"</c>.</summary>
    public string Operation { get; init; } = "read";

    public bool Recursive { get; init; }
    public IReadOnlyList<string>? Extensions { get; init; }

    /// <summary>
    /// Optional per-fixture comparison tweaks. Most fixtures should
    /// use defaults; per-fixture overrides are for fixtures that have
    /// known, documented divergences (e.g. PDF page-break ambiguity).
    /// </summary>
    public NormalizedJsonComparisonOptions? CompareOptions { get; init; }

    /// <summary>
    /// Build a <see cref="ParityRunOptions"/> rooted at the discovered
    /// <c>eval-gen/tests/fixtures/</c> directory.
    /// </summary>
    public ParityRunOptions BuildRunOptions()
    {
        string fixturesDir = Path.Combine(EvalGenLocator.GetEvalGenRoot(), "tests", "fixtures");
        return new ParityRunOptions
        {
            Operation = Operation,
            Fixture = Path.Combine(fixturesDir, RelativePath),
            Recursive = Recursive,
            Extensions = Extensions,
        };
    }

    public override string ToString() => $"{Id} ({RelativePath})";
}
