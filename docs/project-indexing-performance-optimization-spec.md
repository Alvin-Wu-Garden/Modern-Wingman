# 專案知識圖譜索引效能優化 SPEC（V2）

> 狀態：V2 基線已實作並完成本文件第 11 節驗收；進階 module cache／Java 局部增量不在本版範圍
>
> 日期：2026-07-17
>
> 適用範圍：Modern Wingman 對單一 repository 的 C#、Java、SQL/ORM 與設定 artifact 進行全量／增量知識圖譜索引。
>
> 優先序：**圖譜正確性與可維護性 > 速度 > 索引範圍擴張**。

## 1. 背景、目標與非目標

目前 `ProjectIndexService` 每次索引都重新掃描、分析並以 `Neo4jCodeGraphStore.ReplaceProjectAsync` 寫入完整圖譜。這個策略安全，但對大型專案重複付出 Roslyn workspace、Java 解析、資料結構抽取與大量 Neo4j 寫入的成本；現行「增量」也因不能保證未修改 caller、dispatch 與資料關係仍正確，而安全地回退全量重建。

本 SPEC 將索引器改為一條可驗證的 V2 管線：以完整重建的 canonical graph 作為唯一正確性標準。只有「既有 C# 檔案、宣告面未改變、目前 snapshot 完整且可證明安全」的 body-only 變更可走 delta；新增／刪除／rename、宣告或 build context 變更、Java、SQL/ORM 及任何不確定情況一律誠實升級 full。此邊界刻意保守，避免為追求局部速度導入難維護的 module cache 或 caller database。

### 1.1 成功目標

首個基準為 nopCommerce，約 **54,031 nodes、123,456 directed edges**。

| 情境 | Fast Index 完成定義 | SLA |
|---|---|---:|
| Warm full index | 掃描、hash、所有必要解析、完整 graph snapshot 寫入、原子發布及 manifest 對齊完成；不含 LLM | p95 ≤ 60 秒 |
| Cold full index | 同上，另包含已 bundled Neo4j 的啟動；不含下載、安裝、restore 或解壓 runtime | p95 ≤ 90 秒 |
| No-op | 已證明所有 artifact、建置語意設定與 analyzer 版本相同；不解析、不寫 Neo4j | p95 ≤ 2 秒 |
| 1–20 檔變更 | 通過安全門檻的既有 C# body-only delta；其餘升級 full | eligible delta p95 ≤ 10 秒；升級後適用 full SLA |
| 21–200 檔變更 | 同上 | eligible delta p95 ≤ 30 秒；升級後適用 full SLA |

參考硬體最低基線為 Windows 11 x64、12 logical processors、32 GB RAM、NVMe SSD、Neo4j 與 Agent Service 同機。驗收以相同 fixture fingerprint、相同設定、1 次 warmup 後連續 10 次測試；報告必須記錄硬體、Neo4j 版本／設定、fixture fingerprint、node／edge 數、snapshot hash 與各階段耗時。第 11 節記錄本次實測環境，不能把不同硬體或污染環境的單次數據混入正式 p95。

### 1.2 非目標與禁止事項

- 不以抽樣、截斷、名稱猜測、跳過 semantic analysis、降低 `confidence`，或用 LLM 補寫程式碼／資料關係來換取 SLA。
- 不實作持久化 module fragment cache、per-symbol ref-count、caller closure database、局部 Louvain、動態 batch 自我學習或長期雙索引器；現有 full + 受限 body delta 已達 SLA，這些複雜度沒有驗收必要性。
- 不擅自下載／安裝 JDK、Maven、Gradle、NuGet 或其他 runtime。只使用 bundled 或使用者已設定且可用的能力。
- AI GraphRAG 摘要不得成為 code/data graph 可查詢的前置條件，也不得修改 canonical graph 的 code/data facts。

### 1.3 破壞式 V2 原則

Modern Wingman 尚未 release，故 V2 **不保留**舊有「每一 node／edge 都帶 current `manifestVersion`」的資料契約。所有內部 consumer 必須改讀 graph-level manifest／anchor；這是一次性的受控破壞式 migration，不維護 V1 graph 的長期相容層。

此破壞僅限索引資料模型與其內部讀取介面；既有產品功能（知識問答、Impact Analysis、Repo Map、GraphRAG、Data Intelligence、圖譜視覺化、read-only Cypher、AGENTS.md、watcher）必須以 golden consumer tests 證明結果未被改壞。

## 2. 既有能力與 V2 正確性邊界

### 2.1 目前實際可提取的圖譜事實

以下是 V2 必須完整保留的**既有實際輸出**，不是只存在 enum 但未穩定產生的種類。

| 範圍 | Nodes |
|---|---|
| 程式碼結構 | `File`、`Namespace`、`Type`、`Method`、`Property`（C#）、`Field` |
| C# build | `Solution`、`Project`、`Assembly`、`Package` |
| Java build | `Module`、`Dependency` |
| Framework／應用 | `Route`、`Endpoint`、`RequestContract`、`ResponseContract`、`ConfigurationKey`、`BackgroundJob`、`EventConsumer`、`Test` |
| Relational data | `DataStore`、`Schema`、`Table`、`Column`、`PrimaryKey`、`ForeignKey`、`Index`、`Constraint`、`View`、`Procedure`、`Query`、`Migration` |
| 領域詞彙 | `DomainTerm` 與別名 |

| 關係 | 方向與例子 |
|---|---|
| 結構／位置 | `Contains`、`DeclaredIn`；例如 `Namespace → Type`、`Type → Method`、`Method → File` |
| 程式語意 | `Calls`、`Inherits`、`Implements`、`Overrides`、`DispatchesTo`、`References` |
| Build | `ProjectReferences`、`DependsOnPackage` |
| API／應用 | `Handles`、`Consumes`、`Produces`、`BindsConfiguration`、`Tests`、`Covers` |
| Data | `MapsTo`、`SerializesTo`、`Reads`、`Writes`、`Migrates`、`ForeignKeyTo`、`Contains`、`References` |
| 領域詞彙 | `SupportedBy`、`Aliases` |

`Annotation`、`Collection`、`DataField`、`Publishes` 目前不是穩定產出，不可列入 V2 的「既有保證」。C# event 目前通常是 `Field`，不是獨立 node。C# Roslyn 產出的結構／binding 關係通常最精確；Java 是 dependency-free structural parser，僅在目標唯一可確認時建立 call；route、DI、ORM mapping、設定綁定等框架慣例關係可為 heuristic，必須保留其 confidence 與來源。

### 2.2 不變的正確性 contract

對同一 working tree、analyzer/extractor 版本、設定與 V2 schema：

1. 增量結果必須與 clean full rebuild 的 canonical graph 完全等價。
2. 比對範圍包括 node key/kind/核心屬性、edge source/kind/target/方向、source locations、`confidence`、extractor metadata、provenance、data schema、domain terms、社群成員與 consumer query 結果。
3. 任一不確定的增量計畫必須升級為 full；不得交付可能漏掉 `CALLS`、`DISPATCHES_TO`、`OVERRIDES`、`IMPLEMENTS`、`REFERENCES`、route、data read/write/mapping 的結果。
4. 取消、失敗或 Neo4j/SQLite 中斷時，上一個成功 snapshot 必須仍可查；不可暴露半張圖。

「精確度」指現行 analyzer 能證明的事實必須完整保留；不把目前 parser 的能力缺口假裝成負關係。需要的 `capabilityGaps` 與 diagnostics 必須可觀察。

## 3. V2 graph 與 provenance model

### 3.1 核心定義

- **Graph snapshot**：某次成功 Fast Index 產生的完整、可查詢圖譜。
- **Graph manifest**：snapshot 的不可變描述與唯一版本；這是唯一的 `manifestVersion` 所在位置。
- **Artifact**：被讀取並參與分析的檔案或建置輸入，具 path、kind、content hash 與所屬 module。
- **Delta fragment**：單次 eligible C# body-only 變更暫時產生的 nodes/edges/diagnostics；只用於與 active snapshot 合成，不持久化、不成為第二套圖譜。
- **Entity provenance**：node/edge 為何存在、由哪一 extractor 和哪些輸入 artifact 產生。其 `contentHash` 是 producer artifact 或 producer-set hash，絕不是全專案 snapshot hash。

### 3.2 Canonical JSON 結構

下列是 golden comparison、cache 與 Neo4j 映射的 canonical IR。JSON key 與 arrays 必須 stable sort；實際儲存可正規化為 SQLite／Neo4j，但語意不得改變。

```json
{
  "schemaVersion": "2.0",
  "projectId": "project_123",
  "manifestVersion": "20260717T100102Z-6ca8e7",
  "createdAt": "2026-07-17T10:01:02Z",
  "analysisProfile": {
    "indexerVersion": "2.0.0",
    "graphSchemaVersion": "2.0",
    "csharpAnalyzer": { "id": "roslyn", "version": "x.y" },
    "javaAnalyzer": { "id": "structural-java", "version": "x.y" },
    "dataExtractors": [{ "id": "sql-orm", "version": "x.y" }]
  },
  "snapshot": {
    "analysisSnapshotHash": "sha256:...",
    "headCommit": "optional-git-sha",
    "workingTreeFingerprint": "sha256:...",
    "mode": "incremental-module-recompose",
    "status": "ready",
    "nodeCount": 54031,
    "edgeCount": 123456
  },
  "artifacts": [{
    "id": "file:src/Orders/OrderService.cs",
    "path": "src/Orders/OrderService.cs",
    "kind": "csharp-source",
    "moduleId": "csproj:src/Orders/Orders.csproj",
    "contentHash": "sha256:...",
    "analysisScope": "module",
    "status": "analyzed"
  }],
  "nodes": [{
    "id": "type:MyApp.Orders.OrderService",
    "kind": "Type",
    "name": "OrderService",
    "signature": "MyApp.Orders.OrderService",
    "language": "CSharp",
    "technology": "ASP.NET Core",
    "locations": [{ "artifactId": "file:src/Orders/OrderService.cs", "startLine": 12, "endLine": 80, "role": "declaration" }],
    "provenance": {
      "sourceKind": "static-analysis",
      "confidence": "Exact",
      "extractor": { "id": "roslyn", "version": "x.y" },
      "artifactIds": ["file:src/Orders/OrderService.cs"],
      "contentHash": "sha256:producer-artifact-or-set",
      "reason": "declared type"
    }
  }],
  "edges": [{
    "id": "sha256:...",
    "sourceId": "method:MyApp.Orders.OrderService.Place",
    "kind": "Calls",
    "targetId": "method:MyApp.Payments.PaymentService.Charge",
    "directed": true,
    "provenance": {
      "sourceKind": "semantic-analysis",
      "confidence": "Exact",
      "extractor": { "id": "roslyn", "version": "x.y" },
      "artifactIds": ["file:src/Orders/OrderService.cs"],
      "contentHash": "sha256:producer-artifact-or-set",
      "reason": "invocation binding"
    }
  }],
  "diagnostics": [],
  "capabilityGaps": []
}
```

`analysisSnapshotHash` 是排序後 artifact manifest、analysis profile、module context fingerprint 與 canonical graph digest 的 hash；它描述整個 snapshot。node/edge identity 不包含 run time、manifest version 或 absolute local path。edge identity 為 `SHA-256(sourceId + NUL + kind + NUL + targetId + NUL + "forward")`；若未來有多條相同語意但 provenance 不同的 edge，必須先定義合併規則並 schema version bump，不能暗中覆蓋。

### 3.3 持久化規則

- Neo4j 以 `ProjectGraph { projectId, manifestVersion, status: Active|Staging, analysisSnapshotHash, ... }` 作為 graph-level anchor。nodes/edges 以 `graphId` 關聯到 anchor，不保存目前版本的重複欄位。
- SQLite 保存 immutable manifest 與 artifact hashes；`RequiresRetry` 明確區分暫時性 extractor error 與可重現 warning。SQLite current pointer 與 Neo4j active anchor 必須相同。run telemetry 與 enrichment polling 狀態目前為 process-local，不宣稱 durable job queue。
- query API 一律先解析 active graph anchor；read-only Cypher 只暴露 active snapshot 的 projection，不能讓使用者誤查 retired/staging graph。
- 發布必須先寫完並驗證 staging graph，單一 transaction 切換 active anchor，再更新 SQLite pointer；任一步失敗可由 reconciliation 回到已成功 anchor。

## 4. 目標索引管線

```text
Watcher / API / catch-up
  → 單次掃描與 hash（Artifact Manifest）
  → No-op 或 Index Plan
  → eligible C# body delta，或保守 full analyze
  → 正規化、驗證完整 canonical graph
  → Neo4j staging full/delta candidate → atomic anchor switch
  → SQLite manifest promote → Fast Index Ready
  → 背景 AI Enrichment（可失敗、可重試、不阻塞）
```

### 4.1 Fast Index 與 AI Enrichment 狀態

| 狀態機 | 狀態 | 行為 |
|---|---|---|
| Fast Index | `Queued → Scan → Plan → Analyze/Recompose → Stage → Publish → Ready` | `Ready` 後知識問答、Impact、Repo Map、Graph Query、AGENTS.md 立即使用該 snapshot。 |
| Fast Index | `Canceled`／`Failed` | 保持上一 successful graph；若無上一版本，回覆尚無可用索引。 |
| AI Enrichment | `NotRequested`／`Queued → Detecting → CacheCheck → Summarizing → Ready` | 不阻塞 Fast Index；只產出 community metadata/summary。 |
| AI Enrichment | `Degraded`／`Canceled` | Q&A 降級成 local/structural graph answer；顯示可重試原因，不把 LLM 失敗當索引失敗。 |

每個 run 必須有 `runId`、target `manifestVersion`、mode（`no-op`／`full`／`incremental-module-recompose`）、phase、開始／結束時間、error category、scope escalation reason、cache/transaction counters。既有簡易 progress API 可保留映射，新 API 必須回傳這些完整狀態。

### 4.2 Artifact manifest 與內容定址規則

manifest 對所有受支援 source、SQL/ORM 與 build/config artifact 保存 normalized relative path、kind、byte length、原始檔案 bytes 的 SHA-256 與 read diagnostic。`contentHash` 一律是**原始 bytes** 的 hash；不得對解碼後再編碼的文字計算，否則 BOM／非 UTF-8 檔會與掃描器產生不同 identity。文字解析另以 BOM-aware decoder 進行。

no-op key 是完整 artifact `(relativePath, byteLength, contentHash)` 集合，加上 `IndexerVersion` 與 `GraphSchemaVersion`。任何新增、刪除、rename、內容差異、analyzer/schema 版本差異、空 snapshot hash 或 `RequiresRetry=true` 都使 no-op 失效。舊 `Partial` manifest 缺少此欄位時反序列化為 `null`，必須保守 full；只有新 manifest 明確寫入 `false` 才可 no-op。mtime 只供診斷，不能證明內容相同；即使檔案同大小且還原 mtime，也必須以 hash 偵測。

本版不保存 module fragment cache，因此沒有 TTL、eviction 或 cache migration；Roslyn workspace reuse 只是 process-local bounded reuse，cache miss 最多造成 full 變慢，不得改變圖譜。

### 4.3 Incremental plan：受限 C# body delta

只有同時符合以下條件才可增量：

1. current manifest 為 `Fresh`、V2 schema/hash 與記憶體中 active snapshot 完全對齊，且 snapshot 無 diagnostics；
2. 變更數為 1–200，全部是已存在的 `.cs` 檔，沒有新增、刪除、rename、Java、SQL/ORM、build/config 或 glossary 變更；
3. Roslyn 重新分析後的 declaration surface（symbol key/kind/signature、inherit/implement/override/dispatch 等結構面）與舊 snapshot 相同；
4. fragment 的每條 endpoint 都能在 fragment 或 active snapshot 找到，canonicalization、provenance 與 dangling-edge validation 全部通過。

通過後，以 artifact ownership 移除 changed artifact 的舊輸出，保留未修改檔案指向 changed target 的 inbound edges，替換 changed source 的 outgoing edges，合成完整 V2 snapshot，再由 Neo4j 建立 candidate 並原子切換。任何一步不確定或 store 不支援 delta，立即記錄 `scopeEscalationReason` 並走同一條 V2 full publisher；不得發布半張圖。

### 4.4 C# 策略

1. 以 solution 為優先、project 為 fallback，建立 process-local、bounded persistent Roslyn workspace；key 納入 project root、source/build content fingerprint 與 analyzer profile。LRU eviction 只影響速度，不影響輸出。
2. 首次 full index 建立 workspace、solution 與 compilation；相同輸入的後續 full 可重用已載入的 workspace/compilation context，但仍完整產生並驗證 canonical graph，不觸發 restore 或外部安裝。
3. eligible body delta 只更新 changed documents 並重跑既有 Roslyn extraction；若 declaration surface 或 trust path 改變立即 full，不做跨程序 symbol cache 或 caller database。
4. workspace 載入失敗時使用現有 synthetic compilation fallback，保留 confidence/capability gaps；只要 fallback 可能改變跨 project binding，即全專案使用同一可信路徑重建，禁止混合高／低可信 fragments。
5. 首要效能工作是避免重複讀檔、重建 workspace 與重分析未變 project；不是建立難以驗證的 caller closure database。

### 4.5 Java 策略

1. 維持現有 dependency-free structural parser 作為唯一必需路徑：結構、唯一可確認的 call、Maven/Gradle、Spring/test/config artifact。它的保守行為與 confidence 是既有正確性的一部分。
2. Java 或 Maven/Gradle descriptor 的任何變更，本版一律 full。這是明確的 correctness fallback，不宣稱 Java module incremental 已完成。
3. 不在 P0–P2 導入另一條需外部 JDK/classpath 的「精準 Java」主路徑。若日後 bundled Java semantic adapter 已可離線提供，必須先以獨立 schema/analyzer version 與 full rebuild golden baseline 證明其輸出，再以 feature flag 引入；不得把它與 fast path 的結果任意混合。
4. Java 的優化是一個 full run 內一次建立 project-wide maps、bounded parsing、精確 endpoint validation 與 canonical erased parameter 比對；不是以不可靠的跨檔 call 猜測提高 coverage。

### 4.6 Data artifact 策略

`DataSchemaExtractor` 對 `.sql/.cs/.java/.xml` 的 SQL、DDL、ORM mapping、migration、query、schema/table/column/FK/view/procedure 和 contract serialization 以原始 bytes hash 建立 provenance。任何 data artifact 變更本版一律 full，不作 query／column／module cache。

DDL/migration 的 Exact 與 ORM／慣例 mapping 的 Heuristic/Resolved provenance 不得被合併或洗掉；同一 entity 的多個 extractor contribution 必須保留為 evidence。deterministic parser warning 使 manifest 為 `Partial` 但 `RequiresRetry=false`，允許相同輸入 no-op；I/O 或 adapter exception 為 retryable error，必須重新嘗試 full。confirmed glossary 改變同樣走 full。

## 5. Neo4j、發布與清理

V2 絕不直接修改 active graph。full run 寫入完整 staging snapshot；eligible body delta 以 active snapshot 加 canonical delta 產生新的 candidate graph。兩者都只在 candidate 完整驗證後切換 anchor，因此讀者只有單一、易推理的原子可見性模型：

1. 建立 `ProjectGraph` staging anchor 與 graph id。
2. 以 deterministic node batches，再以 edge kind + deterministic batches，`UNWIND` 寫入該 graph id；初始 batch size 固定 2,000，允許由設定調整至 1,000–5,000，但不可在本期自行 adaptive learning。
3. 寫後驗證 node/edge count、edge endpoint existence、唯一 key、provenance completeness、canonical digest 與 expected manifest 相符。
4. 單一 Neo4j transaction 將 staging anchor 設為 active，移除前 active 標記；隨後 SQLite transaction promote 相同 `manifestVersion`。reconcile 以 Neo4j active anchor 為依據冪等修復 SQLite pointer。
5. 取消／失敗只刪 staging；上一 active graph 完整保留。retired snapshots 至少保留上一成功版本，其他在 24 小時後分批清理，且不得清理 SQLite manifest 仍引用的 graph。

timeout/retry：Neo4j write 使用 bounded concurrency（同 project 同時一個 publisher）、可重試的 transient failure 採最多 3 次 exponential backoff；已取消或 validation failure 不重試。每一次 retry、batch、transaction 與 error category 都要 telemetry。

## 6. Community 與 AI Enrichment

Fast Index `Ready` 後才啟動 enrichment，並以 project-level gate 防止相同版本重複執行。狀態包含 target manifest version、phase、錯誤與可重試結果，API/UI 可輪詢 `Ready`／`Degraded`／`Superseded`／`Canceled`；LLM 回應不得阻塞或改寫 canonical code/data facts。

若未來實測證明 LLM 成本需要跨程序 cache，才可加入下列 summary fingerprint；本版不宣稱已有 durable job/cache：

```text
SHA-256(community membership fingerprint + algorithm/projection version + prompt version + provider/model identity)
```

membership fingerprint 相同即重用摘要；不同才重新摘要。社群偵測可先採 deterministic full detection，並用 batch neighborhood query 取代逐 member N×M query。LLM 預設 concurrency 2、timeout 60 秒、最多 3 次 retry；timeout/rate limit/malformed response 都只使 enrichment `Degraded`，不影響 Fast Index、Impact 或 local Q&A。

## 7. 可觀測性與 API/UI

每個 run 必須輸出下列 telemetry，並可依 project、mode、analyzer profile、cache hit/miss 篩選 p50/p95：

| 類別 | 最低欄位 |
|---|---|
| 時間 | scan/hash、plan、workspace load、C#/Java/data analyze、recompose/validate、stage、publish、SQLite promote、cleanup、enrichment 每段時間 |
| 範圍 | changed artifact 數、run mode、scope escalation reason、node/edge count、diagnostic/capability-gap count |
| Cache／DB | no-op hit、Roslyn reuse、batch／transaction／retry latency、staging/retired cleanup；未實作的 module cache 不得上報假指標 |
| AI | community detection time、LLM request/retry/failure count、provider/model identity、latency；不得記錄 prompt、source 或 secret |
| Correctness | golden/shadow diff counts（node/edge/community/query/impact）、manifest mismatch、cancel/crash/recovery result |

UI/API 必須清楚區分：`Fast Index Ready`、`AI Enrichment in progress/Degraded`、`Ready but previous snapshot is shown due to failed new run`。不得把 scope escalation、capability gap 或 LLM failure 隱藏成成功。

## 8. P0–P3 實作計畫

| Phase | 範圍 | 進入條件 | 退出條件 |
|---|---|---|---|
| P0：V2 contract 與基線（完成） | V2 canonical IR/manifest/anchor、golden comparer、consumer/Neo4j acceptance 與 benchmark harness。 | 固定 fixture 可重現。 | full graph hash/count 穩定；atomic publish/reconcile tests 通過。 |
| P1：全量 60 秒（完成） | exact artifact scan、固定批次 staging publisher、V2 anchor、Roslyn reuse、LLM 非阻塞化。 | P0 zero blocking diff。 | 第 11 節 warm p95 31.357 秒；bundled cold end-to-end 80.018 秒。 |
| P2：保守增量（完成） | no-op、既有 C# body-only delta、scope escalation、delta/full graph comparison。Java/data/structural changes 明確 full。 | P1 持續通過。 | no-op ≤2 秒；eligible incremental acceptance 與 clean full zero diff；不安全案例確實 full。 |
| P3：只在有證據時擴張（未啟動） | module cache、Java/data 局部增量、durable enrichment/cache 只有在真實瓶頸與新 golden gates 都成立時才提案。 | 現行 SLA 無法達成或有明確產品需求。 | 另立 SPEC；不得在本版留下半套雙軌。 |

P0 的意思是先固定「什麼算同一張正確的圖」、保存 golden graph，再優化速度；它不是先做一套複雜的增量系統。

## 9. 測試與驗收

### 9.1 Golden／shadow comparison

canonical JSON 使用 UTF-8、stable sort，排除 run timestamp。比較：

1. nodes：`id + kind + name + signature + language + technology + locations + provenance`；
2. edges：`sourceId + kind + targetId + directed + provenance`；
3. communities：algorithm/projection/seed、sorted member keys、membership fingerprint；
4. consumer queries：full-text、neighborhood、centrality、reverse call chain、visual graph、read-only Cypher normalized rows；
5. consumer outputs：Impact target/path/affected files/tests、Repo Map、GraphRAG fallback、Evidence Pack、AGENTS.md deterministic inputs。

同設定下 candidate 與 clean full rebuild 的任何 remove/change/add 都是 failure，除非已有核准的 schema migration fixture。shadow 僅存在測試／rollout，不能長期雙跑或暴露給使用者。

### 9.2 最低測試矩陣

| 類別 | 案例 |
|---|---|
| Manifest/cache | no-op、touch 無內容差異、rename/delete、非 Git、cache invalidation/eviction、module ownership 不明 → full |
| C# | multi-project/TFM/reference、partial/generic/overload/extension、interface/virtual/explicit implementation、DI、ASP.NET、config/job/consumer、xUnit/NUnit/MSTest；修改 callee/interface/base 後與 full graph 相同 |
| Java | Maven/Gradle multi-module、source/test sets、unique/non-unique call、inherit/override、Spring/test/config；module descriptor 改動與 fast parser gap 時 full 或正確 module expansion |
| Data | DDL/migration/view/procedure/FK/index、EF/JPA mapping、embedded SQL、table/column rename/delete、query read/write、contract serialization、glossary revision |
| Neo4j/SQLite | batch sizes 1k/2k/5k、staging validation、anchor atomicity、cancel、retry、process crash before/after publish、SQLite failure、Neo4j restart、retired cleanup |
| Consumer／AI | Q&A、Impact、Repo Map、visualization、read-only Cypher、AGENTS.md；enrichment pending/failure 正確降級 |
| 壓力 | nopCommerce 54k/123k、200-file burst、watcher coalesce/supersede、同 project concurrent request、memory/cache cold-warm |

### 9.3 最終 gate

- [x] V2 full rebuild 完整保留第 2.1 節既有 node／edge 語意、方向、provenance、confidence 與 extractor metadata。
- [x] nopCommerce 達成 full、cold 與 no-op SLA；未以抽樣或降低 analyzer 精度通過。
- [x] eligible C# body delta 與 clean full graph zero diff；其他變更明確升級 full，不宣稱 Java/data 局部增量。
- [x] `CALLS`／dispatch／data edge 的 fixture 不出現假陰性；Impact 使用反向 dependency closure 且拒絕混合 manifest 結果。
- [x] staging atomic publish、SQLite/Neo4j reconciliation、取消、失敗保留上一成功圖均有自動測試。
- [x] Fast Index Ready 不等待 LLM；AI failure 狀態可觀察且不破壞其他功能。
- [x] 只使用 bundled 或既有設定 runtime；不產生永久雙軌與死碼。

## 10. Flags、fallback、rollback 與未決決策

本版索引 flags 僅有 `EnableNoOpFastPath`、`EnableBodyOnlyIncremental` 與 kill switch `ForceFullIndex`。`ForceFullIndex` 仍走單一 V2 full publisher，而非保留 legacy writer。未實作的 module cache／shadow writer 不建立假 flag。

任何 golden diff、manifest mismatch、publisher validation failure 或無法安全定位模組時，停用增量並執行／要求 full；不要放寬 comparator。rollback 是切回上一 active V2 manifest，或以 `ForceFullIndex` 重新建立下一 snapshot。完成 P3 後移除 legacy V1 graph reader/writer 與 shadow-only code。

以下決策已採用保守預設；未來若要改變必須另立 schema/benchmark 變更：

1. **Node key 對 rename 的規則**：預設 path 參與 file node key，rename 視為刪除加新增；symbol key 是否跨檔保留須以現有 key 實測後固定。
2. **Neo4j graphId 表示法與現有 read-only Cypher projection**：預設用 graph-level anchor + query filter，不保留 entity-level manifest version。
3. **Community deterministic algorithm/seed**：預設固定版本與 seed；若 GDS 不可用，namespace fallback 必須成為明確 algorithm id，而非靜默替換。
4. **Full snapshot 儲存成本上限**：預設保留 active + previous success，retired TTL 24 小時；以 nopCommerce 實測磁碟後才調整，不能先導入 in-place delta 複雜度。

## 11. 2026-07-17 驗收紀錄

### 11.1 nopCommerce golden graph 與效能

- fixture：nopCommerce checkout，3,810 個受支援 artifacts。
- fixture fingerprint：`5037b8626d0486bb0a489a5094910eba67f3c65510aa8daf3737ad61f4a3decd`。
- golden graph：54,031 nodes、123,456 directed edges。
- `analysisSnapshotHash`：`6f5d8a8368d6649aaa08d8f8eb88fa4074c7e9529cd078ead1c640e3d7505511`。
- 環境：Windows x64、12 logical processors、約 34 GB GC available memory、NTFS fixed drive、.NET 10.0.8、Neo4j Community 5.26.0，同機隔離實例。
- 正式報告：`temp/nopcommerce-index-formal-v2.json`，1 warmup + 10 measured，`passed=true`。
- formal warmup（Neo4j 已啟動、cold Roslyn）：60,674 ms；不以此冒充 bundled cold start。
- 乾淨 bundled cold：全新 data directory 的 Neo4j console 啟動至 Bolt listen 7,658 ms；cold full 56,728 ms；包含啟動、test host/schema、cold full、no-op 與清理的 end-to-end wall time 80,018 ms（≤90,000 ms）。報告為 `temp/nopcommerce-index-cold-v2.json`，`passed=true`。
- warm full：min 23,231 ms、p50 27,444 ms、p95/max 31,357 ms（≤60,000 ms）。
- warm stage p95：scan 636 ms、C# analyze 4,552 ms、data analyze 959 ms、canonicalize 5,491 ms、Neo4j store 21,176 ms。
- 11 次完整分析的 node count、edge count 與 snapshot hash 全部相同；沒有抽樣、截斷或停用 semantic analysis。
- 相同輸入 no-op 實測 1,703 ms（≤2,000 ms），維持原 graph version/count/hash；nopCommerce 的 deterministic SQL warnings 保持 `Partial`，但 `RequiresRetry=false`，不會無限重建。

### 11.2 Correctness 與 consumer gates

- `.NET` 非大型 benchmark：連接真實隔離 Neo4j 的完整回歸為 273 passed、0 failed、0 skipped；其後新增的 manifest tri-state round-trip 與 Impact mixed-version retry 兩項 targeted tests 亦通過。最終無外部服務重跑為 272 passed、3 個 real-Neo4j tests 預期 skipped、0 failed；三項已在前述 real run 通過。
- desktop：`pnpm --filter @modern-wingman/desktop typecheck` 通過。
- raw-byte/BOM content hash、同大小且還原 mtime、Java dangling endpoint／override signature、data multi-provenance、read-only Cypher project isolation、watcher rename/catch-up、失敗保留上一版本及 manifest reconciliation 均有自動回歸測試。
- Impact Analysis 的 search、reverse closure 與 neighborhood 必須同一 manifest；若發布競態造成混版，bounded retry 一次，仍不一致就明確失敗，不回傳錯接結果。

### 11.3 明確未包含

本版沒有 Java/data module incremental、持久化 module fragment cache、跨程序 Roslyn cache、durable AI summary cache 或 adaptive batch learning。這些不是漏做後假稱完成，而是依「正確性與可維護性優先」明確排除；相關變更會安全升級 full，且本機 warm full 仍在 60 秒 SLA 內。若未來要加入，必須另立 SPEC、schema/analyzer version、golden/shadow gates 與獨立 benchmark，不得直接擴張現有 fast path。
