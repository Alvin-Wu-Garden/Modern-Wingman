# GraphRAG V4 破壞性重構規格書（SPEC + TODO List）

> 狀態：**已定案，供實作使用**
> 日期：2026-07-29
> 前身：`modules/GraphRAG` V3（graphrag-refactor-spec.md 所定義的實作）
> 本文件為**唯一實作依據**。實作時嚴禁參考其他 doc 文件（包括
> `graphrag-refactor-spec.md`、`Modern Wingman System Prompt.md` 等）；
> 若本文件與程式碼現況衝突，以本文件為準。V3 未被本文件推翻的行為
> （staging/atomic publish、no-op fast path、DPAPI 憑證、manifest 對帳）一律保留。

---

## 0. 重構動機（實測數據佐證）

以下所有數據來自 2026-07-29 對 FBL 專案的實際量測
（Neo4j 前次索引圖 version `9c017d…`、SQL Server `FBL_SPV_SIT`、原始碼 `D:\FBL_Release_Trunk`）：

| # | 問題 | 實測證據 |
|---|---|---|
| P1 | 大型 repo 檔名 marker 過濾器砍掉 DAL 層 | 9,638 個 .cs 只有 3,036 個（31.5%）命中 marker；被排除的檔案中 2,283 個屬於 RMDAL/RMQuery/Provider 等資料存取層 |
| P2 | Code:type 節點大量孤立 | 5,096 個 type 節點中 3,600 個（71%）度數為 0，純雜訊 |
| P3 | 菜單雜訊 | 1,013 個 menu-feature 中 539 個（53%）孤立、642 個 inactive；tblMenuMap 過濾條件可將 1,497 筆縮至 698 筆 |
| P4 | SP 幾乎沒抽到 | 圖中 Data:procedure 只有 16 個；DB 實際有 109 SP + 105 TVF + 48 scalar function |
| P5 | FK 未利用 | DB 有 463 條 FK；圖中 Data→Data DEPENDS_ON 只有 19 條 |
| P6 | 邊 evidence 冗餘 | evidenceJson 平均 717 B/邊、P95 2,864 B、最大 85 KB、全圖 16 MB |
| P7 | 社群不可用 | primary 只有 3 個超大社群（一個吞 678 功能）；secondary 有 1,930 個碎片（平均 2-3 成員） |
| P8 | PluginReport 菜單假連通 | 39 個 plugin 菜單全部 ROUTES_TO 同一個 `PluginReport/MenuIndex` 入口；Base64 payload 未解碼，與 `*_ReportKernel` 類斷鏈 |
| P9 | .aspx 完全未索引 | 638 個 .aspx 不在掃描副檔名內；實測 menu→frmalternativefund.js 路徑數 = 0 |
| P10 | `return View("x")` 未抽 | 650 處 named View 呼叫（447 個 controller 檔）沒有對應邊 |
| P11 | React tsx 覆蓋不足 | 562 個 tsx 只有 151 個入圖；`IsPage` 只認複數 `views` 資料夾（FBL 是單數 `Scripts/View`）；無 import 邊導致 component 層斷鏈（fetch 呼叫在 component 層） |

已與產品負責人確認的兩個設計決策：
- **共用元件（shared）**：影響分析中折疊成一句「另影響 N 個共用元件使用者」，不展開明細。
- **ExtJS xtype/component hierarchy**：不常用，**不做**。維持「每檔案一個前端模組節點」的粒度。

---

## 1. 範圍與非目標

### 1.1 範圍（本次要做）
1. 節點抽取規則重寫（四 kind 不變，抽取條件全面修訂）。
2. 邊 payload 瘦身：evidence 移出 Neo4j 至 SQLite，邊上改存結構化小欄位＋預計算 weight。
3. 新增三類鏈路：aspx 前端頁面鏈、PluginReport Base64 動態分派鏈、FK/SP 資料庫依賴鏈。
4. 社群重構：C0/C1/C2 三層階層社群。
5. 檢索端配合修改（weight 讀邊屬性、shared 折疊、communityId O(1) 查找）。
6. 驗收測試與結構化測試報告。

### 1.2 非目標（本次明確不做）
- ❌ Method 級節點（維持禁令；方法名記在邊屬性上）。
- ❌ Embedding／向量檢索（維持 BM25）。
- ❌ Agentic 多輪工具迴圈（檢索仍為單發 deterministic 管線；工具化留待未來版本）。
- ❌ ExtJS component hierarchy 展開（已確認不常用）。
- ❌ WCF／RealTimeService／MQ 跨進程呼叫鏈（已知限制，於回答中誠實聲明）。
- ❌ CFG/DFG、測試範圍推導（TESTS 邊留待未來版本）。

### 1.3 破壞性授權
- Modern Wingman 尚未發佈：**允許不向後相容**。graphVersion schema 直接換代，
  不寫 V3→V4 遷移程式；V4 首次索引即全量重建。
- 測試期間允許直接清空 Neo4j 資料庫與 SQLite 中除 PAT 設定以外的資料表。

---

## 2. 資料模型規格

### 2.1 節點（GraphNode）

四種 kind 不變：`Feature`、`EntryPoint`、`Code`、`Data`。

#### 2.1.1 共通屬性（Neo4j node property）

| 屬性 | 型別 | 說明 |
|---|---|---|
| `id` | string | 確定性 ID，格式 `{kind}:{source}:{logicalKey}`，跨執行穩定 |
| `projectId` / `graphVersion` | string | 沿用 V3 |
| `kind` | string | 四值之一 |
| `role` | string | 白名單管制（GraphRoles） |
| `name` | string | 人類可讀短名稱 |
| `searchableText` | string | BM25 去噪文字 |
| `aliasesText` | string | 別名串接 |
| `language` | string | business / csharp / frontend / sql |
| `state` | string | `active` / `inactive` / `unresolved` / **`shared`**（新增語意，見 2.1.4） |
| `filePath` / `startLine` / `endLine` | string?/int? | 沿用 |
| `attributesJson` | string | 有界結構化屬性 |
| **`degree`** | int | **新增**：assembler 計算的總度數，供檢索排序與孤立清理 |
| **`communityId`** | string? | **新增**：所屬 C1（或 C2）社群 ID，O(1) 讀取 |
| **`evidenceRef`** | string | **變更**：不再存 evidenceJson，改存 SQLite 查詢鍵（= node id） |

#### 2.1.2 各 kind 抽取規則

**Feature**

| role | 來源 | 規則 |
|---|---|---|
| `menu-feature` | tblMenuMap（live DB） | **只抽** `Released=1 AND ISNULL(LinkAddress,'')<>'' AND ID NOT LIKE '88%'`（實測 1,497→698）。`attributes.parentMenuId` 保存 Parent 欄位供 C0/C1 階層 |
| `custom-report` | tblCustomDesignRiskReportTemplate + 菜單 regex | 沿用 V3（實測 664 節點僅 1 孤立，機制正確） |
| `approval-feature` / `schedule` / `batch-report` | live DB | 沿用 V3 |

**EntryPoint**

| role | 來源 | 規則 |
|---|---|---|
| `controller-action` | Roslyn | 收緊：只保留「被任一 Feature ROUTES_TO/TRIGGERS/DISPATCHES_TO」或「有 HANDLES 出邊且所屬 controller 有呼叫鏈」的 action；其餘收進 controller 節點 `attributes.actions` 清單 |
| **`frontend-page`** | **新增 AspxGraphExtractor** | 每個 `Views/**/*.aspx` 建一個節點（638 個候選）；name = 檔名去副檔名；attributes 含 `viewName`、`controllerFolder`、`includedScripts` |
| `scheduled-task` | Roslyn（TaskName 常值） | 沿用 V3 |

**Code**

| role | 來源 | 規則 |
|---|---|---|
| type 系（controller/business-service/repository/data-model/report-plugin/module…） | Roslyn | **兩階段抽取**取代 marker filter：<br>**階段一**（全量、syntax-only）：全部 .cs 檔建 type 節點 + 跨 type CALLS 邊；marker 清單廢除。<br>**階段二**（深度）：只對「從任一 EntryPoint 沿 HANDLES/CALLS 可達」的 type 補完整 attributes（methods/baseTypes/constructorDependencies）。<br>**孤立清理**：assembler 最後刪除 `degree == 0` 的 Code:type 節點（解 P2 的 3,600 孤立） |
| frontend module | FrontendGraphExtractor | `IsPage` 資料夾判斷補單數 `view`；因 es-import 邊（2.2）可達的 component 一律入圖（解 P11 的 411 個被丟棄 tsx） |

**Data**

| role | 來源 | 規則 |
|---|---|---|
| `table` / `view` | live DB sys catalog | 沿用 V3 |
| `procedure` / **`function`（新增 role）** | **live DB sys catalog 為權威來源** | `sys.objects`（type IN P, FN, TF, IF）全量建節點（109 SP + 153 function）；`.sql` 檔案存在時才補 filePath/行號證據，不存在不缺節點（解 P4） |
| 其餘（report-template/csv-format/custom-enum…） | live DB | 沿用 V3 |

#### 2.1.3 節點 ID 規則（新增部分）

```
entry:page:{normalizedRelativePath}          例：entry:page:riskmaster_web/views/alternativefund/frmalternativefund.aspx
data:sql:{db}/{schema}/function/{name}       例：data:sql:FBL_SPV_SIT/dbo/function/fnGetRate
```
其餘沿用 V3 `GraphIdentity` 規則。

#### 2.1.4 `state=shared` 判定與行為

- **判定**（assembler 階段自動標記，不靠人工清單）：
  - Code(frontend module)：入邊（HANDLES/CALLS）來源數 ≥ 10。
  - EntryPoint：入邊 ROUTES_TO 來源 Feature 數 ≥ 10（如 `PluginReport/MenuIndex`）。
  - Code(csharp type)：CALLS 入邊來源數 ≥ 20（如 LogHelper 類）。
  - 閾值定義為 `GraphSharedNodeThresholds` 常數類，可調。
- **行為**：
  1. C1 社群閉包展開遇到 shared 節點：**收錄該節點本身，但不經由它繼續向外擴**。
  2. 檢索影響分析：shared 節點不展開明細，回答中折疊為
     「另影響 N 個共用元件使用者」（N = 該節點入邊來源數，存於 `attributes.sharedConsumerCount`）。

### 2.2 邊（GraphEdge）

九種 kind 不變：`ROUTES_TO`、`HANDLES`、`CALLS`、`DISPATCHES_TO`、`TRIGGERS`、`READS`、`WRITES`、`MAPS_TO`、`DEPENDS_ON`。

#### 2.2.1 邊屬性（Neo4j relationship property）— 全面替換 V3 的 evidenceJson

| 屬性 | 型別 | 說明 |
|---|---|---|
| `id` | string | SHA-256(source\0kind\0target)，沿用 |
| `graphVersion` | string | 沿用 |
| **`weight`** | float | **新增**：索引時預計算（2.2.2），檢索直接讀取 |
| **`confidence`** | string | `certain` / `probable` / `inferred`（全部 evidence 取最高） |
| **`evidenceCount`** | int | 觀察次數 |
| **`reasonCode`** | string | enum 代碼（2.2.3），取代繁中散文 |
| **`topArtifact`** | string | 一筆代表性檔案路徑或 DB logical key |
| **`topLine`** | int? | 代表性行號 |
| **`sourceMethod`** / **`targetMethod`** | string? | 方法名（僅 CALLS/READS/WRITES 有值） |
| ~~`evidenceJson`~~ | — | **刪除**。完整 evidence 移 SQLite（2.3） |
| ~~`sourceId` / `targetId`~~ | — | **刪除**（Neo4j 關係端點已隱含） |

#### 2.2.2 weight 計算公式（索引時由 assembler 寫入）

```
weight = clamp( base(kind) × confidenceFactor × crossNamespaceFactor × sharedTargetFactor × evidenceBonus , 0.05 , 1.0 )

base:      ROUTES_TO 1.00 | HANDLES 0.95 | TRIGGERS 0.95 | DISPATCHES_TO 0.90 | WRITES 0.90
           READS 0.85 | MAPS_TO 0.85 | CALLS 0.75 | DEPENDS_ON 0.70
confidenceFactor:      certain 1.0 | probable 0.9 | inferred 0.8
crossNamespaceFactor:  CALLS 且 source/target 頂層 namespace 不同 → 0.9，否則 1.0
sharedTargetFactor:    target.state == shared → 0.5，否則 1.0
evidenceBonus:         evidenceCount >= 3 → 1.1，否則 1.0
```

#### 2.2.3 reasonCode enum（封閉清單，新增值需改本 SPEC）

```
roslyn-invocation | roslyn-route | roslyn-view-result | roslyn-task-name
scriptdom-read | scriptdom-write | sys-dependency | fk-constraint
menu-link | menu-link-base64 | aspx-script-include | es-import
frontend-url | db-metadata | naming-convention
```

#### 2.2.4 新增／修訂的邊規則

| # | 邊 | 方向 | 規則 | reasonCode |
|---|---|---|---|---|
| E1 | `DISPATCHES_TO` | Feature(menu) → Code(csharp type) | LinkAddress 符合 `/PluginReport/MenuIndex/{base64}` → Base64 解碼 → 取 `{dll}/{FQN}` 的 FQN → 指向 `code:csharp:{FQN}`。**FQN 不存在時**：建 `state=unresolved` 的 stub Code 節點並掛邊（解 P8） | `menu-link-base64` |
| E2 | `DISPATCHES_TO` | EntryPoint(action) → EntryPoint(frontend-page) | Roslyn 抽 action body 內 `return View("viewName")`（含 `View(viewName, model)` 多載）；view 解析採 MVC 慣例：先 `Views/{Controller}/{viewName}.aspx`，再 `Views/Shared/{viewName}.aspx`；找不到 → 不建邊、記入 diagnostics（解 P10） | `roslyn-view-result` |
| E3 | `HANDLES` | EntryPoint(frontend-page) → Code(frontend module) | AspxGraphExtractor 解析 `<script src=…JSPath("…")>` 與 `AbsolutePath("…")`：`.js` 結尾 → 指向對應前端模組節點；`/Controller/Action/` 形式 → 建 `ROUTES_TO` 指向該 action（GetUIData 動態 script 只建到 action 為止，不解析內容）（解 P9） | `aspx-script-include` |
| E4 | `CALLS` | Code(frontend) → Code(frontend) | tsx/js 相對路徑 ES import（`import x from './…'` 或 `'../…'`）；npm 套件 import 一律忽略（解 P11） | `es-import` |
| E5 | `DEPENDS_ON` | Data(table) → Data(table) | `sys.foreign_keys`：FK 子表 → 父表，463 條全建（解 P5） | `fk-constraint` |
| E6 | `READS`/`WRITES` | Data(procedure/function/view) → Data(table) | `sys.sql_expression_dependencies` 為權威來源；ScriptDom 分析 `.sql` 檔案僅用於補行號與 WRITES 判定（INSERT/UPDATE/DELETE/MERGE），sys 無法判讀寫時 confidence=probable、預設 READS（解 P4） | `sys-dependency` |
| E7 | `CALLS` | Code → Code | 兩階段 Roslyn（2.1.2），記 `sourceMethod`/`targetMethod` | `roslyn-invocation` |

### 2.3 SQLite Evidence Store（新增）

Neo4j 不再存 evidence 明細；完整 evidence 落 SQLite（與現有 manifest 同一個 DB 檔）。

```sql
CREATE TABLE IF NOT EXISTS graph_evidence (
    project_id    TEXT NOT NULL,
    graph_version TEXT NOT NULL,
    entity_id     TEXT NOT NULL,   -- node id 或 edge id
    entity_type   TEXT NOT NULL,   -- 'node' | 'edge'
    seq           INTEGER NOT NULL,
    source        TEXT NOT NULL,   -- ast/framework/db-metadata/...
    confidence    TEXT NOT NULL,
    artifact      TEXT NOT NULL,
    reason        TEXT NOT NULL,   -- 繁中說明（僅溯源 UI 顯示用）
    start_line    INTEGER,
    end_line      INTEGER,
    details_json  TEXT,
    PRIMARY KEY (project_id, graph_version, entity_id, seq)
);
CREATE INDEX IF NOT EXISTS ix_graph_evidence_lookup
    ON graph_evidence (project_id, graph_version, entity_id);
```

- 寫入時機：staging 寫 Neo4j 的同一交易流程內（先 SQLite 後 Neo4j publish；publish 失敗時清掉該 graph_version 的 evidence）。
- 清理時機：Neo4j 舊版本 cleanup 時同步 `DELETE WHERE graph_version = retired`。
- 每 entity evidence 上限維持 40 筆。
- **保留 PAT／使用者設定等既有資料表不動**；本次僅新增 `graph_evidence` 與（若需要）版本欄位。

### 2.4 社群（CommunityReport）— 全面重寫

刪除 V3 的 primary（3 個超大社群）與 secondary（1,930 碎片）機制，改為三層：

| tier | 來源 | 預期數量 | 用途 |
|---|---|---|---|
| **C0 模組層** | tblMenuMap 樹頂層節點（Parent=Root 那一層，套用同樣過濾條件） | 10~20 | Global Search 第一層漏斗 |
| **C1 功能層** | 每個過濾後葉子菜單（含 custom-report/approval/schedule Feature）＝一個社群；成員 = 沿 `ROUTES_TO/TRIGGERS/DISPATCHES_TO/HANDLES/CALLS/READS/WRITES` 的可達閉包，約束：depth ≤ 4、只走 `weight ≥ 0.7` 的邊、**遇 shared 節點收錄但不外擴**、成員上限 60 | ≈ 698＋ | Local Search 錨點；「這個功能動到哪些程式與表」 |
| **C2 孤兒補集** | 對「未被任何 C1 覆蓋」的節點跑 Leiden（GDS 有裝）或確定性標籤傳播（fallback，沿用 V3 演算法與參數）；**成員數 < 3 的社群直接丟棄** | 30~80 | 排程/批次/MQ 類的 Global Search |

**CommunityReport 節點屬性**：

| 屬性 | 說明 |
|---|---|
| `communityId` | C0: `community:menu-root:{menuId}`；C1: `community:menu:{menuId}` 或 `community:feature:{featureId}`；C2: `community:leiden:{sortedMemberDigest}` |
| `tier` | `C0` / `C1` / `C2` |
| `parentCommunityId` | C1 → 所屬 C0；C0/C2 為 null |
| `title` / `summary` | 模板骨架 + LLM 離線潤飾 2-4 句繁中（沿用 V3 快取機制：cacheKey = member digest + prompt version） |
| `memberIdsJson` / `memberCount` | 成員清單 |
| `topTables` / `topEntryPoints` | 供摘要與檢索的代表成員（各最多 5 個） |

**節點回填**：每個節點的 `communityId` 指向其 C1（或 C2）；一個節點同屬多個 C1 時取「錨點 Feature 距離最近」者，並在 `attributes.alsoInCommunities` 記其餘（上限 5）。

### 2.5 檢索端配合修改（GraphRetrievalService）

1. `EdgeWeight` hardcode 常數表**刪除**，改讀邊上 `weight` 屬性。
2. BFS 展開遇 `state=shared` 節點：計分收錄、不外擴（與 C1 閉包同規則）。
3. 影響分析（AnalyzeImpact intent）輸出：shared 節點折疊為「另影響 {sharedConsumerCount} 個共用元件使用者」。
4. Global Search 改兩層漏斗：先 C0 term-scoring 選 1~2 個模組，再在其 C1 子集內比對；C2 一律參與第二層。
5. 節點分數公式維持 V3（seed × weight × direction × hopDecay），僅 weight 來源改變。
6. `degree` 屬性納入 BM25 seed 排序的 tie-breaker（同分時度數高者優先）。

---

## 3. 實作 TODO List（依相依順序）

> 標記規則：每項完成後在 PR 描述引用編號。單項預估以「半天」為單位。

### Phase 1 — 資料模型與儲存層

- [ ] **T1.1** `GraphModel.cs`：GraphEdge 增加 `Weight`、`Confidence`、`EvidenceCount`、`ReasonCode`、`TopArtifact`、`TopLine`、`SourceMethod`、`TargetMethod` 欄位；GraphNode 增加 `Degree`、`CommunityId`；新增 `GraphRoles.Function`、reasonCode enum 常數類、`GraphSharedNodeThresholds` 常數類。
- [ ] **T1.2** `GraphIdentity.cs`：新增 `FrontendPageEntry(relativePath)` 與 `SqlFunction(db, schema, name)` ID 產生器。
- [ ] **T1.3** SQLite：manifest store 所在 DB 新增 `graph_evidence` 表與索引（見 2.3 DDL）；實作 `IGraphEvidenceStore`（WriteBatch / ReadByEntity / DeleteByVersion）。**不得動到 PAT 等既有設定表**。
- [ ] **T1.4** `Neo4jGraphStore.cs`：`WriteNodesAsync`/`WriteEdgesAsync` 改寫新屬性集；刪除 evidenceJson/sourceId/targetId 寫入；`SchemaStatements` 維持既有索引並新增 `(graphVersion, communityId)` 複合查詢用 range index；`ValidateStagingAsync` 的「evidence 必須存在」檢查改為「reasonCode 與 weight 必須存在」＋抽樣核對 SQLite evidence 筆數。
- [ ] **T1.5** publish/cleanup 流程整合 evidence store：staging 先寫 SQLite → Neo4j publish 成功 → promote；失敗或 retire 時同步刪除該 version 的 evidence。

### Phase 2 — 抽取器重寫

- [ ] **T2.1** `SqlServerGraphExtractor`：tblMenuMap 查詢加 `Released=1 AND ISNULL(LinkAddress,'')<>'' AND ID NOT LIKE '88%'`；保留 parentMenuId。
- [ ] **T2.2** `SqlServerGraphExtractor`：新增 `TryParsePluginReportTarget`（regex `/PluginReport/MenuIndex/(?<b64>[A-Za-z0-9+/=]+)` → Base64 解碼 → FQN → E1 邊；解碼失敗或格式不符 → 記 diagnostics 不建邊）。
- [ ] **T2.3** `SqlServerGraphExtractor`：SP/TVF/scalar function 全量節點（sys.objects）＋ E6 讀寫邊（sys.sql_expression_dependencies 為主、ScriptDom 補強）；E5 FK 邊（sys.foreign_keys）。
- [ ] **T2.4** `CSharpGraphExtractor`：刪除 `IsLargeRepositoryCallPathFile` marker 過濾器；實作兩階段抽取（全量 syntax-only type+CALLS → EntryPoint 可達集深度補全）；抽 `return View("x")` 產出 view-name 中繼資料（供 T2.5 合併成 E2 邊）。
- [ ] **T2.5** 新增 `AspxGraphExtractor`（新檔案）：掃描 `.aspx`（加入 `GraphIndexingService` 副檔名清單）；建 frontend-page 節點；解析 script include 建 E3 邊；與 T2.4 的 view-name 中繼資料合併建 E2 邊（Views/{Controller}/ → Views/Shared/ 慣例解析）。
- [ ] **T2.6** `FrontendGraphExtractor`：`IsPage` 資料夾清單補單數 `view`/`page`/`screen`；新增 E4 es-import 邊（相對路徑解析至實體檔案，`.ts/.tsx/.js/.jsx/index.*` 補全副檔名）；移除「非 page 且無 URL 即丟棄」規則，改為「有 import 入邊可達者保留」（交由 assembler 孤立清理兜底）。
- [ ] **T2.7** 各 extractor 產出 evidence 時同步產出 `reasonCode`（映射表寫在各 extractor 內，禁止散文以外的新自由文字欄位）。

### Phase 3 — Assembler 與社群

- [ ] **T3.1** `GraphAssembler`：合併重複邊時彙總 `evidenceCount`、取最高 confidence、選代表 topArtifact/topLine；依 2.2.2 公式計算 weight。
- [ ] **T3.2** `GraphAssembler`：計算節點 degree；shared 判定（2.1.4）並寫 `attributes.sharedConsumerCount`；**孤立清理**——刪除 degree==0 的 Code:type 與 frontend module 節點（Feature/Data/EntryPoint 不刪，孤立即為 unresolved 訊號）。
- [ ] **T3.3** `GraphCommunityBuilder` 重寫：C0（菜單樹頂層）→ C1（葉子 Feature 閉包，含 shared 不外擴、depth≤4、weight≥0.7、上限 60）→ C2（未覆蓋節點 Leiden/標籤傳播、<3 成員丟棄）；節點 communityId 回填；輸出帶 tier/parentCommunityId 的 CommunityReport。
- [ ] **T3.4** 社群摘要：模板骨架 + 可選 LLM 潤飾（沿用 V3 快取與降級機制）。

### Phase 4 — 檢索端

- [ ] **T4.1** `GraphRetrievalService`：刪 EdgeWeight 常數表 → 讀邊 weight；BFS shared 不外擴；degree tie-breaker。
- [ ] **T4.2** Global Search 兩層漏斗（C0 → C1/C2）。
- [ ] **T4.3** 影響分析輸出 shared 折疊句；prompt 組裝不再讀 evidenceJson（改由 SourceEvidenceReader 直接讀檔＋必要時查 SQLite evidence）。
- [ ] **T4.4** 溯源 API（visualization/evidence 端點）：evidence 明細改查 SQLite。

### Phase 5 — 索引管線與版本

- [ ] **T5.1** `GraphIndexingService`：副檔名清單加 `.aspx`；`CurrentIndexerVersion` 提升（各 extractor 版本號 +1，確保 V3 圖全部失效重建）。
- [ ] **T5.2** body-delta 增量模式相容性驗證（宣告面比較邏輯不受兩階段抽取影響）。
- [ ] **T5.3** 刪除 V3 遺留：evidenceJson 相關讀寫、marker 清單、舊社群 builder 分支。**grep 確認無 dead code 殘留**。

### Phase 6 — 驗收與測試報告（見 §4、§5）

- [ ] **T6.1** 單元測試（各 extractor 規則、weight 公式、shared 判定、社群閉包）。
- [ ] **T6.2** FBL 全量索引驗收（§4 全部指標）＋ 結構化測試報告產出（§5 格式）。
- [ ] **T6.3** 測試資料清理確認（§4.5）。

---

## 4. 驗收標準（Acceptance Criteria）

> 驗收環境：`D:\FBL_Release_Trunk` ＋ SQL Server `127.0.0.1,3301` / `FBL_SPV_SIT`。
> 驗收前允許 `MATCH (n) DETACH DELETE n` 清空 Neo4j；SQLite 僅保留 PAT 設定，其餘資料表可清空。

### 4.1 圖結構指標（Neo4j Cypher 驗證，全部必須通過）

| # | 指標 | 門檻 | 驗證方式 |
|---|---|---|---|
| A1 | menu-feature 節點數 | = tblMenuMap 過濾 SQL 筆數（誤差 0） | 比對 count |
| A2 | menu-feature 孤立率 | < 5%（V3 為 53%） | degree=0 比例 |
| A3 | Code:type 孤立節點 | = 0（assembler 已清理） | `MATCH (n {kind:'Code'}) WHERE n.degree=0` |
| A4 | Data:procedure＋function 節點數 | ≥ 250（109 SP＋105 TVF＋48 FN，允許系統物件過濾誤差 ±10） | count |
| A5 | FK `DEPENDS_ON` 邊數 | ≥ 440（463 條 FK，允許自參照/跨庫排除） | count by reasonCode |
| A6 | PluginReport 菜單 `DISPATCHES_TO` Code 邊 | ≥ 24（26 個過濾後 plugin 菜單，允許 2 個 unresolved stub） | count |
| A7 | frontend-page 節點數 | ≥ 600（638 個 aspx，允許無效檔排除） | count |
| A8 | E2 `roslyn-view-result` 邊數 | ≥ 550（650 處呼叫去重後） | count by reasonCode |
| A9 | 抽樣鏈路連通：`200298-另類基金基本資料維護(主檔)` → … → `frmalternativefund.js` | 路徑存在且 hop ≤ 4（V3 = 0 條路徑） | 路徑查詢 |
| A10 | 抽樣鏈路連通：`79004-利息收入增減分析` → `InterestIncomeAnlz_ReportKernel` → 任一 Data:table | 路徑存在 | 路徑查詢 |
| A11 | tsx 前端模組入圖數 | ≥ 500（562 個 tsx；shared/孤立清理後仍應大幅高於 V3 的 151） | count |
| A12 | 所有邊都有 weight/confidence/reasonCode | 100% | 缺欄位 count = 0 |
| A13 | Neo4j 邊上不存在 evidenceJson/sourceId/targetId 屬性 | 100% | 屬性存在 count = 0 |
| A14 | DAL 鏈路：`RMDAL` namespace 的 Code 節點數 | ≥ 500（V3 因 marker 全滅） | filePath CONTAINS 'rmdal' |

### 4.2 社群指標

| # | 指標 | 門檻 |
|---|---|---|
| B1 | C0 數量 | 8 ~ 25 |
| B2 | C1 數量 | ≥ 650（每個有效葉子菜單一個，允許閉包為空者跳過） |
| B3 | C1 平均成員數 | 3 ~ 60（不得出現 >60 的社群） |
| B4 | C2 數量 | ≤ 100 且每個成員數 ≥ 3 |
| B5 | 節點 communityId 覆蓋率 | ≥ 90%（Feature/EntryPoint/Code；Data 表允許共享不強制） |
| B6 | 每個 C1 的 parentCommunityId 有效指向 C0 | 100% |

### 4.3 儲存與效能指標

| # | 指標 | 門檻 |
|---|---|---|
| C1 | Neo4j 關係屬性總體積 | 較 V3 (16 MB evidenceJson) 下降 ≥ 80% |
| C2 | SQLite graph_evidence 與 Neo4j entity 對帳 | 抽樣 100 個 entity，evidence 筆數一致率 100% |
| C3 | FBL 全量索引耗時 | ≤ V3 全量耗時 × 1.5（兩階段抽取允許增加，但不得倍增；以同機實測 V3 基線為準） |
| C4 | no-op fast path | 無變更重跑索引 → 不觸發重建（沿用 V3 行為） |
| C5 | Local Search 單次查詢延遲 | P95 ≤ 2 秒（80 節點/120 邊預算不變） |

### 4.4 檢索品質抽測（人工評分，各至少 4/5 通過）

固定五題（繁中提問，走完整 retrieve→prompt 管線，人工判定「定位是否正確」）：

| # | 題目 | 期望定位 |
|---|---|---|
| Q1 | 「另類基金基本資料維護的畫面欄位要加一個欄位，要改哪些檔案？」 | frmAlternativeFund.js（或 .aspx）＋ AlternativeFundController ＋ 相關表 |
| Q2 | 「利息收入增減分析報表數字不對，可能問題在哪？」 | InterestIncomeAnlz_ReportKernel ＋ 其 READS 的表 |
| Q3 | 「tblPosition105 這張表如果加欄位，會影響哪些功能？」 | 沿 READS/WRITES 反向 ＋ FK 波及 ＋ shared 折疊句正確出現 |
| Q4 | 「公告管理的儲存流程改了驗證邏輯，前端要不要跟著改？」 | frmAnnouncement.tsx → component → Announcement/Save_Candidate 鏈路 |
| Q5 | 「排程裡抓 Bloomberg 匯率的功能在哪個模組？」 | C2 社群命中 ＋ scheduled-task 節點 |

### 4.5 清理驗收

| # | 項目 | 門檻 |
|---|---|---|
| D1 | 測試結束後 Neo4j 只保留最後一次成功 publish 的 graphVersion | 舊 version 節點/邊 count = 0 |
| D2 | SQLite 無 retired version 的 evidence 殘留 | count = 0 |
| D3 | SQLite PAT／使用者設定表未被修改 | schema 與內容 diff = 0 |
| D4 | 測試用暫存檔（報告除外）全部刪除 | 人工確認 |

---

## 5. 結構化測試報告格式（T6.2 產出物）

報告落點：`docs/reports/graphrag-v4-acceptance-{yyyyMMdd-HHmm}.md`，格式如下：

```markdown
# GraphRAG V4 驗收報告
- 執行時間 / 執行人 / commit hash / 索引耗時
- 環境：FBL_Release_Trunk commit or snapshot 說明、FBL_SPV_SIT、Neo4j 版本

## 1. 圖結構指標（A1~A14）
| 指標 | 門檻 | 實測值 | 判定 | 備註 |
（每列一項，判定 PASS/FAIL，FAIL 必附原因分析與 Cypher 重現語句）

## 2. 社群指標（B1~B6）
（同上表格式）

## 3. 儲存與效能（C1~C5）
（同上表格式，附 V3 基線數字）

## 4. 檢索品質抽測（Q1~Q5）
（每題附：提問原文、系統回答摘要、人工判定、失分原因）

## 5. 清理驗收（D1~D4）

## 6. 總結
- 通過率 X/Y
- 未通過項的處置決定（修復後重測 / 接受並記錄為已知限制）
- 已知限制清單（WCF/MQ/動態 SQL…）
```

- 指標區（A/B/C/D）由驗收腳本自動產出（腳本放 `scripts/dev/graphrag-v4-acceptance.ps1`，
  以 Cypher over HTTP ＋ sqlcmd ＋ sqlite3 查詢實測值並產 markdown）。
- Q 區人工填寫。
- **全部 PASS 才可合併**；任何 FAIL 需在報告中記錄處置決定並經產品負責人同意。

---

## 6. 風險與已知限制（實作前先知道）

1. **兩階段 Roslyn 的記憶體**：全量 9,638 檔 syntax-only 解析需控制並行度（建議 ≤ CPU 核數）與及早釋放 SyntaxTree；階段一不建 semantic model。
2. **aspx 的 server-side 表達式**：只解析 `JSPath("…")`/`AbsolutePath("…")` 內固定字串；其他動態拼接一律忽略（不建邊、不記 diagnostics 以免噪音）。
3. **View() 無參呼叫**（`return View()` 依 action 名推 view）：本版**不處理**——實測 FBL 慣例以 named view 為主；若驗收 A8 未達標再回頭補此規則。
4. **shared 閾值**（10/10/20）是初值：驗收 Q3 若折疊過度或不足，調 `GraphSharedNodeThresholds` 重跑，不改架構。
5. **C1 成員上限 60**：若驗收 B3 出現大量觸頂，優先調高 weight 門檻（0.7→0.75）而非調高上限。
6. **Neo4j GDS 未安裝時** C2 退回標籤傳播：結果可能與 Leiden 不同，但兩者都須滿足 B4。
