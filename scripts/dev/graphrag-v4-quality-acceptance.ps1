[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectId,

    [string]$BaseUrl = "http://127.0.0.1:5002",

    [string]$OutputPath = "",

    [int]$EdgeSamplePerRelation = 20,

    [bool]$RunFocusedTests = $true
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

<#
.SYNOPSIS
執行 GraphRAG V4 的 A15/A16/A17、S1-S6、Q1-Q8 與 C7 效能驗收。

.DESCRIPTION
本工具只呼叫 Modern Wingman 的唯讀診斷與 read-only graph query API，
不會修改專案、Neo4j、SQLite 或投資系統資料庫。

重要限制：
1. 未經人工覆核的 edge 樣本只會標示 NeedsReview，絕不自動宣告 precision 通過。
2. S/Q fixture 尚未保存人工核准的唯一 Entity ID，因此只產生候選命中與缺口，
   不把「有搜尋結果」冒充為答案正確。
3. 報告只保存 diagnostics 已去敏感的 node ID/kind/role/score；
   edge 樣本只保存圖 ID、關係類型、信心與 reason code。
#>

function Get-RepositoryRoot {
    $scriptDirectory = Split-Path -Parent $PSCommandPath
    return [System.IO.Path]::GetFullPath(
        (Join-Path $scriptDirectory "..\.."))
}

function Invoke-JsonPost {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][object]$Body
    )

    $json = $Body | ConvertTo-Json -Depth 12 -Compress
    return Invoke-RestMethod `
        -Method Post `
        -Uri $Uri `
        -ContentType "application/json; charset=utf-8" `
        -Body $json
}

function Get-ExpectedCoverage {
    param(
        [Parameter(Mandatory = $true)][object]$FixtureItem,
        [Parameter(Mandatory = $true)][object]$Diagnostics
    )

    $actualKinds = @($Diagnostics.hits | ForEach-Object { [string]$_.kind })
    $actualRoles = @($Diagnostics.hits | ForEach-Object { [string]$_.role })
    # ConvertFrom-Json 對不存在的 property 會回傳 null；若不先過濾，
    # 報告會把 null 當成一個必要 kind/role，造成明明沒有角色門檻卻顯示失敗。
    $expectedKinds = @(
        @($FixtureItem.expectedKinds) |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    $requiredKinds = @(
        @($FixtureItem.requiredKinds) |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    $expectedRoles = @(
        @($FixtureItem.expectedRoles) |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    $kindMatches = @($expectedKinds | Where-Object {
        $actualKinds -contains [string]$_
    })
    $roleMatches = @($expectedRoles | Where-Object {
        $actualRoles -contains [string]$_
    })

    return [ordered]@{
        expectedKinds = @($expectedKinds)
        requiredKinds = @($requiredKinds)
        observedKinds = @($actualKinds | Sort-Object -Unique)
        matchedKinds = @($kindMatches | Sort-Object -Unique)
        kindRequirementMet = (
            ($expectedKinds.Count -eq 0 -or $kindMatches.Count -gt 0) -and
            ($requiredKinds.Count -eq 0 -or
             @($requiredKinds | Where-Object {
                 $actualKinds -contains [string]$_
             }).Count -eq $requiredKinds.Count))
        expectedRoles = @($expectedRoles)
        observedRoles = @($actualRoles | Sort-Object -Unique)
        matchedRoles = @($roleMatches | Sort-Object -Unique)
        roleRequirementMet = (
            $expectedRoles.Count -eq 0 -or $roleMatches.Count -gt 0)
    }
}

function Invoke-SeedCase {
    param(
        [Parameter(Mandatory = $true)][object]$FixtureItem,
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$CaseType
    )

    try {
        $uri = "$BaseUrl/api/projects/$ProjectId/retrieval/seed-diagnostics"
        $diagnostics = Invoke-JsonPost `
            -Uri $uri `
            -Body ([ordered]@{ question = $Text; limit = 10 })
        $coverage = Get-ExpectedCoverage `
            -FixtureItem $FixtureItem `
            -Diagnostics $diagnostics
        return [ordered]@{
            id = [string]$FixtureItem.id
            type = $CaseType
            input = $Text
            executionStatus = "Completed"
            reviewStatus = "NeedsReview"
            missingSeed = [bool]$diagnostics.missingSeed
            graphVersion = [string]$diagnostics.graphVersion
            attemptedSeedKinds = @($diagnostics.attemptedSeedKinds)
            coverage = $coverage
            hits = @($diagnostics.hits)
            error = $null
        }
    }
    catch {
        return [ordered]@{
            id = [string]$FixtureItem.id
            type = $CaseType
            input = $Text
            executionStatus = "Failed"
            reviewStatus = "Blocked"
            missingSeed = $true
            graphVersion = $null
            attemptedSeedKinds = @()
            coverage = $null
            hits = @()
            error = $_.Exception.Message
        }
    }
}

function Invoke-RetrievalTiming {
    param(
        [Parameter(Mandatory = $true)][string]$Question
    )

    try {
        $uri = "$BaseUrl/api/projects/$ProjectId/retrieval/diagnostics"
        $result = Invoke-JsonPost `
            -Uri $uri `
            -Body ([ordered]@{ question = $Question })
        return [ordered]@{
            executionStatus = "Completed"
            elapsedMilliseconds = [long]$result.elapsedMilliseconds
            promptCharacters = [int]$result.promptCharacters
            error = $null
        }
    }
    catch {
        return [ordered]@{
            executionStatus = "Failed"
            elapsedMilliseconds = $null
            promptCharacters = $null
            error = $_.Exception.Message
        }
    }
}

function Invoke-RetrievalBenchmark {
    param(
        [Parameter(Mandatory = $true)][string[]]$Questions
    )

    $caseResults = @()
    $allMeasurements = @()
    foreach ($question in $Questions) {
        # 每題先暖機五次，暖機值不納入統計；正式量測固定十次。
        foreach ($iteration in 1..5) {
            $warmup = Invoke-RetrievalTiming -Question $question
            if ($warmup.executionStatus -ne "Completed") {
                return [ordered]@{
                    status = "Blocked"
                    questionCount = $Questions.Count
                    warmupPerQuestion = 5
                    measurementsPerQuestion = 10
                    p95Milliseconds = $null
                    thresholdMilliseconds = 2000
                    cases = $caseResults
                    error = $warmup.error
                }
            }
        }

        $measurements = @()
        foreach ($iteration in 1..10) {
            $timing = Invoke-RetrievalTiming -Question $question
            if ($timing.executionStatus -ne "Completed") {
                return [ordered]@{
                    status = "Blocked"
                    questionCount = $Questions.Count
                    warmupPerQuestion = 5
                    measurementsPerQuestion = 10
                    p95Milliseconds = $null
                    thresholdMilliseconds = 2000
                    cases = $caseResults
                    error = $timing.error
                }
            }
            $measurements += [long]$timing.elapsedMilliseconds
            $allMeasurements += [long]$timing.elapsedMilliseconds
        }

        $ordered = @($measurements | Sort-Object)
        $p95Index = [Math]::Ceiling($ordered.Count * 0.95) - 1
        $caseResults += [ordered]@{
            question = $question
            minimumMilliseconds = $ordered[0]
            maximumMilliseconds = $ordered[-1]
            p95Milliseconds = $ordered[$p95Index]
        }
    }

    $allOrdered = @($allMeasurements | Sort-Object)
    $allP95Index = [Math]::Ceiling($allOrdered.Count * 0.95) - 1
    $p95 = [long]$allOrdered[$allP95Index]
    return [ordered]@{
        status = if ($p95 -le 2000) { "Passed" } else { "Failed" }
        questionCount = $Questions.Count
        warmupPerQuestion = 5
        measurementsPerQuestion = 10
        measuredSamples = $allOrdered.Count
        p95Milliseconds = $p95
        thresholdMilliseconds = 2000
        cases = $caseResults
        error = $null
    }
}

function Invoke-CallsGoldenRecall {
    param(
        [Parameter(Mandatory = $true)][object]$CallsFixture
    )

    $pairs = @($CallsFixture.pairs)
    if ($pairs.Count -lt 100) {
        return [ordered]@{
            status = "NeedsReview"
            golden = $pairs.Count
            found = 0
            recall = $null
            threshold = 0.90
            missingPairIds = @()
            error = "source-frozen internal CALLS golden 少於 100 筆。"
        }
    }

    try {
        $uri = "$BaseUrl/api/projects/$ProjectId/retrieval/calls-golden-diagnostics"
        $result = Invoke-JsonPost `
            -Uri $uri `
            -Body ([ordered]@{ pairs = $pairs })
        $recall = [double]$result.recall
        return [ordered]@{
            status = if ($recall -ge 0.90) { "Passed" } else { "Failed" }
            golden = $pairs.Count
            found = [int]$result.found
            recall = $recall
            threshold = 0.90
            missingPairIds = @($result.missingPairIds)
            graphVersion = [string]$result.graphVersion
            error = $null
        }
    }
    catch {
        return [ordered]@{
            status = "Blocked"
            golden = $pairs.Count
            found = 0
            recall = $null
            threshold = 0.90
            missingPairIds = @()
            error = $_.Exception.Message
        }
    }
}

function Get-EdgeSamples {
    param(
        [Parameter(Mandatory = $true)][object]$ReviewFixture
    )

    $samples = @()
    $relations = @(
        "CALLS",
        "READS",
        "WRITES",
        "ROUTES_TO",
        "DISPATCHES_TO",
        "DEPENDS_ON")
    foreach ($confidence in @("certain", "probable")) {
        foreach ($relation in $relations) {
            $cypher = @"
MATCH (source:GraphEntity {projectId: `$projectId, graphVersion: `$graphVersion})-[relationship]->(target:GraphEntity {projectId: `$projectId, graphVersion: `$graphVersion})
WHERE relationship.confidence = '$confidence' AND type(relationship) = '$relation'
RETURN relationship.id AS edgeId, type(relationship) AS relation, source.id AS sourceId, target.id AS targetId, relationship.confidence AS confidence, relationship.reasonCode AS reasonCode, relationship.topArtifact AS artifact
ORDER BY relationship.id
LIMIT `$limit
"@
            try {
                $uri = "$BaseUrl/api/projects/$ProjectId/graph/query"
                $response = Invoke-JsonPost `
                    -Uri $uri `
                    -Body ([ordered]@{
                        cypher = $cypher
                        limit = $EdgeSamplePerRelation
                    })
                foreach ($row in @($response.rows)) {
                    $labelProperty = $ReviewFixture.labels.PSObject.Properties[
                        [string]$row.edgeId]
                    $hasLabel = $null -ne $labelProperty
                    $samples += [ordered]@{
                        edgeId = [string]$row.edgeId
                        relation = [string]$row.relation
                        sourceId = [string]$row.sourceId
                        targetId = [string]$row.targetId
                        confidence = [string]$row.confidence
                        reasonCode = [string]$row.reasonCode
                        artifact = [string]$row.artifact
                        reviewStatus = if ($hasLabel) { "reviewed" } else { "pending" }
                        manualCorrect = if ($hasLabel) {
                            [bool]$labelProperty.Value
                        }
                        else {
                            $null
                        }
                    }
                }
            }
            catch {
                $samples += [ordered]@{
                    edgeId = $null
                    relation = $relation
                    sourceId = $null
                    targetId = $null
                    confidence = $confidence
                    reasonCode = $null
                    artifact = $null
                    reviewStatus = "blocked"
                    error = $_.Exception.Message
                }
            }
        }
    }
    return @($samples)
}

function Invoke-FocusedQualityTests {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$ReportDirectory
    )

    if (-not $RunFocusedTests) {
        return [ordered]@{
            status = "Skipped"
            exitCode = $null
            trxPath = $null
        }
    }

    $project = Join-Path $RepositoryRoot `
        "apps\UnitTests\AgentService.UnitTests.csproj"
    $trxName = "graphrag-v4-quality-tests.trx"
    $isolatedOutput = Join-Path $ReportDirectory "test-bin"
    # agent-service 可能正在本機執行；測試輸出改放報告目錄，避免覆寫被鎖定的 bin。
    & dotnet test $project `
        --no-restore `
        --filter "FullyQualifiedName~GraphQualityAcceptanceTests" `
        "-p:BaseOutputPath=$isolatedOutput\" `
        --logger "trx;LogFileName=$trxName" `
        --results-directory $ReportDirectory
    $exitCode = $LASTEXITCODE
    return [ordered]@{
        status = if ($exitCode -eq 0) { "Passed" } else { "Failed" }
        exitCode = $exitCode
        trxPath = Join-Path $ReportDirectory $trxName
    }
}

$repositoryRoot = Get-RepositoryRoot
$fixturePath = Join-Path $repositoryRoot `
    "apps\UnitTests\Fixtures\graphrag-v4-search-and-qa-golden.json"
$callsPath = Join-Path $repositoryRoot `
    "apps\UnitTests\Fixtures\graphrag-v4-calls-golden-candidates.json"
$edgeReviewPath = Join-Path $repositoryRoot `
    "apps\UnitTests\Fixtures\graphrag-v4-edge-precision-reviews.json"
if (-not (Test-Path -LiteralPath $fixturePath)) {
    throw "找不到 S/Q fixture：$fixturePath"
}
if (-not (Test-Path -LiteralPath $callsPath)) {
    throw "找不到 CALLS fixture：$callsPath"
}
if (-not (Test-Path -LiteralPath $edgeReviewPath)) {
    throw "找不到 A15 review fixture：$edgeReviewPath"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputPath = Join-Path $repositoryRoot `
        "temp\reports\graphrag-v4-quality-$stamp.json"
}
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$reportDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null

$fixture = Get-Content -LiteralPath $fixturePath -Raw | ConvertFrom-Json
$callsFixture = Get-Content -LiteralPath $callsPath -Raw | ConvertFrom-Json
$edgeReviewFixture = Get-Content -LiteralPath $edgeReviewPath -Raw |
    ConvertFrom-Json
$searchResults = @(
    $fixture.searchSeeds | ForEach-Object {
        Invoke-SeedCase -FixtureItem $_ -Text $_.query -CaseType "SearchSeed"
    })
$questionResults = @()
foreach ($question in @($fixture.questions)) {
    $seed = Invoke-SeedCase `
        -FixtureItem $question `
        -Text $question.question `
        -CaseType "Question"
    $timing = Invoke-RetrievalTiming -Question $question.question
    $seed["retrievalTiming"] = $timing
    $seed["requiredSignals"] = @($question.requiredSignals)
    # Seed search 成功不代表完整 retrieval 成功。任何 traversal/hydration 例外都要
    # 讓本題正式標成 Failed，避免報告只看 seed 而隱藏真正的問答路徑故障。
    if ($timing.executionStatus -ne "Completed") {
        $seed["executionStatus"] = "Failed"
        $seed["reviewStatus"] = "Blocked"
        $seed["error"] = [string]$timing.error
    }
    $questionResults += $seed
}

$edgeSamples = Get-EdgeSamples -ReviewFixture $edgeReviewFixture
$callsRecall = Invoke-CallsGoldenRecall -CallsFixture $callsFixture
$benchmarkQuestions = @(
    @($fixture.searchSeeds | ForEach-Object { [string]$_.query }) +
    @($fixture.questions | ForEach-Object { [string]$_.question }) +
    @($fixture.performanceQuestions | ForEach-Object { [string]$_ })
)
$retrievalBenchmark = Invoke-RetrievalBenchmark -Questions $benchmarkQuestions
$focusedTests = Invoke-FocusedQualityTests `
    -RepositoryRoot $repositoryRoot `
    -ReportDirectory $reportDirectory
$blockedCases = @($searchResults + $questionResults |
    Where-Object { $_.executionStatus -ne "Completed" }).Count
$missingSeeds = @($searchResults + $questionResults |
    Where-Object { $_.missingSeed }).Count
$certainSamples = @($edgeSamples | Where-Object {
    $_.reviewStatus -ne "blocked" -and $_.confidence -eq "certain"
}).Count
$probableSamples = @($edgeSamples | Where-Object {
    $_.reviewStatus -ne "blocked" -and $_.confidence -eq "probable"
}).Count
$certainReviewed = @($edgeSamples | Where-Object {
    $_.reviewStatus -eq "reviewed" -and $_.confidence -eq "certain"
})
$probableReviewed = @($edgeSamples | Where-Object {
    $_.reviewStatus -eq "reviewed" -and $_.confidence -eq "probable"
})
$certainPrecision = if ($certainReviewed.Count -eq 0) {
    $null
}
else {
    @($certainReviewed | Where-Object { $_.manualCorrect }).Count /
        $certainReviewed.Count
}
$probablePrecision = if ($probableReviewed.Count -eq 0) {
    $null
}
else {
    @($probableReviewed | Where-Object { $_.manualCorrect }).Count /
        $probableReviewed.Count
}
$minimumReviewed = [int]$edgeReviewFixture.thresholds.minimumReviewedPerConfidence
$certainPassed = $certainReviewed.Count -ge $minimumReviewed -and
    $certainPrecision -ge [double]$edgeReviewFixture.thresholds.certainPrecision
$probablePassed = $probableReviewed.Count -ge $minimumReviewed -and
    $probablePrecision -ge [double]$edgeReviewFixture.thresholds.probablePrecision

$report = [ordered]@{
    schemaVersion = "4.0"
    reportType = "graphrag-v4-quality-acceptance"
    generatedAt = [DateTimeOffset]::Now.ToString("O")
    projectId = $ProjectId
    baseUrl = $BaseUrl
    policy = [ordered]@{
        sourceContentReturned = $false
        databaseMutationAllowed = $false
        pendingReviewCanPass = $false
    }
    summary = [ordered]@{
        executionStatus = if ($blockedCases -eq 0) { "Completed" } else { "Partial" }
        formalAcceptanceStatus = "NeedsReview"
        blockedCases = $blockedCases
        missingSeeds = $missingSeeds
        edgeSamples = @($edgeSamples |
            Where-Object { $_.reviewStatus -eq "pending" }).Count
        focusedTestStatus = $focusedTests.status
    }
    A15_edgePrecisionSample = [ordered]@{
        status = if ($certainPassed -and $probablePassed) {
            "Passed"
        }
        elseif ($certainSamples -ge 50 -and $probableSamples -ge 50) {
            "NeedsReview"
        }
        else {
            "InsufficientSample"
        }
        sampling = "certain/probable 依六種指定 relation 分層；各層按 edge ID 取前 N 筆"
        requiredPerConfidence = 50
        certainCount = $certainSamples
        probableCount = $probableSamples
        certainReviewed = $certainReviewed.Count
        probableReviewed = $probableReviewed.Count
        certainPrecision = $certainPrecision
        probablePrecision = $probablePrecision
        reviewFixturePath = $edgeReviewPath
        samples = $edgeSamples
    }
    A16_internalCallsGolden = [ordered]@{
        status = $callsRecall.status
        datasetId = [string]$callsFixture.datasetId
        graphVersion = $callsRecall.graphVersion
        golden = $callsRecall.golden
        found = $callsRecall.found
        recall = $callsRecall.recall
        threshold = $callsRecall.threshold
        missingPairIds = $callsRecall.missingPairIds
        error = $callsRecall.error
        fixturePath = $callsPath
    }
    A17_sqlModuleDependency = [ordered]@{
        status = $focusedTests.status
        evidence = "GraphQualityAcceptanceTests.SqlModuleDependencyGolden_應達到Fixture門檻"
        trxPath = $focusedTests.trxPath
    }
    searchSeeds = $searchResults
    questions = $questionResults
    C7_localRetrievalBenchmark = $retrievalBenchmark
    focusedTests = $focusedTests
}

$report | ConvertTo-Json -Depth 30 |
    Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Host "GraphRAG V4 品質報告已產生：$OutputPath"
Write-Host "正式驗收狀態：NeedsReview（S/Q 答案內容尚需人工核准）"

if ($focusedTests.status -eq "Failed" -or
    $blockedCases -gt 0 -or
    $retrievalBenchmark.status -ne "Passed") {
    exit 1
}
