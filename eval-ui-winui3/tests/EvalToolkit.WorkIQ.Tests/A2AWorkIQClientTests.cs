using System.Net;
using System.Text;
using EvalToolkit.WorkIQ;

namespace EvalToolkit.WorkIQ.Tests;

public class A2AWorkIQClientTests
{
    [Fact]
    public async Task AskWithMetadataAsync_PostsA2APayloadAndExtractsTextMetadata()
    {
        var handler = new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") },
            new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") },
            JsonResponse("{\"result\":{\"message\":{\"parts\":[{\"text\":\"hello\"}]},\"contextId\":\"conv-1\",\"citations\":[{\"title\":\"Doc\",\"url\":\"https://example\"}]}}"));
        await using var client = new A2AWorkIQClient(new A2AWorkIQClientOptions
        {
            Endpoint = "https://workiq.example/root/",
            AccessToken = "token-1",
            HttpClient = new HttpClient(handler),
            RetryOptions = NoRetry(),
        });

        WorkIQResponse response = await client.AskWithMetadataAsync(
            "question",
            new WorkIQAskOptions(AgentId: "agent-1", ConversationId: "conv-0"),
            CancellationToken.None);

        Assert.Equal("hello", response.Text);
        Assert.Equal("conv-1", response.ConversationId);
        Citation citation = Assert.Single(response.Citations!);
        Assert.Equal("Doc", citation.Title);
        Assert.Equal("https://example", citation.Url);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        Assert.Equal("https://workiq.example/root/agent-1", handler.Requests[2].RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Requests[2].Headers.Authorization!.Scheme);
        Assert.Equal("token-1", handler.Requests[2].Headers.Authorization!.Parameter);
        Assert.Contains("\"contextId\":\"conv-0\"", handler.Bodies[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskWithMetadataAsync_RateLimitApologyRetriesAsSynthetic429()
    {
        var handler = new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") },
            new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") },
            JsonResponse("{\"result\":{\"parts\":[{\"text\":\"You have reached your request limit. Try again in a little while.\"}]}}"),
            JsonResponse("{\"result\":{\"parts\":[{\"text\":\"after retry\"}]}}"));
        await using var client = new A2AWorkIQClient(new A2AWorkIQClientOptions
        {
            Endpoint = "https://workiq.example",
            AccessToken = "token-1",
            HttpClient = new HttpClient(handler),
            RetryOptions = new WorkIQRetryOptions
            {
                MaxAttempts = 2,
                BackoffBaseMs = 0,
                BackoffMaxMs = 0,
                Jitter = () => 0,
            },
        });

        WorkIQResponse response = await client.AskWithMetadataAsync(
            "question",
            new WorkIQAskOptions(AgentId: "agent-1"),
            CancellationToken.None);

        Assert.Equal("after retry", response.Text);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task AskWithMetadataAsync_RefreshesCommandTokenAfter401()
    {
        var provider = new RefreshingProvider();
        var handler = new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") },
            new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") },
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("denied", Encoding.UTF8, "text/plain") },
            JsonResponse("{\"result\":{\"parts\":[{\"text\":\"ok\"}]}}"));
        await using var client = new A2AWorkIQClient(new A2AWorkIQClientOptions
        {
            Endpoint = "https://workiq.example",
            TokenProvider = provider,
            AuthMode = "command",
            HttpClient = new HttpClient(handler),
            RetryOptions = NoRetry(),
        });

        WorkIQResponse response = await client.AskWithMetadataAsync(
            "question",
            new WorkIQAskOptions(AgentId: "agent-1"),
            CancellationToken.None);

        Assert.Equal("ok", response.Text);
        Assert.Equal(4, provider.Calls);
        Assert.True(provider.ForceRefreshSeen);
        Assert.Equal("old-token", handler.Requests[2].Headers.Authorization!.Parameter);
        Assert.Equal("new-token", handler.Requests[3].Headers.Authorization!.Parameter);
    }

    private static WorkIQRetryOptions NoRetry()
    {
        return new WorkIQRetryOptions
        {
            MaxAttempts = 1,
            BackoffBaseMs = 0,
            BackoffMaxMs = 0,
            Jitter = () => 0,
        };
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public QueueHttpMessageHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(CloneRequest(request));
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No queued response.");
            }
            return _responses.Dequeue();
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return clone;
        }
    }

    private sealed class RefreshingProvider : IA2ATokenProvider
    {
        public int Calls { get; private set; }

        public bool ForceRefreshSeen { get; private set; }

        public Task<string> GetTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            Calls++;
            ForceRefreshSeen |= forceRefresh;
            return Task.FromResult(forceRefresh ? "new-token" : "old-token");
        }
    }
}
