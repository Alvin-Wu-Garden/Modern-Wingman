namespace AgentService.Infrastructure.Tools;

public static class UntrustedContent
{
    public static string Wrap(string source, string content) =>
        $"<external-content trust=\"untrusted\" source=\"{System.Security.SecurityElement.Escape(source)}\">\n" +
        content +
        "\n</external-content>";
}
