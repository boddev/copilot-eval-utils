using System.Net;
using System.Text;

namespace EvalToolkit.EvalScore.Tests.Judges;

/// <summary>
/// Test seam <see cref="HttpMessageHandler"/> for the Azure OpenAI judge
/// tests. Records all requests and responds via a user-supplied handler.
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
