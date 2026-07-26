using System.Reflection;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

namespace AgentService.UnitTests;

/// <summary>
/// 鎖定目前對話 Agent 真正使用的二進位相容組合。
/// 已移除的 Workflow 與 Evaluation 套件不再納入相依圖或測試。
/// </summary>
public sealed class AgentFrameworkPackageContractTests
{
    [Fact]
    public void CoreAgentFrameworkAssemblies_AreOnTheApprovedVersions()
    {
        Assert.Equal(new Version(1, 13, 0, 0), typeof(AIAgent).Assembly.GetName().Version);
        Assert.Equal(new Version(10, 8, 0, 0), typeof(IChatClient).Assembly.GetName().Version);
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
