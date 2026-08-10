using AgentService.Application.Contracts;
using AgentService.Domain.Models;

namespace AgentService.Host.RestEndpoints;

public static class VcsProfileEndpoints
{
    public sealed record SaveVcsProfileRequest(
        string Name,
        string VcsType,
        string BaseUrl,
        bool SslVerificationEnabled,
        string? DefaultWorkspaceRoot,
        bool Enabled,
        string? Username,
        string? SecretType,
        string? SecretValue);

    public static IEndpointRouteBuilder MapVcsProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/vcs/profiles");
        group.MapGet("/", List);
        group.MapGet("/{id}", Get);
        group.MapPost("/", Create);
        group.MapPut("/{id}", Update);
        group.MapDelete("/{id}", Delete);
        group.MapPost("/{id}/test", Test);
        return app;
    }

    private static async Task<IResult> List(
        IVcsProfileRepository repository,
        CancellationToken ct) =>
        Results.Ok((await repository.ListAsync(ct)).Select(ToDto));

    private static async Task<IResult> Get(
        string id,
        IVcsProfileRepository repository,
        CancellationToken ct)
    {
        var profile = await repository.GetAsync(id, ct);
        return profile is null ? Results.NotFound() : Results.Ok(ToDto(profile));
    }

    private static async Task<IResult> Create(
        SaveVcsProfileRequest request,
        IVcsProfileRepository repository,
        CancellationToken ct)
    {
        var validation = Validate(request);
        if (validation is not null)
            return Results.BadRequest(new { error = validation });
        var profile = Map(request, Guid.NewGuid().ToString("N"));
        await repository.SaveAsync(profile, ct);
        return Results.Created($"/api/vcs/profiles/{profile.Id}", ToDto(profile));
    }

    private static async Task<IResult> Update(
        string id,
        SaveVcsProfileRequest request,
        IVcsProfileRepository repository,
        CancellationToken ct)
    {
        var existing = await repository.GetAsync(id, ct);
        if (existing is null)
            return Results.NotFound();
        var validation = Validate(request);
        if (validation is not null)
            return Results.BadRequest(new { error = validation });

        var profile = Map(request, id, existing.CreatedAt);
        if (request.SecretValue is null)
        {
            profile.SecretValue = existing.SecretValue;
            profile.SecretType = existing.SecretType;
            profile.Username = request.Username ?? existing.Username;
        }
        await repository.SaveAsync(profile, ct);
        return Results.Ok(ToDto(profile));
    }

    private static async Task<IResult> Delete(
        string id,
        IVcsProfileRepository repository,
        CancellationToken ct)
    {
        if (await repository.GetAsync(id, ct) is null)
            return Results.NotFound();
        await repository.DeleteAsync(id, ct);
        return Results.NoContent();
    }

    /// <summary>使用已保存的 DPAPI 憑證測試 Profile 的 Base URL。</summary>
    private static async Task<IResult> Test(
        string id,
        IVcsProfileRepository repository,
        IGitClient git,
        ISvnClient svn,
        CancellationToken ct)
    {
        var profile = await repository.GetAsync(id, ct);
        if (profile is null)
            return Results.NotFound();
        if (profile.VcsType == VcsType.Git)
        {
            var result = await git.TestConnectionAsync(id, profile.BaseUrl, ct);
            return Results.Ok(new { result.Success, result.Output, result.Error });
        }
        else
        {
            var result = await svn.TestConnectionAsync(id, profile.BaseUrl, ct);
            return Results.Ok(new { result.Success, result.Output, result.Error });
        }
    }

    private static string? Validate(SaveVcsProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return "必須提供 Profile 名稱。";
        if (!Enum.TryParse<VcsType>(request.VcsType, true, out _))
            return "VcsType must be Git or Svn.";
        if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            return "BaseUrl must be an absolute HTTP or HTTPS URL.";
        if (!string.IsNullOrWhiteSpace(request.SecretValue) &&
            string.IsNullOrWhiteSpace(request.Username))
            return "提供憑證時必須填寫 Username。";
        return null;
    }

    private static VcsConnectionProfile Map(
        SaveVcsProfileRequest request,
        string id,
        DateTimeOffset? createdAt = null)
    {
        var vcsType = Enum.Parse<VcsType>(request.VcsType, true);
        return new VcsConnectionProfile
        {
            Id = id,
            Name = request.Name.Trim(),
            VcsType = vcsType,
            ServerType = vcsType == VcsType.Git
                ? VcsServerType.BitbucketServer
                : VcsServerType.Svn,
            BaseUrl = request.BaseUrl.TrimEnd('/'),
            SslVerificationEnabled = request.SslVerificationEnabled,
            DefaultWorkspaceRoot = string.IsNullOrWhiteSpace(request.DefaultWorkspaceRoot)
                ? null
                : Path.GetFullPath(request.DefaultWorkspaceRoot),
            Enabled = request.Enabled,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Username = request.Username?.Trim(),
            SecretType = Enum.TryParse<VcsSecretType>(request.SecretType, true, out var secretType)
                ? secretType
                : vcsType == VcsType.Git
                    ? VcsSecretType.AccessToken
                    : VcsSecretType.Password,
            SecretValue = request.SecretValue,
        };
    }

    private static object ToDto(VcsConnectionProfile profile) => new
    {
        profile.Id,
        profile.Name,
        vcsType = profile.VcsType.ToString().ToLowerInvariant(),
        serverType = profile.ServerType.ToString(),
        profile.BaseUrl,
        profile.SslVerificationEnabled,
        profile.DefaultWorkspaceRoot,
        profile.Enabled,
        profile.Username,
        secretType = profile.SecretType?.ToString(),
        profile.HasSecret,
        profile.CreatedAt,
        profile.UpdatedAt,
    };
}
