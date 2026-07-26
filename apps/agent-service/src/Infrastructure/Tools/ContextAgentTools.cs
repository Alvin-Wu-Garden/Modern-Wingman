using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Modules.GraphRAG;

namespace AgentService.Infrastructure.Tools;

public sealed class ReadSkillTool(IAgentPolicyEngine policy,IApprovalCoordinator approvals,ISkillProvider skills):PolicyEnforcedAgentTool(policy,approvals)
{
    public override ToolDescriptor Descriptor{get;}=new("read_skill","Load the full SKILL.md for one installed skill on demand.",AgentCapability.Read,AgentRiskLevel.Low,TimeSpan.FromSeconds(30),Source:"skill");
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(ToolExecutionRequest request,CancellationToken ct){var name=RequireString(request.Arguments,"name");var content=await skills.ReadSkillContentAsync(name,ct);return content is null?new(false,"",$"Skill not found: {name}"):new(true,UntrustedContent.Wrap($"skill:{name}",content));}
}

public sealed class QueryCodeGraphTool(IAgentPolicyEngine policy,IApprovalCoordinator approvals,IGraphStore graph):PolicyEnforcedAgentTool(policy,approvals)
{
    public override ToolDescriptor Descriptor{get;}=new("query_code_graph","Search indexed project symbols and optionally load one symbol neighborhood.",AgentCapability.Read,AgentRiskLevel.Low,TimeSpan.FromSeconds(30));
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(ToolExecutionRequest request,CancellationToken ct){var project=request.Context.ProjectId??throw new ArgumentException("Project context is required.");var query=RequireString(request.Arguments,"query");var hits=await graph.SearchAsync(project,query,20,ct);object output=hits;if(request.Arguments.TryGetValue("nodeKey",out var key)&&key is string nodeKey&&!string.IsNullOrWhiteSpace(nodeKey))output=new{hits,neighborhood=await graph.GetNeighborsAsync(project,nodeKey,50,ct)};return new(true,UntrustedContent.Wrap("repository-code-graph-v3",JsonSerializer.Serialize(output)));}
}

public sealed class AnalyzeImpactTool(IAgentPolicyEngine policy,IApprovalCoordinator approvals,GraphRetrievalService impact):PolicyEnforcedAgentTool(policy,approvals)
{
    public override ToolDescriptor Descriptor{get;}=new("analyze_impact","Analyze reverse callers, affected files, and suggested tests for a project symbol.",AgentCapability.Read,AgentRiskLevel.Low,TimeSpan.FromMinutes(1));
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(ToolExecutionRequest request,CancellationToken ct){var project=request.Context.ProjectId??throw new ArgumentException("Project context is required.");var symbol=RequireString(request.Arguments,"symbol");return new(true,JsonSerializer.Serialize(await impact.AnalyzeImpactAsync(project,symbol,3,ct)));}
}
