using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Infrastructure.Tools;

namespace AgentService.Infrastructure.Context;

public sealed partial class WorkspaceContextAssembler(IGitClient git,ISvnClient svn,IContextSnapshotRepository? snapshots=null):IContextAssembler
{
    private const int MaxCharacters=400_000;
    private const int CompressionThreshold=160_000;
    private const int CompressedCharacters=120_000;
    public async Task<AssembledContext> AssembleAsync(string message,string workspacePath,CancellationToken ct=default,string? runId=null)
    {
        var root=Path.GetFullPath(workspacePath);var builder=new StringBuilder();var sources=new List<ContextSource>();var instructions=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var truncated=false;
        foreach(Match match in ReferencePattern().Matches(message)){var kind=match.Groups[1].Value.ToLowerInvariant();var relative=match.Groups[2].Value.Trim('"');var path=WorkspacePathGuard.ResolveReadable(root,relative);if(kind=="file"){await AppendFile(path,"file",builder,sources,ct);CollectAgents(root,Path.GetDirectoryName(path)!,instructions);}else if(Directory.Exists(path)){foreach(var file in Directory.EnumerateFiles(path,"*",SearchOption.AllDirectories).Where(x=>!IsExcluded(x)&&IsSafeTextFile(x)).Take(200)){if(builder.Length>=MaxCharacters){truncated=true;break;}await AppendFile(file,"folder",builder,sources,ct);}CollectAgents(root,path,instructions);}}
        var rootAgents=Path.Combine(root,"AGENTS.md");if(File.Exists(rootAgents))instructions.Add(rootAgents);
        foreach(var path in instructions.OrderBy(x=>x.Length)){if(builder.Length>=MaxCharacters){truncated=true;break;}await AppendFile(path,"instructions",builder,sources,ct);}
        if(message.Contains("@diff",StringComparison.OrdinalIgnoreCase)){string? output=null;if(Directory.Exists(Path.Combine(root,".git")))output=(await git.DiffAsync(root,false,ct)).Output;else if(Directory.Exists(Path.Combine(root,".svn")))output=(await svn.DiffAsync(root,ct)).Output;if(output is not null)AppendSection("diff",root,output,builder,sources);}
        if(builder.Length>MaxCharacters){builder.Length=MaxCharacters;truncated=true;}
        if(builder.Length>CompressionThreshold)
        {
            var original=builder.ToString();var compressed=Compress(original);builder.Clear();builder.Append(compressed);truncated=true;
            if(snapshots is not null)await snapshots.SaveAsync(new ContextSnapshot(Guid.NewGuid().ToString("N"),runId,Hash(original),Hash(compressed),original.Length,compressed.Length,JsonSerializer.Serialize(sources),compressed,DateTimeOffset.UtcNow),ct);
        }
        var prompt=builder.Length==0?message:$"{message}\n\n<context trust=\"untrusted-repository-content\">\n{builder}\n</context>";return new(prompt,sources,(int)Math.Ceiling(builder.Length/4d),truncated);
    }
    private static async Task AppendFile(string path,string kind,StringBuilder builder,List<ContextSource> sources,CancellationToken ct){if(!File.Exists(path)||new FileInfo(path).Length>2_000_000||!IsSafeTextFile(path))return;var content=await File.ReadAllTextAsync(path,ct);AppendSection(kind,path,content,builder,sources);}
    private static void AppendSection(string kind,string path,string content,StringBuilder builder,List<ContextSource> sources){builder.Append("<source kind=\"").Append(kind).Append("\" path=\"").Append(System.Security.SecurityElement.Escape(path)).AppendLine("\">").AppendLine(content).AppendLine("</source>");sources.Add(new(kind,path,content.Length));}
    private static void CollectAgents(string root,string directory,HashSet<string> result){var current=new DirectoryInfo(directory);var rootInfo=new DirectoryInfo(root);while(current is not null&&current.FullName.StartsWith(rootInfo.FullName,StringComparison.OrdinalIgnoreCase)){var path=Path.Combine(current.FullName,"AGENTS.md");if(File.Exists(path))result.Add(path);if(string.Equals(current.FullName,rootInfo.FullName,StringComparison.OrdinalIgnoreCase))break;current=current.Parent;}}
    private static bool IsExcluded(string path)=>path.Split(Path.DirectorySeparatorChar).Any(x=>x is ".git" or ".svn" or "node_modules" or "bin" or "obj" or "target" or "dist");
    private static bool IsSafeTextFile(string path){try{WorkspacePathGuard.ResolveReadable(Path.GetDirectoryName(path)!,path);using var stream=File.OpenRead(path);var buffer=new byte[Math.Min(8192,(int)stream.Length)];var count=stream.Read(buffer);return !buffer.AsSpan(0,count).Contains((byte)0);}catch{return false;}}
    private static string Compress(string content)
    {
        var output=new StringBuilder();foreach(var section in content.Split("</source>",StringSplitOptions.RemoveEmptyEntries)){if(output.Length>=CompressedCharacters)break;var remaining=CompressedCharacters-output.Length;var value=section+"</source>";if(value.Length<=remaining){output.Append(value);continue;}var head=Math.Min(Math.Max(0,remaining-2200),6000);var tail=Math.Min(2000,Math.Max(0,remaining-head-120));output.Append(value.AsSpan(0,head)).AppendLine("\n[context source compressed]").Append(value.AsSpan(value.Length-tail,tail));}if(output.Length>CompressedCharacters)output.Length=CompressedCharacters;return output.ToString();
    }
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    [GeneratedRegex("@(file|folder)(?::|\\s+)(\\\"[^\\\"]+\\\"|[^\\s]+)",RegexOptions.IgnoreCase)]private static partial Regex ReferencePattern();
}
