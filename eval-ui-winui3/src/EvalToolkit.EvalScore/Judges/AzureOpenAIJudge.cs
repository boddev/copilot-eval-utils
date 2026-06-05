using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EvalToolkit.Core;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Judges;

/// <summary>
/// Azure OpenAI judge. Ports TS <c>AzureOpenAIJudge</c> from
/// <c>eval-score/node/src/judge-providers.ts</c>.
///
/// <para>Env reading is at construction time (TS too) but the missing-config
/// check fires inside <see cref="ScoreAsync"/> (matches TS — per round-1
/// review R6).</para>
///
/// <para>Wire shape (TS-faithful):</para>
/// <list type="bullet">
///   <item>URL: <c>{endpoint}/openai/deployments/{Uri-encoded deployment}/chat/completions?api-version={Uri-encoded version}</c>.</item>
///   <item>Headers: <c>Content-Type: application/json</c>, <c>api-key: {key}</c>.</item>
///   <item>Body: <c>{ temperature: 0, messages: [system, user] }</c> with
///         the user content rendered from <see cref="ScoringPromptBuilder.Build"/>
///         using <c>jsonResponse: true</c>.</item>
///   <item>No retry (TS makes a single <c>fetch</c> call). Errors surface as
///         <see cref="InvalidOperationException"/> with the TS message
///         shape <c>"Azure OpenAI HTTP {status}[ retry-after=...]: {body}"</c>.</item>
/// </list>
///
/// <para>HTTP client lifetime: when no <see cref="HttpClient"/> is
/// supplied the judge constructs an owned one with
/// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> (matches TS
/// no-timeout — per round-1 review B1). When an external client is
/// injected (tests), the judge does NOT mutate its
/// <see cref="HttpClient.Timeout"/>.</para>
/// </summary>
public sealed class AzureOpenAIJudge : IJudge, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _apiVersion;

    public AzureOpenAIJudge(HttpClient? httpClient = null)
    {
        if (httpClient is null)
        {
            _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }

        _endpoint = TrimTrailingSlashes(
            Environment.GetEnvironmentVariable(EnvVars.AzureOpenAiEndpoint)
            ?? Environment.GetEnvironmentVariable(EnvVars.AzureAiOpenAiEndpoint)
            ?? string.Empty);
        _apiKey =
            Environment.GetEnvironmentVariable(EnvVars.AzureOpenAiApiKey)
            ?? Environment.GetEnvironmentVariable(EnvVars.AzureAiApiKey)
            ?? string.Empty;
        _apiVersion =
            Environment.GetEnvironmentVariable(EnvVars.AzureOpenAiApiVersion)
            ?? Environment.GetEnvironmentVariable(EnvVars.AzureAiApiVersion)
            ?? string.Empty;
        Model =
            Environment.GetEnvironmentVariable(EnvVars.AzureOpenAiDeployment)
            ?? Environment.GetEnvironmentVariable(EnvVars.AzureAiModelName)
            ?? string.Empty;
    }

    public JudgeProvider Provider => JudgeProvider.AzureOpenAi;

    /// <summary>Azure deployment name (used as the model label).</summary>
    public string Model { get; }

    string? IJudge.Model => string.IsNullOrEmpty(Model) ? null : Model;

    public async Task<JudgeScore> ScoreAsync(EvalRow row, EvaluatorName evaluator = EvaluatorName.Similarity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        // TS error message — preserved verbatim.
        if (string.IsNullOrEmpty(_endpoint) || string.IsNullOrEmpty(_apiKey)
            || string.IsNullOrEmpty(_apiVersion) || string.IsNullOrEmpty(Model))
        {
            throw new InvalidOperationException(
                "Azure OpenAI judging requires AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, "
                + "AZURE_OPENAI_API_VERSION, and AZURE_OPENAI_DEPLOYMENT.");
        }

        string url = $"{_endpoint}/openai/deployments/{Uri.EscapeDataString(Model)}/chat/completions?api-version={Uri.EscapeDataString(_apiVersion)}";

        var payload = new
        {
            temperature = 0,
            messages = new object[]
            {
                new { role = "system", content = "You are a strict evaluation judge. Return only valid JSON." },
                new { role = "user", content = ScoringPromptBuilder.Build(row, evaluator, jsonResponse: true) },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.TryAddWithoutValidation("api-key", _apiKey);

        using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string? retryAfter = response.Headers.RetryAfter is RetryConditionHeaderValue rc
                ? rc.ToString()
                : null;
            string body = string.Empty;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // TS: `.catch(() => '')`
            }
            string retrySegment = string.IsNullOrEmpty(retryAfter) ? string.Empty : $" retry-after={retryAfter}";
            string message = $"Azure OpenAI HTTP {(int)response.StatusCode}{retrySegment}: {body}".Trim();
            throw new InvalidOperationException(message);
        }

        string content = await ExtractContentAsync(response, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(content))
        {
            throw new InvalidOperationException("Azure OpenAI returned an empty judge response.");
        }

        JudgeScore parsed = JudgeScoreParser.Parse(content);
        return parsed.Model is null ? parsed with { Model = Model } : parsed;
    }

    private static async Task<string> ExtractContentAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // TS: raw.choices?.[0]?.message?.content
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return string.Empty;
        if (!doc.RootElement.TryGetProperty("choices", out JsonElement choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return string.Empty;
        }
        JsonElement first = choices[0];
        if (first.ValueKind != JsonValueKind.Object
            || !first.TryGetProperty("message", out JsonElement message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out JsonElement contentEl)
            || contentEl.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }
        return contentEl.GetString() ?? string.Empty;
    }

    private static string TrimTrailingSlashes(string s)
    {
        // TS: .replace(/\/+$/, '')
        int end = s.Length;
        while (end > 0 && s[end - 1] == '/') end--;
        return end == s.Length ? s : s[..end];
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
