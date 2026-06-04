using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace EvalToolkit.Parity.Harness;

/// <summary>
/// Options describing a parity invocation of the TS entrypoint. Keep
/// this in lockstep with <c>eval-gen/src/parity-entrypoint.ts</c>'s
/// <c>parseArgs</c>: a flag added there MUST also be added here, or
/// the harness will silently drop it from invocations and produce
/// misleading parity-diff output.
/// </summary>
public sealed record ParityRunOptions
{
    /// <summary>The op verb (e.g. <c>"read"</c>). Required.</summary>
    public required string Operation { get; init; }

    /// <summary>The fixture path passed to the TS entrypoint. Required.</summary>
    public required string Fixture { get; init; }

    /// <summary>
    /// Recursive directory traversal flag, passed as <c>--recursive</c>.
    /// Defaults to false to match TS default; flip explicitly per
    /// fixture in the harness rather than relying on env defaults.
    /// </summary>
    public bool Recursive { get; init; }

    /// <summary>
    /// Extension allow-list passed as <c>--ext csv,json</c>. Null means
    /// the TS side uses its built-in <c>SUPPORTED_EXTENSIONS</c>.
    /// </summary>
    public IReadOnlyList<string>? Extensions { get; init; }

    /// <summary>
    /// Hard-cap on TS process runtime. Defaults to 60 seconds — the
    /// readers should complete in well under 5s for any sane fixture;
    /// 60s catches infinite loops / accidental network calls without
    /// being so short it flakes on a busy CI runner.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Outcome of a single TS parity-entrypoint invocation. Discriminated
/// by <see cref="Status"/>; the structured fields are populated based
/// on what kind of outcome occurred. Always exposes the raw
/// stdout/stderr/exit code for diagnostic dumping.
/// </summary>
public sealed record TsParityResult
{
    public required TsParityStatus Status { get; init; }
    public required int ExitCode { get; init; }
    public required string Stdout { get; init; }
    public required string Stderr { get; init; }
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Parsed envelope when <see cref="Status"/> is
    /// <see cref="TsParityStatus.Success"/> or
    /// <see cref="TsParityStatus.ReaderError"/>. Null on
    /// <see cref="TsParityStatus.BadUsage"/> / <see cref="TsParityStatus.Crashed"/> /
    /// <see cref="TsParityStatus.Timeout"/>.
    /// </summary>
    public JsonElement? Envelope { get; init; }

    /// <summary>
    /// The <c>error</c> field from the envelope when the TS reader
    /// rejected the fixture (exit code 3). Null otherwise.
    /// </summary>
    public string? ReaderErrorMessage { get; init; }
}

/// <summary>Discriminator for <see cref="TsParityResult"/>.</summary>
public enum TsParityStatus
{
    /// <summary>Exit 0, stdout parsed as a valid envelope.</summary>
    Success,

    /// <summary>Exit 3 — TS reader threw on the fixture (caught and serialized into envelope).</summary>
    ReaderError,

    /// <summary>Exit 2 — bad CLI usage (typically a harness bug, not an algorithm bug).</summary>
    BadUsage,

    /// <summary>Exit 1 or other unexpected non-zero exit; stdout may be empty / unparseable.</summary>
    Crashed,

    /// <summary>Process killed because it exceeded <see cref="ParityRunOptions.Timeout"/>.</summary>
    Timeout,
}

/// <summary>
/// Drives the TypeScript reference implementation via
/// <c>node dist/parity-entrypoint.js</c> and captures a structured
/// result. Stateless; thread-safe.
/// </summary>
public sealed class TsParityRunner
{
    private readonly string _nodeExecutable;
    private readonly string _entrypointPath;

    /// <summary>
    /// Construct a runner using auto-detected paths
    /// (<c>node</c> on PATH, <see cref="EvalGenLocator"/> for the
    /// entrypoint). Throws if the entrypoint isn't built.
    /// </summary>
    public TsParityRunner()
        : this(nodeExecutable: "node", entrypointPath: EvalGenLocator.GetParityEntrypointPath())
    {
    }

    /// <summary>
    /// Construct a runner with explicit paths — useful for tests that
    /// want to stub node / point at a custom entrypoint, or for CI
    /// configurations that prefer to pin a Node version.
    /// </summary>
    public TsParityRunner(string nodeExecutable, string entrypointPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(entrypointPath);
        _nodeExecutable = nodeExecutable;
        _entrypointPath = entrypointPath;
    }

    /// <summary>Run the TS entrypoint and return a structured result.</summary>
    public async Task<TsParityResult> RunAsync(ParityRunOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        ProcessStartInfo psi = new()
        {
            FileName = _nodeExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_entrypointPath) ?? Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add(_entrypointPath);
        psi.ArgumentList.Add(options.Operation);
        psi.ArgumentList.Add(options.Fixture);
        if (options.Recursive)
        {
            psi.ArgumentList.Add("--recursive");
        }
        if (options.Extensions is { Count: > 0 })
        {
            psi.ArgumentList.Add("--ext");
            psi.ArgumentList.Add(string.Join(",", options.Extensions));
        }

        Stopwatch sw = Stopwatch.StartNew();
        using Process process = new() { StartInfo = psi };

        StringBuilder stdoutBuf = new();
        StringBuilder stderrBuf = new();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuf.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuf.AppendLine(e.Data); };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"Failed to start node process '{_nodeExecutable}'. " +
                $"Is Node.js on PATH?");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using CancellationTokenSource timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.Timeout);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Hit the timeout (not the caller's cancellation).
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* best effort */ }
        }

        sw.Stop();

        string stdout = stdoutBuf.ToString();
        string stderr = stderrBuf.ToString();

        if (timedOut)
        {
            return new TsParityResult
            {
                Status = TsParityStatus.Timeout,
                ExitCode = -1,
                Stdout = stdout,
                Stderr = stderr,
                Duration = sw.Elapsed,
            };
        }

        int exitCode = process.ExitCode;
        JsonElement? envelope = null;
        string? readerError = null;

        if (stdout.Length > 0)
        {
            // Best-effort parse. The TS entrypoint writes a single
            // JSON blob with no trailing newline; AppendLine added one
            // on our side, so trim before parse.
            string trimmed = stdout.TrimEnd();
            try
            {
                using JsonDocument doc = JsonDocument.Parse(trimmed);
                envelope = doc.RootElement.Clone();
                if (envelope.Value.ValueKind == JsonValueKind.Object &&
                    envelope.Value.TryGetProperty("error", out JsonElement errProp) &&
                    errProp.ValueKind == JsonValueKind.String)
                {
                    readerError = errProp.GetString();
                }
            }
            catch (JsonException)
            {
                // Unparseable stdout; envelope stays null.
            }
        }

        TsParityStatus status = exitCode switch
        {
            0 => TsParityStatus.Success,
            2 => TsParityStatus.BadUsage,
            3 => TsParityStatus.ReaderError,
            _ => TsParityStatus.Crashed,
        };

        return new TsParityResult
        {
            Status = status,
            ExitCode = exitCode,
            Stdout = stdout,
            Stderr = stderr,
            Duration = sw.Elapsed,
            Envelope = envelope,
            ReaderErrorMessage = readerError,
        };
    }
}
