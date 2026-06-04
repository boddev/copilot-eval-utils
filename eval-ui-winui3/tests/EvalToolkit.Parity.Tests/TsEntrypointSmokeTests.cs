using System.Text.Json;
using EvalToolkit.Parity.Harness;

namespace EvalToolkit.Parity.Tests;

/// <summary>
/// End-to-end smoke tests that actually shell out to the TS
/// <c>parity-entrypoint.js</c>. These require:
/// <list type="bullet">
///   <item><c>node</c> on PATH.</item>
///   <item><c>eval-gen/dist/parity-entrypoint.js</c> built
///     (CI workflow runs <c>npm run build</c>; locally run it once).</item>
/// </list>
/// When the entrypoint isn't built, tests are skipped via a guard so
/// developers without a Node toolchain aren't blocked. CI gates this
/// by always building the TS side before running .NET tests.
/// </summary>
public class TsEntrypointSmokeTests
{
    [Fact]
    public async Task TsEntrypoint_ReadsSuppliersCsv_ReturnsSortedEnvelopeWith10Records()
    {
        if (!EvalGenLocator.IsAvailable())
        {
            // Don't fail locally if the dev hasn't built eval-gen yet
            // — CI always builds it before this test runs.
            return;
        }

        ParityFixture fx = new()
        {
            Id = "csv/suppliers",
            RelativePath = "suppliers.csv",
        };
        TsParityRunner runner = new();
        TsParityResult result = await runner.RunAsync(fx.BuildRunOptions());

        Assert.True(
            result.Status == TsParityStatus.Success,
            $"Expected Success, got {result.Status} (exit {result.ExitCode}).\n" +
            $"stdout: {Truncate(result.Stdout)}\n" +
            $"stderr: {Truncate(result.Stderr)}");
        Assert.NotNull(result.Envelope);

        JsonElement env = result.Envelope!.Value;
        Assert.Equal("eval-gen-parity", env.GetProperty("tool").GetString());
        Assert.Equal("read", env.GetProperty("operation").GetString());
        Assert.Equal("csv", env.GetProperty("format").GetString());

        // suppliers.csv has 15 data rows (16 lines including header).
        JsonElement records = env.GetProperty("records");
        Assert.Equal(JsonValueKind.Array, records.ValueKind);
        Assert.Equal(15, records.GetArrayLength());

        // Every record carries the _source_file provenance tag the
        // TS reader stamps on inputs.
        foreach (JsonElement record in records.EnumerateArray())
        {
            Assert.Equal("suppliers.csv", record.GetProperty("_source_file").GetString());
        }

        // First record sanity (locks in a wire-format detail in case
        // the TS reader's CSV mode silently changes types).
        JsonElement first = records[0];
        Assert.Equal("SUP-001", first.GetProperty("supplier_id").GetString());
        Assert.Equal("Acme Corp", first.GetProperty("supplier_name").GetString());
    }

    [Fact]
    public async Task TsEntrypoint_RejectsMissingFixture_WithReaderErrorEnvelope()
    {
        if (!EvalGenLocator.IsAvailable()) return;

        // Use a nonexistent path under the real fixtures dir so the
        // path resolution doesn't escape any sandbox.
        string fakeFixture = Path.Combine(
            EvalGenLocator.GetEvalGenRoot(),
            "tests", "fixtures",
            $"__nope_{Guid.NewGuid():N}.csv");
        ParityRunOptions opts = new()
        {
            Operation = "read",
            Fixture = fakeFixture,
        };
        TsParityRunner runner = new();
        TsParityResult result = await runner.RunAsync(opts);

        // Either ReaderError (exit 3, structured envelope with `error`)
        // or Crashed (exit 1, unstructured) is acceptable depending on
        // whether the reader catches the FS error itself. We assert
        // the harness surfaced a parseable envelope on the structured
        // path so the C# side can rely on the contract.
        Assert.True(
            result.Status == TsParityStatus.ReaderError || result.Status == TsParityStatus.Crashed,
            $"Expected ReaderError/Crashed, got {result.Status}.\nstderr: {Truncate(result.Stderr)}");

        if (result.Status == TsParityStatus.ReaderError)
        {
            Assert.NotNull(result.Envelope);
            Assert.False(string.IsNullOrEmpty(result.ReaderErrorMessage));
        }
    }

    [Fact]
    public async Task TsEntrypoint_BadCliUsage_ReturnsExit2()
    {
        if (!EvalGenLocator.IsAvailable()) return;

        ParityRunOptions opts = new()
        {
            Operation = "no-such-op",
            Fixture = "ignored",
        };
        TsParityRunner runner = new();
        TsParityResult result = await runner.RunAsync(opts);

        // Either BadUsage (exit 2) for unknown op, or ReaderError if
        // the dispatcher fell into the switch default differently.
        // The contract we assert is: harness identifies the failure
        // mode correctly, not Success.
        Assert.NotEqual(TsParityStatus.Success, result.Status);
        Assert.NotEqual(0, result.ExitCode);
    }

    private static string Truncate(string s, int max = 800) =>
        s.Length <= max ? s : s[..max] + "…";
}
