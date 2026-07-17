using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using AgentService.Infrastructure.VersionControl;

namespace AgentService.UnitTests;

public sealed class ProtectedRefMatcherTests
{
    [Theory]
    [InlineData(VcsType.Git, "main", true)]
    [InlineData(VcsType.Git, "release/2026.1", true)]
    [InlineData(VcsType.Git, "wingman/task", false)]
    [InlineData(VcsType.Svn, "trunk", true)]
    [InlineData(VcsType.Svn, "/tags/v1", true)]
    [InlineData(VcsType.Svn, "branches/task", false)]
    public async Task Defaults_MatchExpectedRefs(VcsType type, string reference, bool expected)
    {
        var matcher = new ProtectedRefMatcher(new EmptyRepository());
        Assert.Equal(expected, await matcher.IsProtectedAsync(type, reference));
    }

    private sealed class EmptyRepository : IVcsStateRepository
    {
        public Task<ProjectVcsBinding?> GetBindingAsync(string projectId, CancellationToken ct = default) => Task.FromResult<ProjectVcsBinding?>(null);
        public Task SaveBindingAsync(ProjectVcsBinding binding, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<VcsProtectedRef>> ListProtectedRefsAsync(VcsType type, string? projectId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<VcsProtectedRef>>([]);
        public Task SaveProtectedRefAsync(VcsProtectedRef rule, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteProtectedRefAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveOperationAsync(VcsOperation operation, CancellationToken ct = default) => Task.CompletedTask;
    }
}
