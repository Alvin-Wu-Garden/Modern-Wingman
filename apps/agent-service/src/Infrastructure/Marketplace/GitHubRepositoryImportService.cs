using System.Net.Http.Headers;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Infrastructure.Providers;
using Microsoft.Extensions.Options;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.Infrastructure.Marketplace;

/// <summary>Resolves a user-supplied GitHub repository/ref to a commit SHA before importing its archive.</summary>
public sealed class GitHubRepositoryImportService(
    IHttpClientFactory httpClientFactory,
    IProviderSettingStore providerSettings,
    IOptions<AgentServiceOptions> options,
    MarketplaceRegistryPathResolver paths,
    IMarketplaceArtifactService artifacts) : IGitHubRepositoryImportService
{
    public async Task<GitHubRepositoryImportResult> ImportAsync(string repositoryUrl, string? reference, CancellationToken cancellationToken = default)
    {
        var (owner, repository) = ParseRepository(repositoryUrl);
        var requestedRef = string.IsNullOrWhiteSpace(reference) ? "HEAD" : reference.Trim();
        var client = httpClientFactory.CreateClient("marketplace-github");
        var commit = await GetJsonAsync<CommitResponse>(client, $"repos/{owner}/{repository}/commits/{Uri.EscapeDataString(requestedRef)}", cancellationToken)
            ?? throw new InvalidDataException("GitHub 未回傳 commit SHA。");
        if (string.IsNullOrWhiteSpace(commit.Sha)) throw new InvalidDataException("GitHub 未回傳有效 commit SHA。");
        Directory.CreateDirectory(paths.StagingRoot);
        var archive = Path.Combine(paths.StagingRoot, $"github-{Guid.NewGuid():N}.zip");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"repos/{owner}/{repository}/zipball/{Uri.EscapeDataString(commit.Sha)}");
            AddHeaders(request);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = File.Create(archive))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }
            var canonicalUrl = $"https://github.com/{owner}/{repository}";
            var import = await artifacts.ImportArchiveAsync(archive, $"{canonicalUrl}@{commit.Sha}", cancellationToken);
            return new GitHubRepositoryImportResult(canonicalUrl, requestedRef, commit.Sha, import);
        }
        finally { if (File.Exists(archive)) File.Delete(archive); }
    }

    private async Task<T?> GetJsonAsync<T>(HttpClient client, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path); AddHeaders(request);
        using var response = await client.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
    }

    private void AddHeaders(HttpRequestMessage request)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var token = providerSettings.GetApiKey(options.Value.ActiveProfileId);
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static (string Owner, string Repository) ParseRepository(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("只支援 GitHub repository URL。", nameof(value));
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2) throw new ArgumentException("請提供 GitHub repository root URL，例如 https://github.com/owner/repository。", nameof(value));
        var repository = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        if (string.IsNullOrWhiteSpace(segments[0]) || string.IsNullOrWhiteSpace(repository)) throw new ArgumentException("GitHub repository URL 無效。", nameof(value));
        return (segments[0], repository);
    }

    private sealed record CommitResponse(string Sha);
}
