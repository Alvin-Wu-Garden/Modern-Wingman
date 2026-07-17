using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Telemetry;

public sealed class ToolCallTelemetry(IDbContextFactory<AppDbContext> factory,ISensitiveDataRedactor redactor,ILogger<ToolCallTelemetry> logger):IToolCallTelemetry
{
    public async Task<string?> StartAsync(ToolExecutionRequest request,CancellationToken ct=default)
    {
        try{await using var db=await factory.CreateDbContextAsync(ct);var requestId=await db.AiRequestLogs.Where(x=>x.RunId==request.Context.RunId).OrderByDescending(x=>x.StartedAt).Select(x=>x.Id).FirstOrDefaultAsync(ct);if(requestId is null)return null;var input=JsonSerializer.Serialize(request.Arguments);var metadata=request.ToolName is "run_command" or "run_build" or "run_test"?JsonSerializer.Serialize(new{executable=request.Arguments.GetValueOrDefault("executable")?.ToString(),argumentsHash=Hash(JsonSerializer.Serialize(request.Arguments.GetValueOrDefault("arguments"))),cwd=request.Arguments.GetValueOrDefault("workingDirectory")?.ToString()??request.Context.WorkspacePath}):null;var row=new AiToolCallLogRecord{RequestLogId=requestId,ToolCallId=Guid.NewGuid().ToString("N"),ToolName=request.ToolName,ToolType=Type(request.ToolName),SkillId=request.ToolName=="run_skill_script"&&request.Arguments.TryGetValue("skillName",out var skill)?skill?.ToString():null,McpServerId=request.ToolName=="call_mcp_tool"?request.Arguments.GetValueOrDefault("server")?.ToString():request.ToolName.StartsWith("mcp:",StringComparison.OrdinalIgnoreCase)?request.ToolName.Split(':').ElementAtOrDefault(1):null,StartedAt=DateTimeOffset.UtcNow,InputHash=Hash(input),InputPreviewRedacted=Preview(redactor.Redact(input)),MetadataJson=metadata};db.AiToolCallLogs.Add(row);await db.SaveChangesAsync(ct);return row.Id;}catch(Exception ex)when(ex is not OperationCanceledException){logger.LogWarning(ex,"Tool telemetry start failed");return null;}
    }
    public async Task CompleteAsync(string? id,ToolExecutionResult result,CancellationToken ct=default)
    {
        if(id is null)return;try{await using var db=await factory.CreateDbContextAsync(ct);var row=await db.AiToolCallLogs.FindAsync([id],ct);if(row is null)return;row.Status=result.TimedOut?"timed_out":result.Success?"succeeded":"failed";row.EndedAt=DateTimeOffset.UtcNow;row.DurationMs=result.DurationMs>0?result.DurationMs:(long?)(row.EndedAt-row.StartedAt)?.TotalMilliseconds;row.OutputHash=Hash(result.Output);row.OutputPreviewRedacted=Preview(redactor.Redact(result.Output));row.ErrorMessageSanitized=result.Error is null?null:Preview(redactor.Redact(result.Error));row.ApprovalRequired=result.ApprovalRequired;row.ApprovalResult=result.ApprovalResult;var existing=string.IsNullOrWhiteSpace(row.MetadataJson)?new Dictionary<string,object?>():JsonSerializer.Deserialize<Dictionary<string,object?>>(row.MetadataJson!)??[];if(result.ExitCode is not null||result.TimedOut){existing["exitCode"]=result.ExitCode;existing["timedOut"]=result.TimedOut;}if(!string.IsNullOrWhiteSpace(result.MetadataJson)){existing["execution"]=JsonSerializer.Deserialize<object>(result.MetadataJson!);}row.MetadataJson=existing.Count==0?null:JsonSerializer.Serialize(existing);await db.SaveChangesAsync(ct);}catch(Exception ex)when(ex is not OperationCanceledException){logger.LogWarning(ex,"Tool telemetry completion failed");}
    }
    private static string Type(string name)=>name=="call_mcp_tool"||name.StartsWith("mcp:",StringComparison.OrdinalIgnoreCase)?"mcp":name=="run_skill_script"?"skill":name.StartsWith("git_",StringComparison.OrdinalIgnoreCase)||name.StartsWith("svn_",StringComparison.OrdinalIgnoreCase)?"vcs":"builtin";
    private static string? Hash(string? value)=>value is null?null:Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Preview(string value)=>value.Length>500?value[..500]:value;
}
