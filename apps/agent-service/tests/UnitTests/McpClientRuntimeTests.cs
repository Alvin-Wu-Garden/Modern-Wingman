using System.Text.Json;
using AgentService.Application.Models;
using AgentService.Infrastructure.Mcp;

namespace AgentService.UnitTests;

public sealed class McpClientRuntimeTests
{
    [Fact]
    public async Task StdioServer_InitializesDiscoversAndCallsTool()
    {
        var root=Path.Combine(Path.GetTempPath(),"wingman-mcp-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var script=Path.Combine(root,"server.ps1");
        await File.WriteAllTextAsync(script, """
            while (($line = [Console]::In.ReadLine()) -ne $null) {
              $request = $line | ConvertFrom-Json
              if ($request.id -eq 1) {
                @{ jsonrpc='2.0'; id=1; result=@{ protocolVersion='2025-06-18'; capabilities=@{ tools=@{} }; serverInfo=@{ name='test'; version='1' } } } | ConvertTo-Json -Depth 10 -Compress
              } elseif ($request.method -eq 'tools/list') {
                @{ jsonrpc='2.0'; id=$request.id; result=@{ tools=@(@{ name='echo'; description='Echo input'; inputSchema=@{ type='object' }; annotations=@{ readOnlyHint=$true } }) } } | ConvertTo-Json -Depth 10 -Compress
              } elseif ($request.method -eq 'tools/call') {
                @{ jsonrpc='2.0'; id=$request.id; result=@{ content=@(@{ type='text'; text=[string]$request.params.arguments.value }); isError=$false } } | ConvertTo-Json -Depth 10 -Compress
              }
            }
            """);
        try
        {
            var server=new McpServerDefinition(1,"test",McpTransport.Stdio,FindPwsh(),["-NoLogo","-NoProfile","-File",script],null,new Dictionary<string,string>(),true);
            var runtime=new McpClientRuntime(new UnusedHttpClientFactory());
            var tools=await runtime.DiscoverToolsAsync(server);
            var tool=Assert.Single(tools);
            Assert.Equal("echo",tool.Name);
            Assert.True(tool.ReadOnly);
            using var arguments=JsonDocument.Parse("{\"value\":\"hello\"}");
            var result=await runtime.CallToolAsync(server,"echo",arguments.RootElement);
            Assert.True(result.Success,result.Error);
            Assert.Equal("hello",result.Output.Trim());
        }
        finally { try { Directory.Delete(root,true); } catch { } }
    }

    private static string FindPwsh() => (Environment.GetEnvironmentVariable("PATH")??"")
        .Split(Path.PathSeparator,StringSplitOptions.RemoveEmptyEntries)
        .Select(path=>Path.Combine(path,"pwsh.exe")).FirstOrDefault(File.Exists)
        ?? throw new InvalidOperationException("pwsh.exe is required.");

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("HTTP should not be used.");
    }
}
