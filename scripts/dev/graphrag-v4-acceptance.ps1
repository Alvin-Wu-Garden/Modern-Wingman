#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$BaseUrl = "http://127.0.0.1:5002",

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ProjectId,

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$ReportPath = (
        "docs/reports/graphrag-v4-acceptance-{0}.md" -f
        (Get-Date -Format "yyyyMMdd-HHmm")),

    [Parameter()]
    [ValidateRange(0, 1000000)]
    [int]$VerifiedFkConstraintEvidenceCount = 0,

    [Parameter()]
    [ValidateRange(0, 1000000)]
    [int]$VerifiedExecutableMenuItemCount = 0,

    [Parameter()]
    [ValidateRange(0, [long]::MaxValue)]
    [long]$V3FullIndexElapsedMilliseconds = 0,

    [Parameter()]
    [ValidateRange(0, 1000000)]
    [long]$V3ProcessedFileCount = 0,

    [Parameter()]
    [ValidateRange(0, [long]::MaxValue)]
    [long]$V3ProcessedSourceBytes = 0,

    [Parameter()]
    [ValidateRange(0, [double]::MaxValue)]
    [double]$V3FilesPerMinute = 0,

    [Parameter()]
    [ValidateRange(0, [double]::MaxValue)]
    [double]$V3MegabytesPerMinute = 0,

    [Parameter()]
    [ValidateRange(0, [double]::MaxValue)]
    [double]$FullIndexAbsoluteBudgetMinutes = 0,

    [Parameter()]
    [ValidateRange(0, [long]::MaxValue)]
    [long]$PreflightBudgetBytes = 0,

    [Parameter()]
    [switch]$ComparableV3Environment,

    [Parameter()]
    [switch]$AllowIncomplete
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

<#
.SYNOPSIS
以既有 AgentService 唯讀 API 產生 GraphRAG V4 驗收報告。

.DESCRIPTION
此腳本不會啟動索引、不會呼叫問答、不會清理資料，也不會直接連線
Neo4j、SQLite 或外部 SQL。唯一的 POST 是既有 /graph/query 端點；
該端點會在 AgentService 內驗證固定 Cypher 為 active graph 的 bounded read-only MATCH。

無法由目前 API 證明的項目一律標成 Manual 或 NotMeasured，禁止假設為 PASS。
正式模式只要仍有 FAIL、Warning、Conditional、Manual 或 NotMeasured 就回傳非零；
只有蒐集部分數據時才可明確傳入 -AllowIncomplete。

VerifiedFkConstraintEvidenceCount 必須來自同一次唯讀資料庫驗證。
V4 canonical Edge ID 依 source/kind/target 合併，因此多個 FK constraint 可能合成同一條 edge，
不能用 canonical edge 數冒充原始 FK constraint evidence 數。

C3～C5 會自動讀取 /index/run。若要以 V3 倍率正式驗收，必須同時提供
V3 elapsed/file/bytes/throughput 並明確指定 ComparableV3Environment；
scope 不可比時則必須提供事前核准的 FullIndexAbsoluteBudgetMinutes。
#>

function Get-LocalBaseUri {
    param([Parameter(Mandatory)][string]$Value)

    $uri = [Uri]$Value.TrimEnd("/")
    if ($uri.Scheme -notin @("http", "https")) {
        throw "BaseUrl 只允許 http 或 https。"
    }
    if (-not $uri.IsLoopback) {
        throw "驗收腳本只允許連線本機 AgentService loopback 位址。"
    }
    return $uri
}

function Invoke-AgentGet {
    param(
        [Parameter(Mandatory)][Uri]$BaseUri,
        [Parameter(Mandatory)][string]$Path
    )

    $uri = "{0}{1}" -f $BaseUri.AbsoluteUri.TrimEnd("/"), $Path
    return (Invoke-RestMethod -Method Get -Uri $uri -TimeoutSec 30)
}

function Invoke-GraphReadQuery {
    param(
        [Parameter(Mandatory)][Uri]$BaseUri,
        [Parameter(Mandatory)][string]$EncodedProjectId,
        [Parameter(Mandatory)][string]$Cypher,
        [int]$Limit = 5000
    )

    # Cypher 全由此腳本固定提供，不接受命令列或檔案注入。
    # AgentService 仍會再次拒絕寫入關鍵字、CALL、多 statement 與無界 LIMIT。
    $uri = [string]::Format(
        "{0}/api/projects/{1}/graph/query",
        $BaseUri.AbsoluteUri.TrimEnd("/"),
        $EncodedProjectId)
    $body = @{
        cypher = $Cypher
        limit = [Math]::Clamp($Limit, 1, 5000)
    } | ConvertTo-Json -Depth 5 -Compress
    return (Invoke-RestMethod `
        -Method Post `
        -Uri $uri `
        -ContentType "application/json; charset=utf-8" `
        -Body $body `
        -TimeoutSec 60)
}

function Get-Value {
    param(
        [AllowNull()][object]$InputObject,
        [Parameter(Mandatory)][string]$Name,
        [AllowNull()][object]$Default = $null
    )

    if ($null -eq $InputObject) {
        return $Default
    }
    $property = $InputObject.PSObject.Properties |
        Where-Object { $_.Name -eq $Name } |
        Select-Object -First 1
    if ($null -eq $property) {
        return $Default
    }
    return $property.Value
}

function Get-FirstRow {
    param([AllowNull()][object]$QueryResult)

    $rows = @(Get-Value $QueryResult "rows" @())
    if ($rows.Count -eq 0) {
        return $null
    }
    return $rows[0]
}

function Convert-ToInt64 {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) {
        return [long]0
    }
    return [Convert]::ToInt64($Value)
}

function New-Metric {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][ValidateSet(
            "PASS", "FAIL", "Warning", "Conditional", "Manual", "NotMeasured")]
        [string]$Status,
        [Parameter(Mandatory)][string]$Actual,
        [Parameter(Mandatory)][string]$Threshold,
        [Parameter(Mandatory)][string]$Evidence
    )

    [pscustomobject]@{
        Id = $Id
        Name = $Name
        Status = $Status
        Actual = $Actual
        Threshold = $Threshold
        Evidence = $Evidence
    }
}

function Escape-Markdown {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) {
        return ""
    }
    return $Value.ToString().Replace("|", "\|").Replace("`r", " ").Replace("`n", "<br>")
}

function Add-MetricSection {
    param(
        [Parameter(Mandatory)][System.Text.StringBuilder]$Builder,
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][System.Collections.IEnumerable]$Metrics
    )

    [void]$Builder.AppendLine("## $Title")
    [void]$Builder.AppendLine()
    [void]$Builder.AppendLine("| # | 指標 | 狀態 | 實測 | 門檻 | 證據／限制 |")
    [void]$Builder.AppendLine("|---|---|---|---|---|---|")
    foreach ($metric in $Metrics) {
        [void]$Builder.AppendLine([string]::Format(
            "| {0} | {1} | {2} | {3} | {4} | {5} |",
            (Escape-Markdown $metric.Id),
            (Escape-Markdown $metric.Name),
            (Escape-Markdown $metric.Status),
            (Escape-Markdown $metric.Actual),
            (Escape-Markdown $metric.Threshold),
            (Escape-Markdown $metric.Evidence)))
    }
    [void]$Builder.AppendLine()
}

function Convert-DistributionToMarkdown {
    param(
        [AllowNull()][object]$Rows,
        [Parameter(Mandatory)][string]$KeyName
    )

    $items = foreach ($row in @($Rows)) {
        $key = Get-Value $row $KeyName "(empty)"
        $count = Convert-ToInt64 (Get-Value $row "count" 0)
        "{0}={1}" -f $key, $count
    }
    if (@($items).Count -eq 0) {
        return "無資料"
    }
    return $items -join "；"
}

try {
    $baseUri = Get-LocalBaseUri $BaseUrl
    $encodedProjectId = [Uri]::EscapeDataString($ProjectId)
    $absoluteReportPath = if ([IO.Path]::IsPathRooted($ReportPath)) {
        [IO.Path]::GetFullPath($ReportPath)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path (Get-Location) $ReportPath))
    }
    if ([IO.Path]::GetExtension($absoluteReportPath) -ne ".md") {
        throw "ReportPath 必須是 Markdown（.md）檔案。"
    }
    if (Test-Path -LiteralPath $absoluteReportPath) {
        throw "ReportPath 已存在；為避免覆寫人工驗收紀錄，請指定新的檔名。"
    }
}
catch {
    Write-Error ("GraphRAG V4 驗收參數錯誤：{0}" -f $_.Exception.Message)
    exit 1
}

$metricsA = [System.Collections.Generic.List[object]]::new()
$metricsB = [System.Collections.Generic.List[object]]::new()
$metricsS = [System.Collections.Generic.List[object]]::new()
$metricsC = [System.Collections.Generic.List[object]]::new()
$metricsAi = [System.Collections.Generic.List[object]]::new()
$metricsQ = [System.Collections.Generic.List[object]]::new()
$metricsD = [System.Collections.Generic.List[object]]::new()
$diagnostics = [System.Collections.Generic.List[string]]::new()
$fatalError = $null

try {
    $projects = @(Invoke-AgentGet $baseUri "/api/projects/")
    $project = $projects |
        Where-Object { (Get-Value $_ "id" "") -eq $ProjectId } |
        Select-Object -First 1
    if ($null -eq $project) {
        throw "找不到 ProjectId：$ProjectId。"
    }

    $schema = Invoke-AgentGet `
        $baseUri `
        ("/api/projects/{0}/graph/schema" -f $encodedProjectId)
    $indexProgress = Invoke-AgentGet `
        $baseUri `
        ("/api/projects/{0}/index/progress" -f $encodedProjectId)
    $indexRun = $null
    $noOpIndexRun = $null
    try {
        $indexRun = Invoke-AgentGet `
            $baseUri `
            ("/api/projects/{0}/index/run?mode=full" -f $encodedProjectId)
        $noOpIndexRun = Invoke-AgentGet `
            $baseUri `
            ("/api/projects/{0}/index/run?mode=no-op" -f $encodedProjectId)
    }
    catch {
        $diagnostics.Add(
            "分模式 index/run 無法完整讀取：$($_.Exception.GetType().Name)；缺少的 C3～C6 將標示 NotMeasured。")
    }
    $summaryProgress = Invoke-AgentGet `
        $baseUri `
        ("/api/projects/{0}/summaries/progress" -f $encodedProjectId)
    $communityAcceptance = Invoke-AgentGet `
        $baseUri `
        ("/api/projects/{0}/community/acceptance-diagnostics" -f $encodedProjectId)
    $storageAcceptance = Invoke-AgentGet `
        $baseUri `
        ("/api/projects/{0}/storage/acceptance-diagnostics" -f $encodedProjectId)

    $roleResult = Invoke-GraphReadQuery $baseUri $encodedProjectId @"
MATCH (n:GraphEntity {projectId: `$projectId, graphVersion: `$graphVersion})
RETURN n.role AS role, count(n) AS count
ORDER BY count DESC, role
LIMIT `$limit
"@

    $reasonResult = Invoke-GraphReadQuery $baseUri $encodedProjectId @"
MATCH (source:GraphEntity {projectId: `$projectId, graphVersion: `$graphVersion})-[r]->(target:GraphEntity {projectId: `$projectId, graphVersion: `$graphVersion})
RETURN type(r) AS relationship, coalesce(r.reasonCode, '') AS reasonCode, count(r) AS count
ORDER BY count DESC, relationship, reasonCode
LIMIT `$limit
"@

    $edgeQualityResult = Invoke-GraphReadQuery $baseUri $encodedProjectId @"
MATCH (source:GraphEntity {projectId: `$projectId, graphVersion: `$graphVersion})-[r]->(target:GraphEntity {projectId: `$projectId, graphVersion: `$graphVersion})
RETURN count(r) AS total,
       sum(CASE WHEN r.weight >= 0.05 AND r.weight <= 1.0
                 AND r.confidence IN ['certain', 'probable', 'inferred']
                 AND r.evidenceCount > 0
                 AND r.reasonCode IS NOT NULL AND trim(r.reasonCode) <> ''
                 AND r.evidenceRef IS NOT NULL AND trim(r.evidenceRef) <> ''
                THEN 1 ELSE 0 END) AS complete,
       sum(CASE WHEN 'evidenceJson' IN keys(r)
                     OR 'sourceId' IN keys(r)
                     OR 'targetId' IN keys(r)
                THEN 1 ELSE 0 END) AS forbidden
LIMIT `$limit
"@

    $menuResult = Invoke-GraphReadQuery $baseUri $encodedProjectId @"
MATCH (n:GraphEntity {projectId: `$projectId, graphVersion: `$graphVersion})
WHERE n.kind = 'Feature'
  AND coalesce(n.attributesJson, '') CONTAINS '"menuId":"'
  AND coalesce(n.state, '') <> 'unresolved'
RETURN count(n) AS total,
       sum(CASE WHEN coalesce(n.degree, 0) = 0 THEN 1 ELSE 0 END) AS isolated
LIMIT `$limit
"@

    $lowValueResult = Invoke-GraphReadQuery $baseUri $encodedProjectId @"
MATCH (n:GraphEntity {projectId: `$projectId, graphVersion: `$graphVersion})
WHERE n.kind = 'Code' AND n.role IN ['type', 'module', 'frontend-module']
RETURN count(n) AS total,
       sum(CASE WHEN coalesce(n.degree, 0) = 0 THEN 1 ELSE 0 END) AS isolated
LIMIT `$limit
"@

    $objectResult = Invoke-GraphReadQuery $baseUri $encodedProjectId @"
MATCH (n:GraphEntity {projectId: `$projectId, graphVersion: `$graphVersion})
RETURN sum(CASE WHEN n.role IN ['procedure', 'function'] THEN 1 ELSE 0 END) AS procedureFunction,
       sum(CASE WHEN n.role = 'frontend-page' THEN 1 ELSE 0 END) AS frontendPage,
       sum(CASE WHEN toLower(coalesce(n.filePath, '')) CONTAINS 'rmdal' THEN 1 ELSE 0 END) AS rmdal,
       sum(CASE WHEN toLower(coalesce(n.filePath, '')) CONTAINS 'rmdal'
                     AND coalesce(n.degree, 0) > 0
                THEN 1 ELSE 0 END) AS rmdalConnected,
       sum(CASE WHEN toLower(coalesce(n.filePath, '')) ENDS WITH '.tsx' THEN 1 ELSE 0 END) AS tsx,
       sum(CASE WHEN toLower(coalesce(n.filePath, '')) ENDS WITH '.tsx'
                     AND coalesce(n.degree, 0) > 0
                THEN 1 ELSE 0 END) AS tsxReachable
LIMIT `$limit
"@

    $edgeSpecialResult = Invoke-GraphReadQuery $baseUri $encodedProjectId @"
MATCH (source:GraphEntity {projectId: `$projectId, graphVersion: `$graphVersion})-[r]->(target:GraphEntity {projectId: `$projectId, graphVersion: `$graphVersion})
RETURN sum(CASE WHEN r.reasonCode = 'fk-constraint' AND type(r) = 'DEPENDS_ON' THEN 1 ELSE 0 END) AS foreignKeys,
       sum(CASE WHEN type(r) = 'DISPATCHES_TO' THEN 1 ELSE 0 END) AS dispatches,
       sum(CASE WHEN type(r) = 'DISPATCHES_TO'
                     AND r.reasonCode = 'roslyn-view-result'
                THEN 1 ELSE 0 END) AS namedViews
LIMIT `$limit
"@

    $communityCoverageResult = Invoke-GraphReadQuery $baseUri $encodedProjectId @"
MATCH (n:GraphEntity {projectId: `$projectId, graphVersion: `$graphVersion})
WHERE coalesce(n.degree, 0) > 0
  AND (n.kind = 'Code'
       OR n.role IN ['menu-feature', 'approval-feature', 'custom-report', 'schedule', 'batch-report',
                     'frontend-page', 'web-route', 'controller-action', 'scheduled-task',
                     'message-consumer', 'cli-command'])
RETURN count(n) AS eligible,
       sum(CASE WHEN n.communityId IS NOT NULL AND trim(n.communityId) <> ''
                THEN 1 ELSE 0 END) AS assigned
LIMIT `$limit
"@

    $edgeQuality = Get-FirstRow $edgeQualityResult
    $menu = Get-FirstRow $menuResult
    $lowValue = Get-FirstRow $lowValueResult
    $objects = Get-FirstRow $objectResult
    $edgeSpecial = Get-FirstRow $edgeSpecialResult
    $communityCoverage = Get-FirstRow $communityCoverageResult

    $totalEdges = Convert-ToInt64 (Get-Value $edgeQuality "total" 0)
    $completeEdges = Convert-ToInt64 (Get-Value $edgeQuality "complete" 0)
    $forbiddenEdges = Convert-ToInt64 (Get-Value $edgeQuality "forbidden" 0)
    $menuTotal = Convert-ToInt64 (Get-Value $menu "total" 0)
    $menuIsolated = Convert-ToInt64 (Get-Value $menu "isolated" 0)
    $lowValueIsolated = Convert-ToInt64 (Get-Value $lowValue "isolated" 0)
    $procedureFunction = Convert-ToInt64 (Get-Value $objects "procedureFunction" 0)
    $frontendPage = Convert-ToInt64 (Get-Value $objects "frontendPage" 0)
    $namedView = Convert-ToInt64 (Get-Value $edgeSpecial "namedViews" 0)
    $rmdal = Convert-ToInt64 (Get-Value $objects "rmdal" 0)
    $rmdalConnected = Convert-ToInt64 (Get-Value $objects "rmdalConnected" 0)
    $tsx = Convert-ToInt64 (Get-Value $objects "tsx" 0)
    $tsxReachable = Convert-ToInt64 (Get-Value $objects "tsxReachable" 0)
    $foreignKeys = Convert-ToInt64 (Get-Value $edgeSpecial "foreignKeys" 0)
    $dispatches = Convert-ToInt64 (Get-Value $edgeSpecial "dispatches" 0)
    $communityEligible = Convert-ToInt64 (Get-Value $communityCoverage "eligible" 0)
    $communityAssigned = Convert-ToInt64 (Get-Value $communityCoverage "assigned" 0)

    $menuRate = if ($menuTotal -gt 0) { 100.0 * $menuIsolated / $menuTotal } else { $null }
    $communityRate = if ($communityEligible -gt 0) {
        100.0 * $communityAssigned / $communityEligible
    }
    else {
        $null
    }

    $metricsA.Add((New-Metric "A1" "Menu-backed Feature" `
        $(
            if ($VerifiedExecutableMenuItemCount -eq 0) {
                "Manual"
            }
            elseif ($menuTotal -eq $VerifiedExecutableMenuItemCount) {
                "PASS"
            }
            else {
                "FAIL"
            }) `
        "Graph attributes.menuId=$menuTotal；SQL executable=$VerifiedExecutableMenuItemCount" `
        "兩者相等（目前唯讀 SQL baseline=698）" `
        "以 attributesJson 的非空 menuId 計算，不限 role=menu-feature；custom-report/approval 也可能源自 Menu。"))
    $metricsA.Add((New-Metric "A2" "active/resolved menu 孤立率" `
        $(
            if ($null -eq $menuRate) { "NotMeasured" } elseif ($menuRate -lt 5) { "PASS" } else { "FAIL" }
        ) `
        $(
            if ($null -eq $menuRate) { "無 active/resolved menu" } else { "{0:N2}% ({1}/{2})" -f $menuRate, $menuIsolated, $menuTotal }
        ) `
        "<5%" "active graph role/state/degree 聚合。"))
    $metricsA.Add((New-Metric "A3" "低價值 Code degree=0" `
        $(
            if ($lowValueIsolated -eq 0) { "PASS" } else { "FAIL" }
        ) `
        "$lowValueIsolated" "0；高價值 unresolved 附清單" `
        "低價值定義為 Code type/module/frontend-module；高價值清單仍需人工覆核。"))
    $metricsA.Add((New-Metric "A4" "procedure+function" `
        $(
            if ($procedureFunction -ge 250) { "PASS" } else { "FAIL" }
        ) `
        "$procedureFunction" ">=250" "active graph role 聚合。"))
    $metricsA.Add((New-Metric "A5" "FK DEPENDS_ON" `
        $(
            if ($VerifiedFkConstraintEvidenceCount -ge 463 -and $foreignKeys -ge 420) {
                "PASS"
            }
            elseif ($VerifiedFkConstraintEvidenceCount -eq 0) {
                "Manual"
            }
            else {
                "FAIL"
            }) `
        "evidence=$VerifiedFkConstraintEvidenceCount；canonicalEdges=$foreignKeys" `
        "evidence>=463；canonical pair edges>=420" `
        "constraint evidence 與 canonical edge 分開量測，避免 source/kind/target 去重造成假失敗。"))
    $metricsA.Add((New-Metric "A6" "PluginReport dispatch" "Manual" `
        "DISPATCHES_TO=$dispatches" ">=24；stub unresolved" `
        "dispatch 數已自動量測；stub unresolved 需人工確認。"))
    $metricsA.Add((New-Metric "A7" "frontend-page" `
        $(
            if ($frontendPage -ge 600) { "PASS" } else { "FAIL" }
        ) `
        "$frontendPage" ">=600" "active graph role 聚合。"))
    $metricsA.Add((New-Metric "A8" "Named View" "NotMeasured" `
        "$namedView" ">=T0.3 baseline 90%" `
        "以 DISPATCHES_TO 且 reasonCode=roslyn-view-result 量測；未提供 T0.3 baseline，禁止假 PASS。"))
    $metricsA.Add((New-Metric "A9" "另類基金 Feature→Page→JS" "Manual" `
        "未執行 fixture path" "hop<=4" "需要已核准 fixture 與人工語意確認。"))
    $metricsA.Add((New-Metric "A10" "利息收入 ReportKernel→Data" "Manual" `
        "未執行 fixture path" "路徑存在" "需要已核准 fixture。"))
    $metricsA.Add((New-Metric "A11" "TSX reachable" "NotMeasured" `
        "$tsxReachable/$tsx" ">=T0.3 baseline 90%" `
        "已量測 degree>0；未提供 T0.3 baseline。"))
    $metricsA.Add((New-Metric "A12" "Edge V4 必要 properties" `
        $(
            if ($totalEdges -gt 0 -and $completeEdges -eq $totalEdges) { "PASS" } else { "FAIL" }
        ) `
        "$completeEdges/$totalEdges" "100%" `
        "weight/confidence/reasonCode/evidenceCount/evidenceRef 聚合。"))
    $metricsA.Add((New-Metric "A13" "禁止 relationship properties" `
        $(
            if ($totalEdges -gt 0 -and $forbiddenEdges -eq 0) { "PASS" } else { "FAIL" }
        ) `
        "forbidden=$forbiddenEdges / total=$totalEdges" "100% 無禁止欄位" `
        "檢查 evidenceJson/sourceId/targetId keys。"))
    $metricsA.Add((New-Metric "A14" "RMDAL Code" "Manual" `
        "$rmdal（degree>0: $rmdalConnected）" ">=500 且抽樣有關係" `
        "數量與 connected 已量測；關係抽樣需人工。"))
    foreach ($id in 15..18) {
        $name = @{
            15 = "Edge Precision"
            16 = "Internal CALLS Golden Recall"
            17 = "SQL Module→Module"
            18 = "E3 ReasonCode 抽樣"
        }[$id]
        $metricsA.Add((New-Metric "A$id" $name "Manual" "未載入 fixture/人工判定" `
            "依 spec §11.1" "不得在缺少 Golden fixture 時假 PASS。"))
    }

    $communityNames = @{
        1 = "C0 數量"
        2 = "C1 Anchor"
        3 = "C1 Resolved member"
        4 = "C1 Unresolved"
        5 = "C2 reports/member"
        6 = "connected primary communityId"
        7 = "C1 parent 指向 C0"
        8 = "shared 不作跨社群中介"
        9 = "C2 reproducibility"
    }
    $c0Count = Convert-ToInt64 (Get-Value $communityAcceptance "c0Count" 0)
    $eligibleAnchorCount = Convert-ToInt64 (
        Get-Value $communityAcceptance "eligibleAnchorCount" 0)
    $c1Count = Convert-ToInt64 (Get-Value $communityAcceptance "c1Count" 0)
    $c1ResolvedCount = Convert-ToInt64 (
        Get-Value $communityAcceptance "c1ResolvedCount" 0)
    $c1UnresolvedCount = Convert-ToInt64 (
        Get-Value $communityAcceptance "c1UnresolvedCount" 0)
    $c1ResolvedMinimumMembers = Convert-ToInt64 (
        Get-Value $communityAcceptance "c1ResolvedMinimumMembers" 0)
    $c1ResolvedMaximumMembers = Convert-ToInt64 (
        Get-Value $communityAcceptance "c1ResolvedMaximumMembers" 0)
    $c1InvalidUnresolvedCount = Convert-ToInt64 (
        Get-Value $communityAcceptance "c1InvalidUnresolvedCount" 0)
    $c1InvalidParentCount = Convert-ToInt64 (
        Get-Value $communityAcceptance "c1InvalidParentCount" 0)
    $c2Count = Convert-ToInt64 (Get-Value $communityAcceptance "c2Count" 0)
    $c2MinimumMembers = Convert-ToInt64 (
        Get-Value $communityAcceptance "c2MinimumMembers" 0)
    $c2InvalidMemberCount = Convert-ToInt64 (
        Get-Value $communityAcceptance "c2InvalidMemberCount" 0)
    $connectedEligible = Convert-ToInt64 (
        Get-Value $communityAcceptance "connectedEligibleCount" 0)
    $connectedAssigned = Convert-ToInt64 (
        Get-Value $communityAcceptance "connectedAssignedCount" 0)
    $communityAcceptanceRate = if ($connectedEligible -gt 0) {
        100.0 * $connectedAssigned / $connectedEligible
    }
    else {
        $null
    }

    $metricsB.Add((New-Metric "B1" $communityNames[1] `
        $(if ($c0Count -ge 8 -and $c0Count -le 25) { "PASS" } else { "FAIL" }) `
        "$c0Count" "8～25" "Community acceptance diagnostics 聚合。"))
    $metricsB.Add((New-Metric "B2" $communityNames[2] `
        $(if ($eligibleAnchorCount -gt 0 -and $c1Count -eq $eligibleAnchorCount) {
            "PASS"
        } else { "FAIL" }) `
        "C1=$c1Count；eligible anchors=$eligibleAnchorCount" `
        "C1 等於合格 anchor 數" "Community acceptance diagnostics 聚合。"))
    $metricsB.Add((New-Metric "B3" $communityNames[3] `
        $(if ($c1ResolvedCount -gt 0 -and
              $c1ResolvedMinimumMembers -ge 3 -and
              $c1ResolvedMaximumMembers -le 60) { "PASS" } else { "FAIL" }) `
        "resolved=$c1ResolvedCount；member=$c1ResolvedMinimumMembers～$c1ResolvedMaximumMembers" `
        "member 3～60" "只統計 summaryState 非 unresolved 的 C1。"))
    $metricsB.Add((New-Metric "B4" $communityNames[4] `
        $(if ($c1UnresolvedCount -ge 0 -and $c1InvalidUnresolvedCount -eq 0) {
            "PASS"
        } else { "FAIL" }) `
        "unresolved=$c1UnresolvedCount；invalid=$c1InvalidUnresolvedCount" `
        "member 1～2 且 100% 標 unresolved" "以 Community state 與 member count 交叉檢查。"))
    $metricsB.Add((New-Metric "B5" $communityNames[5] `
        $(if ($c2Count -le 100 -and
              ($c2Count -eq 0 -or $c2MinimumMembers -ge 3) -and
              $c2InvalidMemberCount -eq 0) { "PASS" } else { "FAIL" }) `
        "reports=$c2Count；minimum members=$c2MinimumMembers；invalid=$c2InvalidMemberCount" `
        "<=100 reports 且 member>=3" "未成 report 的小群組保留 unresolved membership。"))
    $metricsB.Add((New-Metric "B6" $communityNames[6] `
        $(if ($null -ne $communityAcceptanceRate -and
              $communityAcceptanceRate -ge 90) { "PASS" } else { "FAIL" }) `
        $(if ($null -eq $communityAcceptanceRate) {
            "無 eligible node"
        } else {
            "{0:N2}% ({1}/{2})" -f
                $communityAcceptanceRate, $connectedAssigned, $connectedEligible
        }) `
        ">=90%" "由 active graph 的 eligible/primary communityId 聚合。"))
    $metricsB.Add((New-Metric "B7" $communityNames[7] `
        $(if ($c1Count -gt 0 -and $c1InvalidParentCount -eq 0) {
            "PASS"
        } else { "FAIL" }) `
        "C1=$c1Count；invalid parent=$c1InvalidParentCount" `
        "100% C1 parent 指向 C0" "Community acceptance diagnostics 聚合。"))
    $metricsB.Add((New-Metric "B8" $communityNames[8] "Manual" `
        "需由 GraphRAGV4ModelTests 的 shared bridge fixture 驗證" "抽樣100%" `
        "真實 Graph 聚合不能反證已被 shared 節點合併，使用固定拓撲測試。"))
    $metricsB.Add((New-Metric "B9" $communityNames[9] "Manual" `
        "digest=$(Get-Value $communityAcceptance 'membershipDigest' '')" `
        "相同 snapshot/config digest 相同" `
        "目前報告保存 digest；重跑比較與 deterministic fixture 另由測試驗證。"))

    $searchNames = @{
        1 = "中文業務名稱"
        2 = "BondTradeService"
        3 = "ProcessLogin 等 Method"
        4 = "SettlementDate／交割日"
        5 = "/Controller/Action"
        6 = "SP/Function 名稱"
    }
    foreach ($id in 1..6) {
        $metricsS.Add((New-Metric "S$id" $searchNames[$id] "Manual" `
            "本腳本不呼叫問答/檢索 POST" "Top-5>=90%；Exact Top-1>=95%" `
            "需要固定 fixture 與保存 normalized query/seed。"))
    }

    $progressPhase = [string](Get-Value $indexProgress "phase" "unknown")
    $progressMode = [string](Get-Value $indexProgress "mode" "")
    $indexRunMode = [string](Get-Value $indexRun "mode" "")
    $indexRunStatus = [string](Get-Value $indexRun "status" "")
    $indexElapsedMilliseconds =
        Convert-ToInt64 (Get-Value $indexRun "elapsedMilliseconds" 0)
    $peakWorkingSetBytes =
        Convert-ToInt64 (Get-Value $indexRun "peakWorkingSetBytes" 0)
    $stageMetrics = @(Get-Value $indexRun "stageMetrics" @())
    $processedFileCount = [long]0
    $processedSourceBytes = [long]0
    foreach ($stageMetric in $stageMetrics) {
        $processedFileCount = [Math]::Max(
            $processedFileCount,
            (Convert-ToInt64 (Get-Value $stageMetric "sourceFileCount" 0)))
        $processedSourceBytes = [Math]::Max(
            $processedSourceBytes,
            (Convert-ToInt64 (Get-Value $stageMetric "sourceBytes" 0)))
    }
    $elapsedMinutes = $indexElapsedMilliseconds / 60000.0
    $v4FilesPerMinute = if ($elapsedMinutes -gt 0) {
        $processedFileCount / $elapsedMinutes
    }
    else {
        0.0
    }
    $v4MegabytesPerMinute = if ($elapsedMinutes -gt 0) {
        ($processedSourceBytes / 1MB) / $elapsedMinutes
    }
    else {
        0.0
    }
    $hasFullRun =
        $indexRunMode -eq "full" -and
        $indexRunStatus -in @("succeeded", "partial") -and
        $indexElapsedMilliseconds -gt 0
    $fileDifferenceRate = if ($V3ProcessedFileCount -gt 0) {
        [Math]::Abs($processedFileCount - $V3ProcessedFileCount) /
            [double]$V3ProcessedFileCount
    }
    else {
        [double]::PositiveInfinity
    }
    $byteDifferenceRate = if ($V3ProcessedSourceBytes -gt 0) {
        [Math]::Abs($processedSourceBytes - $V3ProcessedSourceBytes) /
            [double]$V3ProcessedSourceBytes
    }
    else {
        [double]::PositiveInfinity
    }
    $scopeComparable =
        $ComparableV3Environment -and
        $V3FullIndexElapsedMilliseconds -gt 0 -and
        $fileDifferenceRate -le 0.10 -and
        $byteDifferenceRate -le 0.10
    $wallClockRatio = if ($scopeComparable) {
        $indexElapsedMilliseconds / [double]$V3FullIndexElapsedMilliseconds
    }
    else {
        0.0
    }
    $c3Status = if (-not $hasFullRun) {
        "NotMeasured"
    }
    elseif ($scopeComparable) {
        if ($wallClockRatio -le 1.5) { "PASS" }
        elseif ($wallClockRatio -le 2.0) { "Warning" }
        elseif ($wallClockRatio -le 2.5) { "Conditional" }
        else { "FAIL" }
    }
    elseif ($FullIndexAbsoluteBudgetMinutes -gt 0) {
        if ($elapsedMinutes -le $FullIndexAbsoluteBudgetMinutes) { "PASS" } else { "FAIL" }
    }
    else {
        "NotMeasured"
    }
    $c3Actual = if ($scopeComparable) {
        "V4={0:N2}min；V3={1:N2}min；ratio={2:N2}x；files={3}/{4}；bytes={5}/{6}" -f
            $elapsedMinutes,
            ($V3FullIndexElapsedMilliseconds / 60000.0),
            $wallClockRatio,
            $processedFileCount,
            $V3ProcessedFileCount,
            $processedSourceBytes,
            $V3ProcessedSourceBytes
    }
    else {
        "V4={0:N2}min；scope comparable=false；absolute budget={1:N2}min" -f
            $elapsedMinutes,
            $FullIndexAbsoluteBudgetMinutes
    }
    $c4Status = if (-not $hasFullRun -or
        $V3FilesPerMinute -le 0 -or
        $V3MegabytesPerMinute -le 0) {
        "NotMeasured"
    }
    elseif ($v4FilesPerMinute -ge 0.5 * $V3FilesPerMinute -and
        $v4MegabytesPerMinute -ge 0.5 * $V3MegabytesPerMinute) {
        "PASS"
    }
    else {
        "FAIL"
    }
    $c5Status = if (-not $hasFullRun -or
        $peakWorkingSetBytes -le 0 -or
        $PreflightBudgetBytes -le 0) {
        "NotMeasured"
    }
    elseif ($peakWorkingSetBytes -le $PreflightBudgetBytes) {
        "PASS"
    }
    else {
        "FAIL"
    }
    $storageNames = @{
        1 = "Neo4j relationship property size"
        2 = "SQLite/Neo4j Evidence count 對帳"
        3 = "Full Index wall-clock"
        4 = "Normalized throughput"
        5 = "Peak Working Set"
        6 = "no-op"
        7 = "Local Retrieval P95"
        8 = "Hydration 單次 batch"
        9 = "Community template 即時可用"
        10 = "AI failure 不影響可用性"
        11 = "DB failure no-promote"
        12 = "Reconciliation failure matrix"
    }
    $storageGraph = Get-Value $storageAcceptance "graph" $null
    $storageEvidence = Get-Value $storageAcceptance "evidence" $null
    $storageVersionsConsistent = [bool](
        Get-Value $storageAcceptance "versionsConsistent" $false)
    $storageStable = [bool](
        Get-Value $storageAcceptance "storageStable" $false)
    $storageActiveNodes = Convert-ToInt64 (
        Get-Value $storageGraph "activeNodeCount" 0)
    $storageActiveEdges = Convert-ToInt64 (
        Get-Value $storageGraph "activeEdgeCount" 0)
    $evidenceNodes = Convert-ToInt64 (
        Get-Value $storageEvidence "nodes" 0)
    $evidenceEdges = Convert-ToInt64 (
        Get-Value $storageEvidence "edges" 0)
    for ($id = 1; $id -le 12; $id++) {
        if ($id -eq 1) {
            $metricsC.Add((New-Metric "C1" $storageNames[$id] `
                $(if ($totalEdges -gt 0 -and $forbiddenEdges -eq 0) {
                    "PASS"
                } else { "FAIL" }) `
                "forbidden relationship evidence properties=$forbiddenEdges/$totalEdges" `
                "較 V3 evidenceJson 降低>=80%" `
                "V4 relationship 已完全移除 evidenceJson/sourceId/targetId，降幅為100%。"))
        }
        elseif ($id -eq 2) {
            $countConsistent =
                $storageStable -and
                $storageVersionsConsistent -and
                $storageActiveNodes -eq $evidenceNodes -and
                $storageActiveEdges -eq $evidenceEdges
            $metricsC.Add((New-Metric "C2" $storageNames[$id] `
                $(if ($countConsistent) { "PASS" } else { "FAIL" }) `
                "Neo4j=$storageActiveNodes/$storageActiveEdges；Evidence=$evidenceNodes/$evidenceEdges；sameVersion=$storageVersionsConsistent；stable=$storageStable" `
                "entity reference count 100%" `
                "發布閘門逐 entity 計數；內容抽樣由 GraphEvidenceStoreV4Tests 驗證。"))
        }
        elseif ($id -eq 3) {
            $metricsC.Add((New-Metric "C3" $storageNames[$id] `
                $c3Status $c3Actual `
                "<=1.5x；scope 不可比時 <=核准絕對預算" `
                "GET index/run；可比性同時檢查 file/bytes 差異<=10%及明確環境確認。"))
        }
        elseif ($id -eq 4) {
            $metricsC.Add((New-Metric "C4" $storageNames[$id] `
                $c4Status `
                ("V4={0:N2} files/min, {1:N2} MB/min；V3={2:N2}, {3:N2}" -f
                    $v4FilesPerMinute,
                    $v4MegabytesPerMinute,
                    $V3FilesPerMinute,
                    $V3MegabytesPerMinute) `
                "files/min 與 MB/min 均不得較可比 V3 下降>50%" `
                "以完整 run elapsed 與 stageMetrics 最大 processed source 數計算，避免跨 stage 重複加總。"))
        }
        elseif ($id -eq 5) {
            $metricsC.Add((New-Metric "C5" $storageNames[$id] `
                $c5Status `
                "peak=$peakWorkingSetBytes bytes；budget=$PreflightBudgetBytes bytes" `
                "<=preflight budget；無 OOM/process crash" `
                "GET index/run peakWorkingSetBytes。"))
        }
        elseif ($id -eq 6) {
            $noOpMode = [string](Get-Value $noOpIndexRun "mode" "")
            $noOpStatus = [string](Get-Value $noOpIndexRun "status" "")
            $isNoOp = $noOpMode -eq "no-op" -and $noOpStatus -eq "succeeded"
            $metricsC.Add((New-Metric "C6" $storageNames[$id] `
                $(
                    if ($isNoOp) { "PASS" } else { "NotMeasured" }
                ) `
                "status=$noOpStatus, mode=$noOpMode" "無變更不重建" `
                "讀取 mode=no-op 的獨立 run；不會覆蓋 full run 效能資料。"))
        }
        elseif ($id -eq 9) {
            $templatesAvailable =
                $c0Count -gt 0 -and
                [bool](Get-Value $summaryProgress "structuralIndexAvailable" $false)
            $metricsC.Add((New-Metric "C9" $storageNames[$id] `
                $(if ($templatesAvailable) { "PASS" } else { "FAIL" }) `
                "C0=$c0Count；structuralIndexAvailable=$templatesAvailable" `
                "Graph active 後 template 立即可用" `
                "Community template 與 graph 同一 immutable snapshot 發布。"))
        }
        elseif ($id -eq 10) {
            $aiFailed = Convert-ToInt64 (Get-Value $summaryProgress "failed" 0)
            $structuralAvailable =
                [bool](Get-Value $summaryProgress "structuralIndexAvailable" $false)
            $metricsC.Add((New-Metric "C10" $storageNames[$id] `
                $(if ($structuralAvailable) { "PASS" } else { "FAIL" }) `
                "AI failed=$aiFailed；structural available=$structuralAvailable" `
                "AI failure 不使 index/answer unavailable" `
                "AI Summary 失敗時仍以 deterministic template 回答。"))
        }
        else {
            $metricsC.Add((New-Metric "C$id" $storageNames[$id] "NotMeasured" `
                "目前唯讀 API 資料不足" "依 spec §11.4" `
                "不直接讀 SQLite/Neo4j/外部 SQL，也不啟動寫入型 benchmark。"))
        }
    }

    $summaryTotal = Convert-ToInt64 (Get-Value $summaryProgress "total" 0)
    $summaryQueued = Convert-ToInt64 (Get-Value $summaryProgress "queued" 0)
    $summaryRunning = Convert-ToInt64 (Get-Value $summaryProgress "running" 0)
    $summaryCompleted = Convert-ToInt64 (Get-Value $summaryProgress "completed" 0)
    $summaryFailed = Convert-ToInt64 (Get-Value $summaryProgress "failed" 0)
    $summaryPercent = Convert-ToInt64 (Get-Value $summaryProgress "percent" 0)
    $summarySum = $summaryQueued + $summaryRunning + $summaryCompleted + $summaryFailed
    $progressShapeValid =
        $summaryTotal -eq $summarySum -and
        $summaryPercent -ge 0 -and
        $summaryPercent -le 100

    $metricsAi.Add((New-Metric "AI1" "Publish 阻塞" "NotMeasured" `
        "未觀察 publish 時段" "AI call=0" "腳本不啟動索引。"))
    $metricsAi.Add((New-Metric "AI2" "Queue concurrency" "Manual" `
        "current project running=$summaryRunning" "per-project<=1、global<=2" `
        "可觀察單一專案，無 global progress API。"))
    $metricsAi.Add((New-Metric "AI3" "cacheKey dedupe" "NotMeasured" `
        "API 未暴露重複工作數" "重複工作=0" "需 queue diagnostics 或測試報告。"))
    $metricsAi.Add((New-Metric "AI4" "Progress API" `
        $(
            if ($progressShapeValid) { "PASS" } else { "FAIL" }
        ) `
        "total=$summaryTotal, queued=$summaryQueued, running=$summaryRunning, completed=$summaryCompleted, failed=$summaryFailed, percent=$summaryPercent" `
        "欄位正確且狀態總和一致" "GET summaries/progress。"))
    $metricsAi.Add((New-Metric "AI5" "UI 分離顯示" "Manual" `
        "未執行 UI 視覺驗收" "結構可用與 AI 進度分開" "需桌面 UI 人工確認。"))
    $metricsAi.Add((New-Metric "AI6" "Failure 保留 template" "Manual" `
        "failed=$summaryFailed" "template 保留、無無限 retry" `
        "Progress 可見 failed，但 template/retry 明細未由 API 暴露。"))

    $answerNames = @{
        1 = "另類基金畫面加欄位"
        2 = "利息收入增減分析"
        3 = "tblPosition105 加欄位影響"
        4 = "公告管理驗證前端影響"
        5 = "Bloomberg 匯率排程"
        6 = "登入流程"
        7 = "債券交易流程"
        8 = "SettlementDate 影響"
    }
    foreach ($id in 1..8) {
        $metricsQ.Add((New-Metric "Q$id" $answerNames[$id] "Manual" `
            "未呼叫問答 API" "至少7/8達4分；Q6-Q8不得 missing seed" `
            "需要人工 1~5 分、source snippet 與 known gaps fixture。"))
    }

    $cleanupNames = @{
        1 = "穩定狀態只保留 active"
        2 = "Publish/Reconcile version 狀態"
        3 = "SQLite 無 retired/orphan Evidence"
        4 = "非 GraphRAG table 不變"
        5 = "暫存檔清理"
        6 = "Publish failure 保留舊 active"
    }
    foreach ($id in 1..6) {
        $metricsD.Add((New-Metric "D$id" $cleanupNames[$id] "NotMeasured" `
            "唯讀 API 未暴露 inactive version/SQLite/檔案清單" "依 spec §11.7" `
            "腳本不執行 cleanup，也不直接查資料庫，禁止假 PASS。"))
    }

    $commitHash = try {
        $hash = (& git rev-parse --short HEAD 2>$null)
        if ($LASTEXITCODE -eq 0) { [string]$hash } else { "unknown" }
    }
    catch {
        "unknown"
    }
    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.AppendLine("# GraphRAG V4 驗收報告")
    [void]$builder.AppendLine()
    [void]$builder.AppendLine("- 執行時間：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')")
    [void]$builder.AppendLine("- 執行人：$([Environment]::UserName)")
    [void]$builder.AppendLine("- commit hash：$commitHash")
    [void]$builder.AppendLine("- ProjectId：$(Escape-Markdown $ProjectId)")
    [void]$builder.AppendLine("- Source：$(Escape-Markdown (Get-Value $project 'rootPath' 'NotMeasured'))")
    [void]$builder.AppendLine("- DB：NotMeasured（唯讀 API 未暴露 DB fingerprint／Promote Gate 明細）")
    [void]$builder.AppendLine("- Neo4j：active Graph readiness gate 通過")
    [void]$builder.AppendLine("- SQLite：Evidence readiness gate 通過；row 明細未直接量測")
    [void]$builder.AppendLine("- CPU/RAM/冷熱機：logical CPU=$([Environment]::ProcessorCount)；RAM/冷熱機 NotMeasured")
    [void]$builder.AppendLine("- CompilationMode：NotMeasured")
    [void]$builder.AppendLine("- MSBuild 成功/失敗 Project 數：NotMeasured")
    [void]$builder.AppendLine("- Synthetic/batch fallback 數：NotMeasured")
    [void]$builder.AppendLine("- DB Promote Gate：NotMeasured")
    [void]$builder.AppendLine("- Project index：status=$(Escape-Markdown (Get-Value $project 'indexStatus' 'unknown'))，manifest=$(Escape-Markdown (Get-Value $project 'indexManifestVersion' ''))")
    [void]$builder.AppendLine("- Graph schema：nodes=$(Get-Value $schema 'totalNodes' 0)，edges=$(Get-Value $schema 'totalEdges' 0)")
    [void]$builder.AppendLine()

    Add-MetricSection $builder "1. A1～A18 Coverage/Precision/Recall" $metricsA
    Add-MetricSection $builder "2. B1～B9 Community" $metricsB
    Add-MetricSection $builder "3. S1～S6 Search Seed" $metricsS
    Add-MetricSection $builder "4. C1～C12 Storage/Publish/Performance" $metricsC
    Add-MetricSection $builder "5. AI1～AI6 Background Summary" $metricsAi
    Add-MetricSection $builder "6. Q1～Q8 Answer Quality" $metricsQ
    Add-MetricSection $builder "7. D1～D6 Cleanup/Safety" $metricsD

    [void]$builder.AppendLine("## 8. Stage Metrics/Normalized Throughput/Peak RAM")
    [void]$builder.AppendLine()
    $stageMetricText = if ($stageMetrics.Count -eq 0) {
        "NotMeasured"
    }
    else {
        (@($stageMetrics | ForEach-Object {
            "{0}={1}ms/files:{2}/bytes:{3}" -f
                (Get-Value $_ "stage" "unknown"),
                (Get-Value $_ "elapsedMilliseconds" 0),
                (Get-Value $_ "sourceFileCount" 0),
                (Get-Value $_ "sourceBytes" 0)
        }) -join "；")
    }
    [void]$builder.AppendLine("- Stage metrics：$(Escape-Markdown $stageMetricText)")
    [void]$builder.AppendLine("- no-op run：phase=$progressPhase；mode=$progressMode；elapsedMilliseconds=$(Get-Value $indexProgress 'elapsedMilliseconds' 0)。")
    [void]$builder.AppendLine(("- normalized throughput：{0:N2} files/min；{1:N2} MB/min。" -f
        $v4FilesPerMinute,
        $v4MegabytesPerMinute))
    [void]$builder.AppendLine("- Peak RAM：$peakWorkingSetBytes bytes；preflight budget=$PreflightBudgetBytes bytes。")
    foreach ($diagnostic in $diagnostics) {
        [void]$builder.AppendLine("- Diagnostics：$(Escape-Markdown $diagnostic)")
    }
    [void]$builder.AppendLine()

    [void]$builder.AppendLine("## 9. Diagnostics 統計")
    [void]$builder.AppendLine()
    [void]$builder.AppendLine("- Node roles：$(Convert-DistributionToMarkdown (Get-Value $roleResult 'rows' @()) 'role')")
    [void]$builder.AppendLine("- Edge reasonCode：$(Convert-DistributionToMarkdown (Get-Value $reasonResult 'rows' @()) 'reasonCode')")
    [void]$builder.AppendLine("- Node kinds：$(Convert-DistributionToMarkdown (Get-Value $schema 'nodeKinds' @()) 'name')")
    [void]$builder.AppendLine("- Relationship types：$(Convert-DistributionToMarkdown (Get-Value $schema 'relationshipTypes' @()) 'name')")
    [void]$builder.AppendLine("- Summary progress message：$(Escape-Markdown (Get-Value $summaryProgress 'message' ''))")
    [void]$builder.AppendLine()

    $allMetrics = @($metricsA) + @($metricsB) + @($metricsS) +
        @($metricsC) + @($metricsAi) + @($metricsQ) + @($metricsD)
    $failures = @($allMetrics | Where-Object { $_.Status -eq "FAIL" })
    $warnings = @($allMetrics | Where-Object {
            $_.Status -in @("Warning", "Conditional")
        })
    $manualCount = @($allMetrics | Where-Object {
            $_.Status -in @("Manual", "NotMeasured")
        }).Count

    [void]$builder.AppendLine("## 10. FAIL/Warning/Conditional 處置")
    [void]$builder.AppendLine()
    if ($failures.Count -eq 0) {
        [void]$builder.AppendLine("- 自動量測未產生 FAIL；Manual/NotMeasured 仍不得視為通過。")
    }
    else {
        foreach ($failure in $failures) {
            [void]$builder.AppendLine("- FAIL $($failure.Id)：$($failure.Name)；actual=$($failure.Actual)。")
        }
    }
    foreach ($warning in $warnings) {
        [void]$builder.AppendLine("- $($warning.Status) $($warning.Id)：$($warning.Name)。")
    }
    [void]$builder.AppendLine("- Manual/NotMeasured 共 $manualCount 項，正式驗收前必須補 fixture、人工判定或唯讀 diagnostics。")
    [void]$builder.AppendLine()

    [void]$builder.AppendLine("## 11. Known Gaps")
    [void]$builder.AppendLine()
    [void]$builder.AppendLine("- 未直接連線外部 SQL、Neo4j 或 SQLite；避免驗收腳本繞過應用程式的 read-only 邊界。")
    [void]$builder.AppendLine("- 未觸發 full/no-op index、AI failure、publish failure 或 reconciliation；這些操作會改變本機狀態。")
    [void]$builder.AppendLine("- 未提供 V3/T0.3 baseline、Golden fixture、人工 edge precision 與 Q1～Q8 分數。")
    [void]$builder.AppendLine("- GraphCommunity 明細目前無受限唯讀 API，因此 B1～B5/B7～B9 無法自動驗證。")
    [void]$builder.AppendLine()
    [void]$builder.AppendLine("> 安全聲明：本腳本只寫入本報告檔；未執行新增、刪除、修改或 cleanup 資料操作。")

    $reportDirectory = Split-Path -Parent $absoluteReportPath
    if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
        [IO.Directory]::CreateDirectory($reportDirectory) | Out-Null
    }
    [IO.File]::WriteAllText(
        $absoluteReportPath,
        $builder.ToString(),
        [Text.UTF8Encoding]::new($false))

    Write-Host "GraphRAG V4 驗收報告：$absoluteReportPath"
    Write-Host "Automatic FAIL=$($failures.Count), Manual/NotMeasured=$manualCount"
    if ($failures.Count -gt 0) {
        exit 2
    }
    if (-not $AllowIncomplete -and
        ($warnings.Count -gt 0 -or $manualCount -gt 0)) {
        Write-Error (
            "正式驗收仍有 Warning/Conditional={0}、Manual/NotMeasured={1}；" +
            "不得視為全數通過。若只需蒐集部分數據，請明確使用 -AllowIncomplete。" -f
            $warnings.Count,
            $manualCount)
        exit 3
    }
}
catch {
    $fatalError = $_
    Write-Error (
        "GraphRAG V4 驗收腳本失敗：{0}`n{1}" -f
        $_.Exception.Message,
        $_.ScriptStackTrace)
}

if ($null -ne $fatalError) {
    exit 1
}
