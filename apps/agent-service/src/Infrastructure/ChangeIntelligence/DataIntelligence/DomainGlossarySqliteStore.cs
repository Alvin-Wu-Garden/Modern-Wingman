using System.Data;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.ChangeIntelligence.DataIntelligence;

public sealed class DomainGlossarySqliteStore(IDbContextFactory<AppDbContext> dbFactory) : IDomainGlossaryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<DomainGlossaryEntry>> ListAsync(string projectId, GlossaryProposalStatus? status = null, CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await EnsureSchemaAsync(db, cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT "Id","ProjectId","Term","Definition","AliasesJson","Sensitivity","Status","EvidenceKeysJson",
                   "ProposedBy","ReviewedBy","ReviewComment","CreatedAt","UpdatedAt"
            FROM "domain_glossary_entries"
            WHERE "ProjectId" = $projectId AND ($status IS NULL OR "Status" = $status)
            ORDER BY CASE "Status" WHEN 'Proposed' THEN 0 WHEN 'Confirmed' THEN 1 ELSE 2 END, "Term" COLLATE NOCASE
            """;
        Add(command, "$projectId", projectId); Add(command, "$status", status?.ToString());
        await OpenAsync(command, cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<DomainGlossaryEntry>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    public async Task<DomainGlossaryEntry?> GetAsync(string projectId, string id, CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId); ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await EnsureSchemaAsync(db, cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT "Id","ProjectId","Term","Definition","AliasesJson","Sensitivity","Status","EvidenceKeysJson",
                   "ProposedBy","ReviewedBy","ReviewComment","CreatedAt","UpdatedAt"
            FROM "domain_glossary_entries" WHERE "ProjectId"=$projectId AND "Id"=$id
            """;
        Add(command, "$projectId", projectId); Add(command, "$id", id);
        await OpenAsync(command, cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<DomainGlossaryEntry> ProposeAsync(string projectId, ProposeGlossaryEntryRequest request, CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId); ValidateProposal(request);
        var now = DateTimeOffset.UtcNow;
        var entry = new DomainGlossaryEntry(
            Guid.NewGuid().ToString("N"), projectId, request.Term.Trim(), request.Definition.Trim(), Normalize(request.Aliases),
            request.Sensitivity, GlossaryProposalStatus.Proposed, Normalize(request.EvidenceKeys), request.ProposedBy.Trim(),
            null, null, now, now);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await EnsureSchemaAsync(db, cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            INSERT INTO "domain_glossary_entries"
            ("Id","ProjectId","Term","Definition","AliasesJson","Sensitivity","Status","EvidenceKeysJson","ProposedBy","CreatedAt","UpdatedAt")
            VALUES ($id,$projectId,$term,$definition,$aliases,$sensitivity,$status,$evidence,$proposedBy,$createdAt,$updatedAt)
            """;
        Bind(command, entry);
        await OpenAsync(command, cancellationToken);
        try { await command.ExecuteNonQueryAsync(cancellationToken); }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        { throw new InvalidOperationException("此專案已存在相同的領域詞彙。", ex); }
        return entry;
    }

    public async Task<DomainGlossaryEntry> ReviewAsync(string projectId, string id, ReviewGlossaryEntryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.ReviewedBy);
        var existing = await GetAsync(projectId, id, cancellationToken) ?? throw new KeyNotFoundException("找不到 Glossary proposal。");
        if (existing.Status != GlossaryProposalStatus.Proposed) throw new InvalidOperationException("此 proposal 已完成審核，不可重複改寫確認紀錄。");
        var updated = existing with
        {
            Definition = string.IsNullOrWhiteSpace(request.Definition) ? existing.Definition : request.Definition.Trim(),
            Aliases = request.Aliases is null ? existing.Aliases : Normalize(request.Aliases),
            Sensitivity = request.Sensitivity ?? existing.Sensitivity,
            Status = request.Confirm ? GlossaryProposalStatus.Confirmed : GlossaryProposalStatus.Rejected,
            ReviewedBy = request.ReviewedBy.Trim(), ReviewComment = request.Comment?.Trim(), UpdatedAt = DateTimeOffset.UtcNow,
        };
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await EnsureSchemaAsync(db, cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            UPDATE "domain_glossary_entries" SET "Definition"=$definition,"AliasesJson"=$aliases,"Sensitivity"=$sensitivity,
              "Status"=$status,"ReviewedBy"=$reviewedBy,"ReviewComment"=$comment,"UpdatedAt"=$updatedAt
            WHERE "ProjectId"=$projectId AND "Id"=$id AND "Status"='Proposed'
            """;
        Add(command, "$definition", updated.Definition); Add(command, "$aliases", JsonSerializer.Serialize(updated.Aliases, JsonOptions));
        Add(command, "$sensitivity", updated.Sensitivity.ToString()); Add(command, "$status", updated.Status.ToString());
        Add(command, "$reviewedBy", updated.ReviewedBy); Add(command, "$comment", updated.ReviewComment); Add(command, "$updatedAt", updated.UpdatedAt.ToString("O"));
        Add(command, "$projectId", projectId); Add(command, "$id", id);
        await OpenAsync(command, cancellationToken);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("Glossary proposal 已被其他使用者審核，請重新整理。");
        return updated;
    }

    private static async Task EnsureSchemaAsync(AppDbContext db, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "domain_glossary_entries" (
              "Id" TEXT NOT NULL PRIMARY KEY,
              "ProjectId" TEXT NOT NULL,
              "Term" TEXT NOT NULL COLLATE NOCASE,
              "Definition" TEXT NOT NULL,
              "AliasesJson" TEXT NOT NULL DEFAULT '[]',
              "Sensitivity" TEXT NOT NULL DEFAULT 'Unknown',
              "Status" TEXT NOT NULL DEFAULT 'Proposed',
              "EvidenceKeysJson" TEXT NOT NULL DEFAULT '[]',
              "ProposedBy" TEXT NOT NULL,
              "ReviewedBy" TEXT,
              "ReviewComment" TEXT,
              "CreatedAt" TEXT NOT NULL,
              "UpdatedAt" TEXT NOT NULL,
              UNIQUE("ProjectId","Term")
            );
            CREATE INDEX IF NOT EXISTS "IX_domain_glossary_project_status_term"
              ON "domain_glossary_entries"("ProjectId","Status","Term");
            """, ct);
    }

    private static void ValidateProjectId(string projectId) => ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
    private static void ValidateProposal(ProposeGlossaryEntryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Term); ArgumentException.ThrowIfNullOrWhiteSpace(request.Definition); ArgumentException.ThrowIfNullOrWhiteSpace(request.ProposedBy);
        if (request.Term.Length > 200 || request.Definition.Length > 4000) throw new ArgumentException("詞彙或定義超過允許長度。", nameof(request));
        if (request.EvidenceKeys is null || !request.EvidenceKeys.Any(key => !string.IsNullOrWhiteSpace(key)))
            throw new ArgumentException("Glossary proposal 至少需要一個可追溯的圖譜 evidence key。", nameof(request));
        if (request.EvidenceKeys.Any(key => key.Length > 1000))
            throw new ArgumentException("Glossary evidence key 超過允許長度。", nameof(request));
    }
    private static IReadOnlyList<string> Normalize(IEnumerable<string>? values) => values?.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList() ?? [];
    private static void Bind(System.Data.Common.DbCommand command, DomainGlossaryEntry entry)
    {
        Add(command, "$id", entry.Id); Add(command, "$projectId", entry.ProjectId); Add(command, "$term", entry.Term); Add(command, "$definition", entry.Definition);
        Add(command, "$aliases", JsonSerializer.Serialize(entry.Aliases, JsonOptions)); Add(command, "$sensitivity", entry.Sensitivity.ToString()); Add(command, "$status", entry.Status.ToString());
        Add(command, "$evidence", JsonSerializer.Serialize(entry.EvidenceKeys, JsonOptions)); Add(command, "$proposedBy", entry.ProposedBy); Add(command, "$createdAt", entry.CreatedAt.ToString("O")); Add(command, "$updatedAt", entry.UpdatedAt.ToString("O"));
    }
    private static void Add(System.Data.Common.DbCommand command, string name, object? value) { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter); }
    private static async Task OpenAsync(System.Data.Common.DbCommand command, CancellationToken ct) { if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(ct); }
    private static DomainGlossaryEntry Read(System.Data.Common.DbDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Deserialize(reader.GetString(4)),
        Enum.TryParse<GlossarySensitivity>(reader.GetString(5), out var sensitivity) ? sensitivity : GlossarySensitivity.Unknown,
        Enum.TryParse<GlossaryProposalStatus>(reader.GetString(6), out var status) ? status : GlossaryProposalStatus.Proposed,
        Deserialize(reader.GetString(7)), reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
        DateTimeOffset.Parse(reader.GetString(11)), DateTimeOffset.Parse(reader.GetString(12)));
    private static IReadOnlyList<string> Deserialize(string value) => JsonSerializer.Deserialize<List<string>>(value, JsonOptions) ?? [];
}
