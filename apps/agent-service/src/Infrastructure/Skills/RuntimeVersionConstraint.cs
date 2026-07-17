using System.Text.RegularExpressions;

namespace AgentService.Infrastructure.Skills;

public static partial class RuntimeVersionConstraint
{
    public static bool IsSatisfied(Version version, string? constraint)
    {
        if (string.IsNullOrWhiteSpace(constraint) || constraint.Trim() == "*")
            return true;

        foreach (Match match in ConstraintPattern().Matches(constraint))
        {
            var op = match.Groups[1].Value;
            var expected = ParseVersion(match.Groups[2].Value);
            var comparison = version.CompareTo(expected);
            var satisfied = op switch
            {
                ">" => comparison > 0,
                ">=" => comparison >= 0,
                "<" => comparison < 0,
                "<=" => comparison <= 0,
                "=" or "==" => SameSpecifiedComponents(version, match.Groups[2].Value, expected),
                _ => SameSpecifiedComponents(version, match.Groups[2].Value, expected),
            };
            if (!satisfied)
                return false;
        }

        return ConstraintPattern().IsMatch(constraint);
    }

    public static Version ParseVersion(string value)
    {
        var normalized = value.Trim().TrimStart('v', 'V');
        var parts = normalized.Split('.');
        return parts.Length switch
        {
            1 => new Version(int.Parse(parts[0]), 0),
            2 => new Version(int.Parse(parts[0]), int.Parse(parts[1])),
            _ => Version.Parse(string.Join('.', parts.Take(4))),
        };
    }

    private static bool SameSpecifiedComponents(
        Version actual,
        string rawExpected,
        Version expected)
    {
        var count = rawExpected.Trim().TrimStart('v', 'V').Split('.').Length;
        return actual.Major == expected.Major &&
               (count < 2 || actual.Minor == expected.Minor) &&
               (count < 3 || actual.Build == expected.Build) &&
               (count < 4 || actual.Revision == expected.Revision);
    }

    [GeneratedRegex(@"(?:^|\s)(>=|<=|>|<|==|=)?\s*(v?\d+(?:\.\d+){0,3})(?=\s|$)")]
    private static partial Regex ConstraintPattern();
}
