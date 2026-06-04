using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Polly;

namespace EvalToolkit.WorkIQ;

/// <summary>
/// Persistent MCP stdio client for <c>workiq mcp</c>. Ports
/// <c>CliWorkIQClient</c> from <c>eval-score/node/src/workiq-client.ts</c>.
/// </summary>
public sealed class CliWorkIQClient : IWorkIQClient
{
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly WorkIQRetryOptions _retryOptions;
    private readonly int _timeoutMs;

    private Process? _process;
    private Channel<string>? _stdoutLines;
    private Task? _stdoutPump;
    private Task? _stderrPump;
    private int _requestId;
    private string? _tenantId;
    private bool _disposed;

    public CliWorkIQClient(CliWorkIQClientOptions? options = null)
    {
        options ??= new CliWorkIQClientOptions();
        _timeoutMs = options.TimeoutMs ?? WorkIQOptionsDefaults.ParseTimeoutMs();
        _retryOptions = options.RetryOptions ?? WorkIQRetryOptions.FromValues(
            options.MaxAttempts,
            options.BackoffBaseMs,
            options.BackoffMaxMs);
    }

    public async Task StartAsync(string? tenantId = null, CancellationToken cancellationToken = default)
    {
        _tenantId = tenantId;
        ResiliencePipeline<object> pipeline = WorkIQRetry.BuildResiliencePipeline<object>(this, _retryOptions);
        await pipeline.ExecuteAsync(
            async token =>
            {
                await EnsureStartedOnceAsync(token).ConfigureAwait(false);
                return new object();
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> AskAsync(string prompt, string? tenantId = null, CancellationToken cancellationToken = default)
    {
        WorkIQResponse response = await AskWithMetadataAsync(
            prompt,
            tenantId is null ? null : new WorkIQAskOptions(TenantId: tenantId),
            cancellationToken).ConfigureAwait(false);
        return response.Text;
    }

    public async Task<WorkIQResponse> AskWithMetadataAsync(
        string prompt,
        WorkIQAskOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ResiliencePipeline<WorkIQResponse> pipeline = WorkIQRetry.BuildResiliencePipeline<WorkIQResponse>(this, _retryOptions);
        return await pipeline.ExecuteAsync(
            async token =>
            {
                if (!IsProcessRunning())
                {
                    _tenantId = options?.TenantId ?? _tenantId;
                    await EnsureStartedOnceAsync(token).ConfigureAwait(false);
                }

                JsonNode raw = await AskOnceRawAsync(prompt, token).ConfigureAwait(false);
                JsonArray? content = raw["result"]?["content"] as JsonArray;
                if (content is not null && content.Count > 0)
                {
                    string text = GetString(content[0]?["text"]) ?? string.Empty;
                    return new WorkIQResponse(text, ExtractCitations(raw), raw);
                }

                throw new WorkIQException("WorkIQ returned an empty response.");
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopProcessCoreAsync(cancellationToken).ConfigureAwait(false);
            await StartOnceCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopProcessCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
            _lifecycleLock.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    private async Task EnsureStartedOnceAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsProcessRunning())
            {
                return;
            }
            await StartOnceCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StartOnceCoreAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Process process = SpawnWorkIQMcp();
        _process = process;
        _stdoutLines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
        _stdoutPump = PumpStdoutAsync(process, _stdoutLines.Writer);
        _stderrPump = PumpStderrAsync(process);

        JsonObject initRequest = new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 0,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "EvalScore",
                    ["version"] = "1.0.0",
                },
            },
        };
        await WriteAsync(initRequest.ToJsonString(), cancellationToken).ConfigureAwait(false);
        _ = await ReadResponseAsync(0, cancellationToken).ConfigureAwait(false);

        JsonObject initialized = new()
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/initialized",
        };
        await WriteAsync(initialized.ToJsonString(), cancellationToken).ConfigureAwait(false);

        int eulaRequestId = Interlocked.Increment(ref _requestId);
        JsonObject eulaRequest = new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = eulaRequestId,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "accept_eula",
                ["arguments"] = new JsonObject
                {
                    ["eulaUrl"] = "https://github.com/microsoft/work-iq-mcp",
                },
            },
        };
        await WriteAsync(eulaRequest.ToJsonString(), cancellationToken).ConfigureAwait(false);
        _ = await ReadResponseAsync(eulaRequestId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonNode> AskOnceRawAsync(string question, CancellationToken cancellationToken)
    {
        int id = Interlocked.Increment(ref _requestId);
        JsonObject request = new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "ask_work_iq",
                ["arguments"] = new JsonObject
                {
                    ["question"] = question,
                },
            },
        };

        await WriteAsync(request.ToJsonString(), cancellationToken).ConfigureAwait(false);
        JsonNode response = await ReadResponseAsync(id, cancellationToken).ConfigureAwait(false);
        string? errorMessage = GetString(response["error"]?["message"]);
        if (!string.IsNullOrEmpty(errorMessage))
        {
            throw new WorkIQException($"WorkIQ error: {errorMessage}");
        }
        return response;
    }

    private async Task WriteAsync(string data, CancellationToken cancellationToken)
    {
        Process process = _process ?? throw new WorkIQException("WorkIQ MCP process is not running.");
        if (process.HasExited)
        {
            throw new WorkIQException(FormattableString.Invariant($"WorkIQ MCP process exited with code {process.ExitCode}."));
        }
        await process.StandardInput.WriteLineAsync(data.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonNode> ReadResponseAsync(int expectedId, CancellationToken cancellationToken)
    {
        Channel<string> channel = _stdoutLines ?? throw new WorkIQException("WorkIQ MCP process is not running.");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_timeoutMs));

        try
        {
            while (await channel.Reader.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out string? line))
                {
                    JsonNode? msg;
                    try
                    {
                        msg = JsonNode.Parse(line);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }
                    if (msg is null)
                    {
                        continue;
                    }

                    JsonNode? idNode = msg["id"];
                    if (idNode is null)
                    {
                        continue;
                    }
                    if (TryGetInt(idNode, out int id) && id == expectedId)
                    {
                        return msg;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(FormattableString.Invariant($"Timed out waiting for MCP response (id={expectedId})"));
        }

        throw new TimeoutException(FormattableString.Invariant($"Timed out waiting for MCP response (id={expectedId})"));
    }

    private bool IsProcessRunning()
    {
        return _process is { HasExited: false };
    }

    private async Task StopProcessCoreAsync(CancellationToken cancellationToken)
    {
        Process? process = _process;
        _process = null;
        _stdoutLines?.Writer.TryComplete();
        _stdoutLines = null;

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private static Process SpawnWorkIQMcp()
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows())
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("workiq mcp");
        }
        else
        {
            psi.FileName = "workiq";
            psi.ArgumentList.Add("mcp");
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new WorkIQException("Failed to start WorkIQ MCP process.");
        }
        return process;
    }

    private static async Task PumpStdoutAsync(Process process, ChannelWriter<string> writer)
    {
        try
        {
            while (!process.HasExited)
            {
                string? line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    await writer.WriteAsync(trimmed).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
            return;
        }
        writer.TryComplete();
    }

    private static async Task PumpStderrAsync(Process process)
    {
        try
        {
            char[] buffer = new char[1024];
            while (!process.HasExited)
            {
                int read = await process.StandardError.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static List<Citation>? ExtractCitations(JsonNode? raw)
    {
        JsonNode? result = raw?["result"];
        JsonNode?[] possible =
        [
            raw?["citations"],
            raw?["references"],
            result?["citations"],
            result?["references"],
            result?["metadata"]?["citations"],
        ];
        foreach (JsonNode? value in possible)
        {
            if (value is not JsonArray array)
            {
                continue;
            }
            var citations = new List<Citation>();
            foreach (JsonNode? item in array)
            {
                string? rawString = GetString(item);
                if (rawString is not null)
                {
                    citations.Add(new Citation(Title: rawString, Raw: rawString));
                    continue;
                }
                citations.Add(new Citation(
                    Title: FirstNonEmpty(GetString(item?["title"]), GetString(item?["name"])),
                    Url: FirstNonEmpty(GetString(item?["url"]), GetString(item?["uri"])),
                    SourceLocation: FirstNonEmpty(
                        GetString(item?["sourceLocation"]),
                        GetString(item?["source_location"]),
                        GetString(item?["location"])),
                    Raw: item?.DeepClone()));
            }
            if (citations.Count > 0)
            {
                return citations;
            }
        }
        return null;
    }

    private static string? GetString(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }
        return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
    }

    private static bool TryGetInt(JsonNode node, out int value)
    {
        value = 0;
        if (node.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }
        return node.GetValue<int>() == (value = node.GetValue<int>());
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }
        return null;
    }
}
