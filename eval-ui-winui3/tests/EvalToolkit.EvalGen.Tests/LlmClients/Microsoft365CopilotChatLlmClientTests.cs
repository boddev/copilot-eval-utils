using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EvalToolkit.Core;
using EvalToolkit.EvalGen.LlmClients;

namespace EvalToolkit.EvalGen.Tests.LlmClients;

[Collection("EnvVarSerial")]
public sealed class Microsoft365CopilotChatLlmClientTests
{
    public sealed record Reply(string Status);

    private static string MakeCreateConvoResponse(string id)
        => JsonSerializer.Serialize(new { id });

    private static string MakeChatResponse(string text)
        => JsonSerializer.Serialize(new { messages = new[] { new { text } } });

    [Fact]
    public async Task GenerateStructured_HappyPath_CreatesConvoAndPostsChat()
    {
        int call = 0;
        var handler = new MockHttpMessageHandler(req =>
        {
            call++;
            if (call == 1)
            {
                Assert.Equal("https://graph.microsoft.com/beta/copilot/conversations", req.RequestUri!.ToString());
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(MakeCreateConvoResponse("convo-1"), Encoding.UTF8, "application/json"),
                };
            }
            Assert.Equal("https://graph.microsoft.com/beta/copilot/conversations/convo-1/chat", req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(MakeChatResponse("{\"status\":\"good\"}"), Encoding.UTF8, "application/json"),
            };
        });

        using var httpClient = new HttpClient(handler);
        await using var client = new Microsoft365CopilotChatLlmClient(
            new LlmClientOptions
            {
                M365AccessToken = "tok",
                M365TimeZone = "America/Los_Angeles",
                MaxAttempts = 1,
            },
            httpClient,
            new RecordingRunner(_ => throw new InvalidOperationException("az should not be called")));

        Reply reply = await client.GenerateStructuredAsync<Reply>("p", "s");

        Assert.Equal("good", reply.Status);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Bearer tok", handler.Requests[0].Headers.Authorization!.ToString());

        using JsonDocument chatBody = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("America/Los_Angeles", chatBody.RootElement.GetProperty("locationHint").GetProperty("timeZone").GetString());
    }

    [Fact]
    public async Task CreateConvoReturnsNoId_ThrowsButIsRetryable()
    {
        var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        using var httpClient = new HttpClient(handler);
        await using var client = new Microsoft365CopilotChatLlmClient(
            new LlmClientOptions { M365AccessToken = "tok", MaxAttempts = 1 },
            httpClient,
            new RecordingRunner(_ => string.Empty));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.GenerateStructuredAsync<Reply>("p", "s"));
        Assert.Equal("Microsoft 365 Copilot Chat API did not return a conversation id", ex.Message);
    }

    [Fact]
    public async Task Auth_Returns401_NoStaticToken_InvokesAzLoginAndRetriesOnce()
    {
        int convoCall = 0;
        var azCommands = new List<string>(); // GPT-5.5 R1: assert exact sequence.
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString().EndsWith("/conversations", StringComparison.Ordinal))
            {
                convoCall++;
                if (convoCall == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent("nope", Encoding.UTF8, "text/plain"),
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(MakeCreateConvoResponse("c1"), Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(MakeChatResponse("{\"status\":\"after-login\"}"), Encoding.UTF8, "application/json"),
            };
        });

        await EnvScope.WithoutAsync(
            EnvVars.EvalGenM365CopilotToken,
            EnvVars.EvalGenM365TenantId,
            async () =>
            {
                using var httpClient = new HttpClient(handler);
                await using var client = new Microsoft365CopilotChatLlmClient(
                    new LlmClientOptions { MaxAttempts = 1 },
                    httpClient,
                    new RecordingRunner(inv =>
                    {
                        Assert.Equal("az", inv.Command);
                        azCommands.Add(inv.Arguments[0]); // first arg differentiates subcommand
                        return "fake-token-from-az\n";
                    }));

                Reply reply = await client.GenerateStructuredAsync<Reply>("p", "s");

                Assert.Equal("after-login", reply.Status);
                Assert.Equal(2, convoCall);
                Assert.Equal(new[] { "account", "login", "account" }, azCommands);
            });
    }

    [Fact]
    public async Task ChatReturns401_DoesNotTriggerAzLogin()
    {
        // GPT-5.5 round-2 nice-to-have: narrow-scope retry is conversation-only.
        // A /chat 401 must NOT call az login.
        int azCalls = 0;
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString().EndsWith("/conversations", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(MakeCreateConvoResponse("c1"), Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("nope", Encoding.UTF8, "text/plain"),
            };
        });

        using var httpClient = new HttpClient(handler);
        await using var client = new Microsoft365CopilotChatLlmClient(
            new LlmClientOptions { M365AccessToken = "tok", MaxAttempts = 1 },
            httpClient,
            new RecordingRunner(_ =>
            {
                azCalls++;
                return string.Empty;
            }));

        GraphApiException ex = await Assert.ThrowsAsync<GraphApiException>(async () =>
            await client.GenerateStructuredAsync<Reply>("p", "s"));
        Assert.Equal(401, ex.Status);
        Assert.Equal(0, azCalls);
    }

    [Fact]
    public async Task Auth_Returns401_WithStaticToken_DoesNotRunAzLogin_Throws()
    {
        var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("no", Encoding.UTF8, "text/plain"),
            });
        using var httpClient = new HttpClient(handler);
        await using var client = new Microsoft365CopilotChatLlmClient(
            new LlmClientOptions { M365AccessToken = "static", MaxAttempts = 1 },
            httpClient,
            new RecordingRunner(_ => throw new InvalidOperationException("az must not be invoked")));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.GenerateStructuredAsync<Reply>("p", "s"));
        Assert.Contains("Microsoft 365 Copilot authentication failed (403)", ex.Message);
    }

    [Fact]
    public async Task EmptyChatMessages_Throws()
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString().EndsWith("/conversations", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(MakeCreateConvoResponse("c1"), Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"messages\":[]}", Encoding.UTF8, "application/json"),
            };
        });
        using var httpClient = new HttpClient(handler);
        await using var client = new Microsoft365CopilotChatLlmClient(
            new LlmClientOptions { M365AccessToken = "tok", MaxAttempts = 1 },
            httpClient,
            new RecordingRunner(_ => string.Empty));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.GenerateStructuredAsync<Reply>("p", "s"));
        Assert.Equal("Microsoft 365 Copilot Chat API returned no message text", ex.Message);
    }
}
