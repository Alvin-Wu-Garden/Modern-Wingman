using System.Reflection;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

namespace AgentService.UnitTests;

/// <summary>
/// Guards the binary compatibility set selected for the Agent Framework upgrade.
/// These checks intentionally fail on a partial package upgrade, rather than
/// allowing a mixed assembly graph to reach a desktop user.
/// </summary>
public sealed class AgentFrameworkPackageContractTests
{
    [Fact]
    public void CoreAgentFrameworkAssemblies_AreOnTheApprovedVersions()
    {
        Assert.Equal(new Version(1, 13, 0, 0), typeof(AIAgent).Assembly.GetName().Version);
        Assert.Equal(new Version(1, 13, 0, 0), typeof(AgentWorkflowBuilder).Assembly.GetName().Version);
        Assert.Equal(new Version(1, 13, 0, 0), typeof(MessageHandlerAttribute).Assembly.GetName().Version);
        Assert.Equal(new Version(1, 13, 0, 0), typeof(InProcessExecution).Assembly.GetName().Version);
        Assert.Same(typeof(AgentWorkflowBuilder).Assembly, typeof(WorkflowOutputEvent).Assembly);
        Assert.Equal(new Version(10, 8, 0, 0), typeof(IChatClient).Assembly.GetName().Version);
        Assert.Equal(new Version(10, 8, 0, 0), Assembly.Load("Microsoft.Extensions.AI.Evaluation").GetName().Version);
        Assert.Equal(new Version(2, 12, 0, 0), typeof(OpenAIClient).Assembly.GetName().Version);
        Assert.Equal(new Version(1, 14, 0, 0), typeof(ApiKeyCredential).Assembly.GetName().Version);
    }

    [Fact]
    public void CopilotAdapter_UsesItsCompatibleSdkAssembly()
    {
        var adapter = Assembly.Load("Microsoft.Agents.AI.GitHub.Copilot");
        var adapterSdkReference = Assert.Single(
            adapter.GetReferencedAssemblies(),
            reference => reference.Name == "GitHub.Copilot.SDK");

        Assert.Equal(new Version(1, 13, 0, 0), adapter.GetName().Version);
        Assert.Equal(new Version(1, 0, 0, 0), adapterSdkReference.Version);
        Assert.Equal(adapterSdkReference.Version, typeof(CopilotClient).Assembly.GetName().Version);
    }
}
