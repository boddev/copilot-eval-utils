using System.Net;
using System.Text;
using System.Text.Json;
using EvalToolkit.EvalGen.LlmClients;

namespace EvalToolkit.EvalGen.Tests.LlmClients;

[Collection("EnvVarSerial")]
public sealed class AzureOpenAILlmClientTests
{
    public sealed record Reply(string Status);

    private static string MakeChatCompletion(string contentJson)
    {
        return JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { content = contentJson } },
            },
        });
    }

    [Fact]
    public async Task SendsExpectedRequestAndParsesResponse()
    {
        var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(MakeChatCompletion("{\"status\":\"ok\"}"), Encoding.UTF8, "application/json"),
            });
        using var httpClient = new HttpClient(handler);
        await using var client = new AzureOpenAILlmClient(
            new LlmClientOptions
            {
                Endpoint = "https://example.openai.azure.com/",
                ApiKey = "k",
                Model = "gpt-4o",
            },
            httpClient);

        Reply reply = await client.GenerateStructuredAsync<Reply>("p", "s");

        Assert.Equal("ok", reply.Status);
        Assert.Single(handler.Requests);
        Assert.Equal(
            "https://example.openai.azure.com/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21",
            handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("k", handler.Requests[0].Headers.GetValues("api-key").Single());
    }

    [Fact]
    public async Task FailsOnNon2xx_NoRetry()
    {
        int calls = 0;
        var handler = new MockHttpMessageHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("oops", Encoding.UTF8, "text/plain"),
            };
        });
        using var httpClient = new HttpClient(handler);
        await using var client = new AzureOpenAILlmClient(
            new LlmClientOptions
            {
                Endpoint = "https://example.openai.azure.com/",
                ApiKey = "k",
                Model = "gpt-4o",
            },
            httpClient);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.GenerateStructuredAsync<Reply>("p", "s"));

        Assert.Equal(1, calls);    // No retry, per TS source.
        Assert.Contains("Azure OpenAI API error (500)", ex.Message);
    }

    [Fact]
    public async Task EmptyChoices_Throws()
    {
        var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[]}", Encoding.UTF8, "application/json"),
            });
        using var httpClient = new HttpClient(handler);
        await using var client = new AzureOpenAILlmClient(
            new LlmClientOptions
            {
                Endpoint = "https://example.openai.azure.com/",
                ApiKey = "k",
                Model = "gpt-4o",
            },
            httpClient);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.GenerateStructuredAsync<Reply>("p", "s"));
        Assert.Equal("Azure OpenAI returned empty response", ex.Message);
    }

    [Fact]
    public async Task RequestBodyShapeMatchesTs()
    {
        var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(MakeChatCompletion("{}"), Encoding.UTF8, "application/json"),
            });
        using var httpClient = new HttpClient(handler);
        await using var client = new AzureOpenAILlmClient(
            new LlmClientOptions
            {
                Endpoint = "https://example.openai.azure.com/",
                ApiKey = "k",
                Model = "gpt-4o",
            },
            httpClient);

        await client.GenerateStructuredAsync<Reply>("the prompt", "the schema");

        using JsonDocument body = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.Equal(0.7, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(16000, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());

        JsonElement messages = body.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains("the schema", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("the prompt", messages[1].GetProperty("content").GetString());
    }
}
