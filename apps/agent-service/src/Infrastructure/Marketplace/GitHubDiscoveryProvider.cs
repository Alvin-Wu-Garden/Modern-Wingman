using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Infrastructure.Providers;
using Microsoft.Extensions.Options;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.Infrastructure.Marketplace;

/// <summary>GitHub repository search adapter. PAT 只用於當次 request，絕不寫入 Marketplace persistence。</summary>
public sealed class GitHubDiscoveryProvider(
    IHttpClientFactory httpClientFactory,
    IProviderSettingStore providerSettings,
    IOptions<AgentServiceOptions> options) : IDiscoveryProvider
{
    public string ProviderId => "github-discovery";

    public async Task<IReadOnlyList<DiscoveryCandidate>> DiscoverAsync(
        DiscoveryQuery query,
        CancellationToken cancellationToken = default)
    {
        var token = providerSettings.GetApiKey(options.Value.ActiveProfileId);
        if (string.IsNullOrWhiteSpace(token))
            throw new MarketplacePrerequisiteException("GitHub PAT 尚未在 Settings 設定；請先設定後再重新整理 Marketplace。");

        var count = Math.Clamp(query.MaxResults, 1, 100);
        var path = $"search/repositories?q={Uri.EscapeDataString(query.QueryText)}&sort=updated&order=desc&per_page={count}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await httpClientFactory.CreateClient("marketplace-github").SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new MarketplacePrerequisiteException("GitHub PAT 無法用於 Marketplace Discovery；請檢查 token 或其可讀取權限。");
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<SearchResponse>(stream, Json, cancellationToken)
            ?? new SearchResponse([]);
        return payload.Items.Select(item => item.ToCandidate(query.KindHint)).ToList();
    }

    private sealed record SearchResponse(IReadOnlyList<RepositoryResponse> Items);

    private sealed record RepositoryResponse(
        string Node_Id,
        string Html_Url,
        string Name,
        string Full_Name,
        string? Description,
        IReadOnlyList<string>? Topics,
        LicenseResponse? License,
        bool Archived,
        int Stargazers_Count,
        int Forks_Count,
        DateTimeOffset? Updated_At,
        DateTimeOffset? Pushed_At)
    {
        public DiscoveryCandidate ToCandidate(MarketplaceArtifactKind kindHint)
        {
            var split = Full_Name.Split('/', 2, StringSplitOptions.TrimEntries);
            return new(
                Node_Id,
                Html_Url.TrimEnd('/'),
                split.ElementAtOrDefault(0) ?? string.Empty,
                split.ElementAtOrDefault(1) ?? Name,
                Name,
                Description,
                Topics ?? [],
                License?.Spdx_Id,
                Archived,
                Stargazers_Count,
                Forks_Count,
                Updated_At,
                Pushed_At,
                kindHint);
        }
    }

    private sealed record LicenseResponse(string? Spdx_Id);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
}
