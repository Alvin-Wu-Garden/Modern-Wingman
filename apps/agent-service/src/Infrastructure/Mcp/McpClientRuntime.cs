using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;

namespace AgentService.Infrastructure.Mcp;

public sealed class McpClientRuntime(IHttpClientFactory httpClientFactory) : IMcpClientRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<McpToolDefinition>> DiscoverToolsAsync(McpServerDefinition server, CancellationToken ct = default)
    {
        using var response = await InvokeWithReconnectAsync(server, "tools/list", new { }, ct);
        if (!response.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("tools", out var tools))
            throw RpcError(response.RootElement);
        return tools.EnumerateArray().Select(tool => new McpToolDefinition(
            server.Id,
            server.Name,
            tool.GetProperty("name").GetString() ?? throw new InvalidDataException("MCP tool name is missing."),
            tool.TryGetProperty("description", out var description) ? description.GetString() : null,
            tool.TryGetProperty("inputSchema", out var schema) ? schema.Clone() : JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone(),
            tool.TryGetProperty("annotations", out var annotations) &&
            annotations.TryGetProperty("readOnlyHint", out var hint) && hint.ValueKind == JsonValueKind.True)).ToList();
    }

    public async Task<McpCallResult> CallToolAsync(McpServerDefinition server, string toolName, JsonElement arguments, CancellationToken ct = default)
    {
        try
        {
            using var response = await InvokeAsync(server, "tools/call", new { name=toolName, arguments }, ct);
            if (!response.RootElement.TryGetProperty("result", out var result))
                return new(false, "", RpcError(response.RootElement).Message);
            var isError = result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
            var output = result.TryGetProperty("content", out var content)
                ? string.Join(Environment.NewLine, content.EnumerateArray().Select(FormatContent))
                : result.GetRawText();
            return new(!isError, output, isError ? output : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, "", ex.Message);
        }
    }

    private async Task<JsonDocument> InvokeAsync(McpServerDefinition server, string method, object parameters, CancellationToken ct)
    {
        Validate(server);
        return server.Transport == McpTransport.Stdio
            ? await InvokeStdioAsync(server, method, parameters, ct)
            : await InvokeHttpAsync(server, method, parameters, ct);
    }

    private async Task<JsonDocument> InvokeWithReconnectAsync(McpServerDefinition server,string method,object parameters,CancellationToken ct)
    {
        try{return await InvokeAsync(server,method,parameters,ct);}catch(Exception ex)when(ex is HttpRequestException or IOException or EndOfStreamException){await Task.Delay(200,ct);return await InvokeAsync(server,method,parameters,ct);}
    }

    private static async Task<JsonDocument> InvokeStdioAsync(McpServerDefinition server, string method, object parameters, CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName=server.Command!, UseShellExecute=false, CreateNoWindow=true,
            RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true,
        };
        foreach (var argument in server.Arguments) start.ArgumentList.Add(argument);
        var path=Environment.GetEnvironmentVariable("PATH"); var root=Environment.GetEnvironmentVariable("SystemRoot");
        start.Environment.Clear();
        if (path is not null) start.Environment["PATH"]=path;
        if (root is not null) start.Environment["SystemRoot"]=root;
        foreach (var (name,value) in server.Environment) start.Environment[name]=value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start MCP server.");
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await WriteAsync(process, 1, "initialize", new { protocolVersion="2025-06-18", capabilities=new { }, clientInfo=new { name="Modern Wingman", version="1.0" } });
            await ReadResponseAsync(process, 1, ct);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc="2.0", method="notifications/initialized" }, JsonOptions));
            await WriteAsync(process, 2, method, parameters);
            return await ReadResponseAsync(process, 2, ct);
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            try{if(!process.HasExited)process.Kill(true);}catch{}
            string stderr="";try{stderr=await stderrTask.WaitAsync(TimeSpan.FromSeconds(2));}catch{}
            if(stderr.Length>4000)stderr=stderr[..4000];
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)?ex.Message:$"{ex.Message} MCP stderr: {stderr.Trim()}",ex);
        }
        finally
        {
            try { process.StandardInput.Close(); if (!process.HasExited) process.Kill(true); } catch { }
            try { await stderrTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }
    }

    private async Task<JsonDocument> InvokeHttpAsync(McpServerDefinition server, string method, object parameters, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("mcp");
        string? sessionId = null;
        var initialized = await SendHttpAsync(client, server.Url!, 1, "initialize", new { protocolVersion="2025-06-18", capabilities=new { }, clientInfo=new { name="Modern Wingman", version="1.0" } }, null, ct);
        using (initialized.Document)
        {
            sessionId = initialized.SessionId;
            if (!initialized.Document.RootElement.TryGetProperty("result", out _)) throw RpcError(initialized.Document.RootElement);
        }
        using var notification = new HttpRequestMessage(HttpMethod.Post, server.Url!) { Content=JsonContent.Create(new { jsonrpc="2.0", method="notifications/initialized" }, options:JsonOptions) };
        notification.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        using var ignored = await client.SendAsync(notification, ct);
        var response = await SendHttpAsync(client, server.Url!, 2, method, parameters, sessionId, ct);
        using (response.Document)
            return JsonDocument.Parse(response.Document.RootElement.GetRawText());
    }

    private static async Task<(JsonDocument Document, string? SessionId)> SendHttpAsync(HttpClient client, string url, int id, string method, object parameters, string? sessionId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content=JsonContent.Create(new { jsonrpc="2.0", id, method, @params=parameters }, options:JsonOptions) };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (sessionId is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        var json = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? string.Join("", body.Split('\n').Where(x=>x.StartsWith("data:",StringComparison.Ordinal)).Select(x=>x[5..].Trim()))
            : body;
        return (JsonDocument.Parse(json), response.Headers.TryGetValues("Mcp-Session-Id",out var values)?values.FirstOrDefault():sessionId);
    }

    private static Task WriteAsync(Process process, int id, string method, object parameters) =>
        process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc="2.0", id, method, @params=parameters }, JsonOptions));

    private static async Task<JsonDocument> ReadResponseAsync(Process process, int id, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new EndOfStreamException("MCP server closed its output stream.");
            if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{')) continue;
            var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("id", out var responseId) && responseId.TryGetInt32(out var value) && value == id) return document;
            document.Dispose();
        }
    }

    private static void Validate(McpServerDefinition server)
    {
        if (!server.Enabled) throw new InvalidOperationException("MCP server is disabled.");
        if (server.Transport == McpTransport.Stdio && (string.IsNullOrWhiteSpace(server.Command) || server.Command.IndexOfAny(['\r','\n','\0']) >= 0)) throw new InvalidOperationException("Invalid MCP stdio command.");
        if (server.Transport != McpTransport.Stdio && (!Uri.TryCreate(server.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))) throw new InvalidOperationException("Invalid MCP HTTP URL.");
        if (server.Arguments.Any(x=>x.Contains('\0')) || server.Environment.Keys.Any(x=>x.Contains('=')||x.Contains('\0'))) throw new InvalidOperationException("Invalid MCP process configuration.");
    }

    private static string FormatContent(JsonElement content) => content.TryGetProperty("text", out var text) ? text.GetString() ?? "" : content.GetRawText();
    private static InvalidOperationException RpcError(JsonElement response) => new(response.TryGetProperty("error", out var error) ? error.GetRawText() : "Invalid MCP response.");
}
