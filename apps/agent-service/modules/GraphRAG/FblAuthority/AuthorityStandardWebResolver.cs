namespace AgentService.Modules.GraphRAG.FblAuthority;

/// <summary>
/// 將 Standard Web Menu 的 Endpoint 解析至 MVC WebAction 與實作 CodeClass。
/// 此階段只採路由、Roslyn class、method、attribute 與繼承等直接事實。
/// </summary>
public sealed class StandardWebEntryResolver
{
    private const string WebControllerDirectory = "RiskMaster_Web/Controllers/";
    private readonly CSharpSourceIndex _sourceIndex;
    private readonly ViewSourceIndex? _viewIndex;
    private readonly ClientScriptSourceIndex? _clientScriptIndex;
    private readonly HashSet<string> _processedClientScripts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>建立使用既有 Roslyn 索引的 Standard Web Resolver。</summary>
    public StandardWebEntryResolver(
        CSharpSourceIndex sourceIndex,
        ViewSourceIndex? viewIndex = null,
        ClientScriptSourceIndex? clientScriptIndex = null)
    {
        _sourceIndex = sourceIndex;
        _viewIndex = viewIndex;
        _clientScriptIndex = clientScriptIndex;
    }

    /// <summary>解析全部 Standard Web Menu，並保留所有無法連線的阻擋問題。</summary>
    public ExtractionResult Resolve(GraphDocument inventoryDocument)
    {
        // Resolver instance 可重用，但每個 GraphDocument 都必須重新處理實際可達 Script。
        _processedClientScripts.Clear();
        var builder = GraphDocumentBuilder.FromDocument(
            inventoryDocument,
            GraphBuildStage.StandardWebExtraction);
        var issues = new List<PreflightIssue>();
        var endpointsByMenu = inventoryDocument.Relationships
            .Where(edge => edge.Kind == GraphRelationshipKind.Opens)
            .ToDictionary(edge => edge.SourceKey, edge => edge.TargetKey, StringComparer.Ordinal);

        // 只處理 resolver_kind 明確為 StandardWeb 的696中心 Menu 子集合。
        foreach (var menu in inventoryDocument.Nodes.Where(IsStandardWebMenu))
        {
            ResolveMenu(builder, menu, endpointsByMenu, issues);
        }

        return new ExtractionResult(builder.Build(), issues);
    }

    /// <summary>解析單一 Menu 的 MVC route、Controller、Action 與繼承關係。</summary>
    private void ResolveMenu(
        GraphDocumentBuilder builder,
        GraphNode menu,
        IReadOnlyDictionary<string, string> endpointsByMenu,
        ICollection<PreflightIssue> issues)
    {
        var menuId = menu.Properties.GetValueOrDefault("menu_id")?.ToString();
        var normalizedLinkAddress = menu.Properties.GetValueOrDefault("normalized_link_address")?.ToString();
        if (string.IsNullOrWhiteSpace(normalizedLinkAddress)
            || !MvcRoute.TryParse(normalizedLinkAddress, out var route)
            || route is null
            || !endpointsByMenu.TryGetValue(menu.Key, out var endpointKey))
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Error,
                PreflightReasonCode.MenuRouteUnresolved,
                "Standard Web Menu 無法解析 MVC route 或找不到 OPENS Endpoint。",
                MenuId: menuId,
                FromKey: menu.Key,
                TargetText: normalizedLinkAddress));
            return;
        }

        var controllerCandidates = FindControllerCandidates(route.ControllerName);
        if (controllerCandidates.Count != 1)
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Error,
                PreflightReasonCode.ControllerNotFound,
                controllerCandidates.Count == 0
                    ? $"找不到 {route.ControllerName}Controller。"
                    : $"{route.ControllerName}Controller 有多個執行期候選，無法唯一決定。",
                MenuId: menuId,
                FromKey: endpointKey,
                TargetText: $"{route.ControllerName}Controller",
                Candidates: controllerCandidates.Select(candidate => candidate.FullName).ToArray()));
            return;
        }

        var runtimeController = controllerCandidates[0];
        var actionResolution = ResolveAction(runtimeController, route.ActionName, preferNonPost: true);
        if (actionResolution is null)
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Error,
                PreflightReasonCode.ActionNotFoundOrAmbiguous,
                $"{runtimeController.FullName} 找不到 public Action '{route.ActionName}'。",
                MenuId: menuId,
                FromKey: endpointKey,
                TargetText: route.ActionName));
            return;
        }

        var webActionKey = AddResolvedAction(builder, menu, endpointKey, route, actionResolution);
        if (_viewIndex is not null)
        {
            ResolveView(builder, menuId, webActionKey, route, actionResolution, issues);
        }
    }

    /// <summary>依 Controller simple name 尋找 RiskMaster_Web 執行期候選。</summary>
    private IReadOnlyList<IndexedCSharpType> FindControllerCandidates(string controllerName)
    {
        var expectedTypeName = $"{controllerName}Controller";
        var candidates = _sourceIndex.FindTypes(expectedTypeName);
        var directMvcCandidates = candidates
            .Where(type => type.Parts.Any(part => string.Equals(
                GetRepositoryDirectory(part.RelativePath),
                WebControllerDirectory.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var webCandidates = candidates
            .Where(type => type.Parts.Any(part =>
                part.RelativePath.StartsWith(WebControllerDirectory, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        // MVC Controller 優先採 Controllers 直接子檔，避免誤選 WebAPI 子 namespace 的同名 ApiController。
        return directMvcCandidates.Length > 0
            ? directMvcCandidates
            : webCandidates.Length > 0
            ? webCandidates
            : candidates.Count == 1
                ? candidates
                : Array.Empty<IndexedCSharpType>();
    }

    /// <summary>從 runtime Controller 沿單一繼承鏈尋找可路由的 public Action。</summary>
    private ResolvedAction? ResolveAction(
        IndexedCSharpType runtimeController,
        string routeActionName,
        bool preferNonPost)
    {
        var queue = new Queue<(IndexedCSharpType Type, IReadOnlyList<IndexedCSharpType> Path)>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue((runtimeController, new[] { runtimeController }));

        while (queue.Count > 0)
        {
            // 同一深度的 class 先一起檢查，避免多重候選時任意選取第一個。
            var currentDepthCount = queue.Count;
            var foundAtCurrentDepth = new List<ResolvedAction>();
            for (var index = 0; index < currentDepthCount; index++)
            {
                var current = queue.Dequeue();
                if (!visited.Add(current.Type.FullName))
                {
                    continue;
                }

                var methods = FindRoutableMethods(current.Type, routeActionName, preferNonPost);
                if (methods.Count > 0)
                {
                    foundAtCurrentDepth.Add(new ResolvedAction(
                        runtimeController,
                        current.Type,
                        methods,
                        current.Path));
                    continue;
                }

                foreach (var baseType in ResolveBaseTypes(current.Type))
                {
                    queue.Enqueue((baseType, current.Path.Append(baseType).ToArray()));
                }
            }

            // C# class 只能有單一 base class；若索引解析出多條可行路徑，視為歧義而不猜測。
            if (foundAtCurrentDepth.Count == 1)
            {
                return foundAtCurrentDepth[0];
            }

            if (foundAtCurrentDepth.Count > 1)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>尋找 route name 相符且可由 MVC 呼叫的方法 overload 集合。</summary>
    private static IReadOnlyList<IndexedCSharpMethod> FindRoutableMethods(
        IndexedCSharpType type,
        string routeActionName,
        bool preferNonPost)
    {
        var matchingMethods = type.Methods
            .Where(method => method.IsPublic && !method.IsNonAction)
            .Where(method => string.Equals(
                method.RouteName,
                routeActionName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Menu 開啟是 GET；若同時存在 GET 與 HttpPost overload，只保留非 HttpPost 方法。
        var getCandidates = matchingMethods.Where(method => !method.IsHttpPost).ToArray();
        return preferNonPost && getCandidates.Length > 0 ? getCandidates : matchingMethods;
    }

    /// <summary>將 base type 語法名稱解析回索引中的唯一 class。</summary>
    private IReadOnlyList<IndexedCSharpType> ResolveBaseTypes(IndexedCSharpType type)
    {
        var resolved = new List<IndexedCSharpType>();
        var currentNamespace = GetNamespace(type.FullName);

        foreach (var baseTypeText in type.BaseTypeNames)
        {
            var simpleName = GetSimpleTypeName(baseTypeText);
            var sameNamespace = _sourceIndex.FindTypeByFullName($"{currentNamespace}.{simpleName}");
            if (sameNamespace is not null)
            {
                resolved.Add(sameNamespace);
                continue;
            }

            var candidates = _sourceIndex.FindTypes(simpleName);
            if (candidates.Count == 1)
            {
                resolved.Add(candidates[0]);
            }
        }

        return resolved.DistinctBy(candidate => candidate.FullName).ToArray();
    }

    /// <summary>建立 WebAction、Controller CodeClass 與 enum 關係。</summary>
    private static string AddResolvedAction(
        GraphDocumentBuilder builder,
        GraphNode menu,
        string endpointKey,
        MvcRoute route,
        ResolvedAction resolution)
    {
        var webActionKey = route.CreateWebActionKey();
        var runtimeControllerKey = $"code:{resolution.RuntimeController.FullName}";

        builder.AddNode(
            GraphNodeKind.WebAction,
            webActionKey,
            new Dictionary<string, object?>
            {
                ["controller"] = route.ControllerName,
                ["action"] = route.ActionName,
                ["route_values"] = route.RouteValues.ToArray(),
                ["method_names"] = resolution.Methods.Select(method => method.Name).Distinct().ToArray(),
                ["source_files"] = resolution.Methods.Select(method => method.RelativePath).Distinct().ToArray(),
                ["declaring_controller"] = resolution.DeclaringController.FullName,
            });
        CodeClassNodeFactory.Add(builder, resolution.RuntimeController, CodeClassRole.Controller);
        CodeClassNodeFactory.Add(builder, resolution.DeclaringController, CodeClassRole.Controller);

        // ROUTES_TO 直接由 Menu LinkAddress 與已解析 MVC route 支援。
        builder.AddRelationship(
            GraphRelationshipKind.RoutesTo,
            endpointKey,
            webActionKey,
            new GraphEvidence
            {
                SourceKind = GraphSourceKind.Route,
                DatabaseObject = "dbo.tblMenuMap",
                DatabaseColumn = "LinkAddress",
                RowKey = menu.Properties.GetValueOrDefault("menu_id")?.ToString(),
                RawValue = menu.Properties.GetValueOrDefault("link_address")?.ToString(),
            });

        // MVC 實際以 runtime Controller 處理 Action；若 method 在 base class，另以 EXTENDS 連回宣告處。
        var firstMethod = resolution.Methods[0];
        builder.AddRelationship(
            GraphRelationshipKind.ImplementedBy,
            webActionKey,
            runtimeControllerKey,
            new GraphEvidence
            {
                SourceKind = GraphSourceKind.SourceCode,
                SourceFile = firstMethod.RelativePath,
                SourceLine = firstMethod.SourceLine,
                SourceText = $"public {firstMethod.Name}(...) using route '{firstMethod.RouteName}'",
            });

        // 對繼承 Action 建立完整 EXTENDS 鏈，讓 runtime Controller 可追溯到宣告類別。
        for (var index = 0; index < resolution.InheritancePath.Count - 1; index++)
        {
            var child = resolution.InheritancePath[index];
            var parent = resolution.InheritancePath[index + 1];
            CodeClassNodeFactory.Add(builder, child, CodeClassRole.Controller);
            CodeClassNodeFactory.Add(builder, parent, CodeClassRole.ControllerBase);
            builder.AddRelationship(
                GraphRelationshipKind.Extends,
                $"code:{child.FullName}",
                $"code:{parent.FullName}",
                new GraphEvidence
                {
                    SourceKind = GraphSourceKind.SourceCode,
                    SourceFile = child.Parts[0].RelativePath,
                    SourceLine = child.Parts[0].SourceLine,
                    SourceText = $"{child.Name} : {parent.Name}",
                });
        }

        return webActionKey;
    }

    /// <summary>從 Action 語法與 route values 唯一解析 ViewPage 並建立 RENDERS。</summary>
    private void ResolveView(
        GraphDocumentBuilder builder,
        string? menuId,
        string webActionKey,
        MvcRoute route,
        ResolvedAction resolution,
        ICollection<PreflightIssue> issues)
    {
        var viewCalls = MvcViewCallResolver.Resolve(resolution.Methods, route);
        var resolvedCandidates = viewCalls
            .SelectMany(call => _viewIndex!
                .FindViews(route.ControllerName, call.ViewName)
                .Select(view => new { Call = call, View = view }))
            .GroupBy(candidate => candidate.View.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        // View 名稱沒有對應檔案或多條條件分支仍可到達時，都不得猜測其中一個。
        if (resolvedCandidates.Length != 1)
        {
            issues.Add(new PreflightIssue(
                PreflightSeverity.Error,
                PreflightReasonCode.ViewNotFoundOrAmbiguous,
                resolvedCandidates.Length == 0
                    ? $"{route.ControllerName}/{route.ActionName} 找不到可確定的 View。"
                    : $"{route.ControllerName}/{route.ActionName} 可到達多個 View，無法唯一決定。",
                MenuId: menuId,
                FromKey: webActionKey,
                TargetText: string.Join(", ", viewCalls.Select(call => call.ViewName)),
                Candidates: resolvedCandidates.Select(candidate => candidate.View.RelativePath).ToArray()));
            return;
        }

        var resolved = resolvedCandidates[0];
        var viewKey = $"view:{resolved.View.RelativePath}";
        builder.AddNode(
            GraphNodeKind.ViewPage,
            viewKey,
            new Dictionary<string, object?>
            {
                ["name"] = resolved.Call.ViewName,
                ["path"] = resolved.View.RelativePath,
            });
        builder.AddRelationship(
            GraphRelationshipKind.Renders,
            webActionKey,
            viewKey,
            new GraphEvidence
            {
                SourceKind = GraphSourceKind.SourceCode,
                SourceFile = resolved.Call.SourceFile,
                SourceLine = resolved.Call.SourceLine,
                SourceText = $"return View(\"{resolved.Call.ViewName}\")",
            });

        if (_clientScriptIndex is not null)
        {
            ResolveClientScripts(builder, menuId, viewKey, resolved.View, issues);
        }
    }

    /// <summary>解析 View 中有效的 JSPath／script src 並建立 LOADS 關係。</summary>
    private void ResolveClientScripts(
        GraphDocumentBuilder builder,
        string? menuId,
        string viewKey,
        IndexedViewFile view,
        ICollection<PreflightIssue> issues)
    {
        foreach (var reference in ViewScriptReferenceParser.Parse(view.Text))
        {
            var candidates = _clientScriptIndex!.FindScripts(reference.LogicalPath);
            if (candidates.Count != 1)
            {
                issues.Add(new PreflightIssue(
                    PreflightSeverity.Error,
                    PreflightReasonCode.ClientScriptNotFound,
                    candidates.Count == 0
                        ? $"View 明確載入的 Client Script '{reference.LogicalPath}' 找不到原始檔。"
                        : $"Client Script '{reference.LogicalPath}' 有多個候選，無法唯一決定。",
                    MenuId: menuId,
                    FromKey: viewKey,
                    TargetText: reference.LogicalPath,
                    SourceFile: view.RelativePath,
                    SourceLine: reference.SourceLine,
                    Candidates: candidates.Select(candidate => candidate.RelativePath).ToArray()));
                continue;
            }

            var script = candidates[0];
            var scriptKey = $"client-script:{script.RelativePath}";
            builder.AddNode(
                GraphNodeKind.ClientScript,
                scriptKey,
                new Dictionary<string, object?>
                {
                    ["name"] = Path.GetFileName(script.RelativePath),
                    ["path"] = script.RelativePath,
                });
            builder.AddRelationship(
                GraphRelationshipKind.Loads,
                viewKey,
                scriptKey,
                new GraphEvidence
                {
                    SourceKind = GraphSourceKind.SourceCode,
                    SourceFile = view.RelativePath,
                    SourceLine = reference.SourceLine,
                    SourceText = reference.LogicalPath,
                });

            // 共用程式庫只代表頁面已載入，不能據此斷言其中所有延遲 Store 都屬於目前功能。
            // 只有功能自己的 Scripts/View 目錄才向後展開可執行的 MVC 呼叫。
            if (ShouldExpandScriptActions(script.RelativePath)
                && _processedClientScripts.Add(script.RelativePath))
            {
                ResolveScriptActions(builder, menuId, scriptKey, script, issues);
            }
        }
    }

    /// <summary>判斷 Client Script 是否位於功能程式目錄並可安全向後展開。</summary>
    private static bool ShouldExpandScriptActions(string relativePath)
    {
        return relativePath.StartsWith(
            "RiskMaster_Web/Scripts/View/",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>將 Client Script 中可執行 URL 解析至 MVC WebAction 並建立 CALLS。</summary>
    private void ResolveScriptActions(
        GraphDocumentBuilder builder,
        string? menuId,
        string scriptKey,
        IndexedClientScriptFile script,
        ICollection<PreflightIssue> issues)
    {
        foreach (var reference in BrowserScriptRouteReferenceParser.Parse(script.Text))
        {
            var exclusion = KnownScriptRouteExclusionPolicy.Match(
                script.RelativePath,
                reference.NormalizedRoute);
            if (exclusion.HasValue)
            {
                // 歷史 Store 不建立 CALLS，但仍輸出可稽核 Information Log。
                issues.Add(new PreflightIssue(
                    PreflightSeverity.Information,
                    PreflightReasonCode.HistoricalScriptRouteExcluded,
                    $"已依人工確認排除歷史前端路由：{exclusion.Value}。",
                    MenuId: menuId,
                    FromKey: scriptKey,
                    TargetText: reference.NormalizedRoute,
                    SourceFile: script.RelativePath,
                    SourceLine: reference.SourceLine));
                continue;
            }

            if (!MvcRoute.TryParse(reference.NormalizedRoute, out var route) || route is null)
            {
                continue;
            }

            var controllerCandidates = FindControllerCandidates(route.ControllerName);
            if (controllerCandidates.Count != 1)
            {
                issues.Add(CreateScriptActionIssue(
                    menuId,
                    scriptKey,
                    script.RelativePath,
                    reference,
                    $"Client Script URL 找不到唯一 {route.ControllerName}Controller。",
                    controllerCandidates.Select(candidate => candidate.FullName).ToArray()));
                continue;
            }

            var actionResolution = ResolveAction(
                controllerCandidates[0],
                route.ActionName,
                preferNonPost: false);
            if (actionResolution is null)
            {
                issues.Add(CreateScriptActionIssue(
                    menuId,
                    scriptKey,
                    script.RelativePath,
                    reference,
                    $"Client Script URL 找不到 public Action '{route.ControllerName}/{route.ActionName}'。",
                    Array.Empty<string>()));
                continue;
            }

            AddScriptAction(builder, scriptKey, script, reference, route, actionResolution);
        }
    }

    /// <summary>建立 ClientScript→WebAction、IMPLEMENTED_BY 與必要 EXTENDS 關係。</summary>
    private static void AddScriptAction(
        GraphDocumentBuilder builder,
        string scriptKey,
        IndexedClientScriptFile script,
        JavaScriptRouteReference reference,
        MvcRoute route,
        ResolvedAction resolution)
    {
        var webActionKey = route.CreateWebActionKey();
        var runtimeControllerKey = $"code:{resolution.RuntimeController.FullName}";
        builder.AddNode(
            GraphNodeKind.WebAction,
            webActionKey,
            new Dictionary<string, object?>
            {
                ["controller"] = route.ControllerName,
                ["action"] = route.ActionName,
                ["route_values"] = route.RouteValues.ToArray(),
                ["method_names"] = resolution.Methods.Select(method => method.Name).Distinct().ToArray(),
                ["source_files"] = resolution.Methods.Select(method => method.RelativePath).Distinct().ToArray(),
                ["declaring_controller"] = resolution.DeclaringController.FullName,
            });
        CodeClassNodeFactory.Add(builder, resolution.RuntimeController, CodeClassRole.Controller);
        CodeClassNodeFactory.Add(builder, resolution.DeclaringController, CodeClassRole.Controller);

        // CALLS 的來源是 comment-aware parser 找到的可執行 URL literal。
        builder.AddRelationship(
            GraphRelationshipKind.Calls,
            scriptKey,
            webActionKey,
            new GraphEvidence
            {
                SourceKind = GraphSourceKind.SourceCode,
                SourceFile = script.RelativePath,
                SourceLine = reference.SourceLine,
                SourceText = reference.RawValue,
                RawValue = reference.RawValue,
            });

        var firstMethod = resolution.Methods[0];
        builder.AddRelationship(
            GraphRelationshipKind.ImplementedBy,
            webActionKey,
            runtimeControllerKey,
            new GraphEvidence
            {
                SourceKind = GraphSourceKind.SourceCode,
                SourceFile = firstMethod.RelativePath,
                SourceLine = firstMethod.SourceLine,
                SourceText = $"public {firstMethod.Name}(...) using route '{firstMethod.RouteName}'",
            });

        // 繼承 Action 仍建立 runtime Controller 到 declaring Controller 的完整路徑。
        for (var index = 0; index < resolution.InheritancePath.Count - 1; index++)
        {
            var child = resolution.InheritancePath[index];
            var parent = resolution.InheritancePath[index + 1];
            CodeClassNodeFactory.Add(builder, child, CodeClassRole.Controller);
            CodeClassNodeFactory.Add(builder, parent, CodeClassRole.ControllerBase);
            builder.AddRelationship(
                GraphRelationshipKind.Extends,
                $"code:{child.FullName}",
                $"code:{parent.FullName}",
                new GraphEvidence
                {
                    SourceKind = GraphSourceKind.SourceCode,
                    SourceFile = child.Parts[0].RelativePath,
                    SourceLine = child.Parts[0].SourceLine,
                    SourceText = $"{child.Name} : {parent.Name}",
                });
        }
    }

    /// <summary>建立統一格式的 SCRIPT_ACTION_UNRESOLVED 阻擋訊息。</summary>
    private static PreflightIssue CreateScriptActionIssue(
        string? menuId,
        string scriptKey,
        string sourceFile,
        JavaScriptRouteReference reference,
        string message,
        IReadOnlyList<string> candidates)
    {
        return new PreflightIssue(
            PreflightSeverity.Error,
            PreflightReasonCode.ScriptActionUnresolved,
            message,
            MenuId: menuId,
            FromKey: scriptKey,
            TargetText: reference.NormalizedRoute,
            SourceFile: sourceFile,
            SourceLine: reference.SourceLine,
            Candidates: candidates);
    }

    /// <summary>判斷 Menu 是否應交給 Standard Web Resolver。</summary>
    private static bool IsStandardWebMenu(GraphNode node)
    {
        return node.Kind == GraphNodeKind.Menu
            && string.Equals(
                node.Properties.GetValueOrDefault("resolver_kind")?.ToString(),
                MenuResolverKind.StandardWeb.ToString(),
                StringComparison.Ordinal);
    }

    /// <summary>從 fully-qualified name 取得 namespace。</summary>
    private static string GetNamespace(string fullName)
    {
        var separatorIndex = fullName.LastIndexOf('.');
        return separatorIndex > 0 ? fullName[..separatorIndex] : string.Empty;
    }

    /// <summary>取得統一使用正斜線且不含尾端斜線的 Repository 目錄。</summary>
    private static string GetRepositoryDirectory(string relativePath)
    {
        var separatorIndex = relativePath.LastIndexOf('/');
        return separatorIndex > 0 ? relativePath[..separatorIndex] : string.Empty;
    }

    /// <summary>從泛型或 namespace-qualified 語法取得 base type simple name。</summary>
    private static string GetSimpleTypeName(string typeText)
    {
        var withoutGenericArguments = typeText.Split('<')[0];
        return withoutGenericArguments.Split('.').Last().Trim();
    }

    /// <summary>保存單一 Action 的 runtime、宣告類別、overload 與繼承路徑。</summary>
    private sealed record ResolvedAction(
        IndexedCSharpType RuntimeController,
        IndexedCSharpType DeclaringController,
        IReadOnlyList<IndexedCSharpMethod> Methods,
        IReadOnlyList<IndexedCSharpType> InheritancePath);
}

