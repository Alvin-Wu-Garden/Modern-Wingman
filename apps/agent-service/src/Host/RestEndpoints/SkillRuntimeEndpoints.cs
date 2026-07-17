using AgentService.Application.Contracts;
using AgentService.Domain.Models;
namespace AgentService.Host.RestEndpoints;
public static class SkillRuntimeEndpoints
{
    public static IEndpointRouteBuilder MapSkillRuntimeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/runtimes",async(IRuntimeResolver resolver,CancellationToken ct)=>
        {
            var root=Environment.CurrentDirectory;var results=new List<object>();
            foreach(var kind in Enum.GetValues<SkillRuntimeKind>()){var runtime=await resolver.ResolveAsync(new(kind,null,root,root),ct);results.Add(new{kind=kind.ToString().ToLowerInvariant(),available=runtime is not null,version=runtime?.Version.ToString(),source=runtime?.Source,executablePath=runtime?.ExecutablePath});}
            return Results.Ok(results);
        });
        app.MapPost("/api/runtimes/import-path",async(RuntimeImportRequest request,IRuntimeImportService importer,CancellationToken ct)=>
        {
            if(!TryParseKind(request.Kind,out var kind))return Results.BadRequest(new{error="Unsupported runtime kind."});
            try{return Results.Ok(await importer.ImportRuntimeAsync(kind,request.Path,ct));}catch(Exception ex)when(ex is IOException or InvalidDataException or InvalidOperationException){return Results.BadRequest(new{error=ex.Message});}
        });
        app.MapPost("/api/runtimes/package-cache/import-path",async(RuntimeImportRequest request,IRuntimeImportService importer,CancellationToken ct)=>
        {
            if(!TryParseKind(request.Kind,out var kind))return Results.BadRequest(new{error="Unsupported runtime kind."});
            try{return Results.Ok(await importer.ImportPackageCacheAsync(kind,request.Path,ct));}catch(Exception ex)when(ex is IOException or InvalidDataException or InvalidOperationException){return Results.BadRequest(new{error=ex.Message});}
        });return app;
    }
    private static bool TryParseKind(string value,out SkillRuntimeKind kind)=>Enum.TryParse(value switch{"nodejs"=>"node","pwsh"=>"powershell",_=>value},true,out kind);
    private sealed record RuntimeImportRequest(string Kind,string Path);
}
