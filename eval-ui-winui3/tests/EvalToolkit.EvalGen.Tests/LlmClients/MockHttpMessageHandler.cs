using System.Net;
using System.Text;

namespace EvalToolkit.EvalGen.Tests.LlmClients;

/// <summary>
/// Test seam <see cref="HttpMessageHandler"/> for unit tests over LLM
/// clients that use <see cref="HttpClient"/>. Records all requests and
/// responds via a user-supplied handler delegate.
/// </summary>
internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> RequestBodies { get; } = [];

    public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    public MockHttpMessageHandler(int status, string body, string contentType = "application/json")
    {
        _handler = _ => new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (request.Content is not null)
        {
            RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        else
        {
            RequestBodies.Add(string.Empty);
        }
        return _handler(request);
    }
}
