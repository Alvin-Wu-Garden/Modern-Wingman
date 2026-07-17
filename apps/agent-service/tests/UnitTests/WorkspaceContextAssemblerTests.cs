using AgentService.Infrastructure.Context;

namespace AgentService.UnitTests;

public sealed class WorkspaceContextAssemblerTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"wingman-context-"+Guid.NewGuid().ToString("N"));
    public WorkspaceContextAssemblerTests(){Directory.CreateDirectory(Path.Combine(_root,"src"));File.WriteAllText(Path.Combine(_root,"AGENTS.md"),"root instruction");File.WriteAllText(Path.Combine(_root,"src","AGENTS.md"),"nested instruction");File.WriteAllText(Path.Combine(_root,"src","app.cs"),"class App {}");}
    [Fact]public async Task FileReference_IncludesFileAndNestedInstructions(){var result=await new WorkspaceContextAssembler(null!,null!).AssembleAsync("inspect @file:src/app.cs",_root);Assert.Contains("class App",result.Prompt);Assert.Contains("root instruction",result.Prompt);Assert.Contains("nested instruction",result.Prompt);Assert.Equal(3,result.Sources.Count);Assert.True(result.EstimatedTokens>0);}
    [Fact]public async Task Traversal_IsRejected(){var outside=Path.Combine(Path.GetDirectoryName(_root)!,"outside.txt");File.WriteAllText(outside,"secret");try{await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>new WorkspaceContextAssembler(null!,null!).AssembleAsync("@file:../outside.txt",_root));}finally{File.Delete(outside);}}
    public void Dispose()=>Directory.Delete(_root,true);
}
