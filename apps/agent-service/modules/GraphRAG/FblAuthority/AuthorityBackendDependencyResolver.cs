using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;

namespace AgentService.Modules.GraphRAG.FblAuthority;

/// <summary>
/// 從可到達的 MVC Action 掃描直接業務類別，再以 QR／DAL 檔名家族連接 DD 與實際 DB Object。
/// 本 Resolver 不遞迴展開大型共用 Utility，避免把「類別可用」誤當成「目前功能已使用」。
/// </summary>
public sealed class BackendDependencyResolver
{
    private readonly CSharpSourceIndex _sourceIndex;
    private readonly IReadOnlyList<DatabaseObjectCatalogItem> _databaseObjects;

    /// <summary>建立只使用原始碼索引與 FBL_SPV_SIT 目錄事實的 Resolver。</summary>
    public BackendDependencyResolver(
        CSharpSourceIndex sourceIndex,
        IReadOnlyList<DatabaseObjectCatalogItem> databaseObjects)
    {
        _sourceIndex = sourceIndex;
        _databaseObjects = databaseObjects;
    }

    /// <summary>把 Standard Web 圖向後擴充至直接 CodeClass 與產生器資料存取家族。</summary>
    public ExtractionResult Resolve(ExtractionResult input)
    {
        var builder = GraphDocumentBuilder.FromDocument(
            input.Document,
            GraphBuildStage.StandardWebExtraction);
        var issues = input.Issues.ToList();
        var discoveredDataClasses = new Dictionary<string, IndexedCSharpType>(StringComparer.Ordinal);
        var processedDefinitions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var action in input.Document.Nodes.Where(node => node.Kind == GraphNodeKind.WebAction))
        {
            ResolveActionDependencies(builder, action, discoveredDataClasses, issues);
        }

        foreach (var codeNode in input.Document.Nodes.Where(node =>
                     node.Kind == GraphNodeKind.CodeClass
                     && IsSupportingDataOwner(node)))
        {
            ResolveSupportingDataDependencies(builder, codeNode, discoveredDataClasses, issues);
        }

        foreach (var type in discoveredDataClasses.Values.OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            ResolveGeneratedFamily(builder, type, processedDefinitions, issues);
        }

        return new ExtractionResult(builder.Build(), issues);
    }

    /// <summary>判斷是否為可安全掃描整個類別的特定 Transform／Upload Handler。</summary>
    private static bool IsSupportingDataOwner(GraphNode node)
    {
        var role = node.Properties.GetValueOrDefault("role")?.ToString();
        return role is nameof(CodeClassRole.Transform)
            or nameof(CodeClassRole.UploadHandler)
            or nameof(CodeClassRole.ReportKernel);
    }

    /// <summary>掃描特定 Transform／Upload 類別的全部方法，只保留實際 QR／DAL 類別。</summary>
    private void ResolveSupportingDataDependencies(
        GraphDocumentBuilder builder,
        GraphNode ownerNode,
        IDictionary<string, IndexedCSharpType> discoveredDataClasses,
        ICollection<PreflightIssue> issues)
    {
        var fullName = ownerNode.Properties.GetValueOrDefault("full_name")?.ToString();
        var ownerType = string.IsNullOrWhiteSpace(fullName) ? null : _sourceIndex.FindTypeByFullName(fullName);
        if (ownerType is null)
        {
            return;
        }

        foreach (var method in ownerType.Methods)
        {
            foreach (var creation in method.Syntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var candidates = ResolveTypeCandidates(creation.Type.ToString(), method.Syntax);
                if (candidates.Count != 1)
                {
                    continue;
                }

                var target = candidates[0];
                var role = InferRole(target, CodeClassRole.Other);
                if (role is not (CodeClassRole.Query or CodeClassRole.DataAccess))
                {
                    continue;
                }

                CodeClassNodeFactory.Add(builder, target, role);
                builder.AddRelationship(
                    SelectRelationship(role),
                    ownerNode.Key,
                    $"code:{target.FullName}",
                    new GraphEvidence
                    {
                        SourceKind = GraphSourceKind.SourceCode,
                        SourceFile = method.RelativePath,
                        SourceLine = GetSourceLine(creation),
                        SourceText = $"new {creation.Type}(...)以上述方法本文為準",
                        Operations = InferOperations(method.Syntax, creation, role),
                    });
                discoveredDataClasses[target.FullName] = target;
            }
        }
    }

    /// <summary>只掃描 WebAction 實際對應的方法本文，不把同一 Controller 其他功能一併帶入。</summary>
    private void ResolveActionDependencies(
        GraphDocumentBuilder builder,
        GraphNode action,
        IDictionary<string, IndexedCSharpType> discoveredDataClasses,
        ICollection<PreflightIssue> issues)
    {
        var declaringName = action.Properties.GetValueOrDefault("declaring_controller")?.ToString();
        if (string.IsNullOrWhiteSpace(declaringName))
        {
            return;
        }

        var controller = _sourceIndex.FindTypeByFullName(declaringName);
        if (controller is null)
        {
            return;
        }

        var methodNames = ReadStringValues(action.Properties.GetValueOrDefault("method_names"));
        foreach (var method in controller.Methods.Where(method => methodNames.Contains(method.Name)))
        {
            foreach (var creation in method.Syntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var typeText = creation.Type.ToString();
                var role = InferRoleFromReference(typeText);
                if (role is null)
                {
                    continue;
                }

                var candidates = ResolveTypeCandidates(typeText, method.Syntax);
                if (candidates.Count != 1)
                {
                    // Utility／Builder 等後綴也可能是 .NET Framework 類別；只有強業務前綴才阻擋。
                    if (RequiresUniqueResolution(typeText))
                    {
                        issues.Add(new PreflightIssue(
                            PreflightSeverity.Error,
                            PreflightReasonCode.CodeDependencyUnresolved,
                            $"Action 中的業務類別 '{typeText}' 找不到唯一實作。",
                            FromKey: action.Key,
                            TargetText: typeText,
                            SourceFile: method.RelativePath,
                            SourceLine: GetSourceLine(creation),
                            Candidates: candidates.Select(candidate => candidate.FullName).ToArray()));
                    }

                    continue;
                }

                var target = candidates[0];
                var actualRole = InferRole(target, role.Value);
                CodeClassNodeFactory.Add(builder, target, actualRole);
                builder.AddRelationship(
                    SelectRelationship(actualRole),
                    $"code:{controller.FullName}",
                    $"code:{target.FullName}",
                    new GraphEvidence
                    {
                        SourceKind = GraphSourceKind.SourceCode,
                        SourceFile = method.RelativePath,
                        SourceLine = GetSourceLine(creation),
                        SourceText = $"new {typeText}(...)以上述 Action 本文為準",
                        Operations = InferOperations(method.Syntax, creation, actualRole),
                    });

                if ((actualRole is CodeClassRole.Query or CodeClassRole.DataAccess)
                    && IsOutOfScopeConnection(creation))
                {
                    issues.Add(new PreflightIssue(
                        PreflightSeverity.Information,
                        PreflightReasonCode.OutOfScopeDatabase,
                        $"'{target.FullName}' 使用非 FBL_SPV_SIT 連線變數，依範圍規則停止向 DB Object 展開。",
                        FromKey: $"code:{controller.FullName}",
                        TargetText: target.FullName,
                        SourceFile: method.RelativePath,
                        SourceLine: GetSourceLine(creation)));
                }
                else if (actualRole is CodeClassRole.Query or CodeClassRole.DataAccess)
                {
                    discoveredDataClasses[target.FullName] = target;
                }
            }
        }
    }

    /// <summary>依 QR／DAL 來源檔家族名稱尋找 DD，並以 DataTableName 驗證實際 DB Object。</summary>
    private void ResolveGeneratedFamily(
        GraphDocumentBuilder builder,
        IndexedCSharpType dataClass,
        ISet<string> processedDefinitions,
        ICollection<PreflightIssue> issues)
    {
        var role = InferRole(dataClass, CodeClassRole.Other);
        var referencedDefinitions = FindReferencedDefinitions(dataClass);
        if (referencedDefinitions.Count > 0)
        {
            foreach (var reference in referencedDefinitions)
            {
                AddDefinitionMapping(
                    builder,
                    dataClass,
                    reference.Definition,
                    reference.SourceFile,
                    reference.SourceLine,
                    reference.SourceText,
                    processedDefinitions,
                    issues);
            }

            return;
        }

        if (ResolveDirectDatabaseObjects(builder, dataClass))
        {
            return;
        }

        var families = FindGeneratorFamilies(dataClass, role);
        var familyMatches = families
            .Select(family => new
            {
                Family = family,
                Definitions = _sourceIndex.FindTypes($"DD{family}")
                    .Where(candidate => candidate.Parts.Any(part =>
                        part.RelativePath.StartsWith("RMDBDefinition/", StringComparison.OrdinalIgnoreCase)))
                    .ToArray(),
            })
            .Where(match => match.Definitions.Length == 1)
            .ToArray();
        if (familyMatches.Length == 0)
        {
            var family = families.FirstOrDefault() ?? dataClass.Name;
            issues.Add(CreateFamilyIssue(dataClass, family, role, Array.Empty<IndexedCSharpType>()));
            return;
        }

        foreach (var match in familyMatches)
        {
            var evidencePart = dataClass.Parts.First(part =>
                GetGeneratorFamily(part.RelativePath, role) == match.Family);
            AddDefinitionMapping(
                builder,
                dataClass,
                match.Definitions[0],
                evidencePart.RelativePath,
                evidencePart.SourceLine,
                $"{Path.GetFileName(evidencePart.RelativePath)} → DD{match.Family}",
                processedDefinitions,
                issues);
        }
    }

    /// <summary>支援少數未經 DD 產生器、但 SQL literal 明確呼叫 Function／SP 的 QR。</summary>
    private bool ResolveDirectDatabaseObjects(
        GraphDocumentBuilder builder,
        IndexedCSharpType dataClass)
    {
        var found = false;
        foreach (var part in dataClass.Parts)
        {
            foreach (var literal in part.Syntax.DescendantNodes().OfType<LiteralExpressionSyntax>()
                         .Where(item => item.IsKind(SyntaxKind.StringLiteralExpression)))
            {
                var sqlText = literal.Token.ValueText;
                foreach (var databaseObject in _databaseObjects.Where(item =>
                             ContainsSqlIdentifier(sqlText, item.ObjectName)))
                {
                    found = true;
                    builder.AddNode(
                        GraphNodeKind.DatabaseObject,
                        databaseObject.CreateNodeKey(),
                        new Dictionary<string, object?>
                        {
                            ["database"] = "FBL_SPV_SIT",
                            ["schema"] = databaseObject.SchemaName,
                            ["name"] = databaseObject.ObjectName,
                            ["object_kind"] = databaseObject.Kind.ToString(),
                        });
                    builder.AddRelationship(
                        databaseObject.Kind is DatabaseObjectKind.Function or DatabaseObjectKind.StoredProcedure
                            ? GraphRelationshipKind.Executes
                            : GraphRelationshipKind.ReadsData,
                        $"code:{dataClass.FullName}",
                        databaseObject.CreateNodeKey(),
                        new GraphEvidence
                        {
                            SourceKind = GraphSourceKind.SourceCode,
                            SourceFile = part.RelativePath,
                            SourceLine = GetSourceLine(literal),
                            SourceText = databaseObject.ObjectName,
                        });
                }
            }
        }

        return found;
    }

    /// <summary>以 identifier 邊界比對 SQL literal，避免短物件名稱命中另一長名稱。</summary>
    private static bool ContainsSqlIdentifier(string sqlText, string objectName)
    {
        var index = sqlText.IndexOf(objectName, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var beforeIsIdentifier = index > 0
                && (char.IsLetterOrDigit(sqlText[index - 1]) || sqlText[index - 1] == '_');
            var afterIndex = index + objectName.Length;
            var afterIsIdentifier = afterIndex < sqlText.Length
                && (char.IsLetterOrDigit(sqlText[afterIndex]) || sqlText[afterIndex] == '_');
            if (!beforeIsIdentifier && !afterIsIdentifier)
            {
                return true;
            }

            index = sqlText.IndexOf(objectName, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>建立資料存取類別到 DD，再由 DD 連至資料庫物件。</summary>
    private void AddDefinitionMapping(
        GraphDocumentBuilder builder,
        IndexedCSharpType dataClass,
        IndexedCSharpType definition,
        string sourceFile,
        int sourceLine,
        string sourceText,
        ISet<string> processedDefinitions,
        ICollection<PreflightIssue> issues)
    {
        CodeClassNodeFactory.Add(builder, definition, CodeClassRole.DataDefinition);
        builder.AddRelationship(
            GraphRelationshipKind.UsesDefinition,
            $"code:{dataClass.FullName}",
            $"code:{definition.FullName}",
            new GraphEvidence
            {
                SourceKind = GraphSourceKind.SourceCode,
                SourceFile = sourceFile,
                SourceLine = sourceLine,
                SourceText = sourceText,
            });
        // 同一 DD 可被多個 QR／DAL 使用；MAPS_TO 與缺失問題只需處理一次。
        if (processedDefinitions.Add(definition.FullName))
        {
            ResolveDefinitionObject(builder, definition, issues);
        }
    }

    /// <summary>優先採用 QR／DAL 本文直接引用的 DD 類別，支援 V2 與非同名家族。</summary>
    private IReadOnlyList<DefinitionReference> FindReferencedDefinitions(IndexedCSharpType dataClass)
    {
        var references = new Dictionary<string, DefinitionReference>(StringComparer.Ordinal);
        foreach (var part in dataClass.Parts)
        {
            foreach (var identifier in part.Syntax.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var name = identifier.Identifier.ValueText;
                if (!name.StartsWith("DD", StringComparison.Ordinal))
                {
                    continue;
                }

                var candidates = _sourceIndex.FindTypes(name)
                    .Where(candidate => candidate.Parts.Any(candidatePart =>
                        candidatePart.RelativePath.StartsWith("RMDBDefinition/", StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                if (candidates.Length == 1)
                {
                    references.TryAdd(
                        candidates[0].FullName,
                        new DefinitionReference(
                            candidates[0],
                            part.RelativePath,
                            GetSourceLine(identifier),
                            name));
                }
            }
        }

        return references.Values.OrderBy(reference => reference.Definition.FullName, StringComparer.Ordinal).ToArray();
    }

    /// <summary>從 DD 的 const DataTableName 取得名稱，且只能映射至一個實際資料庫物件。</summary>
    private void ResolveDefinitionObject(
        GraphDocumentBuilder builder,
        IndexedCSharpType definition,
        ICollection<PreflightIssue> issues)
    {
        var declarations = definition.Parts
            .SelectMany(part => part.Syntax.Members.OfType<FieldDeclarationSyntax>()
                .SelectMany(field => field.Declaration.Variables.Select(variable => (part, field, variable))))
            .Where(item => item.field.Modifiers.Any(SyntaxKind.ConstKeyword)
                && string.Equals(item.variable.Identifier.ValueText, "DataTableName", StringComparison.Ordinal))
            .ToArray();
        var names = declarations
            .Select(item => EvaluateConstantString(item.variable.Initializer?.Value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length != 1)
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Error,
                PreflightReasonCode.DatabaseObjectNotFound,
                $"DD '{definition.FullName}' 沒有唯一可判定的 const DataTableName。",
                FromKey: $"code:{definition.FullName}",
                Candidates: names));
            return;
        }

        var parsedName = ParseDatabaseObjectName(names[0]);
        var matches = _databaseObjects
            .Where(item => string.Equals(item.ObjectName, parsedName.ObjectName, StringComparison.OrdinalIgnoreCase)
                && (parsedName.SchemaName is null
                    || string.Equals(item.SchemaName, parsedName.SchemaName, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (matches.Length != 1)
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Error,
                PreflightReasonCode.DatabaseObjectNotFound,
                $"FBL_SPV_SIT 中找不到唯一資料庫物件 '{parsedName.DisplayName}'。",
                FromKey: $"code:{definition.FullName}",
                TargetText: parsedName.DisplayName,
                Candidates: matches.Select(item => $"{item.SchemaName}.{item.ObjectName}").ToArray()));
            return;
        }

        var databaseObject = matches[0];
        builder.AddNode(
            GraphNodeKind.DatabaseObject,
            databaseObject.CreateNodeKey(),
            new Dictionary<string, object?>
            {
                ["database"] = "FBL_SPV_SIT",
                ["schema"] = databaseObject.SchemaName,
                ["name"] = databaseObject.ObjectName,
                ["object_kind"] = databaseObject.Kind.ToString(),
            });
        var declaration = declarations[0];
        builder.AddRelationship(
            GraphRelationshipKind.MapsTo,
            $"code:{definition.FullName}",
            databaseObject.CreateNodeKey(),
            new GraphEvidence
            {
                SourceKind = GraphSourceKind.SourceCode,
                SourceFile = declaration.part.RelativePath,
                SourceLine = GetSourceLine(declaration.variable),
                SourceText = $"DataTableName = \"{names[0]}\"",
            });
    }

    /// <summary>使用 fully-qualified name 或來源檔 using namespace 唯一解析類別。</summary>
    private IReadOnlyList<IndexedCSharpType> ResolveTypeCandidates(
        string typeText,
        MethodDeclarationSyntax method)
    {
        var normalized = RemoveGenericArguments(typeText).Replace("global::", string.Empty, StringComparison.Ordinal);
        if (normalized.Contains('.'))
        {
            var exact = _sourceIndex.FindTypeByFullName(normalized);
            if (exact is not null)
            {
                return new[] { exact };
            }

            // 舊 Web 專案偶爾省略 APEX 根 namespace；只在 suffix 唯一時接受。
            var suffixCandidates = _sourceIndex.FindTypes(normalized.Split('.').Last())
                .Where(candidate => candidate.FullName.EndsWith($".{normalized}", StringComparison.Ordinal))
                .ToArray();
            return suffixCandidates;
        }

        var candidates = _sourceIndex.FindTypes(normalized);
        if (candidates.Count <= 1)
        {
            return candidates;
        }

        var usingNamespaces = method.SyntaxTree.GetCompilationUnitRoot().Usings
            .Where(usingDirective => usingDirective.Alias is null && !usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
            .Select(usingDirective => usingDirective.Name?.ToString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
        var imported = candidates
            .Where(candidate => usingNamespaces.Contains(GetNamespace(candidate.FullName)))
            .ToArray();
        return imported.Length > 0 ? imported : candidates;
    }

    /// <summary>依實際來源目錄與產生器檔名判定類別職責。</summary>
    private static CodeClassRole InferRole(IndexedCSharpType type, CodeClassRole fallback)
    {
        if (type.Parts.Any(part => GetGeneratorFamily(part.RelativePath, CodeClassRole.DataAccess) is not null))
        {
            return CodeClassRole.DataAccess;
        }

        if (type.Parts.Any(part => GetGeneratorFamily(part.RelativePath, CodeClassRole.Query) is not null))
        {
            return CodeClassRole.Query;
        }

        if (type.Parts.Any(part => part.RelativePath.StartsWith("RMDBDefinition/", StringComparison.OrdinalIgnoreCase)))
        {
            return CodeClassRole.DataDefinition;
        }

        return fallback;
    }

    /// <summary>只接受具有業務命名慣例的 object creation，框架與 DTO 不建立依賴。</summary>
    private static CodeClassRole? InferRoleFromReference(string typeText)
    {
        var name = RemoveGenericArguments(typeText).Split('.').Last();
        if (name.StartsWith("QR", StringComparison.Ordinal)) return CodeClassRole.Query;
        if (name.StartsWith("DAL", StringComparison.Ordinal)) return CodeClassRole.DataAccess;
        if (name.StartsWith("BZ", StringComparison.Ordinal)) return CodeClassRole.BusinessLogic;
        if (name.StartsWith("DynamicRawData", StringComparison.Ordinal)) return CodeClassRole.BusinessLogic;
        if (name.StartsWith("Transform", StringComparison.Ordinal)) return CodeClassRole.Transform;
        if (name.StartsWith("Upload_", StringComparison.Ordinal)) return CodeClassRole.UploadHandler;
        if (name.Contains("Batch", StringComparison.Ordinal)) return CodeClassRole.BatchProcessor;
        if (name.EndsWith("Utility", StringComparison.Ordinal)) return CodeClassRole.Utility;
        if (name.EndsWith("Locator", StringComparison.Ordinal)) return CodeClassRole.Locator;
        if (name.EndsWith("Builder", StringComparison.Ordinal)) return CodeClassRole.Builder;
        return null;
    }

    /// <summary>判斷解析失敗時是否必須阻擋，排除 StringBuilder 等框架類別。</summary>
    private static bool RequiresUniqueResolution(string typeText)
    {
        var name = RemoveGenericArguments(typeText).Split('.').Last();
        return name.StartsWith("QR", StringComparison.Ordinal)
            || name.StartsWith("DAL", StringComparison.Ordinal)
            || name.StartsWith("BZ", StringComparison.Ordinal)
            || name.StartsWith("DynamicRawData", StringComparison.Ordinal)
            || name.StartsWith("Transform", StringComparison.Ordinal)
            || name.StartsWith("Upload_", StringComparison.Ordinal);
    }

    /// <summary>辨識原始碼明確使用 Accounting／IMS 等非中心 DB 連線，僅記錄邊界不向後展開。</summary>
    private static bool IsOutOfScopeConnection(ObjectCreationExpressionSyntax creation)
    {
        var arguments = creation.ArgumentList?.ToString() ?? string.Empty;
        return arguments.Contains("AccountingDBConnectionString", StringComparison.OrdinalIgnoreCase)
            || arguments.Contains("IMSDBConnectionString", StringComparison.OrdinalIgnoreCase)
            || arguments.Contains("IMSConnectionString", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>依 CodeClass 職責選擇受控 enum 關係。</summary>
    private static GraphRelationshipKind SelectRelationship(CodeClassRole role) => role switch
    {
        CodeClassRole.Query => GraphRelationshipKind.ReadsVia,
        CodeClassRole.DataAccess => GraphRelationshipKind.WritesVia,
        CodeClassRole.UploadHandler => GraphRelationshipKind.UsesUploadHandler,
        CodeClassRole.BatchProcessor => GraphRelationshipKind.UsesBatchProcessor,
        _ => GraphRelationshipKind.Uses,
    };

    /// <summary>從 DAL 呼叫名稱推導實際寫入操作；QR 固定為 Read。</summary>
    private static IReadOnlyList<GraphOperation> InferOperations(
        MethodDeclarationSyntax method,
        ObjectCreationExpressionSyntax creation,
        CodeClassRole role)
    {
        if (role == CodeClassRole.Query)
        {
            return new[] { GraphOperation.Read };
        }

        if (role != CodeClassRole.DataAccess)
        {
            return Array.Empty<GraphOperation>();
        }

        // DAL 可能以連鎖呼叫或區域變數使用；目前只記錄方法本文中直接出現的 CRUD 名稱。
        var names = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(invocation => invocation.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                _ => string.Empty,
            });
        var operations = new HashSet<GraphOperation>();
        foreach (var name in names)
        {
            if (name.StartsWith("Insert", StringComparison.OrdinalIgnoreCase)) operations.Add(GraphOperation.Insert);
            if (name.StartsWith("Update", StringComparison.OrdinalIgnoreCase)) operations.Add(GraphOperation.Update);
            if (name.StartsWith("Delete", StringComparison.OrdinalIgnoreCase)) operations.Add(GraphOperation.Delete);
        }

        return operations.OrderBy(operation => operation).ToArray();
    }

    /// <summary>從來源檔名稱取得 QRXXXX／DALXXXX 的 XXXX 家族。</summary>
    private static IReadOnlyList<string> FindGeneratorFamilies(
        IndexedCSharpType type,
        CodeClassRole role)
    {
        return type.Parts
            .Select(part => GetGeneratorFamily(part.RelativePath, role))
            .Where(family => !string.IsNullOrWhiteSpace(family))
            .Select(family => family!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>只有 RMQR／RMDAL 的產生器檔名可證實家族，其他同名檔案不採用。</summary>
    private static string? GetGeneratorFamily(string relativePath, CodeClassRole role)
    {
        var expectedDirectory = role == CodeClassRole.Query ? "RMQR/" : "RMDAL/";
        var prefix = role == CodeClassRole.Query ? "QR" : "DAL";
        if (!relativePath.StartsWith(expectedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var fileName = Path.GetFileNameWithoutExtension(relativePath);
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var family = fileName[prefix.Length..];
        return family.EndsWith("Base", StringComparison.Ordinal)
            ? family[..^"Base".Length]
            : family;
    }

    /// <summary>建立 QR／DAL 缺少 DD 的一致 Preflight 訊息。</summary>
    private static PreflightIssue CreateFamilyIssue(
        IndexedCSharpType dataClass,
        string family,
        CodeClassRole role,
        IReadOnlyList<IndexedCSharpType> candidates)
    {
        var reason = role == CodeClassRole.Query
            ? PreflightReasonCode.QueryDefinitionMappingMissing
            : PreflightReasonCode.DataAccessDefinitionMappingMissing;
        return new PreflightIssue(
            PreflightSeverity.Error,
            reason,
            $"{role} 家族 '{family}' 找不到唯一 DD{family}。",
            FromKey: $"code:{dataClass.FullName}",
            TargetText: $"DD{family}",
            Candidates: candidates.Select(candidate => candidate.FullName).ToArray());
    }

    /// <summary>讀取 Graph 屬性中的字串陣列，不接受其他動態型別。</summary>
    private static HashSet<string> ReadStringValues(object? value)
    {
        return value switch
        {
            string[] values => values.ToHashSet(StringComparer.Ordinal),
            IReadOnlyList<string> values => values.ToHashSet(StringComparer.Ordinal),
            _ => new HashSet<string>(StringComparer.Ordinal),
        };
    }

    /// <summary>移除最外層 generic argument，保留可供索引查詢的型別名稱。</summary>
    private static string RemoveGenericArguments(string typeText)
    {
        var index = typeText.IndexOf('<');
        return index < 0 ? typeText : typeText[..index];
    }

    /// <summary>從 fully-qualified name 取得 namespace。</summary>
    private static string GetNamespace(string fullName)
    {
        var index = fullName.LastIndexOf('.');
        return index < 0 ? string.Empty : fullName[..index];
    }

    /// <summary>取得 Roslyn 節點的 1-based 行號。</summary>
    private static int GetSourceLine(Microsoft.CodeAnalysis.SyntaxNode node)
    {
        return node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    }

    /// <summary>計算由字串 literal 與加號組成的 const expression。</summary>
    private static string? EvaluateConstantString(Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax? expression)
    {
        return expression switch
        {
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression)
                => literal.Token.ValueText,
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression)
                => EvaluateConstantString(binary.Left) is { } left
                    && EvaluateConstantString(binary.Right) is { } right
                        ? left + right
                        : null,
            ParenthesizedExpressionSyntax parenthesized => EvaluateConstantString(parenthesized.Expression),
            _ => null,
        };
    }

    /// <summary>移除 UDF 參數並拆出可選 schema，保留實際 sys.objects 名稱。</summary>
    private static ParsedDatabaseObjectName ParseDatabaseObjectName(string rawName)
    {
        var withoutArguments = rawName.Split('(')[0].Trim();
        var segments = withoutArguments.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length >= 2
            ? new ParsedDatabaseObjectName(segments[^2], segments[^1], $"{segments[^2]}.{segments[^1]}")
            : new ParsedDatabaseObjectName(null, withoutArguments, withoutArguments);
    }

    /// <summary>保存 DD identifier 的直接原始碼定位。</summary>
    private sealed record DefinitionReference(
        IndexedCSharpType Definition,
        string SourceFile,
        int SourceLine,
        string SourceText);

    /// <summary>保存正規化後的 schema、object name 與審閱顯示文字。</summary>
    private sealed record ParsedDatabaseObjectName(
        string? SchemaName,
        string ObjectName,
        string DisplayName);
}

