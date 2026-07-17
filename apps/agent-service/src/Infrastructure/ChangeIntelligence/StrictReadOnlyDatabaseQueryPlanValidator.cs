using AgentService.Application.Contracts;
using AgentService.Application.Models;

namespace AgentService.Infrastructure.ChangeIntelligence;

/// <summary>
/// Dialect-agnostic SQL fallback gate. It deliberately accepts a small common subset instead of
/// attempting to sanitise arbitrary SQL. Database Runtime Plugins must still use a read-only
/// account/transaction and enforce the returned plan at execution time.
/// </summary>
public sealed class StrictReadOnlyDatabaseQueryPlanValidator : IReadOnlyDatabaseQueryPlanValidator
{
    private const int MaximumRowLimit = 1_000;
    private const int MaximumResultBytes = 1_048_576;
    private static readonly HashSet<string> ForbiddenKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "CREATE", "ALTER", "DROP", "TRUNCATE",
        "GRANT", "REVOKE", "DENY", "EXEC", "EXECUTE", "CALL", "PRAGMA", "ATTACH",
        "DETACH", "VACUUM", "REINDEX", "ANALYZE", "SET", "USE", "BEGIN", "COMMIT",
        "ROLLBACK", "SAVEPOINT", "RELEASE", "LOCK", "COPY", "LOAD", "UNLOAD", "INTO",
        "OUTFILE", "INFILE", "SHOW", "DESCRIBE", "EXPLAIN"
    };

    public DatabaseQueryPlanValidationResult Validate(DatabaseReadOnlyQueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var errors = new List<string>();
        var references = new List<string>();
        var parameters = new List<string>();

        if (string.IsNullOrWhiteSpace(plan.Statement))
            errors.Add("SQL statement 不可為空白。");
        if (plan.RowLimit is < 1 or > MaximumRowLimit)
            errors.Add($"RowLimit 必須介於 1 到 {MaximumRowLimit}。");
        if (plan.Timeout <= TimeSpan.Zero || plan.Timeout > TimeSpan.FromMinutes(2))
            errors.Add("Timeout 必須大於零且不超過兩分鐘。");
        if (plan.MaxResultBytes is < 1 or > MaximumResultBytes)
            errors.Add($"MaxResultBytes 必須介於 1 到 {MaximumResultBytes}。");
        if (plan.Parameters.Count == 0)
            errors.Add("Fallback SQL 必須宣告至少一個具名參數。");
        if (plan.ObjectAllowlist.Count == 0)
            errors.Add("Fallback SQL 必須提供 schema、object 與 column allowlist。");

        var declaredParameters = plan.Parameters
            .Select(parameter => parameter.Name?.Trim() ?? string.Empty)
            .ToList();
        if (declaredParameters.Any(name => !IsParameter(name)))
            errors.Add("參數名稱必須使用 @、: 或 $ 前綴，且只含英數與底線。");
        if (declaredParameters.Distinct(StringComparer.OrdinalIgnoreCase).Count() != declaredParameters.Count)
            errors.Add("參數名稱不可重複。");

        var allowedObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in plan.ObjectAllowlist)
        {
            if (string.IsNullOrWhiteSpace(item.Schema) || string.IsNullOrWhiteSpace(item.ObjectName) || item.AllowedColumns.Count == 0)
            {
                errors.Add("每個 allowlist object 都必須指定 schema、object 與至少一個 column。");
                continue;
            }

            allowedObjects.Add($"{item.Schema.Trim()}.{item.ObjectName.Trim()}");
        }

        if (errors.Count > 0)
            return Result(false, errors, references, parameters);

        var parse = Parse(plan.Statement);
        if (parse.Error is not null)
            return Result(false, [parse.Error], references, parameters);

        var tokens = parse.Tokens.ToList();
        var delimiterIndex = tokens.IndexOf(";");
        if (delimiterIndex >= 0)
        {
            if (delimiterIndex == tokens.Count - 1)
                tokens.RemoveAt(delimiterIndex);
            else
                errors.Add("Fallback SQL 不允許 multi-statement 或 statement delimiter。");
        }
        if (tokens.Count == 0 || (tokens[0] is not "SELECT" and not "WITH"))
            errors.Add("只允許單一 SELECT 或受限 WITH ... SELECT statement。");
        if (tokens.Any(ForbiddenKeywords.Contains))
            errors.Add("SQL 含有不允許的寫入、DDL、管理或 procedure 關鍵字。");
        if (tokens.Any(token => token == "*"))
            errors.Add("Fallback SQL 不允許 wildcard projection；必須列出 allowlist 欄位。");
        if (tokens.Count(token => token == ";") > 0)
            errors.Add("Fallback SQL 不允許 multi-statement 或 statement delimiter。");

        parameters.AddRange(tokens.Where(IsParameter).Distinct(StringComparer.OrdinalIgnoreCase));
        if (!new HashSet<string>(parameters, StringComparer.OrdinalIgnoreCase)
                .SetEquals(declaredParameters))
            errors.Add("SQL 具名參數必須與 query plan 宣告完全一致。");

        if (!HasBoundedLiteralLimit(tokens, plan.RowLimit))
            errors.Add("SQL 必須包含不超過 RowLimit 的 literal LIMIT、TOP 或 FETCH FIRST/NEXT row limit。");

        ValidateProjectionColumns(tokens, plan.ObjectAllowlist, errors);

        var cteNames = GetCteNames(tokens);
        foreach (var reference in GetObjectReferences(tokens))
        {
            if (cteNames.Contains(reference))
                continue;

            references.Add(reference);
            if (!reference.Contains('.', StringComparison.Ordinal) || !allowedObjects.Contains(reference))
                errors.Add($"資料物件 '{reference}' 不在 schema-qualified allowlist 內。");
        }

        if (references.Count == 0)
            errors.Add("Fallback SQL 必須從 allowlist 中的資料物件讀取。");

        return Result(errors.Count == 0, errors, references, parameters);
    }

    private static DatabaseQueryPlanValidationResult Result(bool valid, IReadOnlyList<string> errors, IReadOnlyList<string> references, IReadOnlyList<string> parameters) =>
        new(valid, errors.Distinct(StringComparer.Ordinal).ToList(), references.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), parameters.Distinct(StringComparer.OrdinalIgnoreCase).ToList());

    private static bool IsParameter(string token) => token.Length > 1
        && token[0] is '@' or ':' or '$'
        && (char.IsLetter(token[1]) || token[1] == '_')
        && token.Skip(2).All(character => char.IsLetterOrDigit(character) || character == '_');

    private static bool HasBoundedLiteralLimit(IReadOnlyList<string> tokens, int rowLimit)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            var isLimit = tokens[index] is "LIMIT" or "TOP";
            var isFetch = tokens[index] is "FIRST" or "NEXT"
                && index > 0 && tokens[index - 1] == "FETCH";
            if (!isLimit && !isFetch)
                continue;

            var valueIndex = index + 1;
            if (valueIndex < tokens.Count && tokens[valueIndex] == "(")
                valueIndex++;
            if (valueIndex < tokens.Count && int.TryParse(tokens[valueIndex], out var value) && value is >= 1 && value <= rowLimit)
                return true;
        }

        return false;
    }

    private static HashSet<string> GetCteNames(IReadOnlyList<string> tokens)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tokens.Count == 0 || tokens[0] != "WITH")
            return names;

        for (var index = 1; index + 1 < tokens.Count; index++)
        {
            if (IsIdentifier(tokens[index]) && tokens[index + 1] == "AS")
                names.Add(tokens[index]);
        }

        return names;
    }

    private static IEnumerable<string> GetObjectReferences(IReadOnlyList<string> tokens)
    {
        for (var index = 0; index + 1 < tokens.Count; index++)
        {
            if (tokens[index] is not ("FROM" or "JOIN"))
                continue;

            var candidate = tokens[index + 1];
            if (candidate is "(")
                continue;
            if (IsIdentifier(candidate))
                yield return candidate;
        }
    }

    private static bool IsIdentifier(string token) => token.Length > 0
        && token.All(character => char.IsLetterOrDigit(character) || character is '_' or '.');

    private static void ValidateProjectionColumns(
        IReadOnlyList<string> tokens,
        IReadOnlyList<DatabaseQueryObjectAllowlist> objectAllowlist,
        ICollection<string> errors)
    {
        var allowedColumns = objectAllowlist
            .SelectMany(item => item.AllowedColumns)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var selectIndex = 0; selectIndex < tokens.Count; selectIndex++)
        {
            if (tokens[selectIndex] != "SELECT")
                continue;

            var fromIndex = -1;
            for (var index = selectIndex + 1; index < tokens.Count; index++)
            {
                if (tokens[index] == "FROM")
                {
                    fromIndex = index;
                    break;
                }
            }
            if (fromIndex < 0)
            {
                errors.Add("每個 SELECT 都必須明確指定 FROM allowlist object。");
                continue;
            }

            var expression = new List<string>();
            for (var index = selectIndex + 1; index <= fromIndex; index++)
            {
                if (index == fromIndex || tokens[index] == ",")
                {
                    ValidateProjectionExpression(expression, allowedColumns, errors);
                    expression.Clear();
                    continue;
                }
                expression.Add(tokens[index]);
            }
        }
    }

    private static void ValidateProjectionExpression(
        IReadOnlyList<string> expression,
        IReadOnlySet<string> allowedColumns,
        ICollection<string> errors)
    {
        if (expression.Count == 0)
        {
            errors.Add("SELECT projection 不可為空白。");
            return;
        }

        var column = expression.Count switch
        {
            1 => expression[0],
            3 when expression[1] == "AS" => expression[0],
            _ => null,
        };
        if (column is null || !IsIdentifier(column))
        {
            errors.Add("Fallback SQL 只允許明確的 allowlist column projection（可使用 AS alias）。");
            return;
        }

        var bareColumn = column[(column.LastIndexOf('.') + 1)..];
        if (!allowedColumns.Contains(bareColumn))
            errors.Add($"欄位 '{bareColumn}' 不在 column allowlist 內。");
    }

    private static SqlParseResult Parse(string statement)
    {
        var tokens = new List<string>();
        var buffer = new System.Text.StringBuilder();
        var quote = '\0';

        void Flush()
        {
            if (buffer.Length == 0) return;
            tokens.Add(buffer.ToString().ToUpperInvariant());
            buffer.Clear();
        }

        for (var index = 0; index < statement.Length; index++)
        {
            var character = statement[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    if (quote == '\'' && index + 1 < statement.Length && statement[index + 1] == '\'')
                    {
                        index++;
                        continue;
                    }
                    quote = '\0';
                }
                continue;
            }

            if (character is '\'' or '"' or '`')
            {
                Flush();
                quote = character;
                continue;
            }
            if (character == '[')
            {
                Flush();
                var closing = statement.IndexOf(']', index + 1);
                if (closing < 0) return new([], "SQL 含有未結束的 quoted identifier。");
                index = closing;
                continue;
            }
            if (character == '-' && index + 1 < statement.Length && statement[index + 1] == '-')
                return new([], "Fallback SQL 不允許 comment。");
            if (character == '/' && index + 1 < statement.Length && statement[index + 1] == '*')
                return new([], "Fallback SQL 不允許 comment。");
            if (char.IsWhiteSpace(character))
            {
                Flush();
                continue;
            }
            if (character is '(' or ')' or ',' or ';' or '*')
            {
                Flush();
                tokens.Add(character.ToString());
                continue;
            }
            buffer.Append(character);
        }

        if (quote != '\0') return new([], "SQL 含有未結束的 string literal。");
        Flush();
        return new(tokens, null);
    }

    private sealed record SqlParseResult(IReadOnlyList<string> Tokens, string? Error);
}
