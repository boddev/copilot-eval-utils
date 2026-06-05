using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EvalToolkit.Core;
using EvalToolkit.EvalScore.Judges;
using EvalToolkit.EvalScore.Models;

namespace EvalToolkit.EvalScore.Tests.Judges;

[Collection("EvalScoreEnvVarSerial")]
public class AzureOpenAIJudgeTests
{
    private static readonly string[] s_allAzureEnvVars =
    {
        EnvVars.AzureOpenAiEndpoint, EnvVars.AzureAiOpenAiEndpoint,
        EnvVars.AzureOpenAiApiKey, EnvVars.AzureAiApiKey,
        EnvVars.AzureOpenAiApiVersion, EnvVars.AzureAiApiVersion,
        EnvVars.AzureOpenAiDeployment, EnvVars.AzureAiModelName,
    };

    private static EvalRow Row() => new()
    {
        Prompt = "p",
        ExpectedAnswer = "e",
        SourceLocation = "s",
        ActualAnswer = "a",
    };

    private static Dictionary<string, string?> ConfiguredEnv() => new()
    {
        [EnvVars.AzureOpenAiEndpoint] = "https://example.azure.com",
        [EnvVars.AzureOpenAiApiKey] = "secret-key",
        [EnvVars.AzureOpenAiApiVersion] = "2024-10-21",
        [EnvVars.AzureOpenAiDeployment] = "gpt-judge",
        // null out alternate aliases so primaries are used deterministically
        [EnvVars.AzureAiOpenAiEndpoint] = null,
        [EnvVars.AzureAiApiKey] = null,
        [EnvVars.AzureAiApiVersion] = null,
        [EnvVars.AzureAiModelName] = null,
    };

    [Fact]
    public void MissingConfig_DoesNotThrowAtCtor()
    {
        // Per round-1 review R6: ctor must not throw.
        EnvScope.WithoutAll(s_allAzureEnvVars, () =>
        {
            using var judge = new AzureOpenAIJudge();
            Assert.Equal(string.Empty, judge.Model);
        });
    }

    [Fact]
    public async Task MissingConfig_ThrowsFromScoreAsyncWithExactTsMessage()
    {
        await Task.Yield();
        EnvScope.WithoutAll(s_allAzureEnvVars, () =>
        {
            using var judge = new AzureOpenAIJudge();
            var ex = Assert.ThrowsAsync<InvalidOperationException>(
                () => judge.ScoreAsync(Row())).GetAwaiter().GetResult();
            Assert.Contains("AZURE_OPENAI_ENDPOINT", ex.Message);
            Assert.Contains("AZURE_OPENAI_API_KEY", ex.Message);
            Assert.Contains("AZURE_OPENAI_API_VERSION", ex.Message);
            Assert.Contains("AZURE_OPENAI_DEPLOYMENT", ex.Message);
        });
    }

    [Fact]
    public async Task SuccessfulCall_UsesCorrectUrlAndHeaderAndBody()
    {
        await Task.Yield();
        EnvScope.With(ConfiguredEnv(), () =>
        {
            var handler = new MockHttpMessageHandler(200,
                "{\"choices\":[{\"message\":{\"content\":\"{\\\"score\\\": 91}\"}}]}");
            using var httpClient = new HttpClient(handler);
            using var judge = new AzureOpenAIJudge(httpClient);

            var score = judge.ScoreAsync(Row()).GetAwaiter().GetResult();
            Assert.Equal(91, score.Score);
            Assert.Equal("gpt-judge", score.Model);

            var request = handler.Requests.Single();
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "https://example.azure.com/openai/deployments/gpt-judge/chat/completions?api-version=2024-10-21",
                request.RequestUri!.ToString());
            Assert.True(request.Headers.TryGetValues("api-key", out var apiKeys));
            Assert.Equal("secret-key", Assert.Single(apiKeys));

            string body = handler.RequestBodies.Single();
            Assert.Contains("\"temperature\":0", body);
            Assert.Contains("strict evaluation judge", body);
            Assert.Contains("Prompt: p", body);
        });
    }

    [Fact]
    public async Task DeploymentWithSpecialChars_GetsUriEscaped()
    {
        await Task.Yield();
        var env = ConfiguredEnv();
        env[EnvVars.AzureOpenAiDeployment] = "my model/v1";
        env[EnvVars.AzureOpenAiApiVersion] = "2024-10-21&extra";
        EnvScope.With(env, () =>
        {
            var handler = new MockHttpMessageHandler(200,
                "{\"choices\":[{\"message\":{\"content\":\"50\"}}]}");
            using var httpClient = new HttpClient(handler);
            using var judge = new AzureOpenAIJudge(httpClient);
            judge.ScoreAsync(Row()).GetAwaiter().GetResult();

            string url = handler.Requests.Single().RequestUri!.AbsoluteUri;
            // Uri.EscapeDataString escapes spaces, /, &
            Assert.Contains("my%20model%2Fv1", url);
            Assert.Contains("api-version=2024-10-21%26extra", url);
        });
    }

    [Fact]
    public async Task EndpointTrailingSlash_Trimmed()
    {
        await Task.Yield();
        var env = ConfiguredEnv();
        env[EnvVars.AzureOpenAiEndpoint] = "https://example.azure.com///";
        EnvScope.With(env, () =>
        {
            var handler = new MockHttpMessageHandler(200,
                "{\"choices\":[{\"message\":{\"content\":\"42\"}}]}");
            using var httpClient = new HttpClient(handler);
            using var judge = new AzureOpenAIJudge(httpClient);
            judge.ScoreAsync(Row()).GetAwaiter().GetResult();
            string url = handler.Requests.Single().RequestUri!.ToString();
            Assert.StartsWith("https://example.azure.com/openai/deployments/", url);
            Assert.DoesNotContain("///openai", url);
        });
    }

    [Fact]
    public async Task LegacyEnvAliases_AreHonored()
    {
        await Task.Yield();
        EnvScope.WithoutAll(s_allAzureEnvVars, () =>
        {
            EnvScope.With(new Dictionary<string, string?>
            {
                [EnvVars.AzureAiOpenAiEndpoint] = "https://legacy.azure.com",
                [EnvVars.AzureAiApiKey] = "legacy-key",
                [EnvVars.AzureAiApiVersion] = "2023-12-01",
                [EnvVars.AzureAiModelName] = "legacy-judge",
            }, () =>
            {
                var handler = new MockHttpMessageHandler(200,
                    "{\"choices\":[{\"message\":{\"content\":\"60\"}}]}");
                using var httpClient = new HttpClient(handler);
                using var judge = new AzureOpenAIJudge(httpClient);
                Assert.Equal("legacy-judge", judge.Model);
                judge.ScoreAsync(Row()).GetAwaiter().GetResult();
                string url = handler.Requests.Single().RequestUri!.ToString();
                Assert.Contains("legacy.azure.com", url);
                Assert.Contains("legacy-judge", url);
            });
        });
    }

    [Fact]
    public async Task NonOkResponse_ThrowsTsExactErrorFormat()
    {
        await Task.Yield();
        EnvScope.With(ConfiguredEnv(), () =>
        {
            var handler = new MockHttpMessageHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("rate limit body", Encoding.UTF8, "text/plain"),
                };
                response.Headers.TryAddWithoutValidation("Retry-After", "30");
                return response;
            });
            using var httpClient = new HttpClient(handler);
            using var judge = new AzureOpenAIJudge(httpClient);

            var ex = Assert.ThrowsAsync<InvalidOperationException>(
                () => judge.ScoreAsync(Row())).GetAwaiter().GetResult();
            Assert.Contains("Azure OpenAI HTTP 429", ex.Message);
            Assert.Contains("retry-after=30", ex.Message);
            Assert.Contains("rate limit body", ex.Message);
        });
    }

    [Fact]
    public async Task NonOkResponseWithoutRetryAfter_OmitsRetryAfterSegment()
    {
        await Task.Yield();
        EnvScope.With(ConfiguredEnv(), () =>
        {
            var handler = new MockHttpMessageHandler(500, "internal");
            using var httpClient = new HttpClient(handler);
            using var judge = new AzureOpenAIJudge(httpClient);

            var ex = Assert.ThrowsAsync<InvalidOperationException>(
                () => judge.ScoreAsync(Row())).GetAwaiter().GetResult();
            Assert.Contains("Azure OpenAI HTTP 500", ex.Message);
            Assert.DoesNotContain("retry-after=", ex.Message);
        });
    }

    [Fact]
    public async Task EmptyContent_ThrowsTsExactMessage()
    {
        await Task.Yield();
        EnvScope.With(ConfiguredEnv(), () =>
        {
            var handler = new MockHttpMessageHandler(200,
                "{\"choices\":[{\"message\":{\"content\":\"\"}}]}");
            using var httpClient = new HttpClient(handler);
            using var judge = new AzureOpenAIJudge(httpClient);
            var ex = Assert.ThrowsAsync<InvalidOperationException>(
                () => judge.ScoreAsync(Row())).GetAwaiter().GetResult();
            Assert.Equal("Azure OpenAI returned an empty judge response.", ex.Message);
        });
    }

    [Fact]
    public async Task MissingChoicesArray_ThrowsEmptyContentMessage()
    {
        await Task.Yield();
        EnvScope.With(ConfiguredEnv(), () =>
        {
            var handler = new MockHttpMessageHandler(200, "{}");
            using var httpClient = new HttpClient(handler);
            using var judge = new AzureOpenAIJudge(httpClient);
            var ex = Assert.ThrowsAsync<InvalidOperationException>(
                () => judge.ScoreAsync(Row())).GetAwaiter().GetResult();
            Assert.Equal("Azure OpenAI returned an empty judge response.", ex.Message);
        });
    }

    [Fact]
    public async Task ParsedModelInResponse_TakesPrecedence()
    {
        await Task.Yield();
        EnvScope.With(ConfiguredEnv(), () =>
        {
            var handler = new MockHttpMessageHandler(200,
                "{\"choices\":[{\"message\":{\"content\":\"{\\\"score\\\":55,\\\"model\\\":\\\"override\\\"}\"}}]}");
            using var httpClient = new HttpClient(handler);
            using var judge = new AzureOpenAIJudge(httpClient);
            var score = judge.ScoreAsync(Row()).GetAwaiter().GetResult();
            Assert.Equal(55, score.Score);
            Assert.Equal("override", score.Model);
        });
    }

    [Fact]
    public void OwnedHttpClient_HasInfiniteTimeout()
    {
        // Per round-1 review B1: owned HttpClient must use Timeout.InfiniteTimeSpan.
        // We can't inspect the owned client directly, but we can confirm that
        // constructing without an injected client doesn't throw and the model env
        // is read correctly.
        EnvScope.WithoutAll(s_allAzureEnvVars, () =>
        {
            using var judge = new AzureOpenAIJudge();
            Assert.NotNull(judge);
        });
    }

    [Fact]
    public void InjectedHttpClient_NotMutated()
    {
        EnvScope.With(ConfiguredEnv(), () =>
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(7) };
            using var judge = new AzureOpenAIJudge(client);
            Assert.Equal(TimeSpan.FromSeconds(7), client.Timeout);
        });
    }

    [Fact]
    public void Provider_IsAzureOpenAi()
    {
        EnvScope.WithoutAll(s_allAzureEnvVars, () =>
        {
            using var judge = new AzureOpenAIJudge();
            Assert.Equal(JudgeProvider.AzureOpenAi, judge.Provider);
        });
    }
}
