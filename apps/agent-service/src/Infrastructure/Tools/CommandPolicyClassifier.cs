using System.Text.RegularExpressions;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.Tools;

public static partial class CommandPolicyClassifier
{
    public static (AgentCapability Capabilities, AgentRiskLevel RiskLevel) Classify(
        string executable,
        IEnumerable<string> arguments) =>
        ClassifyRaw(executable + " " + string.Join(' ', arguments));

    public static (AgentCapability Capabilities, AgentRiskLevel RiskLevel) ClassifyRaw(
        string command)
    {
        var capabilities = AgentCapability.Execute;

        if (ForbiddenPattern().IsMatch(command))
        {
            return (
                capabilities | AgentCapability.Write | AgentCapability.ExternalSideEffect |
                AgentCapability.Destructive,
                AgentRiskLevel.Critical);
        }

        if (IsKnownReadOnly(command))
            return (capabilities | AgentCapability.Read, AgentRiskLevel.Low);

        capabilities |= AgentCapability.Write | AgentCapability.Network;
        if (ExternalSideEffectPattern().IsMatch(command))
            capabilities |= AgentCapability.ExternalSideEffect;
        if (DestructivePattern().IsMatch(command))
            capabilities |= AgentCapability.Destructive;

        var risk = (capabilities & (AgentCapability.ExternalSideEffect |
                                    AgentCapability.Destructive)) != 0
            ? AgentRiskLevel.High
            : AgentRiskLevel.Medium;
        return (capabilities, risk);
    }

    public static bool IsKnownReadOnly(string command) =>
        ReadOnlyPattern().IsMatch(command.Trim());

    [GeneratedRegex(
        @"^(rg|where(?:\.exe)?|git\s+(status|diff|log|show|branch(\s+--show-current)?|rev-parse)|svn\s+(status|diff|info|list|log)|dotnet\s+(--info|--version)|node(?:\.exe)?\s+--version|python(?:\.exe)?\s+--version|pwsh(?:\.exe)?\s+--version)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReadOnlyPattern();

    [GeneratedRegex(
        @"\b(git\s+push|svn\s+commit|npm\s+publish|dotnet\s+nuget\s+push|Invoke-RestMethod|Invoke-WebRequest)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExternalSideEffectPattern();

    [GeneratedRegex(
        @"\b(git\s+reset\s+--hard|git\s+clean\s+-[^\r\n]*f|Remove-Item\s+[^\r\n]*-Recurse|rd\s+/s|del\s+/[sq]|format|shutdown|Stop-Computer)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DestructivePattern();

    [GeneratedRegex(
        @"\b(git\s+push\b[^\r\n]*(--force|-f\b)|git\s+push\s+--mirror|bcdedit|Set-ExecutionPolicy|reg(?:\.exe)?\s+(add|delete)|sc(?:\.exe)?\s+(config|delete)|netsh\s+advfirewall)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenPattern();
}
