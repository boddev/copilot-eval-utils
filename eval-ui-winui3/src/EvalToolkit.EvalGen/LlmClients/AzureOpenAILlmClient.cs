using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.LlmClients;

/// <summary>
/// Azure OpenAI provider. Ports <c>AzureOpenAIClient</c> from
/// <c>eval-gen/src/llm-client.ts</c>.
///
/// <para>Per-GPT-5.5 review:</para>
/// <list type="bullet">
///   <item>NO retry — TS Azure path is a single fetch.</item>
///   <item>NO structured-JSON parser — TS calls <c>JSON.parse(content)</c>
///         directly because <c>response_format: json_object</c> guarantees
///         a JSON-object response.</item>
///   <item>URL/api-version/temperature/max_tokens preserved byte-for-byte.</item>
/// </list>
/// </summary>
public sealed class AzureOpenAILlmClient : ILlmClient
{
    private const string ApiVersion = "2024-10-21";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _model;

    private static readonly JsonSerializerOptions s_serializerOptions = new(JsonSerializerDefaults.Web);

    public AzureOpenAILlmClient(LlmClientOptions? options = null, HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;

        _endpoint = (options?.Endpoint
            ?? Environment.GetEnvironmentVariable(EnvVars.EvalGenAzureOpenAiEndpoint)
            ?? string.Empty).TrimEnd('/');

        _apiKey = options?.ApiKey
            ?? Environment.GetEnvironmentVariable(EnvVars.EvalGenAzureOpenAiKey)
            ?? Environment.GetEnvironmentVariable(EnvVars.AzureOpenAiApiKey)
            ?? string.Empty;

        _model = options?.Model
            ?? Environment.GetEnvironmentVariable(EnvVars.EvalGenModel)
            ?? "gpt-4o";

        if (string.IsNullOrEmpty(_endpoint))
        {
            throw new InvalidOperationException(
                "Azure OpenAI endpoint required. Set EVALGEN_AZURE_OPENAI_ENDPOINT or pass endpoint option.");
        }
        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new InvalidOperationException(
                "Azure OpenAI API key required. Set EVALGEN_AZURE_OPENAI_KEY or pass apiKey option.");
        }
    }

    public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<T> GenerateStructuredAsync<T>(string prompt, string schemaDescription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(schemaDescription);

        string url = $"{_endpoint}/openai/deployments/{_model}/chat/completions?api-version={ApiVersion}";

        var body = new
        {
            messages = new object[]
            {
                new { role = "system", content = $"You are a precise data analysis assistant. Always respond with valid JSON matching the requested schema. {schemaDescription}" },
                new { role = "user", content = prompt },
            },
            temperature = 0.7,
            max_tokens = 16000,
            response_format = new { type = "json_object" },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, s_serializerOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("api-key", _apiKey);

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Azure OpenAI API error ({(int)response.StatusCode}): {errorText}");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("choices", out JsonElement choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Azure OpenAI returned empty response");
        }

        if (!choices[0].TryGetProperty("message", out JsonElement message)
            || !message.TryGetProperty("content", out JsonElement content)
            || content.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Azure OpenAI returned empty response");
        }

        string text = content.GetString() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            throw new InvalidOperationException("Azure OpenAI returned empty response");
        }

        // TS uses JSON.parse(content) directly — no structured-extraction fallback.
        T? parsed = JsonSerializer.Deserialize<T>(text, s_serializerOptions);
        if (parsed is null)
        {
            throw new InvalidOperationException("Azure OpenAI returned a null JSON object");
        }
        return parsed;
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
