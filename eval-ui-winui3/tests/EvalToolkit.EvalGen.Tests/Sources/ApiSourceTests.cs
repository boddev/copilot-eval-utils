using System.Net;
using System.Text;
using System.Text.Json;
using EvalToolkit.Core;
using EvalToolkit.EvalGen.Sources;
using EvalToolkit.EvalGen.Tests.LlmClients;

namespace EvalToolkit.EvalGen.Tests.Sources;

public sealed class ApiSourceTests
{
    private static MockHttpMessageHandler MakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => new(handler);

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var json = JsonSerializer.Serialize(body);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    [Fact]
    public async Task DiscoversAndFetchesOpenApiPaths()
    {
        var openApi = new
        {
            paths = new Dictionary<string, object>
            {
                ["/users"] = new { get = new { } },
                ["/users/{id}"] = new { get = new { } },
                ["/posts"] = new { post = new { } },
            },
        };

        var users = new[] { new { id = 1, name = "Ada" }, new { id = 2, name = "Bob" } };

        var handler = MakeHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/openapi.json") return Json(openApi);
            if (path == "/users") return Json(users);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        var source = new ApiSource(new ApiSourceOptions
        {
            BaseUrl = "https://api.example.com",
            SpecUrl = "https://api.example.com/openapi.json",
        }, http);

        var result = await source.FetchAsync();

        Assert.Equal(2, result.Records.Count);
        Assert.Equal("Ada", result.Records[0]["name"]);
    }

    [Fact]
    public async Task ExtractsWrapperKeyArray()
    {
        var handler = MakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/openapi.json")
            {
                return Json(new { paths = new Dictionary<string, object> { ["/things"] = new { get = new { } } } });
            }
            return Json(new { data = new[] { new { id = 1 }, new { id = 2 } } });
        });

        using var http = new HttpClient(handler);
        var source = new ApiSource(new ApiSourceOptions
        {
            BaseUrl = "https://api.example.com",
            SpecUrl = "https://api.example.com/openapi.json",
        }, http);

        var result = await source.FetchAsync();
        Assert.Equal(2, result.Records.Count);
    }

    [Fact]
    public async Task WrapsSingleObjectResponse()
    {
        var handler = MakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/openapi.json")
            {
                return Json(new { paths = new Dictionary<string, object> { ["/me"] = new { get = new { } } } });
            }
            return Json(new { id = 42, name = "solo" });
        });

        using var http = new HttpClient(handler);
        var source = new ApiSource(new ApiSourceOptions
        {
            BaseUrl = "https://api.example.com",
            SpecUrl = "https://api.example.com/openapi.json",
        }, http);

        var result = await source.FetchAsync();
        Assert.Single(result.Records);
        Assert.Equal("solo", result.Records[0]["name"]);
    }

    [Fact]
    public async Task CapsRecordsPerEndpoint()
    {
        var many = Enumerable.Range(0, 1000).Select(i => new { id = i }).ToArray();
        var handler = MakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/openapi.json")
            {
                return Json(new { paths = new Dictionary<string, object> { ["/items"] = new { get = new { } } } });
            }
            return Json(many);
        });

        using var http = new HttpClient(handler);
        var source = new ApiSource(new ApiSourceOptions
        {
            BaseUrl = "https://api.example.com",
            SpecUrl = "https://api.example.com/openapi.json",
            MaxRecordsPerEndpoint = 50,
        }, http);

        var result = await source.FetchAsync();
        Assert.Equal(50, result.Records.Count);
    }

    [Fact]
    public async Task SendsConfiguredHeaders()
    {
        var seenAuth = (string?)null;
        var handler = MakeHandler(req =>
        {
            if (req.Headers.TryGetValues("Authorization", out var v))
            {
                seenAuth = v.First();
            }
            if (req.RequestUri!.AbsolutePath == "/openapi.json")
            {
                return Json(new { paths = new Dictionary<string, object> { ["/items"] = new { get = new { } } } });
            }
            return Json(new[] { new { id = 1 } });
        });

        using var http = new HttpClient(handler);
        var source = new ApiSource(new ApiSourceOptions
        {
            BaseUrl = "https://api.example.com",
            SpecUrl = "https://api.example.com/openapi.json",
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer abc" },
        }, http);

        await source.FetchAsync();
        Assert.Equal("Bearer abc", seenAuth);
    }

    [Fact]
    public async Task ProbesCommonOpenApiPathsWhenUnspecified()
    {
        var paths = new List<string>();
        var handler = MakeHandler(req =>
        {
            paths.Add(req.RequestUri!.AbsolutePath);
            if (req.RequestUri.AbsolutePath == "/api-docs")
            {
                return Json(new { paths = new Dictionary<string, object> { ["/x"] = new { get = new { } } } });
            }
            if (req.RequestUri.AbsolutePath == "/x")
            {
                return Json(new[] { new { id = 1 } });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        var source = new ApiSource(new ApiSourceOptions { BaseUrl = "https://api.example.com" }, http);

        var result = await source.FetchAsync();
        Assert.Contains("/swagger.json", paths);
        Assert.Contains("/openapi.json", paths);
        Assert.Contains("/api-docs", paths);
        Assert.Single(result.Records);
    }

    [Fact]
    public async Task ThrowsWhenNoEndpointsDiscovered()
    {
        var handler = MakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        var source = new ApiSource(new ApiSourceOptions { BaseUrl = "https://api.example.com" }, http);

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchAsync());
    }

    // Round-2: nested object + array values must be preserved as runtime
    // structures (not stringified) so downstream profiling sees the same
    // shape TS does. Regression for GPT-5.5 blocker #1.
    [Fact]
    public async Task PreservesNestedObjectAndArrayValues()
    {
        var payload = new
        {
            id = 1,
            tags = new[] { "alpha", "beta" },
            owner = new { name = "Ada", roles = new[] { "admin", "user" } },
        };
        var handler = MakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/openapi.json")
            {
                return Json(new { paths = new Dictionary<string, object> { ["/things"] = new { get = new { } } } });
            }
            return Json(new[] { payload });
        });
        using var http = new HttpClient(handler);
        var source = new ApiSource(new ApiSourceOptions
        {
            BaseUrl = "https://api.example.com",
            SpecUrl = "https://api.example.com/openapi.json",
        }, http);

        var result = await source.FetchAsync();
        var record = result.Records.Single();
        var tags = Assert.IsType<List<object?>>(record["tags"]);
        Assert.Equal(new object?[] { "alpha", "beta" }, tags);
        var owner = Assert.IsType<Dictionary<string, object?>>(record["owner"]);
        Assert.Equal("Ada", owner["name"]);
        var roles = Assert.IsType<List<object?>>(owner["roles"]);
        Assert.Equal(new object?[] { "admin", "user" }, roles);
    }
}
