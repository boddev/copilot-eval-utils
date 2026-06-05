using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.LlmClients;

/// <summary>
/// Microsoft 365 Copilot Chat API provider. Ports
/// <c>Microsoft365CopilotChatClient</c> from <c>eval-gen/src/llm-client.ts</c>.
///
/// <para>API surface (Microsoft Graph beta):</para>
/// <list type="bullet">
///   <item>POST <c>/copilot/conversations</c> → conversation id.</item>
///   <item>POST <c>/copilot/conversations/{id}/chat</c> with the structured
///         prompt.</item>
/// </list>
///
/// <para>Auth (preserves TS semantics — per GPT-5.5 review the auto
/// <c>az login</c> retry is scoped narrowly to conversation creation
/// only):</para>
/// <list type="bullet">
///   <item>Static bearer token from <see cref="LlmClientOptions.M365AccessToken"/>
///         or <c>EVALGEN_M365_COPILOT_TOKEN</c>.</item>
///   <item>Otherwise <c>az account get-access-token</c> via
///         <see cref="IProcessRunner"/> with Copilot Graph scopes.</item>
///   <item>If conversation creation returns 401/403 AND no static token
///         was provided, run <c>az login</c> and retry conversation
///         creation once. Other Graph errors are not auto-retried with
///         <c>az login</c>.</item>
/// </list>
///
/// <para>Generic LLM retry (jittered backoff) wraps the conversation-id
/// + chat call pair via <see cref="LlmRetry"/>, matching TS.</para>
/// </summary>
public sealed class Microsoft365CopilotChatLlmClient : ILlmClient
{
    private static readonly IReadOnlyList<string> DefaultScopes =
    [
        "https://graph.microsoft.com/Sites.Read.All",
        "https://graph.microsoft.com/Mail.Read",
        "https://graph.microsoft.com/People.Read.All",
        "https://graph.microsoft.com/OnlineMeetingTranscript.Read.All",
        "https://graph.microsoft.com/Chat.Read",
        "https://graph.microsoft.com/ChannelMessage.Read.All",
        "https://graph.microsoft.com/ExternalItem.Read.All",
    ];

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IProcessRunner _processRunner;
    private readonly string? _tenantId;
    private readonly string _timeZone;
    private readonly int _maxAttempts;
    private readonly int _backoffBaseMs;
    private readonly bool _hasProvidedAccessToken;
    private string _accessToken;

    private static readonly JsonSerializerOptions s_serializerOptions = new(JsonSerializerDefaults.Web);

    public Microsoft365CopilotChatLlmClient(
        LlmClientOptions? options = null,
        HttpClient? httpClient = null,
        IProcessRunner? processRunner = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _processRunner = processRunner ?? new SystemProcessRunner();

        _accessToken = options?.M365AccessToken
            ?? Environment.GetEnvironmentVariable(EnvVars.EvalGenM365CopilotToken)
            ?? string.Empty;
        _hasProvidedAccessToken = _accessToken.Length > 0;

        _tenantId = options?.M365TenantId
            ?? Environment.GetEnvironmentVariable(EnvVars.EvalGenM365TenantId);

        _timeZone = options?.M365TimeZone
            ?? Environment.GetEnvironmentVariable(EnvVars.EvalGenM365CopilotTimeZone)
            ?? ResolveLocalTimeZone();

        _maxAttempts = Math.Max(1, options?.MaxAttempts
            ?? EnvHelpers.ParsePositiveIntEnv(EnvVars.EvalGenLlmMaxAttempts, 3));
        _backoffBaseMs = Math.Max(0, options?.BackoffBaseMs
            ?? EnvHelpers.ParsePositiveIntEnv(EnvVars.EvalGenLlmBackoffMs, 2000));
    }

    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        await CreateConversationWithRetryAsync("authentication preflight", cancellationToken).ConfigureAwait(false);
    }

    public Task<T> GenerateStructuredAsync<T>(string prompt, string schemaDescription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(schemaDescription);

        return LlmRetry.RunAsync(
            async ct =>
            {
                JsonElement convo = await CreateConversationWithRetryAsync("conversation creation", ct).ConfigureAwait(false);
                string? conversationId = TryGetString(convo, "id");
                if (string.IsNullOrEmpty(conversationId))
                {
                    throw new InvalidOperationException("Microsoft 365 Copilot Chat API did not return a conversation id");
                }

                string token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
                JsonElement chat = await GraphFetchAsync(
                    $"https://graph.microsoft.com/beta/copilot/conversations/{conversationId}/chat",
                    token,
                    new
                    {
                        message = new { text = StructuredPromptBuilder.Build(prompt, schemaDescription) },
                        locationHint = new { timeZone = _timeZone },
                    },
                    expectedStatus: 200,
                    ct).ConfigureAwait(false);

                string? text = null;
                if (chat.TryGetProperty("messages", out JsonElement messages) && messages.ValueKind == JsonValueKind.Array)
                {
                    for (int i = messages.GetArrayLength() - 1; i >= 0; i--)
                    {
                        string? candidate = TryGetString(messages[i], "text");
                        if (!string.IsNullOrEmpty(candidate))
                        {
                            text = candidate;
                            break;
                        }
                    }
                }
                if (string.IsNullOrEmpty(text))
                {
                    throw new InvalidOperationException("Microsoft 365 Copilot Chat API returned no message text");
                }

                return StructuredJsonParser.Parse<T>(text);
            },
            RetryClassifiers.IsRetryableCopilotApiError,
            new LlmRetry.Options
            {
                MaxAttempts = _maxAttempts,
                BackoffBaseMs = _backoffBaseMs,
            },
            onRetry: null,
            cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private async Task<JsonElement> CreateConversationWithRetryAsync(string operation, CancellationToken cancellationToken)
    {
        try
        {
            return await CreateConversationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception err)
        {
            if (_hasProvidedAccessToken
                || err is not GraphApiException g
                || (g.Status != 401 && g.Status != 403))
            {
                throw EnrichM365AuthError(err);
            }

            await Console.Error.WriteAsync(
                $"  Microsoft 365 Copilot auth check returned {g.Status}; running az login with Copilot Graph scopes...\n").ConfigureAwait(false);
            await RunAzureLoginAsync(cancellationToken).ConfigureAwait(false);
            _accessToken = string.Empty;

            try
            {
                return await CreateConversationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception retryError)
            {
                throw EnrichM365AuthError(retryError);
            }
        }
    }

    private async Task<JsonElement> CreateConversationAsync(CancellationToken cancellationToken)
    {
        string token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        return await GraphFetchAsync(
            "https://graph.microsoft.com/beta/copilot/conversations",
            token,
            new { },
            expectedStatus: 201,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_accessToken)) return _accessToken;
        try
        {
            _accessToken = await GetAzureCliGraphTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (_hasProvidedAccessToken) throw;
            await Console.Error.WriteAsync(
                "  Azure CLI could not acquire a Microsoft Graph token; running az login with Copilot Graph scopes...\n").ConfigureAwait(false);
            await RunAzureLoginAsync(cancellationToken).ConfigureAwait(false);
            _accessToken = await GetAzureCliGraphTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        return _accessToken;
    }

    private async Task<string> GetAzureCliGraphTokenAsync(CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "account",
            "get-access-token",
            "--scope",
            GetM365CopilotScopes(),
            "--query",
            "accessToken",
            "-o",
            "tsv",
        };
        if (!string.IsNullOrEmpty(_tenantId))
        {
            args.Add("--tenant");
            args.Add(_tenantId);
        }

        string output = await _processRunner.RunAsync(
            new ProcessInvocation("az", args, StandardInput: null, UseShell: OperatingSystem.IsWindows()),
            cancellationToken).ConfigureAwait(false);

        string token = output.Trim();
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("Azure CLI did not return a Microsoft Graph access token");
        }
        return token;
    }

    private async Task RunAzureLoginAsync(CancellationToken cancellationToken)
    {
        var args = new List<string> { "login", "--scope", GetM365CopilotScopes() };
        if (!string.IsNullOrEmpty(_tenantId))
        {
            args.Add("--tenant");
            args.Add(_tenantId);
        }
        await _processRunner.RunAsync(
            new ProcessInvocation("az", args, StandardInput: null, UseShell: OperatingSystem.IsWindows()),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> GraphFetchAsync(
        string url,
        string token,
        object body,
        int expectedStatus,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, s_serializerOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode != expectedStatus)
        {
            string errorText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new GraphApiException((int)response.StatusCode, errorText);
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }

    private static string GetM365CopilotScopes()
    {
        return Environment.GetEnvironmentVariable(EnvVars.EvalGenM365CopilotScope)
            ?? string.Join(' ', DefaultScopes);
    }

    private static Exception EnrichM365AuthError(Exception error)
    {
        if (error is not GraphApiException g || (g.Status != 401 && g.Status != 403))
        {
            return error;
        }
        return new InvalidOperationException(
            $"Microsoft 365 Copilot authentication failed ({g.Status}). " +
            "Run `az login` with a work/school account that has a Microsoft 365 Copilot license and delegated Graph consent for " +
            "Sites.Read.All, Mail.Read, People.Read.All, OnlineMeetingTranscript.Read.All, Chat.Read, ChannelMessage.Read.All, and ExternalItem.Read.All. " +
            "If you use a specific tenant, pass --m365-tenant or set EVALGEN_M365_TENANT_ID. " +
            $"Response: {g.ResponseBody}",
            error);
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

    private static string? TryGetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out JsonElement value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
