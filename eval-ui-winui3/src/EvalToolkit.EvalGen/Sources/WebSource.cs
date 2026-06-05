using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Sources;

/// <summary>
/// Port of <c>eval-gen/src/sources/web-source.ts</c>. Crawls static / server-
/// rendered web pages on a single domain, extracting tables when present and
/// falling back to title + headings + paragraphs otherwise.
/// <para>
/// AngleSharp replaces cheerio for HTML parsing; the standards-compliant
/// parser handles the same selector/text/attribute operations the TS source
/// relied on. JavaScript-rendered SPAs remain unsupported — same caveat as TS.
/// </para>
/// </summary>
public sealed class WebSource : IDataSource, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly WebSourceOptions _options;

    public WebSource(WebSourceOptions options, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Url);
        _options = options;
        _http = http ?? new HttpClient();
        _ownsHttp = http is null;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    public async Task<SourceResult> FetchAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var records = new List<IReadOnlyDictionary<string, object?>>();
        var queue = new Queue<string>();
        queue.Enqueue(_options.Url);

        var parserContext = BrowsingContext.New(Configuration.Default);
        var htmlParser = parserContext.GetService<IHtmlParser>()
            ?? throw new InvalidOperationException("AngleSharp HTML parser not available");

        while (queue.Count > 0 && visited.Count < _options.MaxPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = queue.Dequeue();
            if (!visited.Add(url)) continue;

            try
            {
                using var request = BuildRequest(HttpMethod.Get, url);
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;

                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)) continue;

                var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var document = htmlParser.ParseDocument(html);

                ExtractPageContent(document, url, records);
                EnqueueLinks(document, url, visited, queue);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                progress?.Report($"  Warning: Failed to crawl {url}: {ex.Message}");
            }
        }

        if (records.Count == 0)
        {
            throw new InvalidOperationException("No content extracted from web pages");
        }

        return new SourceResult(records, InputFormat.Json, new Uri(_options.Url).Host);
    }

    private static void ExtractPageContent(IDocument document, string url, List<IReadOnlyDictionary<string, object?>> records)
    {
        var title = document.Title?.Trim() ?? string.Empty;
        var headings = document.QuerySelectorAll("h1, h2, h3")
            .Select(el => (el.TextContent ?? string.Empty).Trim())
            .Where(t => t.Length > 0)
            .ToList();
        var paragraphs = document.QuerySelectorAll("p")
            .Select(el => (el.TextContent ?? string.Empty).Trim())
            .Where(p => p.Length > 20)
            .ToList();

        var hasTableData = false;
        foreach (var table in document.QuerySelectorAll("table"))
        {
            var headers = table.QuerySelectorAll("th")
                .Select(th => (th.TextContent ?? string.Empty).Trim())
                .ToList();
            if (headers.Count == 0) continue;

            foreach (var row in table.QuerySelectorAll("tr"))
            {
                var cells = row.QuerySelectorAll("td")
                    .Select(td => (td.TextContent ?? string.Empty).Trim())
                    .ToList();
                if (cells.Count != headers.Count) continue;

                var record = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["_source_url"] = url,
                };
                for (int i = 0; i < headers.Count; i++)
                {
                    record[headers[i]] = cells[i];
                }
                records.Add(record);
                hasTableData = true;
            }
        }

        if (!hasTableData)
        {
            records.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["_source_url"] = url,
                ["title"] = title,
                ["headings"] = string.Join("; ", headings),
                ["content"] = string.Join("\n\n", paragraphs.Take(10)),
            });
        }
    }

    private static void EnqueueLinks(IDocument document, string currentUrl, HashSet<string> visited, Queue<string> queue)
    {
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var baseUri)) return;
        foreach (var anchor in document.QuerySelectorAll("a[href]").OfType<IHtmlAnchorElement>())
        {
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (!Uri.TryCreate(baseUri, href, out var linkUri)) continue;
            if (!string.Equals(linkUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)) continue;
            var canonical = linkUri.GetLeftPart(UriPartial.Query);
            if (visited.Contains(canonical)) continue;
            queue.Enqueue(canonical);
        }
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        if (_options.Headers is { Count: > 0 })
        {
            foreach (var header in _options.Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        return request;
    }
}

/// <summary>
/// Options for <see cref="WebSource"/>. Mirrors the TS
/// <c>WebSourceOptions</c> interface.
/// </summary>
public sealed class WebSourceOptions
{
    public required string Url { get; init; }
    public int MaxPages { get; init; } = 10;
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}
