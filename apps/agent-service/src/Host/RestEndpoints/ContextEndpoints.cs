using AgentService.Application.Contracts;
namespace AgentService.Host.RestEndpoints;
public static class ContextEndpoints
{
    public sealed record PreviewRequest(string Message,string WorkspacePath);
    public sealed record IdeSelectionRequest(string ProjectId,string RelativePath,int StartLine,int EndLine);
    public static IEndpointRouteBuilder MapContextEndpoints(this IEndpointRouteBuilder app){app.MapPost("/api/context/preview",async(PreviewRequest request,IContextAssembler assembler,CancellationToken ct)=>{if(!Directory.Exists(request.WorkspacePath))return Results.BadRequest(new{error="Workspace does not exist."});var result=await assembler.AssembleAsync(request.Message,request.WorkspacePath,ct);return Results.Ok(new{result.Sources,result.EstimatedTokens,result.Truncated});});app.MapPost("/api/context/ide-selection",async(IdeSelectionRequest request,IProjectRepository projects,IIdeSelectionContextService selections,CancellationToken ct)=>{var project=await projects.GetAsync(request.ProjectId,ct);if(project is null)return Results.NotFound();try{return Results.Ok(await selections.ReadAsync(project.RootPath,request.RelativePath,request.StartLine,request.EndLine,ct));}catch(Exception error)when(error is ArgumentException or IOException or UnauthorizedAccessException){return Results.BadRequest(new{error=error.Message});}});return app;}
}
