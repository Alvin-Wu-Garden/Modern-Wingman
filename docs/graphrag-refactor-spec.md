# GraphRAG 破壞式重構 SPEC

> 狀態：**設計已確認，可進入實作**
>
> 日期：2026-07-24
>
> 適用範圍：`apps/agent-service` 的程式碼索引、知識圖譜、GraphRAG 檢索、社群摘要、Impact Analysis、Repo Map 與 AGENTS.md 圖譜上下文
>
> 目標案例：大型 .NET Framework 4.7.2 ASP.NET MVC、Ext.js／React、Java、SQL Server 投資交易與風控系統
>
> 性質：**一次性、允許不相容變更的完整重寫；不維護舊 Graph schema、舊 enum、舊 service class 或雙軌相容層**

---

## 1. 已確認且不得再反轉的設計決策

本節是本次重構的最高層級約束。後續實作者不得為了降低改動範圍，重新引入已被否決的設計。

1. **不實作 profile 機制。**
   - 不新增 `enterprise-lean`、`full`、`legacy` 或其他索引 profile。
   - 不新增 `IncludedNodeKinds`、`ExcludedNodeKinds`、每種 kind 白名單等設定。
   - Graph schema 只有一套；所有專案都使用同一套正確、精簡的模型。
   - 允許連線、逾時、批次大小、檢索 token budget 等技術設定；這些設定不是 profile，也不得改變圖譜語意。

2. **領域節點只保留四種。**
   - `Feature`
   - `EntryPoint`
   - `Code`
   - `Data`

3. **關係只保留九種。**
   - `ROUTES_TO`
   - `HANDLES`
   - `CALLS`
   - `DISPATCHES_TO`
   - `TRIGGERS`
   - `READS`
   - `WRITES`
   - `MAPS_TO`
   - `DEPENDS_ON`
   - 驗收宣告：`GraphEdgeKind` 必須且只能包含上述九個 member，不得以設定、plugin 或 profile 動態增加。

4. **Method、Property、Field、Column、PK、FK、Index、EnumItem、報表參數、排程 task row 等不建立節點。**
   - 它們只能存在於 node／edge 的 evidence、摘要或 attributes。
   - 禁止用 `role` 偷渡回方法級節點，例如建立 `Code(role=method)`。

5. **允許破壞式刪除。**
   - 新模組接管後，必須刪除舊 GraphRAG、CodeGraph、CodeAnalysis 的無用程式碼與檔案。
   - 不保留舊 service 的薄包裝、不雙寫舊／新 schema、不加入長期 compatibility adapter。
   - Neo4j 舊圖一律清除並重新建立，不做 V2 → V3 資料 migration。

6. **正式產生的核心程式碼必須具備詳細繁體中文註解。**
   - 公開型別、公開方法、重要 internal 型別與非直覺演算法都必須說明「用途、輸入、輸出、限制及為何這樣做」。
   - 禁止只把程式碼翻譯成無價值註解。
   - SQL、Cypher、正規表示式、AST visitor、增量發布與信心判定旁必須有中文設計說明。
   - 此要求必須有自動化測試，不只靠 code review。

7. **主要社群以業務功能為準，而不是由圖演算法決定。**
   - `tblMenuMap` 階層是具 Menu 系統的主要社群來源。
   - 排程、批次報表、共用 Enum、共用資料源與共用程式碼是跨社群連接點。
   - Leiden 只能產生次要 discovery community，不可覆寫業務社群。

8. **檢索採 BM25 種子搜尋＋受限圖遍歷，不引入 embedding。**
   - Local Search 用於 bug／新需求的修改範圍定位。
   - Global Search 用於跨模組問題與系統層級總覽。
   - LLM 不負責抽取 compiler／AST 已能確定的事實，也不得改寫 canonical graph。

---

## 2. 重構目標

### 2.1 產品目標

當使用者只提供簡短描述，例如：

- 「國外債券批次報表寄送內容錯誤」
- 「股票基金交易覆核後狀態沒有更新」
- 「新增一種商品 CSV 上傳格式」
- 「某個 Menu 功能查詢結果少一筆」

GraphRAG 必須提供一份可供 LLM 使用的 bounded evidence pack，至少回答：

1. 這是哪一個業務功能與 Menu 路徑？
2. 使用者從哪個前端頁面、route、Controller action 或排程進入？
3. 哪些 Controller、Business Logic、Repository／Query 類別處理此功能？
4. 它會讀寫哪些 Table、View、Procedure 或動態資料源？
5. 是否還有維護／覆核配對、排程、批次報表、CSV、CustomEnum 等隱性設定？
6. 如果是 bug，最可能修改哪些現有檔案？
7. 如果是新需求，哪些既有功能可作為相似實作？

### 2.2 工程目標

- 將目前散落於多個 namespace、超過 9,000 行的 GraphRAG 核心，重寫為一個扁平、易讀、最多 10 個正式 `.cs` 檔的模組。
- 將目前 37 種 `CodeNodeKind` 縮減為 4 種。
- 將目前 25 種 `CodeEdgeKind` 縮減為 9 種。
- 保留既有 V2 效能 SPEC 已驗證的正確性：
  - canonical snapshot
  - full rebuild
  - no-op fast path
  - 僅 C# body-only 的保守增量
  - staging graph 原子發布
  - Neo4j／SQLite manifest reconciliation
  - 失敗時保留上一張成功圖
- 明確排除孤兒、歷史、generated、vendor 與過細節點，讓 LLM 得到的是完整鏈路而不是大量符號。

### 2.3 專案瘦身目標

重構完成後必須同時滿足：

- 舊 GraphRAG production `.cs` 檔已刪除，不是留在原處標記 obsolete。
- 新 `modules/GraphRAG` 正式 `.cs` 檔數量 `<= 10`。
- 不新增 GraphRAG 專屬子專案；新模組直接編入 `AgentService` assembly，避免額外 csproj、adapter 與循環相依。
- 不建立「每張資料表一個 extractor」、「每種 node 一個 handler」或只有一層轉呼叫的 class。
- 單一檔案若超過約 1,500 行，必須證明其內部仍為同一責任；不得以「檔案數限制」製造另一個 3,000 行 God class。

---

## 3. 非目標

本次不做：

- 不索引方法、欄位、資料表欄位為獨立 node。
- 不做全語言通用 compiler framework。
- 不執行來源系統的業務 stored procedure。
- 不擷取交易內容、客戶資料、Email、FTP 密碼、帳號或報表輸出結果。
- 不讓 LLM 在 indexing 階段自由產生 node／edge。
- 不使用 embedding／vector index。
- 不實作跨多個交易資料庫的 federation。
- 不實作 Java 或 SQL 的局部增量；不確定時仍 full rebuild。
- 不保留 V2 graph reader、V2 graph writer 或 V2 → V3 migration。
- 不把所有 DB table 都放進圖；只 materialize 可由功能、程式碼、SQL 或設定到達的資料物件。

---

## 4. 真實投資系統探索基線

以下數字來自 `D:\FBL_Release_Trunk` 與本機 `FBL_SPV_SIT` 的唯讀探索，用於驗證抽取規則是否符合真實系統，而不是硬編碼成正式環境筆數。

### 4.1 Menu 與覆核

- `tblMenuMap` 是功能探索的主要入口。
- 一般處理路徑為：Menu `LinkAddress` → Controller → 前端／後端 → DB。
- `tblAsyncConfirmSourceTypeMapping` 有 201 組維護／覆核配對。
- 201 個 Confirm Menu 與 201 個 Maintain Menu 都能解析。
- `ConfirmSourceType` 與 `RMWebDefinition\Confirm.cs` 的 enum 可交叉驗證。

### 4.2 CSV 與商品類型

- `tblCSVFormat` 有 1,367 筆版本資料、406 個 logical FormatType。
- `tblProductTypeMappingCsvFormatType` 有 322 筆 mapping。
- `tblCustomProductType` 有 18 個目前有效的自訂商品類型。
- 38 筆 mapping 指向已不存在的舊 CustomTypeID，且全部 `Required=false`；應視為低信心歷史 mapping。
- CSV version、field、required 與 schema 差異是 evidence，不是 node。

### 4.3 自訂報表

- `tblCustomDesignRiskReportTemplate` 有 359 份模板。
- Template XML 中有 457 次資料源引用，目前全部能解析。
- 有 5 次 DataSourceGroup 引用，能解析至 5 個群組與 19 筆 group detail。
- `tblCustomDesignReportDataSource` 有 458 個 SQL 資料源。
- 自訂報表資料源共引用約 233 個不同 SQL table／view identifier。
- 89 次自訂參數資料源引用可全部配對，實際涉及 30 個參數資料源。
- Template、DataSource、DataSourceGroup 要建 Data node；template XML 元素、query parameter、return column 不建 node。

### 4.4 CustomEnum

- `tblCustomEnum` 有 287 組 enum。
- `tblCustomEnumItem` 有 3,382 筆 item、284 個具有 item 的 EnumName。
- 兩表正確關聯鍵是 `EnumName`，不是 `DataID`。
- 原始碼至少有 178 個 CustomEnum 查詢呼叫點、72 個可直接解析的 literal EnumName。
- `BPIPEField` 單一 enum 有 1,353 個 item，證明 EnumItem 絕對不能建 node。

### 4.5 排程與批次報表

- `tblSchedule` 有 370 個 schedule。
- `tblScheduleTask` 有 691 筆 task row，其中 458 筆有有效 Schedule、233 筆為孤兒。
- 有 46 個共享 TaskName；44 個能解析到 XML task definition 或專用 C# task class。
- `tblBatchReport` 有 69 個 batch report，其中 68 個被有效排程使用。
- `tblBatchReportDetail` 有 162 筆明細：
  - 159 筆 `CustomDesignReport`，全部可解析到自訂報表模板。
  - 3 筆 `PluginReport`，全部可解析到 `PlugIn_Report_FBL` 的 C# class。
- `tblBatchReportParameter` 有 959 筆固定參數。
- `tblBatchReportContact` 的個人資料不進圖；最多只保留有效 recipient count。
- 找不到 BatchReport 主檔的 6 個 Schedule 都已停用且屬測試／臨時／歷史用途。

### 4.6 由探索得到的核心結論

真實系統需要的不是更多 node kind，而是少量 node 搭配高品質 evidence：

```text
Menu Feature
  → Web EntryPoint
  → Controller Code
  → Business Code
  → Data

Schedule Feature
  → Scheduled Task EntryPoint
  → BatchReport Feature
  → CustomReport Feature / Plugin Code
  → Report DataSource
  → SQL Data
```

---

## 5. 現況問題與破壞式重構理由

### 5.1 Graph 模型過度細分

目前 `CodeNodeKind` 包含 Project、Solution、Assembly、Namespace、Type、Method、Property、Field、Column、PK、FK、Index、Constraint 等 37 種值；`CodeEdgeKind` 也包含 Contains、Implements、Inherits、DeclaredIn 等大量程式結構關係。

這種模型適合 IDE symbol browser，不適合「功能 bug／新需求要改哪些程式碼」：

- Method、Field、Column 數量會淹沒 Menu、Controller、報表與資料庫主路徑。
- 社群演算法容易依 namespace／contains 關係分群，而不是依業務功能分群。
- LLM context 被簽章、欄位與結構邊消耗，卻缺少 Menu、排程與動態 DB 設定。
- 同一個 C# type 的數十個 method 被拆成數十個 node，但最終修改單位仍然是同一檔案／type。

### 5.2 核心責任散落且檔案過大

重構前主要檔案概況：

| 區域 | 代表檔案 | 約略行數 | 問題 |
|---|---|---:|---|
| CodeGraph | `Neo4jCodeGraphStore.cs` | 2,354 | schema、write、query、community、validation 混在同一檔 |
| CodeGraph | `ProjectIndexService.cs` | 1,369 | scan、hash、plan、analyze、snapshot、publish、telemetry 混合 |
| CodeGraph | `GraphSnapshotCanonicalizer.cs` | 722 | 舊 37／25 kind canonicalization 複雜 |
| CodeAnalysis | `JavaCodeAnalyzer.cs` | 1,118 | 方法級輸出造成大量分支 |
| CodeAnalysis | `RoslynCodeAnalyzer.cs` | 991 | 建立所有 symbol node，後續再被 consumer 過濾 |

### 5.3 舊草案方向已失效

舊草案提出 `enterprise-lean` profile 與 node kind filter。此設計已明確否決，原因是：

- 同一專案會存在兩種語意不同的圖，測試與除錯成本上升。
- full profile 繼續保留過度抽取，無法真正刪除舊程式碼。
- filter 在分析完成後才刪 node，無法節省 Roslyn／Java／Neo4j 成本。
- 邊端點被過濾後需額外清理，增加 dangling edge 風險。

新設計必須在 extractor 階段直接輸出精簡模型。

---

## 6. 目標模組與責任邊界

### 6.1 目錄

所有 GraphRAG 正式程式集中到：

```text
apps/agent-service/modules/GraphRAG/
├── GraphModel.cs
├── GraphIndexingService.cs
├── CSharpGraphExtractor.cs
├── TextGraphExtractors.cs
├── SqlServerGraphExtractor.cs
├── GraphAssembler.cs
├── Neo4jGraphStore.cs
├── Neo4jRuntime.cs
├── GraphRetrievalService.cs
└── GraphRAGModule.cs
```

限制：

- 目錄保持扁平，不建立 `Application/Domain/Infrastructure/Adapters/...` 多層資料夾。
- 不建立 `Wingman.GraphRAG.csproj`；直接由 `AgentService.csproj` 編譯。
- 同一檔可以放數個高度相關的 internal type，以降低檔案支數。
- 對外只暴露少量 facade／contract；helper 預設 `internal`。

### 6.2 檔案責任

| 檔案 | 唯一主要責任 | 可包含的相關型別 |
|---|---|---|
| `GraphModel.cs` | V3 domain model 與 canonical snapshot | 4 NodeKind、9 EdgeKind、node、edge、evidence、artifact、snapshot、diagnostic |
| `GraphIndexingService.cs` | 索引 run orchestration | full/no-op/body-delta plan、watcher hosted service、progress、manifest coordination |
| `CSharpGraphExtractor.cs` | C# semantic extraction | workspace loader、trust policy、MVC/Core route、type-level call aggregation |
| `TextGraphExtractors.cs` | Java 與 JS/TS/TSX 精簡抽取 | Java type/endpoint、Ext.js/React route/page、第三方檔案排除 |
| `SqlServerGraphExtractor.cs` | 靜態 SQL 與 live SQL Server 抽取 | metadata query、ScriptDom、Menu/Report/Schedule/Enum/CSV 規則 |
| `GraphAssembler.cs` | identity、dedupe、link、validation、canonicalization | stable ID、edge merge、community seed、dangling edge validation |
| `Neo4jGraphStore.cs` | V3 graph persistence 與 active graph query | schema、staging write、atomic promote、BM25、bounded traversal |
| `Neo4jRuntime.cs` | Neo4j lifecycle | options、credential、download/offline package、start/stop、health |
| `GraphRetrievalService.cs` | 所有 graph consumer view | Local/Global Search、community summary、impact、repo map、AGENTS.md context |
| `GraphRAGModule.cs` | DI 與 technical options | `AddGraphRAG`、options validation、endpoint consumer registration helpers |

### 6.3 不允許的結構

- 不允許 `IMenuExtractor`、`IScheduleExtractor`、`IBatchReportExtractor` 等一表一介面。
- 不允許 `FeatureNodeFactory`、`CodeNodeFactory` 等一 kind 一 factory。
- 不允許 `LegacyGraphAdapter`、`V2GraphCompatibilityService`。
- 不允許 `GraphIndexingProfileOptions`。
- 不允許為了 DI 而建立只呼叫另一個 service 的 wrapper。

---

## 7. V3 Graph Domain Model

### 7.1 NodeKind

```csharp
public enum GraphNodeKind
{
    Feature,
    EntryPoint,
    Code,
    Data,
}
```

`role` 是 node property，不是新的 kind。role 必須使用集中常數，禁止任意拼字。

#### Feature roles

- `menu-feature`
- `approval-feature`
- `custom-report`
- `schedule`
- `batch-report`

#### EntryPoint roles

- `frontend-page`
- `web-route`
- `controller-action`
- `scheduled-task`
- `message-consumer`
- `cli-command`

#### Code roles

- `controller`
- `business-service`
- `repository`
- `data-model`
- `report-plugin`
- `type`
- `module`
- `migration`

#### Data roles

- `table`
- `view`
- `procedure`
- `report-template`
- `report-data-source`
- `report-data-source-group`
- `custom-enum`
- `product-type`
- `custom-product-type`
- `csv-format`
- `configuration`

### 7.2 EdgeKind

```csharp
public enum GraphEdgeKind
{
    RoutesTo,
    Handles,
    Calls,
    DispatchesTo,
    Triggers,
    Reads,
    Writes,
    MapsTo,
    DependsOn,
}
```

Neo4j relationship type 一律轉成大寫 snake case，例如 `ROUTES_TO`。

### 7.3 關係方向與語意

| Source | Edge | Target | 語意 |
|---|---|---|---|
| Feature／EntryPoint | `ROUTES_TO` | EntryPoint | 使用者由功能進入頁面／route，或前端入口呼叫後端 HTTP 入口 |
| EntryPoint | `HANDLES` | Code | 該入口由此程式碼實作處理 |
| Code | `CALLS` | Code | type-level 聚合呼叫；method 細節放 evidence |
| EntryPoint／Code | `DISPATCHES_TO` | Code | reflection、factory、task name、runtime mapping 等動態派送 |
| Feature | `TRIGGERS` | Feature／EntryPoint | 維護觸發覆核、Schedule 觸發 Task、Task 觸發 BatchReport |
| Code／Data | `READS` | Data | 程式或 SQL 讀取資料物件 |
| Code／Data | `WRITES` | Data | 程式或 SQL 修改資料物件 |
| Code／Data | `MAPS_TO` | Data | ORM model→table、ProductType→CSV Format、logical config→physical table |
| 任一 domain node | `DEPENDS_ON` | 任一 domain node | 無法以其他八種關係準確表達的必要依賴 |

`DEPENDS_ON` 是最後選項，不得拿來取代可確定的 `READS`、`WRITES` 或 `CALLS`。

### 7.4 Node schema

```json
{
  "id": "feature:menu:149009",
  "kind": "Feature",
  "role": "custom-report",
  "name": "台幣ETF申購買回投確授權章用印申請單",
  "aliases": ["149009", "ETF用印申請單"],
  "searchableText": "完整 Menu 路徑與精簡業務摘要",
  "language": "business",
  "technology": "tblMenuMap",
  "state": "active",
  "filePath": null,
  "startLine": null,
  "endLine": null,
  "confidence": "Exact",
  "attributes": {
    "menuId": 149009,
    "released": true
  },
  "evidence": [
    {
      "source": "Sql",
      "artifact": "db:FBL_SPV_SIT/tblMenuMap/149009",
      "reason": "由 LinkAddress 與 Menu 階層直接取得"
    }
  ]
}
```

### 7.5 Evidence 規則

每個 node／edge 至少一筆 evidence。Evidence 必須包含：

- `source`：Compiler、AST、Sql、Framework、Heuristic。
- `confidence`：Exact、Resolved、Heuristic、Inferred。
- `artifact`：相對檔案路徑或不含密碼的 DB logical key。
- `location`：可用時包含行號。
- `reason`：繁體中文簡述此事實如何得到。
- `details`：method 名稱、column、parameter、enum item、SQL fragment 等 bounded metadata。

禁止：

- Evidence 存 connection string。
- Evidence 存 Email、FTP password 或交易 row。
- Heuristic edge 標記成 Exact。
- 多 extractor 證據互相覆蓋；相同 node／edge 的 evidence 必須合併並穩定排序。

### 7.6 Stable identity

ID 不包含 absolute path、run time、manifest version。

```text
Feature Menu       feature:menu:{menuId}
Feature Schedule   feature:schedule:{scheduleId}
Feature Batch      feature:batch-report:{batchReportId}
Feature Report     feature:custom-report:{templateId}

Entry web route    entry:web:{normalized-controller}/{normalized-action}
Entry frontend     entry:frontend:{normalized-relative-path}
Entry task         entry:task:{normalized-task-name}

Code C#            code:csharp:{fully-qualified-type}
Code Java          code:java:{fully-qualified-type}
Code JS/TS         code:frontend:{normalized-relative-path}

Data SQL object    data:sql:{database}/{schema}/{object-type}/{name}
Data enum          data:enum:{normalized-enum-name}
Data CSV           data:csv-format:{normalized-format-type}
Data report source data:report-source:{serialId}
```

Edge ID：

```text
SHA-256(sourceId + NUL + edgeKind + NUL + targetId)
```

---

## 8. 原始碼抽取規格

### 8.1 共通排除

一律排除：

- `.git`
- `.vs`
- `bin`
- `obj`
- `node_modules`
- `packages`
- `dist`
- `build`
- `out`
- generated code
- minified JS
- vendor／third-party source
- designer files
- assembly attributes

測試碼不進正式 GraphRAG domain graph。測試與 production 的關係可在 Impact Analysis 即時由檔案搜尋補充，不建立 Test node。

### 8.2 C#

使用 Roslyn Syntax＋SemanticModel：

1. 每個 application-owned named type 最多建立一個 `Code` node。
2. Controller type 的 public MVC action 建立 `EntryPoint` node。
3. ASP.NET Core：
   - attribute route 為 Resolved／Exact。
   - controller/action attribute 合成完整 route。
4. ASP.NET MVC 4.7.2：
   - `System.Web.Mvc.Controller` 衍生類別。
   - convention route 建立 `/{Controller}/{Action}`。
   - `ActionName`、HttpGet／HttpPost 等 attribute 寫入 evidence。
5. Method、property、field 不建 node：
   - public method 列表、重要 signature、DI dependency、資料 model 名稱寫入 Code evidence。
6. method call 聚合為 type-level `CALLS`：
   - 同一 source type→target type 只保留一條 edge。
   - evidence 保存最多 N 個代表 method pair 與檔案行號。
7. `IMPLEMENTS`、`INHERITS`、`OVERRIDES` 不建 edge：
   - base type／interface 寫入 Code attributes。
   - 若 runtime dispatch 會影響修改範圍，轉成 `DISPATCHES_TO`，reason 說明 interface／override dispatch。
8. Data access：
   - `QR*`、repository、DbContext、Dapper、ADO.NET、stored procedure 字串。
   - 能解析 object name 時建立 `READS`／`WRITES`。
   - 只能依命名慣例推論時標記 Heuristic。

### 8.3 Java

1. 每個 top-level class／interface／record 建一個 `Code`。
2. Spring Controller endpoint 建 `EntryPoint`。
3. Spring service、repository、consumer 以 Code role 區分。
4. method call 聚合到 type-level `CALLS`。
5. JPA entity→table 使用 `MAPS_TO`。
6. Java 任一變更目前仍 full rebuild；不宣稱局部增量。

### 8.4 Ext.js、JavaScript、TypeScript、React

1. 只處理 application-owned `.js`、`.ts`、`.tsx`。
2. 前端頁面或可導覽 route 建 `EntryPoint(role=frontend-page)`。
3. 對應檔案建 `Code(role=module)`。
4. EntryPoint `HANDLES` Code module。
5. 擷取：
   - `Ext.Ajax.request`
   - Store proxy URL
   - `url: "/Controller/Action"`
   - `fetch`
   - Axios
   - 專案既有 wrapper，例如 `RMCommonLib.Fetch`
6. URL 可解析時，前端 EntryPoint `ROUTES_TO` 後端 EntryPoint；只有 URL 無法確定、但能證明存在必要依賴時，才可退化為 `DEPENDS_ON` 並在 reason 說明。
7. 不展開 React component tree、Ext component hierarchy 或 npm dependency。

---

## 9. SQL Server 與動態業務設定抽取

### 9.1 連線安全

- Connection string 由專案 secret reference 或環境變數取得，不寫入 repository。
- log、diagnostics、manifest、Neo4j evidence 禁止包含 password。
- 使用 `Microsoft.Data.SqlClient`。
- 所有查詢為固定 allowlisted SELECT。
- command timeout、cancellation token、最大 row count 必須生效。
- 不執行任意 user SQL，不執行 stored procedure。
- 可在連線字串加入 `ApplicationIntent=ReadOnly`，但仍不能把它當作唯一安全防線；DB 帳號本身必須唯讀。

### 9.2 套件

在 `AgentService.csproj` 明確 pin：

```xml
<PackageReference Include="Microsoft.Data.SqlClient" Version="7.0.2" />
<PackageReference Include="Microsoft.SqlServer.TransactSql.ScriptDom" Version="180.37.3" />
```

版本異動必須更新 lock file 並重新執行 SQL parser fixture；不得浮動使用最新版。

### 9.3 SQL object materialization

先收集 reference，再 materialize Data node。以下任一條件成立才建立 Table／View／Procedure：

- 被 Code 的 SQL／DAL reference 指向。
- 被 Procedure／View AST 指向。
- 被 CustomReport DataSource 指向。
- 被必要業務設定表指向。
- 是已入選 Data node 的直接 FK 鄰居且對理解修改範圍有價值。

禁止先建立全 DB 所有欄位再做過濾。

每個 SQL Data node 的 attributes／evidence 可包含：

- schema、object type
- column name／type／nullable
- primary key
- foreign key 摘要
- index 名稱
- procedure parameter
- definition hash

Column、PK、FK、Index 不建立 node。

### 9.4 SQL dependency

優先順序：

1. `sys.sql_modules.definition`
2. ScriptDom AST
3. `sys.dm_sql_referenced_entities` 可用時驗證
4. `sys.sql_expression_dependencies` 只作補充，不可作唯一來源

處理規則：

- temporary table 不 materialize 成 Data node。
- table variable 不 materialize。
- `$AP`、`$Portfolio` 等動態表名記為 capability gap／Heuristic evidence。
- `~IPKDB` 等環境替代符保留 normalized external reference，不把它冒充本 DB exact table。
- parser error 不得讓整個 DB extraction 失敗；該 DataSource 標記 partial diagnostic。

### 9.5 `tblMenuMap`

1. 每個 released／有效 Menu 建 `Feature(role=menu-feature)`。
2. 保存完整父階層路徑、Menu ID、名稱、描述、LinkAddress。
3. 解析 `/Controller/Action/...`：
   - Feature `ROUTES_TO` Web EntryPoint。
4. `/CustomReport/MenuIndex/{TemplateID}`：
   - Menu Feature `DEPENDS_ON` CustomReport Feature。
5. 找不到 Controller 或 Template：
   - 保留 Feature。
   - 寫 diagnostic。
   - 不建立假 Code node。

### 9.6 維護／覆核

來源：

- `tblAsyncConfirmSourceTypeMapping`
- `tblMenuMap`
- source enum／Controller usage

規則：

- 不建立 `ConfirmSourceType` node。
- 維護 Feature `TRIGGERS` 覆核 Feature。
- edge evidence 保存 ConfirmSourceType、MaintainMenuID、ConfirmMenuID。
- 維護與覆核 Feature 使用同一 primary community。

### 9.7 CSV 與商品

來源：

- `tblCSVFormat`
- `tblCSVFormatDBMapping`
- `tblProductTypeMappingCsvFormatType`
- `tblProductType`
- `tblCustomProductType`

規則：

- 一個 logical FormatType 只有一個 `Data(role=csv-format)`。
- version、Enable、Lastest、field schema 存 evidence。
- ProductType／CustomProductType 建 Data。
- ProductType／CustomProductType `MAPS_TO` CSV Format。
- mapping row 不建 node。
- 找不到 CustomType 且 mapping `Required=false`：
  - 有 DisplayName 時只保留 Heuristic evidence。
  - 無名稱時記 diagnostic，不建立無意義 node。

### 9.8 CustomReport

來源：

- `tblCustomDesignRiskReportTemplate`
- `tblCustomDesignReportDataSource`
- `tblCustomDesignReportCustomParameterDataSource`
- `tblCustomDesignReportDataSourceGroup`
- `tblCustomDesignReportDataSourceGroupDetail`

規則：

- Template 建 `Feature(role=custom-report)`；若完全無 Menu，也仍可由 BatchReport 到達。
- Template metadata 另可與同 ID 的 `Data(role=report-template)` 合併為 Feature attributes，**不要同時建立重複 Feature＋Data**。
- DataSource 建 Data。
- DataSourceGroup 建 Data。
- Template `DEPENDS_ON` DataSource／Group。
- DataSource 以 `READS`／`WRITES` 連 SQL Data。
- Query parameter、return column、Excel cell、XML element 不建 node。
- 自訂 parameter data source 只有被 ExtendValue 引用者才 materialize。

### 9.9 CustomEnum

來源：

- `tblCustomEnum`
- `tblCustomEnumItem`
- Code 中的 `QRCustomEnumItem` 查詢
- CustomReport parameter 的 `ExtendValue`

規則：

- 每個 EnumName 建一個 `Data(role=custom-enum)`。
- `tblCustomEnum` 與 `tblCustomEnumItem` 必須以 EnumName 關聯。
- Item／Value／Desc 放 evidence，不建立 item node。
- searchableText 最多放固定數量代表 item；大型 Enum 保存 item count 與 bounded sample。
- Code `READS` CustomEnum。
- Report DataSource `READS` CustomEnum。
- source code 有 literal、DB 無定義時可建立 Heuristic Data，但必須標示 `state=source-only`。

### 9.10 Schedule

來源：

- `tblSchedule`
- `tblScheduleTask`
- `RMScheduleService\TaskDefinition\*.xml`
- `RMScheduleTaskDefinition\ScheduleTask.cs`
- RiskMasterServer AUTO dispatcher

規則：

- Schedule 建 Feature。
- TaskName 建共享 EntryPoint；691 row 不建 691 node。
- Schedule `TRIGGERS` Task EntryPoint。
- Task EntryPoint：
  - XML task definition → `DISPATCHES_TO` 對應 executable／Code。
  - 專用 task class → `HANDLES` Code。
- task order、parameter JSON、enabled、frequency 放 edge／Feature evidence。
- 找不到 Schedule header 的 orphan task 排除。
- Disabled schedule 保留，但 `state=disabled` 並降低 retrieval score。

### 9.11 BatchReport

來源：

- `tblBatchReport`
- `tblBatchReportDetail`
- `tblBatchReportParameter`
- `tblBatchReportContact`

規則：

- BatchReport header 建 Feature。
- Schedule／Task `TRIGGERS` BatchReport。
- Detail 不建 node：
  - CustomDesignReport → BatchReport `DEPENDS_ON` CustomReport Feature。
  - PluginReport → BatchReport `DEPENDS_ON` report plugin Code。
- ParameterName／ParameterValue 放 edge evidence；應做長度限制與敏感值遮罩。
- Contact 不建 node、不保存 Email／UserID；只允許有效 recipient count。
- FTP 資料完全忽略。
- Detail／Parameter／Contact 找不到 header 時排除並記統計 diagnostic。

### 9.12 明確忽略的資料

- `tblCustomProductTypeDetail`：目前空且非定位程式修改所必需。
- `tblBatchReportFTP`：即使未來有值也不抽內容。
- `tblCustomDesignRiskReportTemplateDetail`：目前空；模板主 XML 已足夠。
- `tblCustomDesignReportDataSourceUseSetting`：目前空。
- `tblScheduleConfig`：目前空。
- `tblMQSchedule`、`tblMQScheduleTask`：目前空。
- `tblMenuAction*`、permission row：不建立 action／permission node。
- `tblRawData`、report output、running log、history table row。

---

## 10. Graph Assembly 與品質閘門

### 10.1 組圖順序

```text
Artifact scan
  → C#/Java/Frontend extraction
  → static SQL/ORM extraction
  → optional live SQL Server extraction
  → stable identity normalization
  → node merge
  → edge merge
  → dynamic business linking
  → primary community assignment
  → canonical sort/hash
  → validation
  → staging publish
```

### 10.2 Merge

- 同 ID node 合併 aliases、attributes 與 evidence。
- kind 衝突是 fatal validation error，不得自行挑一個。
- role 衝突時保留較具體 role，並記 diagnostic。
- 相同 source/kind/target edge 合併 evidence。
- Exact／Resolved evidence 不被 Heuristic 覆蓋。

### 10.3 Validation

發布前必須通過：

- NodeKind 只能是四種。
- EdgeKind 只能是九種。
- 所有 edge endpoint 存在。
- stable ID 唯一。
- 不存在 Method／Field／Column 等 legacy kind。
- 每個 node／edge 至少一筆 evidence。
- DB evidence 不含 password。
- Contact evidence 不含 Email。
- active graph node／edge count 與 canonical snapshot 一致。
- canonical digest 重算一致。

### 10.4 Diagnostics

Diagnostic 至少含：

- code
- severity
- artifact
- 中文 message
- retryable
- affected node／edge ID

常見 code：

- `DB_OBJECT_UNRESOLVED`
- `MENU_ROUTE_UNRESOLVED`
- `REPORT_SOURCE_UNRESOLVED`
- `TASK_DEFINITION_UNRESOLVED`
- `DYNAMIC_SQL_IDENTIFIER`
- `SQL_PARSE_PARTIAL`
- `ORPHAN_CONFIGURATION_SKIPPED`
- `HEURISTIC_ONLY_MAPPING`

---

## 11. 社群設計

### 11.1 Primary community

Primary community 是 deterministic business ownership：

1. Menu tree 的業務子樹為 community seed。
2. Feature 的 EntryPoint、主要 Controller、前端與專屬 Data 沿關係加入相同 community。
3. 維護／覆核 pair 強制同 community。
4. CustomReport 跟隨 Menu；若無 Menu但被 BatchReport 使用，跟隨 BatchReport 的主要業務名稱。
5. Schedule／BatchReport 預設屬「排程與批次」community，同時以跨社群 edge 連目標報表。
6. 多個 community 共用的 Enum、table、utility Code 標記 shared，不強制複製。

### 11.2 Secondary discovery community

- Neo4j GDS 可用時，以加權 Leiden 產生 secondary community。
- GDS 不可用時，使用 deterministic connected-component／label propagation fallback。
- Leiden 結果只供 Global Search 與探索 UI，不得覆寫 `primaryCommunityId`。
- relationship weight 依本 SPEC 的 retrieval edge weight。

### 11.3 Community report

Community report 是 graph metadata，不是四種 domain node 之一。

摘要內容固定要求：

- 業務功能名稱與 Menu 路徑
- 前端入口
- Controller／核心 Code
- 主要讀寫 Data
- 維護／覆核
- 排程／批次報表
- shared dependency
- 已知 capability gap

摘要 cache key：

```text
SHA-256(community member ids + member evidence hashes + summary prompt version)
```

LLM 摘要失敗不得阻止 Fast Index Ready。

---

## 12. 檢索設計

### 12.1 Local Search

適用 bug、修改、新需求定位。

流程：

```text
問題正規化
  → BM25 搜尋 Feature / EntryPoint / Code / Data
  → 選最多 12 個 seed
  → relation-aware bounded traversal
  → 產生完整 path candidates
  → 去重與 rerank
  → 建立 bounded evidence pack
  → LLM 回答修改範圍
```

不是單純取分數最高的 80 個 node；必須優先保留完整鏈：

```text
Feature → EntryPoint → Code → Data
```

### 12.2 建議 edge weight

| Edge | Weight |
|---|---:|
| `ROUTES_TO` | 1.00 |
| `HANDLES` | 0.95 |
| `TRIGGERS` | 0.95 |
| `DISPATCHES_TO` | 0.90 |
| `WRITES` | 0.90 |
| `READS` | 0.85 |
| `MAPS_TO` | 0.85 |
| `CALLS` | 0.75 |
| `DEPENDS_ON` | 0.70 |

每一 hop 乘上 `0.75` decay。數值可作 technical tuning，但不得因專案 profile 更換語意。

### 12.3 Node score

建議基礎公式：

```text
0.55 × normalized BM25
+ 0.25 × best path score
+ 0.10 × confidence score
+ 0.10 × state / business priority
```

狀態：

- active／released：1.0
- disabled：0.5
- source-only／heuristic：0.4
- orphan：不得進圖

### 12.4 預設 budget

- seed：12
- traversal depth：4
- candidate node：80
- path：30
- evidence pack：依模型 context 設上限，預設約 12k tokens
- 單一 node evidence：bounded
- 單一 Enum item sample：bounded

超過 budget 時以完整 path、Feature 命中與 write path 優先，不可隨機截斷。

### 12.5 新需求檢索

若問題不是既有 bug，而是「新增」：

1. 搜尋相似 Feature 名稱。
2. 搜尋相同 CustomProductType、Enum、CSV Format。
3. 搜尋讀寫相同 Data 的既有 Code。
4. 搜尋相同 role 的 EntryPoint。
5. 回傳「可參考的既有功能」與「可能新增的入口／資料關係」，但不得虛構尚不存在的 edge。

### 12.6 Global Search

Global Search 使用 primary／secondary community reports 做 map-reduce，適合：

- 哪些功能受某資料表影響？
- 系統有哪些日結與批次報表鏈？
- 哪些模組共用同一 CustomEnum？

Global Search 不用來回答單一 action 的精確修改行號。

---

## 13. Indexing、增量與原子發布

本次重構產生 V3 schema，但必須保留 `project-indexing-performance-optimization-spec.md` 已驗證的正確性。

### 13.1 V3 snapshot

- Schema version：`3.0`
- 完全刪除 `GraphAnalysisProfile`；snapshot 改存新的 `GraphIndexerDescriptor`，它只記錄 extractor 名稱與版本供重現結果，不接受 kind filter，也不影響 schema 語意。
- snapshot 保存：
  - indexer version
  - extractor identities
  - artifact manifest
  - canonical nodes／edges
  - diagnostics
  - capability gaps

### 13.2 No-op

只有以下全部相同才可 no-op：

- normalized artifact path
- byte length
- raw byte SHA-256
- indexer version
- schema version
- extractor version
- DB metadata fingerprint

mtime 不足以證明相同。

### 13.3 Incremental

只允許既有 C# 檔案 body-only、declaration surface 不變的增量。

以下一律 full：

- 新增／刪除／rename
- public signature 變更
- route／attribute 變更
- base type／interface 變更
- Java
- frontend route
- SQL／ORM
- live DB fingerprint 改變
- Menu／Schedule／Report／Enum／CSV 改變
- extractor／schema version 改變
- 任一不確定情況

增量結果必須與 clean full canonical graph zero diff。

### 13.4 發布

1. 建 staging graph。
2. 寫入 node／edge。
3. 驗證 count、endpoint、digest、evidence。
4. 單一 transaction 切 active anchor。
5. SQLite promote 相同 manifest。
6. reconciliation 以 Neo4j active anchor 為準。
7. 保留 active＋previous success。
8. 取消／失敗刪 staging，上一張 active graph 不受影響。

### 13.5 AI enrichment

- Fast Index Ready 後才執行 community summary。
- enrichment 有獨立狀態。
- LLM failure 只造成 Degraded，不回滾 canonical graph。

---

## 14. Neo4j V3 儲存

### 14.1 Labels

Domain node 只使用：

```text
:GraphEntity
```

`kind` 與 `role` 為 properties，不為每個 role 建 label。

內部 metadata 可使用：

- `:ProjectGraph`
- `:GraphCommunity`
- `:CommunityReport`

它們不是 GraphNodeKind，也不出現在 domain graph API。

### 14.2 Relationship

只有九種 relationship type。任何舊 relationship type 出現都視為 schema validation failure。

### 14.3 Index

- unique／range index：projectId、graphId、entity id。
- full-text index：
  - name
  - aliasesText
  - searchableText
- evidenceJson 不進 full-text index。
- full-text analyzer 與 query escaping 必須有中文註解與 fixture。

### 14.4 Query isolation

所有查詢先取得 active ProjectGraph，再限制同 graphId。禁止：

- 未帶 projectId 的 query。
- 混合 active／staging。
- Impact 的 search、reverse traversal、neighborhood 使用不同 manifest。

發布競態可 bounded retry 一次，仍不一致就明確失敗。

---

## 15. 必須刪除與取代的舊檔案

以下清單是正式重構工作，不是「有空再清」。

### 15.1 Domain／Contract／Snapshot

刪除：

```text
src/Domain/Models/CodeGraph.cs
src/Application/Models/GraphSnapshotV2.cs
src/Application/Contracts/ICodeAnalyzer.cs
src/Application/Contracts/ICodeGraphStore.cs
```

由 `modules/GraphRAG/GraphModel.cs` 取代。

### 15.2 舊 CodeAnalysis

新 extractor 完成後刪除整個舊 Graph 專用 `Infrastructure/CodeAnalysis`：

```text
CodeAnalysisProvenance.cs
CSharpCompilationTrustPolicy.cs
CSharpFrameworkGraphExtractor.cs
CSharpProjectGraphExtractor.cs
CSharpWorkspaceLoader.cs
JavaBuildGraphExtractor.cs
JavaCodeAnalyzer.cs
RoslynCodeAnalyzer.cs
```

不得留下 obsolete class。

### 15.3 舊 CodeGraph

刪除：

```text
Infrastructure/CodeGraph/AgentsMdGenerator.cs
Infrastructure/CodeGraph/GraphRagService.cs
Infrastructure/CodeGraph/GraphSnapshotCanonicalizer.cs
Infrastructure/CodeGraph/GraphSnapshotDeltaComposer.cs
Infrastructure/CodeGraph/ImpactAnalysisService.cs
Infrastructure/CodeGraph/LocalNeo4jCredentialStore.cs
Infrastructure/CodeGraph/Neo4jCodeGraphStore.cs
Infrastructure/CodeGraph/Neo4jLifecycleService.cs
Infrastructure/CodeGraph/ProjectIndexService.cs
Infrastructure/CodeGraph/ProjectIndexWatcherService.cs
Infrastructure/CodeGraph/RepoMapService.cs
```

若資料夾清空，刪除 `Infrastructure/CodeGraph` 目錄。

### 15.4 測試

刪除或完全重寫所有直接測試舊 SUT／舊 kind 的測試，例如：

```text
GraphSnapshotV2Tests.cs
GraphSnapshotDeltaComposerTests.cs
Neo4jCodeGraphStoreSearchTests.cs
ProjectIndexWatcherServiceTests.cs
GraphMixedGoldenTests.cs
```

較高層 acceptance／benchmark 不應全部刪除；應改接 V3，繼續證明既有效能與 correctness。

### 15.5 Consumer migration

下列 consumer 必須直接改用新 facade，不保留舊 class wrapper：

- `ServiceRegistration.cs`
- `ProjectEndpoints.cs`
- `ProjectIndexDiagnosticsEndpoints.cs`
- `ContextAgentTools.cs`
- `ExplorePlanCodeVerifyWorkflow.cs`
- `ExplorePlanCodeVerifyExecutors.cs`
- `WingmanChatAgent.cs`
- Change Intelligence 的 data adapter 輸出模型

外部 REST route 可維持相同以降低 UI 連動，但 response schema 若含舊 kind，必須升版或同步修改 UI。

### 15.6 禁止殘留

完成後以下搜尋必須為 0：

```text
CodeNodeKind
CodeEdgeKind
GraphSchemaV2
GraphAnalysisProfile
GraphRagService
Neo4jCodeGraphStore
ProjectIndexOptimizationOptions
enterprise-lean
IncludedNodeKinds
ExcludedNodeKinds
```

新名稱不得只是舊類別原封不動改名；code review 必須確認模型與流程確實精簡。

---

## 16. 中文註解與可維護性規格

### 16.1 必須有 XML doc 的項目

- 所有 public type。
- 所有 public method／property。
- 重要 internal orchestrator／extractor／validator。
- 所有 options property。
- 所有 enum 與 enum member。

XML doc 至少說明：

- 用途。
- 為何屬於 GraphRAG。
- 輸入／輸出。
- 重要限制或安全假設。

### 16.2 必須有 inline 中文說明

- stable ID normalization。
- method→type aggregation。
- heuristic confidence 判定。
- MVC convention route。
- Ext.js URL parsing。
- ScriptDom visitor。
- 動態 SQL placeholder。
- CustomEnum 以 EnumName 關聯的理由。
- Schedule／BatchReport orphan 排除。
- canonical merge。
- delta eligibility。
- Neo4j staging／promote／reconcile。
- BM25 query escaping。
- relation-aware traversal budget。
- community ownership 與 Leiden overlay。

### 16.3 好註解範例

```csharp
// 這裡刻意把方法呼叫聚合到「型別 → 型別」關係。
// GraphRAG 的目標是協助 LLM 找出需要修改的檔案與元件，而不是重建 IDE call hierarchy；
// 若為每個 method 建 node，大型傳統 MVC 專案會產生數萬個低價值節點並破壞業務社群。
// 原始 method pair 仍保存在 edge evidence，必要時可回到精確行號。
```

### 16.4 不合格註解

```csharp
// 迴圈跑所有節點
foreach (var node in nodes)
```

這只是翻譯程式碼，沒有說明設計理由。

### 16.5 自動化閘門

新增 Roslyn-based `GraphRAGChineseDocumentationTests`：

- 掃描 `modules/GraphRAG/*.cs`。
- public symbol 必須有 XML doc。
- 重要 internal type 以 attribute 或 allowlist 判斷，也必須有 doc。
- doc 必須包含中文字元，不接受純英文 placeholder。
- 禁止 `TODO: comment later`、`TBD`。
- 不要求每一行都有註解，避免製造噪音。

---

## 17. 測試規格

### 17.1 Model

- NodeKind 恰好四種。
- EdgeKind 恰好九種。
- stable ID normalization。
- edge ID deterministic。
- canonical sort/hash deterministic。
- conflicting kind merge fails。
- dangling edge fails。
- evidence merge retains provenance。

### 17.2 C#

- ASP.NET Core attribute route。
- ASP.NET MVC convention route。
- ActionName／HTTP verb。
- Controller→service type-level call。
- method 不產生 node。
- inheritance／interface 寫 evidence，不產生 legacy edge。
- body-only delta 與 full zero diff。

### 17.3 Java／Frontend

- Spring endpoint。
- Java type-level calls。
- Ext.Ajax URL。
- Ext Store URL。
- fetch／Axios／wrapper URL。
- min.js／vendor 排除。
- React component 不展開。

### 17.4 SQL

- ScriptDom table／view／procedure dependency。
- SELECT→READS。
- INSERT／UPDATE／DELETE／MERGE→WRITES。
- temp table 排除。
- dynamic identifier diagnostic。
- parser partial 不使全 run crash。
- connection string 不出現在 log／evidence。

### 17.5 FBL fixture

以去敏感 fixture 驗證：

- Menu→MVC Controller。
- Maintain Feature→Confirm Feature。
- ProductType→CSV Format。
- CustomReport→DataSource→Table。
- CustomEnum→Item evidence。
- Schedule→TaskName→XML／C# task。
- Schedule→BatchReport→CustomReport。
- BatchReport→Plugin report class。
- orphan schedule task／batch detail 被排除。
- contact email 不進 snapshot。

### 17.6 Retrieval

最低四組 golden questions：

1. 「債券交易作廢 bug」
   - 必須包含 Menu、前端／route、Controller、BZ、主要交易 Data、覆核鏈。
2. 「新增商品 CSV 格式」
   - 必須包含 ProductType／CustomType、CSV Format、解析／匯入 Code。
3. 「批次報表寄送內容錯誤」
   - 必須包含 Schedule、Task、BatchReport、CustomReport／Plugin、DataSource、SQL Data。
4. 「覆核後資料沒有更新」
   - 必須包含 Maintain／Confirm Feature、ConfirmSourceType evidence、write path。

驗收不是只看「有搜尋結果」，而是計算 expected path coverage 與 noise ratio。

### 17.7 Neo4j

- V3 schema create。
- full-text index ready。
- staging publish。
- cancel／failure 保留 active。
- SQLite promote failure reconciliation。
- active＋previous cleanup。
- mixed manifest query rejected。
- relationship type 僅九種。

### 17.8 Performance

既有 `project-indexing-performance-optimization-spec.md` gate 不下降：

- no-op p95 `<= 2s`
- warm full p95 `<= 60s`
- bundled cold full p95 `<= 90s`
- eligible C# body delta 與 clean full zero diff

新增：

- 本機 SQL Server metadata／business config extraction p95 建議 `<= 15s`。
- 不含 LLM 的 Local Search p95 建議 `<= 1.5s`。
- 同一 fixture 連續 full index 的 snapshot hash 必須相同。

---

## 18. 實作順序

這是破壞式 one-way migration，不建立長期 feature flag。允許短期在同一 branch 尚未編譯，但每個階段結束必須恢復可建置。

### Phase 0：建立 V3 contract

- 建立 `modules/GraphRAG/GraphModel.cs`。
- 加入 4／9 enum、snapshot、evidence、diagnostic。
- 建立 V3 model tests。
- 暫不改 Neo4j。

完成條件：V3 model tests 綠燈。

### Phase 1：Extractor 重寫

- 實作 C#。
- 實作 Java／Frontend。
- 實作 static SQL／live SQL。
- Change Intelligence data adapter 改輸出 V3。

完成條件：extractor fixtures 綠燈，輸出中不存在 method／column node。

### Phase 2：Assembler 與 indexing

- stable ID。
- merge／canonicalization。
- full／no-op／body delta。
- watcher、progress、manifest。

完成條件：full deterministic、delta zero diff。

### Phase 3：Neo4j V3

- 新 schema。
- staging／promote／reconcile。
- full-text。
- bounded traversal。
- 清除開發 DB 舊 V2 graph。

完成條件：Neo4j integration gates 綠燈。

### Phase 4：Retrieval 與 community

- Local／Global。
- primary community。
- Leiden overlay。
- Impact／RepoMap／AGENTS context。
- consumer 直接改用新 facade。

完成條件：四組 golden question coverage 達標。

### Phase 5：刪除與瘦身

- 執行第 15 節完整刪除清單。
- 移除舊 DI。
- 移除舊 tests。
- 移除舊 config。
- 執行禁止殘留搜尋。

完成條件：正式 `.cs <= 10`、禁止字串搜尋為 0、build／test 全綠。

### Phase 6：真實系統驗收

- 對 FBL source＋DB 唯讀 full index。
- 保存 node／edge／diagnostic 統計，不保存 DB credential。
- 執行真實 bug／需求 query。
- 人工確認修改檔案 coverage。

完成條件：本 SPEC Definition of Done 全部成立。

---

## 19. API 與 UI 行為

外部 REST route 原則上維持：

- index
- index progress／diagnostics
- query
- summary progress
- impact
- graph visualization
- repo map
- AGENTS.md generation

但 response 內容必須改為 V3：

- kind 僅四種。
- role 顯示實際語意。
- path 顯示完整 Feature→Code→Data。
- diagnostic 顯示中文訊息與 confidence。
- UI 不再提供 NodeKind profile／filter。
- 視覺化可依 kind 上色、依 role 顯示 icon，但 role 不是新增 label。

---

## 20. 安全與隱私

- DB credential 不進 source control、log、telemetry、Neo4j、prompt。
- BatchReportContact 不保存 Email／UserID。
- BatchReportFTP 完全不抽。
- 報表 ParameterValue 需經敏感值 detector；可能是 path、account、mail、token 時遮罩。
- SQL 只讀帳號。
- 所有 live DB query 有 timeout、cancel、row cap。
- LLM context 僅使用已通過 evidence policy 的內容。
- Diagnostic 不回顯完整 connection error 中可能包含的 credential。

---

## 21. 風險與處理

| 風險 | 處理 |
|---|---|
| 四種 node 太少 | 用 role＋evidence 表達，不新增 kind |
| type-level CALLS 遺失方法細節 | method pair 與行號保留在 edge evidence |
| Menu 不涵蓋新功能 | Code／Data 仍可由 BM25 命中，並搜尋相似 Feature |
| DB 設定有孤兒 | 排除孤兒並輸出統計 diagnostic |
| 動態 SQL 無法解析 | Heuristic／capability gap，不虛構 Exact edge |
| Leiden 拆壞業務社群 | Primary community 永遠由 Menu／規則決定 |
| 中文註解變成噪音 | 測試 public doc，review 設計理由，不要求逐行註解 |
| 破壞式改動造成 UI/API 編譯失敗 | 同一 migration phase 直接改 consumer，不保留 compatibility wrapper |
| 新 DB extraction 拉長索引 | 兩階段 materialization、固定 allowlist、fingerprint no-op |

---

## 22. Definition of Done

只有以下全部成立，才算完成 GraphRAG 重構：

- [x] `modules/GraphRAG` 已存在且正式 `.cs` 檔 `<= 10`。
- [x] 只有 4 種 domain NodeKind。
- [x] 只有 9 種 domain EdgeKind。
- [x] 無 profile 機制。
- [x] 無 Method／Field／Column／EnumItem／ScheduleTaskRow／BatchDetail node。
- [x] Menu→Frontend／Controller→Code→Data 鏈可查。
- [x] Maintain→Confirm 鏈可查。
- [x] ProductType→CSV Format 鏈可查。
- [x] CustomReport→DataSource→SQL Data 鏈可查。
- [x] Schedule→Task→BatchReport→Report 鏈可查。
- [x] CustomEnum 以 EnumName 聚合，Item 只在 evidence。
- [x] Primary community 以 Menu 業務邊界為主。
- [x] Local Search 能產生 bounded、完整修改路徑。
- [x] Global Search 使用 community report map-reduce。
- [x] V3 full/no-op/body-delta correctness 通過。
- [x] Neo4j staging／atomic publish／reconcile 通過。
- [x] 舊 GraphRAG／CodeGraph／CodeAnalysis 檔案已依第 15 節刪除。
- [x] 禁止殘留字串搜尋為 0。
- [x] 新模組 public／重要 internal 程式碼有詳細繁體中文註解。
- [x] 中文註解自動化測試通過。
- [x] DB password、Email、FTP 資料未進 graph／log／prompt。
- [x] 全部 unit、integration、acceptance、performance gate 通過。
- [x] 真實 FBL source＋DB 唯讀驗收完成。

---

## 23. 官方參考

- [Microsoft GraphRAG Indexing Overview](https://microsoft.github.io/graphrag/index/overview/)
- [Microsoft GraphRAG Indexing Architecture](https://microsoft.github.io/graphrag/index/architecture/)
- [Microsoft GraphRAG Query Engine](https://microsoft.github.io/graphrag/query/overview/)
- [Microsoft GraphRAG Global Search](https://microsoft.github.io/graphrag/query/global_search/)
- [Neo4j Graph Data Science Leiden](https://neo4j.com/docs/graph-data-science/current/algorithms/leiden/)
- [Neo4j Full-text Index Configuration](https://neo4j.com/docs/operations-manual/current/performance/index-configuration/)
- [Roslyn Syntax Analysis](https://learn.microsoft.com/dotnet/csharp/roslyn-sdk/get-started/syntax-analysis)
- [Microsoft SqlScriptDOM](https://github.com/microsoft/SqlScriptDOM)
- [Microsoft.SqlServer.TransactSql.ScriptDom NuGet](https://www.nuget.org/packages/Microsoft.SqlServer.TransactSql.ScriptDom/)
