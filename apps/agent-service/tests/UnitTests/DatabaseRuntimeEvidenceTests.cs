using AgentService.Application.Models;
using AgentService.Infrastructure.ChangeIntelligence;

namespace AgentService.UnitTests;

public sealed class DatabaseRuntimeEvidenceTests
{
    private readonly StrictReadOnlyDatabaseQueryPlanValidator _validator = new();
    private readonly DatabaseRuntimeEvidenceRequestValidator _requestValidator = new();

    [Fact]
    public void RuntimeEvidence_OnlyCarriesDerivedRedactedStateAndTtl()
    {
        var observedAt = DateTimeOffset.UtcNow;
        var evidence = new RuntimeEvidence(
            "runtime-config-1", "database-runtime", "production-config", "read_configuration",
            "feature.checkout", RuntimeEvidenceState.Enabled, RuntimeEvidenceRedaction.Redacted,
            observedAt, observedAt.AddMinutes(5), MatchedRecordCount: 1, SourceUpdatedAt: observedAt.AddMinutes(-1));

        Assert.False(evidence.IsExpired(observedAt.AddMinutes(4)));
        Assert.True(evidence.IsExpired(observedAt.AddMinutes(5)));
        Assert.Equal(RuntimeEvidenceRedaction.Redacted, evidence.Redaction);
        Assert.DoesNotContain(typeof(RuntimeEvidence).GetProperties(), property => property.Name.Contains("Value", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_AllowsBoundedParameterizedSelectAgainstAllowlist()
    {
        var result = _validator.Validate(Plan(
            "SELECT Key, UpdatedAt FROM dbo.FeatureFlags WHERE Key = @key LIMIT 10"));

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(["DBO.FEATUREFLAGS"], result.ReferencedObjects);
        Assert.Equal(["@KEY"], result.ReferencedParameters);
    }

    [Theory]
    [InlineData("UPDATE dbo.FeatureFlags SET Enabled = 1 WHERE Key = @key LIMIT 10")]
    [InlineData("SELECT Key FROM dbo.FeatureFlags WHERE Key = @key; SELECT Key FROM dbo.FeatureFlags LIMIT 10")]
    [InlineData("EXEC dbo.GetFlags @key")]
    [InlineData("SELECT * FROM dbo.FeatureFlags WHERE Key = @key LIMIT 10")]
    [InlineData("SELECT Key FROM dbo.FeatureFlags WHERE Key = @key LIMIT 1001")]
    public void Validate_RejectsAnythingOutsideStrictReadOnlySubset(string sql)
    {
        var result = _validator.Validate(Plan(sql));

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Validate_RequiresDeclaredParametersRowLimitsAndCompleteAllowlist()
    {
        var plan = new DatabaseReadOnlyQueryPlan(
            "SELECT Key FROM dbo.FeatureFlags WHERE Key = @key",
            [],
            [new DatabaseQueryObjectAllowlist("dbo", "FeatureFlags", new HashSet<string>())],
            0,
            TimeSpan.FromSeconds(30));

        var result = _validator.Validate(plan);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("參數"));
        Assert.Contains(result.Errors, error => error.Contains("RowLimit"));
        Assert.Contains(result.Errors, error => error.Contains("allowlist"));
    }

    [Fact]
    public void Validate_RejectsUndeclaredOrUnboundParameterAndUnapprovedObject()
    {
        var result = _validator.Validate(Plan(
            "SELECT Key FROM dbo.OtherFlags WHERE Key = @different LIMIT 10"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("參數"));
        Assert.Contains(result.Errors, error => error.Contains("OTHERFLAGS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsColumnOutsideAllowlist()
    {
        var result = _validator.Validate(Plan(
            "SELECT SecretValue FROM dbo.FeatureFlags WHERE Key = @key LIMIT 10"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("column allowlist"));
    }

    [Fact]
    public void Validate_AllowsStructuredConfigurationLookupButRejectsUnboundedDiscovery()
    {
        var allowed = _requestValidator.Validate(new DatabaseConfigurationLookup(
            Key: "feature.checkout", Environment: "production", MaxResults: 1));
        var rejected = _requestValidator.Validate(new DatabaseConfigurationLookup(MaxResults: 101));

        Assert.True(allowed.IsValid, string.Join(Environment.NewLine, allowed.Errors));
        Assert.False(rejected.IsValid);
        Assert.Contains(rejected.Errors, error => error.Contains("至少需要"));
        Assert.Contains(rejected.Errors, error => error.Contains("MaxResults"));
    }

    [Fact]
    public void Validate_AllowsRestrictedWithSelectAndTerminalDelimiter()
    {
        var result = _validator.Validate(Plan(
            "WITH ActiveFlags AS (SELECT Key, UpdatedAt FROM dbo.FeatureFlags WHERE Key = @key LIMIT 10) SELECT Key, UpdatedAt FROM ActiveFlags LIMIT 10;"));

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    private static DatabaseReadOnlyQueryPlan Plan(string sql) => new(
        sql,
        [new DatabaseQueryParameter("@key")],
        [new DatabaseQueryObjectAllowlist("dbo", "FeatureFlags", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Key", "UpdatedAt", "Enabled" })],
        10,
        TimeSpan.FromSeconds(30));
}
