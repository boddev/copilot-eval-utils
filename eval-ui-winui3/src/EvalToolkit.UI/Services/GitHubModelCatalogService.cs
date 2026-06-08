using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EvalToolkit.UI.Services;

/// <summary>
/// Fetches the list of model identifiers from the public GitHub Models
/// catalog so the wizard's model dropdown reflects what GitHub currently
/// offers instead of a hand-maintained static list.
/// </summary>
public interface IGitHubModelCatalogService
{
    /// <summary>
    /// Returns model identifiers (publisher prefix stripped, de-duplicated,
    /// sorted). Returns an empty list on any network/parse failure so the
    /// caller can degrade gracefully to free-text entry.
    /// </summary>
    Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IGitHubModelCatalogService"/> backed by
/// <c>GET https://models.github.ai/catalog/models</c>. The endpoint is
/// public (no token required); only the standard <c>Accept</c> and
/// <c>X-GitHub-Api-Version</c> headers are sent.
///
/// <para>Catalog ids look like <c>openai/gpt-4.1</c> (<c>publisher/name</c>).
/// The GitHub Copilot CLI's <c>--model</c> flag takes the short name, so the
/// publisher prefix is stripped. The dropdown stays editable, so an operator
/// can still type a model id the catalog doesn't list.</para>
/// </summary>
public sealed class GitHubModelCatalogService : IGitHubModelCatalogService
{
    private const string CatalogUrl = "https://models.github.ai/catalog/models";
    private const string ApiVersion = "2026-03-10";

    // Shared instance avoids socket exhaustion; default headers are not
    // mutated (per-request headers are used instead) so it stays safe to
    // reuse across callers.
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    public async Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, CatalogUrl);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
            request.Headers.TryAddWithoutValidation("User-Agent", "EvalToolkit.UI");

            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<string>();
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            using var doc = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var models = new List<string>();

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!item.TryGetProperty("id", out var idProp) ||
                    idProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var id = idProp.GetString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var name = StripPublisher(id);
                if (name.Length != 0 && seen.Add(name))
                {
                    models.Add(name);
                }
            }

            models.Sort(StringComparer.OrdinalIgnoreCase);
            return models;
        }
        catch (Exception ex) when (
            ex is HttpRequestException
               or TaskCanceledException
               or OperationCanceledException
               or JsonException
               or InvalidOperationException
               or IOException)
        {
            return Array.Empty<string>();
        }
    }

    private static string StripPublisher(string id)
    {
        var slash = id.LastIndexOf('/');
        var name = slash >= 0 ? id[(slash + 1)..] : id;
        return name.Trim();
    }
}
