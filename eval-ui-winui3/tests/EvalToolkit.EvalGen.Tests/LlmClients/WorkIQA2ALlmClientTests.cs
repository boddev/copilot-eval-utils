using System.Net;
using System.Text;
using System.Text.Json;
using EvalToolkit.EvalGen.LlmClients;
using EvalToolkit.WorkIQ;

namespace EvalToolkit.EvalGen.Tests.LlmClients;

[Collection("EnvVarSerial")]
public sealed class WorkIQA2ALlmClientTests
{
    public sealed record Reply(string Status);

    private static string MakeTaskCompleted(string artifactText, string? contextId = null)
    {
        return JsonSerializer.Serialize(new
        {
            result = new
            {
                task = new
                {
                    contextId,
                    status = new { state = "TASK_STATE_COMPLETED" },
                    artifacts = new[]
                    {
                        new { parts = new[] { new { text = artifactText } } },
                    },
                },
            },
        });
    }

    [Fact]
    public async Task GenerateStructured_HappyPath()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(MakeTaskCompleted("{\"status\":\"ok\"}"), Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        await using var client = new WorkIQA2ALlmClient(
            new LlmClientOptions(),
            httpClient,
            new StaticTokenA2ATokenProvider("fake-token"));

        Reply reply = await client.GenerateStructuredAsync<Reply>("p", "s");

        Assert.Equal("ok", reply.Status);
        Assert.Single(handler.Requests);
        Assert.Equal("Bearer fake-token", handler.Requests[0].Headers.Authorization!.ToString());
        Assert.Equal("1.0", handler.Requests[0].Headers.GetValues("A2A-Version").Single());

        using JsonDocument body = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.Equal("SendMessage", body.RootElement.GetProperty("method").GetString());
        Assert.Equal("ROLE_USER", body.RootElement.GetProperty("params").GetProperty("message").GetProperty("role").GetString());
    }

    [Fact]
    public async Task JsonRpcError_NotRetried_AndUsesStatusZero()
    {
        int calls = 0;
        var handler = new MockHttpMessageHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { error = new { code = -32603, message = "bad" } }),
                    Encoding.UTF8, "application/json"),
            };
        });
        using var httpClient = new HttpClient(handler);
        await using var client = new WorkIQA2ALlmClient(
            new LlmClientOptions { MaxAttempts = 3, BackoffBaseMs = 0 },
            httpClient,
            new StaticTokenA2ATokenProvider("t"));

        WorkIqA2aLlmException ex = await Assert.ThrowsAsync<WorkIqA2aLlmException>(async () =>
            await client.GenerateStructuredAsync<Reply>("p", "s"));

        Assert.Equal(0, ex.Status);
        Assert.Contains("-32603", ex.Message);
        Assert.Equal(1, calls); // not retried (status==0 → non-retryable)
    }

    [Fact]
    public async Task Http500_IsRetried()
    {
        int calls = 0;
        var handler = new MockHttpMessageHandler(_ =>
        {
            calls++;
            if (calls < 3)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("server boom", Encoding.UTF8, "text/plain"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(MakeTaskCompleted("{\"status\":\"final\"}"), Encoding.UTF8, "application/json"),
            };
        });
        using var httpClient = new HttpClient(handler);
        await using var client = new WorkIQA2ALlmClient(
            new LlmClientOptions { MaxAttempts = 5, BackoffBaseMs = 0 },
            httpClient,
            new StaticTokenA2ATokenProvider("t"));

        Reply reply = await client.GenerateStructuredAsync<Reply>("p", "s");

        Assert.Equal("final", reply.Status);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task MissingResultTask_Throws_OperatorMessage()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        await using var client = new WorkIQA2ALlmClient(
            new LlmClientOptions { MaxAttempts = 1 },
            httpClient,
            new StaticTokenA2ATokenProvider("t"));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.GenerateStructuredAsync<Reply>("p", "s"));
        Assert.Equal("Work IQ A2A response is missing result.task", ex.Message);
    }

    [Fact]
    public async Task NonCompletedState_ThrowsWithDetail()
    {
        string body = JsonSerializer.Serialize(new
        {
            result = new
            {
                task = new
                {
                    status = new
                    {
                        state = "TASK_STATE_INPUT_REQUIRED",
                        message = new
                        {
                            parts = new[] { new { text = "need more info" } },
                        },
                    },
                },
            },
        });
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        await using var client = new WorkIQA2ALlmClient(
            new LlmClientOptions { MaxAttempts = 1 },
            httpClient,
            new StaticTokenA2ATokenProvider("t"));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.GenerateStructuredAsync<Reply>("p", "s"));
        Assert.Equal("Work IQ A2A task ended in state TASK_STATE_INPUT_REQUIRED: need more info", ex.Message);
    }

    [Fact]
    public async Task NoTextArtifact_Throws()
    {
        string body = JsonSerializer.Serialize(new
        {
            result = new
            {
                task = new
                {
                    status = new { state = "TASK_STATE_COMPLETED" },
                    artifacts = Array.Empty<object>(),
                },
            },
        });
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        await using var client = new WorkIQA2ALlmClient(
            new LlmClientOptions { MaxAttempts = 1 },
            httpClient,
            new StaticTokenA2ATokenProvider("t"));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.GenerateStructuredAsync<Reply>("p", "s"));
        Assert.Equal("Work IQ A2A task completed but contained no text artifact", ex.Message);
    }

    [Fact]
    public async Task ContextIdIsCarriedForwardOnSecondCall()
    {
        var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(MakeTaskCompleted("{\"status\":\"one\"}", contextId: "ctx-123"), Encoding.UTF8, "application/json"),
            });
        using var httpClient = new HttpClient(handler);
        await using var client = new WorkIQA2ALlmClient(
            new LlmClientOptions(),
            httpClient,
            new StaticTokenA2ATokenProvider("t"));

        await client.GenerateStructuredAsync<Reply>("p1", "s");
        await client.GenerateStructuredAsync<Reply>("p2", "s");

        Assert.Equal(2, handler.Requests.Count);
        using JsonDocument body2 = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("ctx-123",
            body2.RootElement.GetProperty("params").GetProperty("message").GetProperty("contextId").GetString());
    }
}
