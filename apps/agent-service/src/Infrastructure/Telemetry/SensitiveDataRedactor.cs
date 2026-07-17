using System.Text.RegularExpressions;
using AgentService.Application.Contracts;
namespace AgentService.Infrastructure.Telemetry;
public sealed partial class SensitiveDataRedactor : ISensitiveDataRedactor
{
    public string Redact(string value)
    {
        if(string.IsNullOrEmpty(value))return value;
        var result=Bearer().Replace(value,"$1[REDACTED]");
        result=JsonSecret().Replace(result,"$1[REDACTED]$3");
        result=UrlCredential().Replace(result,"$1[REDACTED]@");
        return result;
    }
    [GeneratedRegex(@"(?i)(bearer\s+|token[=:]\s*|api[-_]?key[=:]\s*|password[=:]\s*)[^\s,;\""""']+")]
    private static partial Regex Bearer();
    [GeneratedRegex("(?i)(\\\"(?:secret|token|password|apiKey|api_key)\\\"\\s*:\\s*\\\")[^\\\"]*(\\\")")]
    private static partial Regex JsonSecret();
    [GeneratedRegex(@"(https?://[^:/@\s]+:)[^@\s]+@",RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredential();
}
