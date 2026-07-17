using System.Text.Json;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.Infrastructure.Marketplace;

/// <summary>Manual-only update check. It only compares refs and never downloads or deploys an update.</summary>
public sealed class MarketplaceUpdateService(
    IHttpClientFactory httpClientFactory,
    IMarketplaceArtifactStore artifactStore,
    IMarketplaceUpdateHistoryStore historyStore,
    IGitHubRepositoryImportService githubImporter,
    IMarketplaceActivityRecorder? activity = null) : IMarketplaceUpdateService
{
    public async Task<IReadOnlyList<MarketplaceArtifactUpdate>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<MarketplaceArtifactUpdate>();
        foreach (var source in await artifactStore.ListArtifactSourcesAsync(cancellationToken))
        {
            if (!TryParseGitHubSource(source.SourceLocation, out var owner, out var repository, out var commit))
            {
                results.Add(new(source.Artifact.Id, source.Artifact.DisplayName, source.SourceLocation, null, "NotApplicable", null, "本機或非 GitHub source 不支援自動更新檢查。"));
                continue;
            }
            try
            {
                using var response = await httpClientFactory.CreateClient("marketplace-github").GetAsync($"repos/{owner}/{repository}/commits/HEAD", cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var payload = await JsonSerializer.DeserializeAsync<CommitResponse>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);
                var latest = payload?.Sha;
                results.Add(new(source.Artifact.Id, source.Artifact.DisplayName, source.SourceLocation, commit, string.Equals(commit, latest, StringComparison.OrdinalIgnoreCase) ? "Current" : "UpdateAvailable", latest));
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            { results.Add(new(source.Artifact.Id, source.Artifact.DisplayName, source.SourceLocation, commit, "CheckFailed", null, ex.Message)); }
        }
        var checks = results.Select(result => new MarketplaceUpdateCheck(
            Guid.NewGuid().ToString("N"), result.ArtifactId, result.SourceLocation, result.InstalledCommitSha,
            result.Status, result.AvailableCommitSha, result.Message, DateTimeOffset.UtcNow)).ToList();
        await historyStore.SaveAsync(checks, cancellationToken);
        if (activity is not null)
        {
            var availableCount = results.Count(item => item.Status == "UpdateAvailable");
            await activity.RecordAsync(Activity("updates-check", "Completed", null, null,
                $"checked={results.Count};available={availableCount}"), cancellationToken);
        }
        return results;
    }

    public Task<IReadOnlyList<MarketplaceUpdateCheck>> ListHistoryAsync(string? artifactId = null, int take = 100, CancellationToken cancellationToken = default)
        => historyStore.ListAsync(artifactId, take, cancellationToken);

    public async Task<MarketplaceArtifactUpdateApplicationResult> ApplyAsync(string artifactId, string expectedCommitSha, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedCommitSha)) throw new ArgumentException("必須指定已確認的 commit SHA。", nameof(expectedCommitSha));
        var source = (await artifactStore.ListArtifactSourcesAsync(cancellationToken)).SingleOrDefault(item => item.Artifact.Id == artifactId)
            ?? throw new KeyNotFoundException("找不到 artifact source。 ");
        if (!TryParseGitHubSource(source.SourceLocation, out var owner, out var repository, out var installedCommit))
            throw new InvalidOperationException("只有固定 GitHub commit 的 artifact 可以套用更新。");

        var update = await GetUpdateAsync(source, cancellationToken);
        if (update.Status != "UpdateAvailable" || !string.Equals(update.AvailableCommitSha, expectedCommitSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("更新已不存在或與確認的 commit SHA 不一致；請先重新檢查更新。");

        // Import uses the immutable SHA and only creates a new local snapshot. It deliberately does not deploy.
        var import = await githubImporter.ImportAsync($"https://github.com/{owner}/{repository}", expectedCommitSha, cancellationToken);
        var result = new MarketplaceArtifactUpdateApplicationResult(artifactId, installedCommit, import.CommitSha, import.Import);
        await historyStore.SaveAsync([new MarketplaceUpdateCheck(Guid.NewGuid().ToString("N"), artifactId, source.SourceLocation,
            installedCommit, "Applied", import.CommitSha, "已建立新的本機 snapshot；既有部署未變更。", DateTimeOffset.UtcNow)], cancellationToken);
        if (activity is not null)
            await activity.RecordAsync(Activity("update-apply", "Completed", artifactId, null, $"from={installedCommit};to={import.CommitSha};deployments=unchanged"), cancellationToken);
        return result;
    }

    private async Task<MarketplaceArtifactUpdate> GetUpdateAsync(MarketplaceArtifactSource source, CancellationToken cancellationToken)
    {
        if (!TryParseGitHubSource(source.SourceLocation, out var owner, out var repository, out var commit))
            return new(source.Artifact.Id, source.Artifact.DisplayName, source.SourceLocation, null, "NotApplicable", null, "本機或非 GitHub source 不支援自動更新檢查。");
        try
        {
            using var response = await httpClientFactory.CreateClient("marketplace-github").GetAsync($"repos/{owner}/{repository}/commits/HEAD", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<CommitResponse>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);
            var latest = payload?.Sha;
            return new(source.Artifact.Id, source.Artifact.DisplayName, source.SourceLocation, commit,
                string.Equals(commit, latest, StringComparison.OrdinalIgnoreCase) ? "Current" : "UpdateAvailable", latest);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return new(source.Artifact.Id, source.Artifact.DisplayName, source.SourceLocation, commit, "CheckFailed", null, ex.Message);
        }
    }

    private static MarketplaceActivityEvent Activity(string type, string status, string? artifactId, string? targetId, string detail)
        => new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), type, status, artifactId, targetId, detail, DateTimeOffset.UtcNow);

    private static bool TryParseGitHubSource(string source, out string owner, out string repository, out string commit)
    {
        owner = repository = commit = string.Empty;
        var separator = source.LastIndexOf('@');
        if (separator < 1 || !Uri.TryCreate(source[..separator], UriKind.Absolute, out var uri) || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(source[(separator + 1)..])) return false;
        owner = parts[0]; repository = parts[1]; commit = source[(separator + 1)..]; return true;
    }

    private sealed record CommitResponse(string Sha);
}
