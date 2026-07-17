using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Skills;

namespace AgentService.Host.RestEndpoints;

public static class SkillRuntimeStatusEndpoints
{
    public static IEndpointRouteBuilder MapSkillRuntimeStatusEndpoints(this IEndpointRouteBuilder app)
    {
        var group=app.MapGroup("/api/skills/runtime");
        group.MapPost("/refresh",(ISkillProvider provider)=>{provider.Refresh();return Results.Ok(new{count=provider.ListSkills().Count});});
        group.MapGet("/status",GetStatus);
        return app;
    }

    private static async Task<IResult> GetStatus(
        ISkillProvider provider,
        ISkillManifestLoader loader,
        IRuntimeResolver resolver,
        CancellationToken ct)
    {
        var result=new List<object>();
        foreach(var skill in provider.ListSkills())
        {
            try
            {
                var root=Path.GetDirectoryName(skill.SkillFilePath)!;
                var manifest=await loader.LoadAsync(root,ct);
                if(manifest?.Runtime is null)
                {
                    result.Add(new{name=skill.Name,executable=false,status="instruction_only",runtime=(string?)null,version=(string?)null,error=(string?)null,dependencyFile=(string?)null,packageManager=(string?)null,network=false,requiresApproval=false,requiredEnvironment=Array.Empty<string>()});
                    continue;
                }
                YamlSkillManifestLoader.TryParseRuntimeKind(manifest.Runtime.Type,out var kind);
                var runtime=await resolver.ResolveAsync(new(kind,manifest.Runtime.Version,root,root),ct);
                var dependencyReady=DependenciesReady(manifest.Runtime,root,kind);
                var status=runtime is null?"missing":dependencyReady?"ready":"dependencies_missing";
                var entrypoints=manifest.Runtime.Entrypoints.Values.ToList();
                result.Add(new{name=skill.Name,executable=true,status,runtime=manifest.Runtime.Type,version=manifest.Runtime.Version,error=runtime is null?$"No compatible {manifest.Runtime.Type} runtime found.":!dependencyReady?"Declared dependencies are not installed.":null,dependencyFile=manifest.Runtime.DependencyFile,packageManager=manifest.Runtime.PackageManager,network=manifest.Runtime.InstallNetwork||entrypoints.Any(x=>x.Network),requiresApproval=entrypoints.Any(x=>x.RequiresApproval),requiredEnvironment=entrypoints.SelectMany(x=>x.RequiredEnvironment).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()});
            }
            catch(Exception ex)
            {
                result.Add(new{name=skill.Name,executable=false,status="invalid",runtime=(string?)null,version=(string?)null,error=ex.Message,dependencyFile=(string?)null,packageManager=(string?)null,network=false,requiresApproval=false,requiredEnvironment=Array.Empty<string>()});
            }
        }
        return Results.Ok(result);
    }

    private static bool DependenciesReady(SkillRuntimeManifest manifest,string root,SkillRuntimeKind kind)
    {
        if(string.IsNullOrWhiteSpace(manifest.DependencyFile))return true;
        return kind switch{SkillRuntimeKind.Python=>File.Exists(Path.Combine(root,".wingman-runtime","Scripts","python.exe")),SkillRuntimeKind.Node=>Directory.Exists(Path.Combine(root,"node_modules")),_=>true};
    }
}
