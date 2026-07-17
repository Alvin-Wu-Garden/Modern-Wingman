using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Telemetry;

public sealed class AgentEvalService(IDbContextFactory<AppDbContext> factory) : IAgentEvalService
{
    public async Task<AgentEvalSummary> GetSummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (to <= from) throw new ArgumentException("The eval end time must be after the start time.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var runs = db.Runs.AsNoTracking().Where(run => run.CreatedAt >= from && run.CreatedAt <= to);
        var totalRuns = await runs.CountAsync(ct);
        var completedRuns = await runs.CountAsync(run => run.Status == nameof(RunStatus.Completed), ct);
        var recoveredRuns = await runs.CountAsync(run => run.Status == nameof(RunStatus.Completed) && run.Error != null, ct);
        var toolCalls = db.AiToolCallLogs.AsNoTracking().Where(call => call.StartedAt >= from && call.StartedAt <= to);
        var toolCount = await toolCalls.CountAsync(ct);
        var toolSucceeded = await toolCalls.CountAsync(call => call.Status == "succeeded", ct);
        var verifyEvents = await db.RunEvents.AsNoTracking()
            .Where(evt => evt.EventType == "run:verify" && evt.Timestamp >= from && evt.Timestamp <= to)
            .Select(evt => evt.PayloadJson)
            .ToListAsync(ct);
        var verifyPassed = verifyEvents.Count(payload =>
        {
            try { return JsonDocument.Parse(payload).RootElement.TryGetProperty("success", out var success) && success.GetBoolean(); }
            catch (JsonException) { return false; }
        });
        return new(totalRuns, completedRuns, Rate(completedRuns, totalRuns), toolCount,
            Rate(toolSucceeded, toolCount), verifyEvents.Count, Rate(verifyPassed, verifyEvents.Count),
            recoveredRuns, from, to);
    }

    private static double Rate(int numerator, int denominator) =>
        denominator == 0 ? 0 : Math.Round((double)numerator / denominator, 4);
}
