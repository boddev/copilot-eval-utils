using System.Net;
using System.Text;
using EvalToolkit.EvalGen.Sources;
using EvalToolkit.EvalGen.Tests.LlmClients;

namespace EvalToolkit.EvalGen.Tests.Sources;

public sealed class WebSourceTests
{
    private static MockHttpMessageHandler Html(Func<HttpRequestMessage, string?> body) => new(req =>
    {
        var content = body(req);
        if (content is null) return new HttpResponseMessage(HttpStatusCode.NotFound);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/html"),
        };
    });

    [Fact]
    public async Task ExtractsTitleHeadingsAndParagraphsFromSinglePage()
    {
        var html = """
            <html><head><title>Sample Page</title></head>
            <body>
                <h1>Top</h1>
                <h2>Subhead</h2>
                <p>This paragraph has more than twenty characters of content.</p>
                <p>short</p>
            </body></html>
            """;
        var handler = Html(_ => html);
        using var http = new HttpClient(handler);
        var source = new WebSource(new WebSourceOptions { Url = "https://example.com/page" }, http);

        var result = await source.FetchAsync();
        var record = result.Records.Single();

        Assert.Equal("Sample Page", record["title"]);
        Assert.Contains("Top", (string)record["headings"]!);
        Assert.Contains("Subhead", (string)record["headings"]!);
        Assert.Contains("twenty characters", (string)record["content"]!);
        Assert.DoesNotContain("short", (string)record["content"]!);
        Assert.Equal("https://example.com/page", record["_source_url"]);
    }

    [Fact]
    public async Task ExtractsTableRowsWhenPresent()
    {
        var html = """
            <html><body>
            <table>
              <tr><th>Name</th><th>Score</th></tr>
              <tr><td>Ada</td><td>95</td></tr>
              <tr><td>Bob</td><td>88</td></tr>
            </table>
            </body></html>
            """;
        var handler = Html(_ => html);
        using var http = new HttpClient(handler);
        var source = new WebSource(new WebSourceOptions { Url = "https://example.com" }, http);

        var result = await source.FetchAsync();
        Assert.Equal(2, result.Records.Count);
        Assert.Equal("Ada", result.Records[0]["Name"]);
        Assert.Equal("95", result.Records[0]["Score"]);
    }

    [Fact]
    public async Task CrawlsSameDomainLinksUpToMaxPages()
    {
        var page1 = """
            <html><body>
              <p>Page 1 content goes here with enough characters.</p>
              <a href="/page2">Page 2</a>
              <a href="/page3">Page 3</a>
              <a href="https://other.com/x">External</a>
            </body></html>
            """;
        var page2 = "<html><body><p>Page 2 content goes here with enough characters.</p></body></html>";
        var page3 = "<html><body><p>Page 3 content goes here with enough characters.</p></body></html>";

        var visited = new List<string>();
        var handler = Html(req =>
        {
            visited.Add(req.RequestUri!.AbsolutePath);
            return req.RequestUri.AbsolutePath switch
            {
                "/" => page1,
                "/page2" => page2,
                "/page3" => page3,
                _ => null,
            };
        });
        using var http = new HttpClient(handler);
        var source = new WebSource(new WebSourceOptions { Url = "https://example.com/", MaxPages = 5 }, http);

        var result = await source.FetchAsync();
        Assert.Equal(3, result.Records.Count);
        Assert.DoesNotContain(visited, p => p.StartsWith("/x", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopsAtMaxPages()
    {
        var page1 = """
            <html><body>
              <p>Page 1 content goes here with enough characters.</p>
              <a href="/page2">Two</a>
              <a href="/page3">Three</a>
            </body></html>
            """;
        var page2 = "<html><body><p>Page 2 content goes here with enough characters.</p></body></html>";
        var page3 = "<html><body><p>Page 3 content goes here with enough characters.</p></body></html>";

        var handler = Html(req => req.RequestUri!.AbsolutePath switch
        {
            "/" => page1,
            "/page2" => page2,
            "/page3" => page3,
            _ => null,
        });
        using var http = new HttpClient(handler);
        var source = new WebSource(new WebSourceOptions { Url = "https://example.com/", MaxPages = 2 }, http);

        var result = await source.FetchAsync();
        Assert.Equal(2, result.Records.Count);
    }

    [Fact]
    public async Task ThrowsWhenNoContentExtracted()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        var source = new WebSource(new WebSourceOptions { Url = "https://example.com" }, http);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchAsync());
    }

    [Fact]
    public async Task SkipsNonHtmlResponses()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not html", Encoding.UTF8, "application/octet-stream"),
        });
        using var http = new HttpClient(handler);
        var source = new WebSource(new WebSourceOptions { Url = "https://example.com" }, http);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchAsync());
    }
}
