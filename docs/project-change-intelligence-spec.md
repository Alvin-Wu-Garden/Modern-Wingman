# Project Change Intelligence SPEC

> 狀態：Draft — 已確認方向，待依 Phase 實作  
> 日期：2026-07-15  
> 目標：將既有的「專案解析／Code Graph」升級為供 IT 使用的變更決策能力；當使用者以自然語言描述 Bug 或需求時，系統能以可追溯的專案證據回答：應改哪裡、可以增加在哪裡、可能壞什麼、影響範圍、如何驗證，以及在資訊不足時該追問什麼。

---

## 1. 產品定位

本功能不是開放域聊天，也不是只回答「這個函式做什麼」。其輸入雖然是自然語言，但問題域被限制為**目前單一 workspace 的 Bug、需求、修改、風險與驗證**。

核心閉環：

```text
Bug / 需求描述
  → Change Brief（結構化變更假設）
  → Clarification Planner（不足時逼問 IT）
  → Evidence Planner（Code / Route / Test / Config / Data）
  → 可驗證的 Evidence Pack
  → 變更落點、影響範圍、風險、測試與驗收計畫
```

### 1.1 必須回答的問題

- Bug 最可能在哪一層、哪個模組、哪條執行路徑？
- 新需求應放在哪裡；有沒有較安全的替代落點？
- 修改指定檔案、Symbol、API、設定或 Git diff，直接與間接會影響什麼？
- 哪些 API contract、背景工作、測試、設定、資料結構及整合需要同步調整？
- 哪些結論是程式證據，哪些只是架構推論，哪些目前無法確認？
- 應新增／執行哪些自動化與手動驗收？

### 1.2 範圍與非目標

- 第一版範圍固定為一個 workspace；不做跨 repo 依賴分析。
- 預設只讀取已選擇 workspace 內的檔案與 Git metadata；P3 在分析確有必要、且使用者已配置對應 Database Runtime Plugin 時，可透過 MAF tool 取得**最小必要的唯讀資料庫證據**。
- 不另引入第三套持久化資料庫：圖譜關係保留 Neo4j，既有 App metadata 仍使用 SQLite。
- 不以 LLM 推論覆蓋靜態分析、Schema 或原始碼已證實的事實。
- 不保證每一種語言、框架、ORM 都能精確解析；不支援時必須明示能力缺口與可信度。
- 不自動修改使用者 repository，也不自動產生或覆寫專案文件。

---

## 2. 共通資料與可信度模型

### 2.1 Canonical Graph Model

既有 `CodeNode`／`CodeEdge` 模型擴展為可演進的統一模型。所有節點與邊至少需保留：

- `projectId`、穩定 ID、kind、名稱、原始碼位置、language／technology。
- `sourceKind`：compiler、AST、framework adapter、migration、SQL、heuristic、LLM proposal、IT confirmed。
- `confidence`：`Exact`、`Resolved`、`Heuristic`、`Inferred`、`Confirmed`。
- `extractorId`、`extractorVersion`、`indexedAt`、檔案 content hash。
- 對 heuristic／inference 邊記錄 `reason` 與候選依據。

所有回答必須將證據分成：

| 標記 | 定義 |
|---|---|
| 已證實 | 由 compiler、AST、DDL、migration、明確設定或 IT 確認得到 |
| 高可信解析 | 已解析的跨檔案／跨模組關係，但可能受 build classpath 影響 |
| 推論 | 命名、框架慣例或 LLM 提案；不可當作事實敘述 |
| 未知 | 現有索引、原始碼或設定無法支持結論 |

### 2.2 Index Manifest 與新鮮度

每個 workspace 必須有可查詢的 Index Manifest：

- repository root、當前 HEAD commit、working-tree fingerprint、未追蹤檔案摘要。
- source file hash、索引開始／完成時間、indexer version、成功／失敗／略過檔案。
- `Fresh`、`PendingChanges`、`Indexing`、`Partial`、`Failed`、`Stale` 狀態。
- 對話、Impact Report 與 UI 顯示使用的 manifest version。

當證據涉及 pending 或 stale 檔案時，回答必須顯示警告；Agent 必須直接讀取該檔案或要求重新索引，不能將舊圖譜當成最新事實。

### 2.3 Evidence Pack

任何變更分析都輸出受 token 預算約束的結構化 Evidence Pack，而非整個圖譜：

- 解析後的 Change Brief 與已知／未知條件。
- 目標 Symbol、檔案、Route、設定鍵或資料項目。
- 已排序的證據片段（檔案、行號、signature、原始碼節錄）。
- 上游／下游路徑、相依模組、相關測試與 Git working-tree 狀態。
- 每項關係的可信度與來源。
- 被截斷的範圍與未能解析的技術。

---

## 3. Phase P0 — 圖譜正確性與新鮮度基座

### 3.1 目標

在擴大圖譜範圍前，先保證既有呼叫關係不因增量索引而消失，並讓使用者與 Agent 一眼判斷索引是否可信。

### 3.2 必做項目

1. **修正增量索引一致性**
   - 變更檔案刪除節點時，不得遺失未修改 caller／reference 指向變更目標的邊。
   - 依變更 Symbol 建立受影響檔案 closure，重新解析變更檔案與必要的 upstream／downstream 檔案。
   - 無法安全建立 closure 時，回退到原子性的 full re-index；不可回傳可能不完整的成功結果。
   - 索引寫入需能辨識本次 version；失敗或取消不得留下半套新舊混合圖。

2. **檔案變更同步**
   - 使用 OS file watcher 監聽已支援 source 與設定檔，採 debounce 合併 burst 變更。
   - Agent／Project Analysis session 建立時執行快速 hash catch-up。
   - 尚在 debounce 或索引中的檔案標為 pending；查詢結果必須夾帶 staleness metadata。
   - UI 提供狀態、最後索引時間、pending 檔案數與手動「重新索引」。

3. **查詢正確性**
   - `GetNeighborhoodAsync(depth)` 必須真正使用 depth，並提供節點／邊數上限與截斷旗標。
   - reverse call chain 的跨專案邊、最大深度、循環與截斷需可觀測。
   - Repo Map 不得依賴不保證有效的 full-text wildcard；改用明確的 centrality／entry-point 查詢介面。

4. **可診斷性**
   - 顯示 analyzer 支援度、略過檔案原因、解析錯誤、圖譜節點／邊統計與 manifest。
   - 提供固定 fixture 的 graph snapshot／diff，避免 schema 演進時靜默降低準確率。

### 3.3 驗收條件

- [ ] C#／Java fixture 中，變更被呼叫方法後，未修改 caller 的 `CALLS` 邊仍存在。
- [ ] 新增、修改、刪除、rename、Git staged／unstaged／untracked 檔案皆可正確同步。
- [ ] 索引被取消或失敗時，上一個成功 manifest 仍可用，且 UI 明確標為 stale／failed。
- [ ] depth=2 查詢可取得二跳關係，並在超過限制時回傳 `truncated=true`。
- [ ] 任一分析結果可回溯到 manifest version、檔案與 source span。
- [ ] P0 回歸測試涵蓋全量、增量、watcher debounce、catch-up 與 Neo4j unavailable。

---

## 4. Phase P1 — C# 與 Java 深度語意 Code Intelligence

### 4.1 目標

不以語言數量為 KPI；先讓 C# 與 Java 的跨檔案、跨模組、動態派發、Web 入口與測試關係足以支持可信的變更影響分析。

P1 完成後，Java regex analyzer 不得再被標示為 `Exact` 或 `Resolved` 的來源；它只能作為不支援語法時的明確 fallback。

### 4.2 C# Analyzer

#### 專案與建置模型

- 偵測 `.sln`、`.slnx`、`.csproj`、project reference、target framework 與 package reference。
- 使用 workspace-aware compilation 解析 solution／project；可解析時採 compiler semantic model，而非只以單一 `object` reference 建立 compilation。
- 無法載入 MSBuild、restore 不完整或平台相依時，降級成 partial analysis，並記錄缺失 reference 與 confidence。
- 建立 `Solution`、`Project`、`Assembly`、`Package`、`File` 節點及 `CONTAINS`、`PROJECT_REFERENCES`、`DEPENDS_ON_PACKAGE` 關係。

#### 必須精確支援的語意

- namespace、nested／partial type、record、interface、abstract／sealed type、generic type 與 generic method。
- constructor、method overload、extension method、async／await、property、field、event、delegate、lambda 的可解析關係。
- inheritance、interface implementation、override、explicit interface implementation。
- invocation、object creation、type reference、attribute、DI constructor injection、configuration binding 的來源位置。
- `CALLS` 與 `DISPATCHES_TO` 分開表示；interface／virtual 呼叫需列出已解析的可能實作與可信度。

#### ASP.NET 與測試

- 支援 Controller attribute route、minimal API、endpoint group、HTTP method、route template、handler。
- 產生 `Route`、`Endpoint`、`RequestContract`、`ResponseContract`、`ConfigurationKey`、`Test` 節點。
- 辨識 xUnit、NUnit、MSTest 的 test、fixture、data-driven test，建立 `TESTS`／`COVERS` 關係；無法靜態證實時不得假稱 coverage。

### 4.3 Java Analyzer

#### 專案與建置模型

- 偵測 Maven `pom.xml`、Gradle Groovy/Kotlin DSL、multi-module root／submodule、source set 與 test source set。
- 使用 AST 為主要解析依據；優先採可取得 binding／type resolution 的 Java language service 或 parser adapter。
- 若 classpath、JDK 或 generated source 不完整，輸出 partial analysis 與明確原因；不可用 method-name regex 多候選結果冒充精確 call edge。
- 建立 Module、Package、Type、Method、Field、Annotation、Dependency、File、Test 節點與對應關係。

#### 必須精確支援的語意

- import、nested／anonymous type、interface、abstract class、inheritance、implements、override、generic method／type、constructor overload。
- instance／static invocation、method reference、lambda、exception type、field／parameter／return type reference。
- `CALLS`、`OVERRIDES`、`IMPLEMENTS`、`DISPATCHES_TO` 分開表示，並標注靜態解析或候選派發。

#### Spring 與測試

- 支援 Spring MVC／WebFlux controller mapping、HTTP method、path variable、request／response contract。
- 支援常見事件與排程入口：`@Scheduled`、`@KafkaListener`、`@RabbitListener`、`@EventListener`，依 adapter 支援度標注 confidence。
- 支援 JUnit 4／5、TestNG 常見 test／parameterized test，建立可追溯 `TESTS` 關係。

### 4.4 共通解析與模型要求

- `Route`、`Endpoint`、`BackgroundJob`、`EventConsumer`、`ConfigurationKey`、`Test` 是 P1 共通節點。
- 每個 Route 必須能連至 handler，handler 可再沿 `CALLS` 追蹤；無法解析時保留 route artifact 與失敗原因。
- 每條 framework convention 產生的邊必須具有 `sourceKind=framework adapter`、adapter id 與 confidence。
- 可插拔 `ICodeAnalyzer`／`IFrameworkExtractor`／`ITestExtractor` 架構；新增框架不可修改既有解析器核心流程。
- 不支援技術只輸出 capability gap，不由 LLM 靜默補圖。

### 4.5 P1 驗收條件

- [ ] C# multi-project fixture 可追蹤跨 project reference 的 definition、reference、call 與 interface implementation。
- [ ] C# fixture 可識別 ASP.NET Controller 與 minimal API route → handler → service 路徑。
- [ ] C# fixture 可分辨 overload、override、explicit interface implementation 與至少一種 DI constructor injection。
- [ ] Java Maven 與 Gradle fixture 可辨識 module、dependency、source／test source set。
- [ ] Java fixture 可解析 interface dispatch、overload、inheritance 與跨檔案 call，且每條邊具可信度。
- [ ] Java Spring fixture 可辨識至少 MVC route 與一種背景／事件入口。
- [ ] xUnit、NUnit、MSTest、JUnit 的 fixture 可產生 test 節點與可追溯的關係；不將名稱相似誤報為已覆蓋。
- [ ] P1 所有 fixture 均有預期 graph snapshot、正向查詢與 impact query 驗證。
- [ ] 對故意缺少 SDK、JDK、NuGet／Maven dependency 的 fixture，系統回覆 partial analysis 與清楚限制，而非失敗或編造關係。

---

## 5. Phase P2 — Change Intelligence 對話與可交付變更計畫

### 5.1 Change Brief 與問題模式

對話輸入先正規化為：

- 類型：Bug、New Feature、Enhancement、Refactor、Risk Assessment。
- 目標：檔案、Symbol、Route、Git diff、錯誤 log、自然語言業務概念。
- 症狀／預期行為／限制／已知系統邊界。
- 候選修改點、需要驗證的假設與未知資訊。

支援五種模式：問題定位、變更落點、影響分析、實作規劃、驗證與回歸。

### 5.2 Clarification Planner

- 資訊不足時，Agent 必須主動提出 1–10 題依優先度排序的問題。
- 每題附帶「此答案會影響哪個設計／風險判斷」，禁止泛泛詢問「需求是什麼」。
- 優先詢問會改變資料模型、API 相容性、權限、外部整合、歷史資料處理或驗收策略的問題。
- 若已有足夠證據，直接給暫定分析與變更計畫，不為了湊題數阻塞使用者。

### 5.3 Evidence Planner 與回答規格

- Router 採明確目標與規則優先，LLM 判斷兜底；不可讓 Agent 任意決定是否使用圖譜。
- `Impact` 問題至少查詢 caller、callee、route、test、configuration、working-tree diff；P3 完成後再加 data evidence。
- 對話回答固定包含：
  1. 結論與建議修改點。
  2. 直接／間接影響範圍與風險等級。
  3. 可點擊的檔案、Symbol、Route 證據。
  4. 建議修改順序、測試與手動驗收條件。
  5. 已證實／推論／未知與 index freshness。
- 提供明確 UI 入口：選取檔案、Symbol、Route、Git diff 或貼上錯誤 log 後執行「分析修改影響」。

### 5.4 P2 驗收條件

- [ ] 模糊 Bug／需求 fixture 會先提出具決策價值的澄清問題，回答後可收斂候選修改點。
- [ ] 指定 file、symbol、route、Git diff 與 error log 五種輸入皆可啟動分析。
- [ ] 每份 Impact Report 具有可追溯 evidence、可信度、未知項目與 index manifest。
- [ ] 一般專案解析對話能自動取得 Evidence Pack；不依賴模型自行記得呼叫 graph tool。
- [ ] 可產出可交給開發人員的變更計畫：修改範圍、順序、測試、驗收條件與風險。

---

## 6. Phase P3 — Project Data Intelligence、即時設定證據與領域詞彙治理

### 6.1 目標

讓影響分析涵蓋資料結構、業務語意與必要的執行期設定，而非只看方法呼叫。

P3 分為兩種證據層：

- **靜態資料證據**：從 workspace 的 migration、DDL、ORM mapping、query、API／event contract 擷取 metadata，建立長期可索引的 Data Graph。
- **即時設定證據**：設定值或 feature flag 實際存放於資料庫時，Evidence Planner 可透過已配置的 Wingman Plugin／MAF tool 進行最小必要的唯讀查詢。此結果只作為本次分析的時間戳證據，不預設持久化到 Neo4j。

### 6.2 Data Schema Extractor

在既有檔案索引管線中擴充 extractor，不重複建立另一套原始碼索引。

- Canonical data node：`DataStore`、`Schema`、`Table/Collection`、`Column/Field`、`PrimaryKey`、`ForeignKey`、`Index`、`Constraint`、`View`、`Procedure`、`Query`、`Migration`。
- Canonical data edge：`MAPS_TO`、`READS`、`WRITES`、`FK_TO`、`MIGRATES`、`SERIALIZES_TO`、`PUBLISHES`、`CONSUMES`。
- 技術採 adapter 模式，不限定 database 或 ORM。支援度依偵測到的 artifact／adapter 決定。
- 證據優先序：DDL／Migration > ORM mapping > 明確 query usage > API／DTO contract > naming heuristic > LLM proposal。

### 6.3 Database Runtime Plugin 與 MAF Tools

資料庫連線不綁定單一資料庫或 ORM，也不放進 Code Graph 核心。以只有 Wingman 使用的 Plugin 封裝 connection driver、dialect、capability 與設定畫面，向 MAF 暴露一致的唯讀工具契約。

```text
Change / Bug 問題
  → 靜態 Data Graph 判斷是否需要 live evidence
  → 已啟用的 Database Runtime Plugin
  → MAF read-only tool
  → 時間戳化的 Runtime Evidence
  → Impact Report
```

建議的統一工具能力如下；實際 Plugin 可依技術支援子集，但必須宣告 capability：

| Tool capability | 目的 | 例子 |
|---|---|---|
| `inspect_schema` | 讀取 schema metadata，用於比對靜態索引與實際環境 | table、column、constraint、view |
| `find_configuration` | 以 key、namespace、功能名稱或已知 table／column 找設定 | feature flag、租戶設定、系統參數 |
| `read_configuration` | 以結構化條件讀取少量設定項目與版本／更新時間 | 指定 key、environment、tenant |
| `validate_query_plan` | 在執行前驗證查詢範圍、欄位與 row limit | 對未知 dialect 降級 |
| `execute_readonly_query` | 僅在前述能力不足時執行參數化唯讀查詢 | `SELECT`／受限 `WITH` |

#### 非可協商的安全與資料處理規則

- Plugin 連線設定由使用者配置；秘密值以既有受保護設定機制保存，永不寫入 manifest、Code Graph、聊天紀錄或診斷 log。
- Runtime Plugin 必須使用資料庫層級的唯讀帳號／唯讀 transaction；Wingman tool policy 也只允許讀取語意。禁止 DDL、DML、stored procedure 執行、multi-statement、寫入型 function 與無限制 dump。
- 優先呼叫 `find_configuration`／`read_configuration` 這類結構化工具，而非讓 LLM 自由組任意 SQL。
- fallback SQL 必須參數化、單 statement、schema allowlist、column allowlist、硬性 row limit、timeout 與結果大小上限；dialect 無法可靠判斷時拒絕執行並要求 Plugin 提供結構化 capability。
- 對疑似 secret、token、password、connection string、PII 欄位，預設只回傳「是否存在／是否符合預期／更新時間／雜湊差異」等衍生資訊；原值只在該 Plugin 明確宣告可安全呈現時才進入 Evidence Pack。
- Runtime Evidence 不寫入 Neo4j 作長期節點屬性；若需暫存，限當前分析 session、加密、TTL 清除，並記錄 `observedAt`、來源 Plugin、資料庫 identity 與 redaction 狀態。

#### 即時設定與 Code Graph 的關聯

靜態圖譜可以先找出：

```text
ConfigurationKey / FeatureFlag
  → 程式讀取點
  → Service / Route / BackgroundJob
  → API、事件、資料寫入與測試
```

當 IT 問「此環境目前啟用某功能嗎？若修改設定會影響什麼？」時，Plugin 只查詢相符 key 的即時值或狀態；Impact Report 再把它與靜態依賴路徑合併。這可同時回答「目前設定是什麼」與「改掉會波及哪裡」。

### 6.4 Domain Glossary

- Agent 可提出 table／column／API 欄位與業務詞彙的候選對應、別名、定義與敏感資料分類。
- 候選必須由 IT 在 Wingman 確認、修正或拒絕後才成為 `Confirmed` 知識。
- 每個 Glossary term 可追溯到支持它的 schema／code evidence 與確認紀錄。
- LLM 不得自動把欄位命名推論寫成已確認領域事實。

### 6.5 P3 驗收條件

- [ ] migration／DDL、ORM mapping、query 與 DTO／contract 可在同一圖譜中建立可追溯關係。
- [ ] 修改 table／column 或 API payload 時，Impact Report 可列出程式讀寫、關聯 migration、contract、測試與不確定項。
- [ ] 未配置 Database Runtime Plugin 時，系統仍可只依靜態證據完成 schema metadata 分析，並明示無法驗證即時設定值。
- [ ] 已配置 Plugin 時，`find_configuration`／`read_configuration` 可將即時設定證據與靜態讀取點、Route、測試和影響分析合併。
- [ ] 即時查詢僅能以唯讀帳號／唯讀 transaction 執行，且 DDL、DML、multi-statement、無限制 dump 與不受控 stored procedure 都被拒絕。
- [ ] secret／PII 設定值不會寫入 Neo4j、SQLite log、聊天歷程或診斷紀錄；Evidence Pack 顯示 redaction 與 observed time。
- [ ] Agent 提出的 Domain Glossary 候選須經 IT 明確確認後才可作為已證實證據。
- [ ] 遇到未支援的 ORM／資料存取層時，系統可保留來源、列出能力缺口，或提出待確認候選，但不輸出假確定關係。

---

## 7. 發布 Gate 與品質量測

### 7.1 Phase Gate

- P1 不得在 P0 的 graph correctness、staleness 與 fixture gate 未通過前宣稱可做可靠影響分析。
- P2 不得只用 LLM 摘要取代 P0／P1 的證據；無 Evidence Pack 時必須降級說明。
- P3 不得因加入資料詞彙而覆蓋已證實的 code／schema 關係。

### 7.2 Evals

每個 workspace 建立可人工審核的 Golden Questions，至少覆蓋：

- Bug 定位、Route 到 Service 流程、interface dispatch、跨 project／module 變更、Git diff 影響、測試建議、schema／contract 變更。
- 指標：正確 target recall、錯誤 relation rate、證據引用覆蓋率、過期索引告警率、澄清問題的有效收斂率、分析延遲與 token 使用量。
- 任何「未知」正確揭露都優於沒有證據的自信結論。

---

## 8. 建議實作順序

1. P0.1：Incremental correctness 與 manifest transaction。
2. P0.2：watcher、catch-up、staleness UI／metadata、query depth 修正。
3. P1.1：C# workspace-aware semantic model 與 multi-project fixture。
4. P1.2：Java AST／binding resolver 與 Maven／Gradle fixture。
5. P1.3：ASP.NET／Spring routes、測試、背景／事件 entry point。
6. P2：Change Brief、Clarification Planner、Evidence Pack、Impact Report UI。
7. P3：Schema extractor、Data Graph、Domain Glossary confirmation。

這個順序刻意把「正確與新鮮」放在「模型變聰明」之前；對修改前影響分析而言，漏掉依賴的危害遠高於回答不夠漂亮。
