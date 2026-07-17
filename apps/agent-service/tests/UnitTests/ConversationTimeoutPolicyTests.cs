using AgentService.Host.RestEndpoints;
using AgentService.Application.Models;
using AgentService.Infrastructure.Telemetry;

namespace AgentService.UnitTests;

public sealed class ConversationTimeoutPolicyTests
{
    [Theory]
    [InlineData(false, 10, 60, LlmTimeoutKind.FirstToken)]
    [InlineData(true, 10, 60, LlmTimeoutKind.IdleStream)]
    [InlineData(true, 60, 60, LlmTimeoutKind.TotalRequest)]
    public void ClassifiesTimeouts(bool firstTokenSeen,int elapsedSeconds,int totalSeconds,string expected)
    {
        Assert.Equal(expected,ConversationEndpoints.ResolveTimeoutKind(
            firstTokenSeen,
            TimeSpan.FromSeconds(totalSeconds),
            TimeSpan.FromSeconds(elapsedSeconds)));
    }

    [Theory]
    [InlineData(LlmTimeoutKind.FirstToken,false,true)]
    [InlineData(LlmTimeoutKind.IdleStream,true,false)]
    [InlineData(LlmTimeoutKind.TotalRequest,true,false)]
    public void AutomaticRetry_IsLimitedToBeforeFirstToken(string kind,bool firstTokenSeen,bool expected)
    {
        Assert.Equal(expected,ConversationEndpoints.CanAutomaticallyRetryTimeout(kind,firstTokenSeen));
    }
}
