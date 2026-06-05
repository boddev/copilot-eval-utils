using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EvalToolkit.Core;
using EvalToolkit.WorkIQ;

namespace EvalToolkit.EvalGen.LlmClients;

/// <summary>
/// Microsoft Work IQ A2A (Agent-to-Agent) public preview API. Ports
/// <c>WorkIQA2AClient</c> from <c>eval-gen/src/llm-client.ts</c>.
///
/// <para>Implements the eval-gen wire protocol LOCALLY (per GPT-5.5
/// review blocker B1): the existing <c>A2AWorkIQClient</c> in
/// <c>EvalToolkit.WorkIQ</c> uses a different, agent-id-aware protocol
/// (<c>message/send</c> with variants header + agent URL resolution).
/// This client speaks the simpler eval-gen <c>SendMessage</c> shape that
/// the public Work IQ Gateway accepts.</para>
///
/// <para>Auth strategy (per GPT-5.5 review recommendation G):</para>
/// <list type="bullet">
///   <item>If <c>EVALGEN_WORKIQ_TOKEN</c> (or <see cref="LlmClientOptions"/>
///         carries an access token) is set, use that directly.</item>
///   <item>Otherwise hand off to <see cref="A2ATokenProviderFactory"/>
///         which honors the broader app's <c>EVALSCORE_A2A_AUTH_MODE</c>
///         / MSAL config.</item>
///   <item>If neither path yields a token, surface the operator-facing
///         TS error so people know to set the env var.</item>
/// </list>
/// </summary>
public sealed class WorkIQA2ALlmClient : ILlmClient
{
    private const string Endpoint = "https://workiq.svc.cloud.microsoft/a2a/";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IA2ATokenProvider _tokenProvider;
    private readonly string _timeZone;
    private readonly int _timeoutMs;
    private readonly int _maxAttempts;
    private readonly int _backoffBaseMs;
    private string? _contextId;

    private static readonly JsonSerializerOptions s_serializerOptions = new(JsonSerializerDefaults.Web);

    public WorkIQA2ALlmClient(
        LlmClientOptions? options = null,
        HttpClient? httpClient = null,
        IA2ATokenProvider? tokenProvider = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;

        _timeoutMs = options?.TimeoutMs
            ?? EnvHelpers.ParsePositiveIntEnv(EnvVars.EvalGenLlmTimeoutMs, 300_000);
        _maxAttempts = Math.Max(1, options?.MaxAttempts
            ?? EnvHelpers.ParsePositiveIntEnv(EnvVars.EvalGenLlmMaxAttempts, 3));
        _backoffBaseMs = Math.Max(0, options?.BackoffBaseMs
            ?? EnvHelpers.ParsePositiveIntEnv(EnvVars.EvalGenLlmBackoffMs, 2000));
        _timeZone = options?.M365TimeZone
            ?? Environment.GetEnvironmentVariable(EnvVars.EvalGenM365CopilotTimeZone)
            ?? ResolveLocalTimeZone();

        if (tokenProvider is not null)
        {
            _tokenProvider = tokenProvider;
            return;
        }

        // GPT-5.5 G: prefer the simple EVALGEN_WORKIQ_TOKEN path; fall
        // back to the full A2ATokenProviderFactory for richer modes.
        string evalGenToken = Environment.GetEnvironmentVariable(EnvVars.EvalGenWorkIqToken) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(evalGenToken))
        {
            _tokenProvider = new StaticTokenA2ATokenProvider(evalGenToken.Trim());
            return;
        }

        IA2ATokenProvider chained = A2ATokenProviderFactory.CreateFromEnvironment();
        if (chained is NoopA2ATokenProvider)
        {
            throw new InvalidOperationException(
                "Work IQ A2A requires a delegated bearer token. Set EVALGEN_WORKIQ_TOKEN to a JWT issued for your app " +
                "registration with the WorkIQAgent.Ask scope and audience api://workiq.svc.cloud.microsoft. " +
                "See https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/work-iq-api-quickstart for setup steps.");
        }
        _tokenProvider = chained;
    }

    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        string reply = await SendMessageAsync(
            "Reply with exactly this JSON object and no extra text: {\"ok\":true}",
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(reply))
        {
            throw new InvalidOperationException("Work IQ A2A authentication preflight returned an empty response");
        }
    }

    public async Task<T> GenerateStructuredAsync<T>(string prompt, string schemaDescription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(schemaDescription);

        string text = await SendMessageAsync(
            StructuredPromptBuilder.Build(prompt, schemaDescription),
            cancellationToken).ConfigureAwait(false);
        return StructuredJsonParser.Parse<T>(text);
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private Task<string> SendMessageAsync(string text, CancellationToken cancellationToken)
    {
        return LlmRetry.RunAsync(
            ct => SendMessageOnceAsync(text, ct),
            RetryClassifiers.IsRetryableA2AError,
            new LlmRetry.Options
            {
                MaxAttempts = _maxAttempts,
                BackoffBaseMs = _backoffBaseMs,
            },
            onRetry: null,
            cancellationToken);
    }

    private async Task<string> SendMessageOnceAsync(string text, CancellationToken cancellationToken)
    {
        string token = await _tokenProvider.GetTokenAsync(false, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException(
                "Work IQ A2A token provider returned an empty access token.");
        }

        string requestId = Guid.NewGuid().ToString();
        string messageId = Guid.NewGuid().ToString();
        int timeZoneOffsetMinutes = -(int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow).TotalMinutes;

        var metadata = new Dictionary<string, object>
        {
            ["Location"] = new Dictionary<string, object>
            {
                ["timeZone"] = _timeZone,
                ["timeZoneOffset"] = timeZoneOffsetMinutes,
            },
        };
        var message = new Dictionary<string, object?>
        {
            ["role"] = "ROLE_USER",
            ["messageId"] = messageId,
            ["parts"] = new[] { new { text } },
            ["metadata"] = metadata,
        };
        if (!string.IsNullOrEmpty(_contextId))
        {
            message["contextId"] = _contextId;
        }

        var body = new
        {
            jsonrpc = "2.0",
            id = requestId,
            method = "SendMessage",
            @params = new { message },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, s_serializerOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("A2A-Version", "1.0");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeoutMs);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Work IQ A2A request timed out after {_timeoutMs} ms");
        }

        try
        {
            if (!response.IsSuccessStatusCode)
            {
                string errorText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new WorkIqA2aLlmException((int)response.StatusCode, errorText);
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("error", out JsonElement errEl) && errEl.ValueKind == JsonValueKind.Object)
            {
                int code = 0;
                if (errEl.TryGetProperty("code", out JsonElement codeEl) && codeEl.TryGetInt32(out int c))
                {
                    code = c;
                }
                string msg = errEl.TryGetProperty("message", out JsonElement msgEl) && msgEl.ValueKind == JsonValueKind.String
                    ? msgEl.GetString() ?? string.Empty
                    : string.Empty;
                throw new WorkIqA2aLlmException(0, $"Work IQ A2A JSON-RPC error {code}: {msg}");
            }

            if (!root.TryGetProperty("result", out JsonElement result)
                || !result.TryGetProperty("task", out JsonElement task)
                || task.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Work IQ A2A response is missing result.task");
            }

            if (task.TryGetProperty("contextId", out JsonElement contextEl) && contextEl.ValueKind == JsonValueKind.String)
            {
                _contextId = contextEl.GetString();
            }

            string? state = null;
            if (task.TryGetProperty("status", out JsonElement status))
            {
                if (status.TryGetProperty("state", out JsonElement stateEl) && stateEl.ValueKind == JsonValueKind.String)
                {
                    state = stateEl.GetString();
                }
                if (state is not null && state != "TASK_STATE_COMPLETED")
                {
                    string? detail = TryExtractFirstPartText(status, "message");
                    string detailPart = detail is null ? string.Empty : $": {detail}";
                    throw new InvalidOperationException($"Work IQ A2A task ended in state {state}{detailPart}");
                }
            }

            string? artifactText = TryExtractArtifactText(task);
            if (artifactText is null)
            {
                throw new InvalidOperationException("Work IQ A2A task completed but contained no text artifact");
            }
            return artifactText;
        }
        finally
        {
            response.Dispose();
        }
    }

    private static string? TryExtractFirstPartText(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement child) || child.ValueKind != JsonValueKind.Object) return null;
        if (!child.TryGetProperty("parts", out JsonElement parts) || parts.ValueKind != JsonValueKind.Array) return null;
        foreach (JsonElement part in parts.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object
                && part.TryGetProperty("text", out JsonElement textEl)
                && textEl.ValueKind == JsonValueKind.String)
            {
                string? s = textEl.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        return null;
    }

    private static string? TryExtractArtifactText(JsonElement task)
    {
        if (!task.TryGetProperty("artifacts", out JsonElement artifacts) || artifacts.ValueKind != JsonValueKind.Array) return null;
        foreach (JsonElement artifact in artifacts.EnumerateArray())
        {
            if (artifact.ValueKind != JsonValueKind.Object) continue;
            if (!artifact.TryGetProperty("parts", out JsonElement parts) || parts.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement part in parts.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object
                    && part.TryGetProperty("text", out JsonElement textEl)
                    && textEl.ValueKind == JsonValueKind.String)
                {
                    string? s = textEl.GetString();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
        }
        return null;
    }

    private static string ResolveLocalTimeZone()
    {
        try
        {
            string id = TimeZoneInfo.Local.Id;
            if (OperatingSystem.IsWindows())
            {
                if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out string? iana) && !string.IsNullOrEmpty(iana))
                {
                    return iana;
                }
            }
            return id;
        }
        catch
        {
            return "UTC";
        }
    }
}
