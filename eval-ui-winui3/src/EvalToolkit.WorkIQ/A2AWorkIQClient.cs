using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EvalToolkit.Core;
using Polly;

namespace EvalToolkit.WorkIQ;

/// <summary>
/// HTTP A2A WorkIQ client ported from <c>A2AWorkIQClient</c> in
/// <c>eval-score/node/src/workiq-client.ts</c>.
/// </summary>
public sealed class A2AWorkIQClient : IWorkIQClient
{
    private const string VariantsHeaderValue = "feature.EnableA2AServer";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IA2ATokenProvider _tokenProvider;
    private readonly bool _hasExplicitTokenProvider;
    private readonly WorkIQRetryOptions _retryOptions;
    private readonly int _timeoutMs;
    private readonly Dictionary<string, string> _resolvedAgentUrls = [];

    private string _endpoint;
    private string _authMode;
    private string? _tenantId;

    public A2AWorkIQClient(A2AWorkIQClientOptions? options = null)
    {
        options ??= new A2AWorkIQClientOptions();
        _endpoint = TrimTrailingSlashes(options.Endpoint ?? Environment.GetEnvironmentVariable(EnvVars.WorkIqA2aEndpoint) ?? string.Empty);
        string accessToken = options.AccessToken ?? Environment.GetEnvironmentVariable(EnvVars.WorkIqA2aAccessToken) ?? string.Empty;
        string tokenCommand = options.TokenCommand ?? EnvHelpers.GetFirstEnv(
            EnvVars.WorkIqA2aTokenCommand,
            EnvVars.EvalScoreA2aTokenCommand);
        _authMode = A2ATokenProviderFactory.NormalizeAuthMode(
            options.AuthMode ?? EnvHelpers.GetFirstEnv(
                EnvVars.EvalScoreA2aAuthMode,
                EnvVars.WorkIqA2aAuthMode,
                EnvVars.EvalScoreA2aAuth,
                EnvVars.WorkIqA2aAuth));
        // Per Opus-4.8 plan-stage review (M4): preserve whether the
        // caller injected a token provider explicitly. Once
        // A2ATokenProviderFactory wraps the env config into a real
        // MsalA2ATokenProvider we cannot distinguish "explicit
        // injection" from "factory auto-built msal mode" by type
        // alone, so msal config validation would either always-fire
        // or always-skip. Storing the flag at construction preserves
        // the TS validateConfig() branch behavior.
        _hasExplicitTokenProvider = options.TokenProvider is not null;
        _tokenProvider = A2ATokenProviderFactory.Create(
            accessToken,
            tokenCommand,
            _authMode,
            msalConfig: null,
            msalBroker: options.InteractiveAuthBroker,
            options.TokenProvider,
            // Per round-2 reviewer feedback (GPT-5.5): _tenantId is
            // assigned at StartAsync()/AskWithMetadataAsync() time,
            // *after* the factory has already created the lazy
            // provider. Supply a callback so the lazy MSAL config is
            // built with the current tenant override at first token
            // request, not whatever was captured at construction
            // (which would always be null).
            msalTenantIdProvider: () => _tenantId);
        _httpClient = options.HttpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttpClient = options.HttpClient is null;
        _timeoutMs = options.TimeoutMs ?? WorkIQOptionsDefaults.ParseTimeoutMs();
        _retryOptions = options.RetryOptions ?? WorkIQRetryOptions.FromValues(
            options.MaxAttempts,
            options.BackoffBaseMs,
            options.BackoffMaxMs);
    }

    public Task StartAsync(string? tenantId = null, CancellationToken cancellationToken = default)
    {
        _tenantId = tenantId;
        ValidateConfig();
        return Task.CompletedTask;
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
        _tenantId = options?.TenantId ?? _tenantId;
        ValidateConfig();
        if (string.IsNullOrEmpty(options?.AgentId))
        {
            throw new WorkIQException("M365 agent ID targeting requires an agentId.");
        }

        ResiliencePipeline<WorkIQResponse> pipeline = WorkIQRetry.BuildResiliencePipeline<WorkIQResponse>(this, _retryOptions);
        return await pipeline.ExecuteAsync(
            async token => await SendA2AMessageAsync(prompt, options, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private void ValidateConfig()
    {
        if (string.IsNullOrEmpty(_endpoint))
        {
            throw new WorkIQException("M365 agent ID targeting requires WORK_IQ_A2A_ENDPOINT.");
        }
        // Per Opus-4.8 plan-stage review (M4): the TS validateConfig
        // path for msal mode is "if (this.authMode === 'msal') { if
        // (!this.tokenProvider) validateMsalConfig(); return; }" — an
        // explicitly-injected provider skips validation and the msal
        // branch *returns* unconditionally (does NOT fall through to
        // the access-token-required check). Mirror that exactly.
        if (_authMode == "msal")
        {
            if (!_hasExplicitTokenProvider)
            {
                ValidateMsalConfig();
            }
            return;
        }
        if (_tokenProvider is NoopA2ATokenProvider)
        {
            throw new WorkIQException(
                "M365 agent ID targeting requires WORK_IQ_A2A_ACCESS_TOKEN, WORK_IQ_A2A_TOKEN_COMMAND, or EVALSCORE_A2A_AUTH_MODE=msal.");
        }
    }

    /// <summary>
    /// Mirror TS <c>validateMsalConfig()</c>: enumerate missing
    /// fields and throw the same wire-string error so operator-
    /// facing messages survive the port verbatim.
    /// </summary>
    private void ValidateMsalConfig()
    {
        MsalA2ATokenProviderConfig config = MsalA2ATokenProviderConfig.FromEnvironment(_tenantId);
        IReadOnlyList<string> missing = config.GetMissingFields();
        if (missing.Count > 0)
        {
            throw new WorkIQException(
                $"MSAL A2A auth requires {string.Join(", ", missing)}. " +
                "Set EVALSCORE_A2A_CLIENT_ID, EVALSCORE_A2A_TENANT_ID or --tenant-id, and EVALSCORE_A2A_SCOPES.");
        }
    }

    private async Task<string> ResolveAgentUrlAsync(string agentId, CancellationToken cancellationToken)
    {
        if (_resolvedAgentUrls.TryGetValue(agentId, out string? cached))
        {
            return cached;
        }

        string fallbackUrl = $"{_endpoint}/{Uri.EscapeDataString(agentId)}";
        string? discoveredUrl = await DiscoverAgentUrlAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(discoveredUrl))
        {
            _resolvedAgentUrls[agentId] = discoveredUrl;
            return discoveredUrl;
        }

        string cardUrl = $"{fallbackUrl}/.well-known/agent-card.json";
        try
        {
            string token = await GetAccessTokenAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, cardUrl, token);
            using HttpResponseMessage response = await SendWithTimeoutAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                JsonNode? card = await ReadJsonNodeAsync(response, cancellationToken).ConfigureAwait(false);
                string resolved = GetString(card?["url"]) ?? fallbackUrl;
                _resolvedAgentUrls[agentId] = resolved;
                return resolved;
            }
        }
        catch (HttpRequestException)
        {
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (JsonException)
        {
        }

        _resolvedAgentUrls[agentId] = fallbackUrl;
        return fallbackUrl;
    }

    private async Task<string?> DiscoverAgentUrlAsync(string agentId, CancellationToken cancellationToken)
    {
        try
        {
            string token = await GetAccessTokenAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, $"{_endpoint}/.agents", token);
            using HttpResponseMessage response = await SendWithTimeoutAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            JsonNode? raw = await ReadJsonNodeAsync(response, cancellationToken).ConfigureAwait(false);
            JsonArray? agents = raw as JsonArray ?? raw?["agents"] as JsonArray;
            if (agents is null)
            {
                return null;
            }

            foreach (JsonNode? agentNode in agents)
            {
                if (agentNode is not JsonObject agent)
                {
                    continue;
                }

                if (GetString(agent["id"]) == agentId
                    || GetString(agent["agentId"]) == agentId
                    || GetString(agent["name"]) == agentId)
                {
                    return GetString(agent["url"])
                        ?? GetString(agent["endpoint"])
                        ?? GetString(agent["agentUrl"]);
                }
            }
        }
        catch (HttpRequestException)
        {
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private async Task<WorkIQResponse> SendA2AMessageAsync(
        string question,
        WorkIQAskOptions options,
        CancellationToken cancellationToken)
    {
        string agentUrl = await ResolveAgentUrlAsync(options.AgentId!, cancellationToken).ConfigureAwait(false);
        string messageId = $"evalscore-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}";
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = messageId,
            ["method"] = "message/send",
            ["params"] = new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["kind"] = "message",
                    ["role"] = "user",
                    ["parts"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["kind"] = "text",
                            ["text"] = question,
                        },
                    },
                    ["messageId"] = messageId,
                    ["metadata"] = new JsonObject
                    {
                        ["location"] = new JsonObject
                        {
                            ["countryOrRegion"] = "US",
                            ["countryOrRegionConfidence"] = 1.0,
                            ["timeZone"] = "America/Chicago",
                        },
                    },
                },
            },
        };

        if (!string.IsNullOrEmpty(options.ConversationId)
            && payload["params"] is JsonObject parameters)
        {
            parameters["contextId"] = options.ConversationId;
        }

        string token = await GetAccessTokenAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        using HttpResponseMessage response = await PostPayloadAsync(agentUrl, token, payload, cancellationToken).ConfigureAwait(false);
        HttpResponseMessage effectiveResponse = response;
        if (response.StatusCode == HttpStatusCode.Unauthorized && CanForceRefreshToken())
        {
            string refreshedToken = await GetAccessTokenAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
            effectiveResponse = await PostPayloadAsync(agentUrl, refreshedToken, payload, cancellationToken).ConfigureAwait(false);
            response.Dispose();
        }

        using (effectiveResponse)
        {
            if (!effectiveResponse.IsSuccessStatusCode)
            {
                string retryAfter = effectiveResponse.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    ?? effectiveResponse.Headers.RetryAfter?.Date?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    ?? (effectiveResponse.Headers.TryGetValues("retry-after", out IEnumerable<string>? values) ? values.FirstOrDefault() : null)
                    ?? string.Empty;
                string body = await effectiveResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new WorkIQHttpException((int)effectiveResponse.StatusCode, retryAfter, body);
            }

            JsonNode? raw = await ReadJsonNodeAsync(effectiveResponse, cancellationToken).ConfigureAwait(false);
            string text = ExtractA2AText(raw);
            if (string.IsNullOrEmpty(text))
            {
                throw new WorkIQException("WorkIQ A2A returned an empty response.");
            }

            if (WorkIQRetry.LooksLikeRateLimitText(text))
            {
                string preview = text.Length <= 200 ? text : text[..200];
                throw new WorkIQException($"WorkIQ A2A 429 rate-limited: {preview}");
            }

            return new WorkIQResponse(
                text,
                ExtractCitations(raw),
                raw,
                ExtractContextId(raw));
        }
    }

    private async Task<HttpResponseMessage> PostPayloadAsync(
        string url,
        string token,
        JsonNode payload,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, url, token);
        string json = payload.ToJsonString();
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await SendWithTimeoutAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        request.Headers.TryAddWithoutValidation("X-variants", VariantsHeaderValue);
        return request;
    }

    private async Task<HttpResponseMessage> SendWithTimeoutAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_timeoutMs));
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("WorkIQ A2A request timed out.", ex);
        }
    }

    private bool CanForceRefreshToken()
    {
        return _authMode == "msal" || _tokenProvider is not StaticTokenA2ATokenProvider and not NoopA2ATokenProvider;
    }

    private async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        return await _tokenProvider.GetTokenAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonNode?> ReadJsonNodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string ExtractA2AText(JsonNode? raw)
    {
        JsonNode? result = raw?["result"];
        if (GetString(result?["kind"]) == "task")
        {
            List<string> parts = [];
            AddTextParts(parts, result?["status"]?["message"]?["parts"] as JsonArray, requireTextKind: true);
            if (result?["artifacts"] is JsonArray artifacts)
            {
                foreach (JsonNode? artifact in artifacts)
                {
                    AddTextParts(parts, artifact?["parts"] as JsonArray, requireTextKind: true);
                }
            }
            string taskText = string.Join('\n', parts).Trim();
            if (taskText.Length > 0)
            {
                return taskText;
            }
        }

        JsonArray?[] candidates =
        [
            result?["message"]?["parts"] as JsonArray,
            result?["parts"] as JsonArray,
            raw?["message"]?["parts"] as JsonArray,
        ];
        foreach (JsonArray? candidate in candidates)
        {
            List<string> parts = [];
            AddTextParts(parts, candidate, requireTextKind: false);
            string text = string.Join('\n', parts).Trim();
            if (text.Length > 0)
            {
                return text;
            }
        }

        return GetString(result) ?? string.Empty;
    }

    private static void AddTextParts(List<string> parts, JsonArray? source, bool requireTextKind)
    {
        if (source is null)
        {
            return;
        }
        foreach (JsonNode? part in source)
        {
            string? kind = GetString(part?["kind"]);
            if (requireTextKind && kind is not (null or "text"))
            {
                continue;
            }
            string? text = GetString(part?["text"]);
            if (!string.IsNullOrEmpty(text))
            {
                parts.Add(text);
            }
        }
    }

    private static string? ExtractContextId(JsonNode? raw)
    {
        JsonNode? result = raw?["result"];
        return FirstNonEmpty(
            GetString(result?["contextId"]),
            GetString(result?["context_id"]),
            GetString(raw?["contextId"]),
            GetString(raw?["context_id"]));
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

    private static string TrimTrailingSlashes(string value)
    {
        return value.TrimEnd('/');
    }
}
