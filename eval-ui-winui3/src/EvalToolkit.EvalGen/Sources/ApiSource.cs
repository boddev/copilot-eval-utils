using System.Net.Http.Json;
using System.Text.Json;
using EvalToolkit.Core;

namespace EvalToolkit.EvalGen.Sources;

/// <summary>
/// Port of <c>eval-gen/src/sources/api-source.ts</c>. Samples records from
/// REST APIs, optionally discovering endpoints via an OpenAPI / Swagger spec.
/// <para>
/// The TS source used <c>fetch()</c> directly; this port takes an injectable
/// <see cref="HttpClient"/> so the CLI shim can configure timeouts/handlers
/// once and tests can swap a stub <see cref="HttpMessageHandler"/>.
/// </para>
/// </summary>
public sealed class ApiSource : IDataSource, IDisposable
{
    private static readonly string[] CommonSpecPaths =
    {
        "/swagger.json",
        "/openapi.json",
        "/api-docs",
        "/v1/openapi.json",
    };

    private static readonly string[] ArrayWrapperKeys =
    {
        "data",
        "results",
        "items",
        "records",
        "value",
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ApiSourceOptions _options;

    public ApiSource(ApiSourceOptions options, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BaseUrl);
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
        var endpoints = _options.Endpoints is { Count: > 0 }
            ? _options.Endpoints
            : await DiscoverEndpointsAsync(cancellationToken).ConfigureAwait(false);

        if (endpoints.Count == 0)
        {
            throw new InvalidOperationException("No API endpoints discovered. Provide --endpoints or a valid OpenAPI spec.");
        }

        var allRecords = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var endpoint in endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var records = await FetchEndpointAsync(endpoint, cancellationToken).ConfigureAwait(false);
                allRecords.AddRange(records);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                progress?.Report($"  Warning: Failed to fetch {endpoint}: {ex.Message}");
            }
        }

        if (allRecords.Count == 0)
        {
            throw new InvalidOperationException("No data retrieved from any API endpoint");
        }

        return new SourceResult(allRecords, InputFormat.Json, new Uri(_options.BaseUrl).Host);
    }

    private async Task<IReadOnlyList<string>> DiscoverEndpointsAsync(CancellationToken cancellationToken)
    {
        var specUrl = _options.SpecUrl;
        if (string.IsNullOrEmpty(specUrl))
        {
            var trimmedBase = _options.BaseUrl.TrimEnd('/');
            foreach (var candidate in CommonSpecPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidateUrl = trimmedBase + candidate;
                try
                {
                    using var probe = BuildRequest(HttpMethod.Get, candidateUrl);
                    using var response = await _http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        specUrl = candidateUrl;
                        break;
                    }
                }
                catch
                {
                    // mirror TS try/catch continue
                }
            }
        }

        if (string.IsNullOrEmpty(specUrl))
        {
            throw new InvalidOperationException("No OpenAPI spec found. Provide --openapi-spec or --endpoints.");
        }

        using var specRequest = BuildRequest(HttpMethod.Get, specUrl);
        using var specResponse = await _http.SendAsync(specRequest, cancellationToken).ConfigureAwait(false);
        if (!specResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Failed to fetch OpenAPI spec: {(int)specResponse.StatusCode}");
        }

        var spec = await specResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        var endpoints = new List<string>();
        if (spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object)
        {
            foreach (var pathEntry in paths.EnumerateObject())
            {
                var path = pathEntry.Name;
                if (path.Contains('{', StringComparison.Ordinal)) continue;
                if (pathEntry.Value.ValueKind != JsonValueKind.Object) continue;
                if (!pathEntry.Value.TryGetProperty("get", out _)) continue;
                endpoints.Add(path);
            }
        }

        return endpoints.Count > 10 ? endpoints.Take(10).ToList() : endpoints;
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> FetchEndpointAsync(string endpoint, CancellationToken cancellationToken)
    {
        var url = _options.BaseUrl.TrimEnd('/') + endpoint;
        using var request = BuildRequest(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var data = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        return ShapeRecords(data);
    }

    private IReadOnlyList<IReadOnlyDictionary<string, object?>> ShapeRecords(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array)
        {
            return TakeArray(data);
        }

        if (data.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in ArrayWrapperKeys)
            {
                if (data.TryGetProperty(key, out var inner) && inner.ValueKind == JsonValueKind.Array)
                {
                    return TakeArray(inner);
                }
            }

            return new[] { (IReadOnlyDictionary<string, object?>)ConvertObject(data) };
        }

        return Array.Empty<IReadOnlyDictionary<string, object?>>();
    }

    private List<IReadOnlyDictionary<string, object?>> TakeArray(JsonElement array)
    {
        var max = _options.MaxRecordsPerEndpoint;
        var output = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var element in array.EnumerateArray())
        {
            if (output.Count >= max) break;
            output.Add(ConvertObject(element));
        }
        return output;
    }

    private static Dictionary<string, object?> ConvertObject(JsonElement element)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
        {
            dict["value"] = ConvertElement(element);
            return dict;
        }
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = ConvertElement(prop.Value);
        }
        return dict;
    }

    private static object? ConvertElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.Object => ConvertObject(element),
        JsonValueKind.Array => ConvertArray(element),
        _ => element.GetRawText(),
    };

    private static List<object?> ConvertArray(JsonElement array)
    {
        var list = new List<object?>();
        foreach (var element in array.EnumerateArray())
        {
            list.Add(ConvertElement(element));
        }
        return list;
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
/// Options for <see cref="ApiSource"/>. Mirrors the TS
/// <c>ApiSourceOptions</c> interface.
/// </summary>
public sealed class ApiSourceOptions
{
    public required string BaseUrl { get; init; }
    public string? SpecUrl { get; set; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public IReadOnlyList<string>? Endpoints { get; init; }
    public int MaxRecordsPerEndpoint { get; init; } = 50;
}
