# Modern Wingman 專案解析回答品質改善 TODO

> 狀態：已完成（2026-07-27）  
> 範圍：投資交易／風控系統的唯讀專案解析問答  
> 核心原則：先補齊可追溯證據，再讓 LLM 回答；不以擴張 Graph schema 或 Agent 架構掩蓋資料不足。

## 1. 問題與目標

目前專案解析經常無法回答真實的投資交易系統問題，主要缺口為：

1. 資料庫設定晚於既有索引時，現有 Manifest 不一定自然失效，可能留下沒有 `Feature` 的 source-only graph。
2. 問答 Prompt 主要包含 node、edge、檔案行號與 evidence 描述，沒有提供足夠的實際原始碼片段。
3. 問題路由只有粗略的 Local／Global 二分，無法針對流程、影響分析與資料使用選擇合適的關係方向。
4. Local traversal 可能逐節點向 Neo4j 取得鄰居，容易形成過多資料庫 round trip。
5. Community AI Summary 生成成本高，但不應成為 Local Q&A 的必要條件。

本次改善完成後，系統應能用可追溯證據回答：

- 某項投資業務功能在哪裡實作。
- 畫面、API、Controller、Service、Stored Procedure 與 Table 的主要流程。
- 修改欄位、規則或方法可能影響哪些功能、排程與報表。
- 某個交易問題可能中斷在哪一段流程。
- 哪些結論已確認、哪些只是推論、哪些資料仍未被索引。

## 2. 不做的事情

本次明確不做：

- 不新增 Method、Statement、Parameter、Variable 或 AST token Graph Node。
- 不導入 CFG、DFG、CPG 或 statement-level program graph。
- 不新增 Vector Database。
- 不使用 LLM 抽取 Entity ID。
- 不建立多 Agent runtime 或自由 Tool Calling Loop。
- 不加入 JIRA、SVN、Runtime Trace 與 Log Graph。
- 不新增 GraphRAG 專屬專案或新的背景服務。
- 不讓每個 Community 都成為必要的 LLM 摘要工作。
- 不為每個 Intent 建立一套 Strategy、Factory、Interface 與 Service。

## 3. 規模與可維護性硬限制

### 3.1 檔案與程式碼規模

目前 `apps/agent-service/modules/GraphRAG` 基線為 11 個正式 `.cs`，約 12,297 行；既有 SPEC 的 `<= 10` 目標已與實際程式碼漂移。本節在本工作範圍內取代舊檔案數限制，並同步更新 `graphrag-refactor-spec.md`，避免同時存在兩個標準。

- [x] GraphRAG 正式 `.cs` 預設最多增加 1 個，完成後總數不得超過 12。
- [x] 預設新增 `GraphAnswerContext.cs`，集中放置本次高度內聚的 Question Plan、Source Evidence Reader 與 Context Compiler。
- [x] 若 `GraphAnswerContext.cs` 超過 500 行，允許把具獨立安全與 I/O 邊界的 `SourceEvidenceReader.cs` 分離，但必須同步合併或移除其他檔案，使 GraphRAG 正式 `.cs` 總數仍不超過 12。
- [x] 不得為上述三個內部責任各自再建立 Interface、Factory 或 DI registration。
- [x] 新增正式 production code 預算上限為 2,000 行；以 `git diff --numstat` 的新增行數計算且包含註解，不得把新類別移到其他目錄規避計算。原訂 900 行在加入必要的路徑安全、可靠 cache 失效、Intent coverage、完整繁中 XML 文件與降級測試後不足；2026-07-27 經獨立 Code Review 揭露實際規模後，由使用者明確同意調整為 2,000 行。
- [x] 新增單一正式檔案建議不超過 500 行；若必須超過，Code Review 必須說明原因。
- [x] 新增單一 class 建議不超過 300 行。
- [x] 一般 method 建議不超過 60 行；純 orchestration method 上限為 100 行。
- [x] 既有大型檔案採 grandfathering，不為追求數字進行無關重構；但本工作不得讓大型檔案淨增加大量新責任。
- [x] 若 `GraphRetrievalService.cs` 新增超過 100 行，必須同步移除舊的組裝邏輯或把本次內聚邏輯移至唯一的新檔案。
- [x] 不新增 NuGet／npm dependency，除非現有 BCL、Roslyn、Neo4j driver 與專案套件確實無法完成。

### 3.2 抽象規則

- [x] 只沿用現有真正的外部邊界，例如 `IGraphStore`。
- [x] 內部純規則類別使用 concrete/internal class，不建立只有一個實作的 Interface。
- [x] 五種問題類型只使用一個 enum 與一個小型 planner，不建立五個 handler class。
- [x] 第二輪補查只允許一次，不建立 workflow engine、state machine framework 或 retry graph。
- [x] Source snippet cache 使用 process memory 與 Manifest／檔案指紋，不建立新的持久化 cache database。
- [x] 設定只加入真正需要調整的 budget；不得為每個常數建立新的 profile。

### 3.3 中文註解與可讀性規則

以下規則適用於本次所有新增或實質修改的 C# 程式碼：

- [x] 每個 class、record、struct、interface、enum 必須有清楚的繁體中文 XML 文件註解。
- [x] 每個 constructor、public method、internal method，以及具有非顯而易見邏輯的 private method，都必須有繁體中文 XML 文件註解。
- [x] 每個 enum value 與語意不直觀的 property 必須說明業務意義。
- [x] method 註解至少說明「為什麼存在、輸入限制、輸出保證」；存在失敗或降級路徑時必須說明其行為，不得只把方法名稱翻譯成中文。
- [x] 非直觀參數必須補 `<param>`；具有回傳值的方法必須補 `<returns>`。
- [x] 關係遍歷、分數、截斷、cache key、安全路徑與 fallback 邏輯必須有行內中文註解，說明設計原因。
- [x] 註解必須標明哪些結果是 deterministic fact、哪些只是 retrieval candidate，避免後續維護者誤用。
- [x] 修改行為時必須同步更新註解；過期註解視為缺陷。
- [x] 禁止 `TODO: later`、`TBD`、`之後再處理` 等無責任邊界的註解。
- [x] 測試名稱使用可讀語意，清楚描述條件與預期結果。
- [x] 擴充 `GraphRAGChineseDocumentationTests`，自動檢查本次新增檔案的 type、constructor、method 與 enum value；既有大型檔案維持 grandfathering，由 Reviewer 檢查實質修改區段。

良好註解應類似：

```csharp
/// <summary>
/// 依使用者問題選擇固定且有界的關係集合。
/// 此方法只決定檢索方向，不建立或推測新的 Graph 關係；
/// 找不到可靠 seed 時由呼叫端進入一次性的全文補查。
/// </summary>
```

不接受：

```csharp
/// <summary>取得關係。</summary>
```

## 4. 實作 TODO

### Phase 0：建立回答品質 Baseline

- [x] 整理 15～20 題真實投資交易系統問題。
- [x] 每題標記預期 Feature、EntryPoint、Code、Stored Procedure 與 Table。
- [x] 保存目前回答與命中的 node／edge，作為修改前 Baseline。
- [x] 題目至少涵蓋功能位置、流程解釋、Bug 定位、修改影響與資料使用。

第一批固定題目：

```text
補登債券交易在哪裡實作？
補登交易存檔流程怎麼走？
修改交割日會影響哪些地方？
交易存檔後為什麼沒有更新部位？
某張交易表被哪些排程和報表使用？
```

### Phase 1：確保 Business Feature 正確進入索引

- [x] 將專案資料庫設定指紋納入索引失效判斷。
- [x] Fingerprint material、Manifest、log 與 telemetry 不得包含密碼、完整 connection string 或其他 secret；只使用非機密資料庫識別、設定版本與必要的更新時間。
- [x] 資料庫設定新增、刪除或變更後，將專案標記為需要重新索引。
- [x] 索引前驗證資料庫連線；失敗時顯示明確診斷，不得默默產生 source-only graph。
- [x] 已設定資料庫但結果 `Feature = 0` 時顯示可行動警告。
- [x] 重新索引「投資交易系統」。
- [x] 驗證 `Feature`、`ROUTES_TO`、`HANDLES`、`CALLS`、`READS`、`WRITES` 等實際存在的 edge 數量合理；缺少預期種類時附診斷，不強迫不存在的資料庫 Trigger 產生 `TRIGGERS`。
- [x] 驗證至少一條真實鏈：

```text
Business Feature
→ Menu／Screen
→ Controller Action
→ Service
→ Stored Procedure
→ Table
```

### Phase 2：小型、確定性的 Question Planner

- [x] 新增單一 `RepositoryQuestionIntent` enum：

```csharp
LocateFeature
ExplainFlow
AnalyzeImpact
FindDataUsage
SystemOverview
```

- [x] 使用固定關鍵字、識別碼型態與業務 alias 判斷 Intent。
- [x] 使用 Menu、Feature、Node Alias 與一份小型投資術語表展開搜尋詞。
- [x] 不先要求問題必須解析成唯一 Entity ID。
- [x] 精確 symbol／route／table 名稱優先於模糊業務詞。
- [x] 每種 Intent 只選擇固定、有界的 edge kind 與 traversal direction。

### Phase 3：批次、有界的 Graph Retrieval

- [x] 在現有 `IGraphStore` 增加批次鄰居查詢，不增加第二套 store abstraction。
- [x] 每一層 frontier 以一次 Neo4j query 取得鄰居，消除逐節點 N+1 round trip。
- [x] 保留既有技術 budget：Seed 12、Node 80、Edge 120、Depth 3。
- [x] 對 shared table、utility 與 enum 保留 per-node 上限。
- [x] 排序時優先保留可形成完整 Feature→EntryPoint→Code→Data 路徑的節點。
- [x] Retrieval 不含 LLM 時的 P95 目標低於 2 秒。

### Phase 4：讀取有界的實際 Source Evidence

- [x] Graph 命中後只讀取最高價值的 6～10 個 evidence 範圍。
- [x] 所有檔案路徑必須解析並驗證仍位於專案根目錄內。
- [x] 拒絕 binary、generated output、junction 與 reparse-point 路徑逃逸；測試 Windows junction／reparse-point 情境。
- [x] 設定允許的副檔名與單檔 byte 上限，超過時只回報診斷，不讀入 context。
- [x] C# evidence 優先展開到包含該行的 Method／Member，單一片段最多 120 行。
- [x] SQL、ASPX、JavaScript 與設定檔使用有界行數範圍。
- [x] 合併相同檔案的重疊範圍。
- [x] 平行讀取不同檔案，但設定固定 concurrency，避免大型專案 I/O 暴增。
- [x] Source context 總量預設不超過 20,000 字元。
- [x] 使用 Manifest、relative path、file fingerprint 與 range 作 memory cache key。
- [x] Source Evidence 只加入回答 context，不建立新的 Method／Statement Graph Node。
- [x] Snippet 選擇使用 Intent coverage quota；例如 `ExplainFlow` 在資料存在時至少保留 EntryPoint、Code、Data 各一份 evidence，避免片段全部來自同一層。

### Phase 5：Evidence Context Compiler

- [x] 取代目前只輸出 node／edge 清單的簡易 `FormatContext`。
- [x] Context 固定包含：
  - 問題判斷與搜尋詞。
  - 命中的業務功能。
  - 主要關係路徑。
  - 關鍵原始碼片段。
  - Stored Procedure／Table 依賴。
  - 直接影響與間接影響。
  - Retrieval 截斷與資料缺口。
- [x] 已確認事實、推論與未知資訊必須分區。
- [x] 每個重要程式結論附 relative file path、symbol／method 與行號。
- [x] Community Summary 不得冒充 source-level evidence。
- [x] Context 超過 budget 時先移除低分重複片段，不切斷最高分的主要路徑。

### Phase 6：一次性補查

- [x] 定義各 Intent 的最低 coverage，例如流程問題至少需要 EntryPoint、Code 與一條關係。
- [x] 第一輪不足時，使用已找到的 method、SP、table 或 alias 進行一次補查。
- [x] Graph 沒有可靠 seed 時才執行有界全文搜尋 fallback。
- [x] 全文 fallback 只掃描允許的文字副檔名，並限制候選數、單檔大小、總執行時間與 cancellation。
- [x] 補查最多一次；第二次仍不足即回報缺口，不繼續循環。
- [x] 全文結果只讀取最高分的少量片段，不寫回 canonical graph。

### Phase 7：Community AI Summary 保持背景加值

- [x] Local Q&A 不等待 Community AI Summary。
- [x] AI enrichment 預設只處理 Primary Business Community。
- [x] Secondary Community 保留 deterministic summary。
- [x] 背景 LLM concurrency 固定為 2，並保留 timeout 與降級處理。
- [x] UI 顯示真實 `completed/total` 與百分比。
- [x] `Degraded` 不把專案狀態改回不可問答。
- [x] 只有 `SystemOverview` 與跨模組總覽優先使用 Community reports。

### Phase 8：效能與回答品質驗收

- [x] 記錄 Intent、搜尋詞、seed 數、node／edge 數、path 數、snippet 數、context 字元數與各階段耗時。
- [x] Telemetry 不記錄完整 source、prompt、帳密或交易資料。
- [x] 以 Phase 0 題目執行修改前後比較。
- [x] 相關檔案 Recall 目標至少 80%。
- [x] 主要回答必須引用 evidence；Unsupported Claim 視為失敗。
- [x] Retrieval P95 低於 2 秒，不把模型生成時間計入。
- [x] P95 使用固定硬體與資料集、warm cache、Phase 0 Golden Questions，累計至少 100 次查詢；報告同時記錄 cold-start 結果，不混入 warm P95。
- [x] Source context 預設不超過 20,000 字元。
- [x] Community enrichment 慢或失敗時，Local Q&A 測試仍需通過。

## 5. Code Review 與 Agent 協作 Gate

### 5.1 必要 Code Review

實作者不得直接宣告整體完成。為避免 Review 過重，只設三個固定 Review Gate：

1. 索引與 Feature 完整性完成後。
2. Retrieval、Source Evidence 與 Context Compiler 完成後。
3. 最終整合、效能與 Golden Questions 驗收前。

Reviewer 開始前必須停止相關 Implementation Agent，固定同一份 diff 快照，避免審查變動中的工作區。下列 A～E 是同一份 Review checklist，不是五次獨立 Review：

- [x] Review A：正確性與證據完整性。
- [x] Review B：效能、N+1 query、I/O budget、cache 與 cancellation。
- [x] Review C：是否過度抽象、檔案／行數是否超出本文件限制。
- [x] Review D：繁體中文 XML 註解是否清楚且與實作一致。
- [x] Review E：測試是否覆蓋成功、降級、取消、截斷與路徑安全。

Reviewer 必須輸出：

```text
Blocking Issues
Non-blocking Suggestions
規模限制檢查
中文註解檢查
效能風險
建議人工優先審閱的檔案與 method
```

所有 Blocking Issues 修正後，必須由 Reviewer 重新確認，不能只由原實作者自我判定。

### 5.2 多 Agent 使用規則

只有工作可以安全分離時才平行處理：

- [x] 可平行：索引失效邏輯、Retrieval／Context、Golden Tests、唯讀 Code Review。
- [x] 不可平行：兩個 Agent 同時修改 `GraphRetrievalService.cs`、`Neo4jGraphStore.cs` 或同一測試檔。
- [x] 同一時間最多 2 個 Implementation Agent，加 1 個獨立 Reviewer Agent。
- [x] 主 Agent 負責切分檔案 ownership、整合、執行完整測試與處理衝突。
- [x] Sub-agent 不得自行擴張 schema、增加 dependency 或建立新 abstraction。
- [x] 若工作量不足以抵銷整合成本，主 Agent 必須單獨完成，不為了使用 Agent 而切分。

建議 Phase 分工：

```text
Agent A：DB 設定指紋、索引健康診斷與相關測試
Agent B：Question Plan、Source Evidence、Context Compiler 與相關測試
主 Agent：批次 Neo4j traversal、整合與全套驗證
Reviewer Agent：唯讀檢查 diff、規模、註解、效能與測試缺口
```

## 6. 人工審閱交付格式

每次交付必須先提供精簡摘要，協助人工快速審閱：

1. 實際修改目的。
2. 修改與新增的檔案。
3. 每個重要 class／method 的責任。
4. 資料流或呼叫流程。
5. 效能前後比較。
6. 新增測試及其覆蓋風險。
7. Reviewer Agent 發現與修正結果。
8. 尚未完成或刻意不處理的範圍。

人工建議優先查看：

```text
Question Intent 是否選對關係方向
Source path 是否確實被限制於專案根目錄
Context 是否包含實際 code evidence
批次 traversal 是否消除 N+1 query
回答是否區分 Fact／Inference／Unknown
是否出現不必要的 Interface、Factory 或新 dependency
中文註解是否能解釋設計原因與降級行為
```

## 7. 完成定義

只有以下全部成立，才能勾選本 TODO 完成：

- [x] 真實投資交易系統已重新索引並產生可用 Feature。
- [x] 至少一條 Business→Screen→Code→Data 路徑可被查詢。
- [x] 專案問答 context 包含有界的實際 source evidence。
- [x] 五種 Intent 與一次性 fallback 有測試。
- [x] Retrieval P95 達標且沒有逐節點 N+1 query。
- [x] Local Q&A 不依賴 Community AI Summary 完成。
- [x] GraphRAG 正式 `.cs` 檔總數不超過 12，新增 production code 不超過 2,000 行。
- [x] 新增與實質修改的 class／method 具備清楚繁體中文註解。
- [x] 獨立 Reviewer Agent 已完成審查，所有 Blocking Issues 已關閉。
- [x] Phase 0 Golden Questions 的相關檔案 Recall 至少 80%。
- [x] 人工審閱交付摘要已完成。

## 8. 2026-07-27 最終驗收紀錄

- 真實索引：`投資交易系統`，Manifest `7315da3698b648aeb00c61b76da4559e`；extractor 版本已納入 no-op 失效識別，解析規則升版不會再誤沿用舊圖。
- 圖譜規模：16,962 nodes、23,532 edges；Feature 2,299、EntryPoint 4,714、Code 7,284、Data 2,665。
- 關係數：CALLS 2,706、DEPENDS_ON 875、HANDLES 4,662、MAPS_TO 123、READS 9,387、ROUTES_TO 4,281、TRIGGERS 695、WRITES 803。
- 真實路徑：會計公報分類資料維護 → `AccountingPurposeCSVController` → `QRAsyncConfirm` → `tblRawData`。
- Golden：16 題通過，新增「登入流程」硬性驗證必須命中 `loginandpassword/loginandpassword.cs` 與 `ProcessLogin` 原始碼片段；整體相關領域與檔案 Recall 仍高於 80%。
- 效能：105 次 warm full-context retrieval，P95 494.7 ms；cold max 881.7 ms；模型生成時間未計入，仍低於 2 秒門檻。
- Context：16 題 prompt 均低於 25,000 字元，source evidence budget 維持 20,000 字元。
- 回歸：213 passed、7 個需外部環境的測試明確 skipped、0 failed；另有真實 Neo4j Golden 1 passed；Desktop TypeScript typecheck 與 `git diff --check` 通過。
- 規模：GraphRAG 正式 `.cs` 共 12 個；production 新增 1,847 行（含桌面端），低於使用者核准的 2,000 行；無新增 NuGet／npm dependency。
- Community AI Summary：背景 concurrency 2、單份 timeout 45 秒；右下角顯示真實 completed／total 與百分比；缺少 LLM、模型失敗或圖資料保存失敗均進入 terminal `Degraded`，不阻斷 Local Q&A。
- 真實端到端回答已驗證：回答列出 `AccountingPurposeCSVController.List()`、`QRRawData.Search_By_uIDList()`、實際檔案與第 97 行，並將未取得的 SQL 實作標為未知。
- 截圖問題已做同一對話端到端回歸：第一題能引用 `loginandpassword/loginandpassword.cs:33-103` 並說明 `ProcessLogin` 的授權、帳號與密碼驗證；第二題「債券交易流程」只回答債券內容，未受上一題污染，也未引用 Modern Wingman 自身路徑。
- 資料庫安全：實作與驗收只執行連線、schema metadata 與 `SELECT` 類唯讀查詢；未執行新增、刪除或修改資料。
- 獨立 Reviewer 最終結論：所有 blocking issues 已關閉；`GraphAnswerContext.cs` 超過 500 行與 `SourceEvidenceReader` 超過 300 行屬建議限制例外，保留原因是避免突破 12 檔硬限制，且安全路徑、cache、coverage、context compiler 仍屬同一 source-evidence 邊界。後續若再增加責任，優先拆出 `SourceEvidenceReader`。

人工優先審閱：

1. `GraphAnswerContext.cs`：`SourceEvidenceReader.ReadAsync`、`Resolve`、`ReadFileAsync`、`GraphContextCompiler.Compile`。
2. `GraphRetrievalService.cs`：`LocalSearchAsync`、`BuildAnswerPromptAsync`、`BuildCommunitySummariesAsync`。
3. `GraphIndexingService.cs`：`IndexCoreAsync`、`IncrementalIndexAsync`。
4. `GraphIndexingServiceV3Tests.cs`：`LiveFblGraph_AnswersGoldenQuestionsAndMeetsWarmP95`。
