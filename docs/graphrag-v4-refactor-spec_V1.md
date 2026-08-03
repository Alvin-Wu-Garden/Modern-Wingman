# GraphRAG V4 破壞性重構規格書 V1（SPEC + TODO List）

> 狀態：**修訂草案，待產品負責人確認後定案**
> 日期：2026-07-29
> 前身：`modules/GraphRAG` V3 與 `graphrag-v4-refactor-spec.md`
> 本文件在原 V4 SPEC 的實測問題與重構方向上，修正 Roslyn CALLS 精度、
> 社群 weight 門檻、選單階層、Evidence 補載、DB fingerprint、孤立節點、
> Community AI Summary 與精度驗收等缺口。
>
> 確認定案後，本文件才成為 V4 唯一實作依據。V3 未被本文件推翻的行為
> （staging/atomic publish、no-op fast path、DPAPI 憑證、manifest 對帳）
> 一律保留。

---

## 0. 重構動機與 V1 修訂原則

### 0.1 V4 實測問題

以下數據來自 2026-07-29 對 FBL 專案的實際量測
（Neo4j 前次索引圖 version `9c017d…`、SQL Server `FBL_SPV_SIT`、
原始碼 `D:\FBL_Release_Trunk`）：

| # | 問題 | 實測證據 |
|---|---|---|
| P1 | 大型 repo 檔名 marker 過濾器砍掉 DAL 層 | 9,638 個 .cs 只有 3,036 個（31.5%）命中 marker；被排除檔案中 2,283 個屬於 RMDAL/RMQuery/Provider 等資料存取層 |
| P2 | Code:type 節點大量孤立 | 5,096 個 type 節點中 3,600 個（71%）度數為 0 |
| P3 | 菜單雜訊 | 1,013 個 menu-feature 中 539 個（53%）孤立、642 個 inactive；葉子功能過濾可將 1,497 筆縮至 698 筆 |
| P4 | SP 幾乎沒抽到 | 圖中 Data:procedure 只有 16 個；DB 實際有 109 SP + 105 TVF + 48 scalar function |
| P5 | FK 未利用 | DB 有 463 條 FK；圖中 Data→Data DEPENDS_ON 只有 19 條 |
| P6 | 邊 evidence 冗餘 | evidenceJson 平均 717 B/邊、P95 2,864 B、最大 85 KB、全圖 16 MB |
| P7 | 社群不可用 | primary 只有 3 個超大社群；secondary 有 1,930 個碎片 |
| P8 | PluginReport 菜單假連通 | 39 個 plugin 菜單全部 ROUTES_TO 同一個 `PluginReport/MenuIndex`；Base64 payload 未解碼 |
| P9 | .aspx 完全未索引 | 638 個 .aspx 不在掃描副檔名內 |
| P10 | `return View("x")` 未抽 | 650 處 named View 呼叫沒有對應邊 |
| P11 | React tsx 覆蓋不足 | 562 個 tsx 只有 151 個入圖；未辨識單數 `View` 資料夾，也沒有 import 邊 |

### 0.2 已確認的產品決策

- 共用元件在影響分析中折疊為「另影響 N 個共用元件使用者」，不展開明細。
- ExtJS xtype/component hierarchy 不做，維持每檔案一個前端模組節點。
- V4 先建立可靠、可重現的 deterministic retrieval，不導入 Agentic tool loop。
- Community 的 deterministic template 必須在結構索引完成時立即可用；
  AI Summary 是可延後、可降級且不阻塞問答的附加能力。

### 0.3 V1 新增修訂原則

1. **CALLS 的 certain/probable 關係不得只靠 syntax-only 推導。**
2. **Weight 只做排序，不得因單一通用門檻切斷重要跨 namespace 鏈路。**
3. **Menu Feature 過濾與 Menu Hierarchy 取得分開處理。**
4. **完整 Evidence 雖移至 SQLite，但最終問答仍必須批次補載精確方法與行號。**
5. **DB fingerprint 必須涵蓋 V4 新增的 function、FK 與 module dependency。**
6. **已知不支援 WCF/MQ/Reflection 時，不可把所有 degree=0 高價值程式直接刪除。**
7. **驗收除覆蓋率外，必須量測錯誤邊比例。**

---

## 1. 範圍與非目標

### 1.1 範圍

1. 節點抽取規則重寫；四種 kind 不變。
2. 邊 payload 瘦身；完整 evidence 移至 SQLite，Neo4j 保留結構化小欄位與 weight。
3. 新增 ASPX、PluginReport Base64、ES import、FK、SP/function 鏈路。
4. C0/C1/C2 三層階層社群。
5. 檢索端改讀 edge weight、處理 shared、批次補載 evidence。
6. Community deterministic template 立即產生，AI Summary 改為分層與按需。
7. DB fingerprint、增量索引與跨儲存 crash recovery。
8. 覆蓋率、精度、效能、清理與結構化報告驗收。

### 1.2 非目標

- ❌ Method 級節點；方法資訊保留在 Edge 代表欄位與 SQLite Evidence。
- ❌ Embedding／向量檢索；維持 BM25。
- ❌ Agentic 多輪工具迴圈。
- ❌ ExtJS component hierarchy。
- ❌ WCF／RealTimeService／MQ 跨進程呼叫鏈。
- ❌ CFG/DFG、完整變數資料流、TESTS 邊。
- ❌ 為了通過節點或邊數驗收而建立無證據的 synthetic edge。

### 1.3 破壞性授權與資料保護

- Modern Wingman 尚未發佈，允許 V4 不向後相容。
- V4 首次索引全量重建，不寫 V3→V4 graph migration。
- 測試期間只允許清除 GraphRAG 自有的 Neo4j graphVersion 與 SQLite graph tables。
- PAT、OAuth、使用者設定、對話與其他非 GraphRAG 資料表不得清除或改 schema。
- 清理腳本必須使用明確 allowlist，不得使用「除 PAT 外全部刪除」類黑名單做法。

---

## 2. 資料模型規格

### 2.1 節點（GraphNode）

四種 kind 不變：`Feature`、`EntryPoint`、`Code`、`Data`。

#### 2.1.1 共通 Neo4j Node Property

| 屬性 | 型別 | 說明 |
|---|---|---|
| `id` | string | 確定性 ID：`{kind}:{source}:{logicalKey}` |
| `projectId` / `graphVersion` | string | 沿用 V3 |
| `kind` | string | Feature / EntryPoint / Code / Data |
| `role` | string | GraphRoles 白名單 |
| `name` | string | 人類可讀短名稱 |
| `searchableText` | string | BM25 去噪文字 |
| `aliasesText` | string | 別名串接 |
| `language` | string | business / csharp / frontend / sql |
| `state` | string | `active` / `inactive` / `unresolved` / `shared` |
| `filePath` / `startLine` / `endLine` | string?/int? | 主要原始碼位置 |
| `attributesJson` | string | 有界結構化屬性 |
| `degree` | int | Assembler 計算總度數 |
| `communityId` | string? | 主要 C1 或 C2 社群 ID |
| `evidenceRef` | string | SQLite 查詢鍵；Node 固定等於 node id |

#### 2.1.2 Feature 抽取規則

| role | 來源 | 規則 |
|---|---|---|
| `menu-feature` | tblMenuMap | 只建立可執行葉子功能：`Released=1 AND ISNULL(LinkAddress,'')<>'' AND ID NOT LIKE '88%'` |
| `custom-report` | report template + 菜單 regex | 沿用 V3 |
| `approval-feature` / `schedule` / `batch-report` | live DB | 沿用 V3 |

Menu hierarchy 必須另外查詢：

```sql
SELECT ID, Parent, Name, Released, LinkAddress
FROM dbo.tblMenuMap
WHERE Released = 1 AND ID NOT LIKE '88%';
```

Hierarchy rows 只用來建立 C0/C1 parent mapping；`LinkAddress` 為空的父選單不一定建立 Feature Node，
但不得因 Feature 過濾條件而消失。

#### 2.1.3 EntryPoint 抽取規則

| role | 來源 | 規則 |
|---|---|---|
| `controller-action` | Roslyn | 保留被 Feature 指向、有可解析 Route、被 frontend-url 指向，或有已證實 HANDLES/CALLS 鏈的 public action |
| `frontend-page` | AspxGraphExtractor | 對所有未排除目錄內的 `.aspx` 建立節點，不限定只能位於 `Views/**` |
| `scheduled-task` | Roslyn TaskName 常值 | 沿用 V3 |

未被 Feature 指向、但有公開 Route 的 action 不刪除，改標記：

```text
state = unresolved
```

其餘完全沒有 route、feature、frontend 或 call evidence 的 action，可收進 Controller
`attributes.actions` 有界清單，不建立獨立節點。

`frontend-page.attributes` 至少包含：

```text
viewName
controllerFolder
includedScripts
```

#### 2.1.4 Code 抽取規則

##### C# Type：三階段 Roslyn

**階段一：全量 Syntax Inventory**

- 掃描全部支援的 `.cs` 檔。
- 建立 type declaration inventory、namespace、base type syntax、public route/action candidate、
  invocation candidate。
- 不建立 `confidence=certain` 的跨 Type CALLS。
- syntax-only 無法唯一解析接收者時，不得猜測 target type。

**階段二：Semantic CALLS Resolution**

- 依可解析的 project/compilation boundary 建立 Roslyn Compilation。
- 對 invocation candidate 按需取得 SemanticModel。
- 只有 Symbol resolution 唯一成功時，建立 `confidence=certain` 的 CALLS。
- 因缺少 reference 只能唯一對應至 repository 內單一 type 時，可建立
  `confidence=probable`，reasonCode 必須為 `naming-convention`。
- 多候選、dynamic、reflection 或無法解析時不建 CALLS，寫入有界 diagnostics。
- CALLS 抽取不得因 namespace 不同而降低可信度。

**階段三：Reachable Deep Attributes**

- 從 EntryPoint、Feature-linked Controller、scheduled-task、report-plugin 與高價值 repository root 出發。
- 沿 HANDLES/CALLS 展開可達 Type。
- 只對可達 Type 補完整 methods/baseTypes/constructorDependencies。
- attributes 仍維持有界清單，不能存整份原始碼。

##### degree=0 清理

可刪除：

- generic type；
- DTO/value object；
- 無業務角色且無任何關係的 frontend module；
- generated code。

不得只因 degree=0 刪除：

- controller；
- business-service；
- repository；
- report-plugin；
- scheduled-task 相關 Type；
- 可搜尋名稱明確且有 source evidence 的高價值程式。

上述高價值孤立節點保留並標記 `state=unresolved`，BM25 排序時降權。

##### Frontend Module

- `IsPage` 資料夾辨識包含單複數：`view/views/page/pages/screen/screens`。
- 解析相對路徑 ES import。
- npm package import 忽略。
- 可由 page/module import 鏈到達的 component 必須入圖。
- 無 import、URL、page 或其他關係的 frontend module 由 Assembler 孤立清理。

#### 2.1.5 Data 抽取規則

| role | 來源 | 規則 |
|---|---|---|
| `table` / `view` | sys catalog | 沿用 V3 |
| `procedure` / `function` | sys.objects | type IN (`P`,`FN`,`TF`,`IF`) 全量建立 |
| 其餘 report-template/csv-format/custom-enum 等 | live DB | 沿用 V3 |

`.sql` 檔只補 filePath、行號與 source evidence；Repository 沒有 `.sql` 不得造成 DB object 缺節點。

#### 2.1.6 新增 ID 規則

```text
entry:page:{normalizedRelativePath}
data:sql:{db}/{schema}/function/{name}
```

路徑必須：

- 使用 `/`；
- 大小寫正規化規則固定；
- 不包含絕對路徑；
- 跨索引執行穩定。

#### 2.1.7 `state=shared`

初始判定門檻：

- Code(frontend module)：HANDLES/CALLS 不重複來源數 ≥ 10。
- EntryPoint：ROUTES_TO 不重複 Feature 來源數 ≥ 10。
- Code(csharp type)：CALLS 不重複來源 Type 數 ≥ 20。

門檻集中於 `GraphSharedNodeThresholds`。

行為：

1. C1 與 Local Search 可收錄 shared 節點。
2. 收錄後不得以 shared 節點作為下一層展開中介。
3. shared 不透過 Edge weight 降低到無法被收錄。
4. `attributes.sharedConsumerCount` 保存不重複入邊來源數。
5. 影響分析輸出折疊句，不展開全部 consumer。

### 2.2 邊（GraphEdge）

九種 kind 不變：

```text
ROUTES_TO
HANDLES
CALLS
DISPATCHES_TO
TRIGGERS
READS
WRITES
MAPS_TO
DEPENDS_ON
```

#### 2.2.1 Neo4j Relationship Property

| 屬性 | 型別 | 說明 |
|---|---|---|
| `id` | string | SHA-256(source\0kind\0target) |
| `graphVersion` | string | graph version |
| `weight` | float | 索引時預計算，只供排序 |
| `confidence` | string | certain / probable / inferred |
| `evidenceCount` | int | 去重後 evidence 數 |
| `reasonCode` | string | 代表性 reason code |
| `topArtifact` | string | 代表性檔案或 DB logical key |
| `topLine` | int? | 代表性行號 |
| `sourceMethod` / `targetMethod` | string? | 代表性 method pair，不代表完整清單 |

Neo4j relationship 不再存：

```text
evidenceJson
sourceId property
targetId property
```

Relationship 的端點仍由 Neo4j topology 表示；GraphEdge 記憶體模型仍保留 SourceId/TargetId。

完整方法配對、其他 artifact 與 evidence 由 SQLite 保存。檢索選中 Edge 後必須一次批次補載，
不能把 `sourceMethod/targetMethod` 當成唯一呼叫。

#### 2.2.2 Weight 計算

```text
weight = clamp(
    base(kind) × confidenceFactor × evidenceBonus,
    0.05,
    1.0)

base:
ROUTES_TO      1.00
HANDLES        0.95
TRIGGERS       0.95
DISPATCHES_TO  0.90
WRITES         0.90
READS          0.85
MAPS_TO        0.85
CALLS          0.75
DEPENDS_ON     0.70

confidenceFactor:
certain  1.00
probable 0.90
inferred 0.75

evidenceBonus:
去重後 evidenceCount >= 3 → 1.05
否則 → 1.00
```

明確刪除：

- `crossNamespaceFactor`：跨 namespace 往往是最重要的 Controller→Service→DAL 鏈路。
- `sharedTargetFactor`：shared 由 traversal 規則控制，不得讓節點無法進入結果。

Weight 不代表事實真偽。是否建立 Edge 由 extractor evidence 與 confidence 決定。

#### 2.2.3 Confidence 彙總

同一 Edge 多份 evidence 合併時：

- certain evidence 存在，代表 confidence = certain；
- 否則 probable 存在，代表 probable；
- 否則 inferred；
- evidenceCount 必須先依 source/artifact/line/reasonCode 去重；
- evidenceBonus 不得由重複掃描同一位置灌高。

#### 2.2.4 ReasonCode

封閉清單：

```text
roslyn-invocation
roslyn-route
roslyn-view-result
roslyn-task-name
scriptdom-read
scriptdom-write
sys-dependency
fk-constraint
menu-link
menu-link-base64
aspx-script-include
es-import
frontend-url
db-metadata
naming-convention
```

所有既有 extractor 關係必須在實作前完成映射表。未映射 reasonCode 的 Edge 不得 publish，
應成為 validation error；禁止用自由文字 reasonCode 規避白名單。

#### 2.2.5 新增／修訂邊規則

| # | 邊 | 方向 | 規則 | reasonCode |
|---|---|---|---|---|
| E1 | DISPATCHES_TO | Feature(menu) → Code(csharp) | 解析 `/PluginReport/MenuIndex/{base64}`，Base64 解碼 `{dll}/{FQN}`；不存在時建立 unresolved stub | menu-link-base64 |
| E2 | DISPATCHES_TO | EntryPoint(action) → EntryPoint(page) | 解析 `View("name")` 與 `View("name", model)`；依 Controller folder、Shared 慣例解析 | roslyn-view-result |
| E3 | HANDLES/ROUTES_TO | EntryPoint(page) → Code(frontend) 或 action | 解析 `JSPath`、`AbsolutePath` 固定字串；動態 GetUIData 只連到 action | aspx-script-include |
| E4 | CALLS | Code(frontend) → Code(frontend) | 相對 ES import；補 `.ts/.tsx/.js/.jsx/index.*`；npm import 忽略 | es-import |
| E5 | DEPENDS_ON | Data(child table) → Data(parent table) | sys.foreign_keys，全量且去重 | fk-constraint |
| E6 | READS/WRITES | Data(module) → Data(table) | sys dependency 產生候選；sys.sql_modules.definition + ScriptDom 判讀讀寫 | sys-dependency / scriptdom-* |
| E7 | CALLS | Code → Code | SemanticModel 唯一解析為 certain；唯一 repository heuristic 為 probable | roslyn-invocation / naming-convention |

##### E6 詳細規則

1. `sys.objects` 與 `sys.sql_expression_dependencies` 找出 object 與依賴候選。
2. 讀取 `sys.sql_modules.definition`，以 ScriptDom 解析 SELECT/INSERT/UPDATE/DELETE/MERGE/EXEC。
3. Repository `.sql` 存在時補 source 行號與交叉驗證。
4. ScriptDom 證實寫入時建立 WRITES。
5. 只證實讀取時建立 READS。
6. dependency 存在但 definition 無法解析時：
   - 不可標成 certain；
   - 可建立 probable DEPENDS_ON，或在已知只讀語境建立 probable READS；
   - diagnostics 必須記錄分類缺口。
7. dynamic SQL、跨 DB、synonym 與 temp table 在回答中列為已知限制。

### 2.3 SQLite Evidence Store

```sql
CREATE TABLE IF NOT EXISTS graph_evidence (
    project_id    TEXT NOT NULL,
    graph_version TEXT NOT NULL,
    entity_id     TEXT NOT NULL,
    entity_type   TEXT NOT NULL, -- node | edge
    seq           INTEGER NOT NULL,
    source        TEXT NOT NULL,
    confidence    TEXT NOT NULL,
    artifact      TEXT NOT NULL,
    reason        TEXT NOT NULL,
    reason_code   TEXT NOT NULL,
    start_line    INTEGER,
    end_line      INTEGER,
    details_json  TEXT,
    PRIMARY KEY (project_id, graph_version, entity_id, seq)
);

CREATE INDEX IF NOT EXISTS ix_graph_evidence_lookup
ON graph_evidence (project_id, graph_version, entity_id);
```

規則：

- 每 entity evidence 上限 40 筆。
- WriteBatch 必須使用單一 SQLite transaction 與 prepared statement。
- ReadByEntities 必須支援一次查多個 entity id，供 Context Compiler 批次 hydration。
- 不允許逐 Node/Edge N+1 查詢。
- evidence details 保存完整 method pairs；Neo4j top method 只作快速預覽。

#### 2.3.1 跨 Neo4j／SQLite Publish

SQLite 與 Neo4j 無法形成真正 distributed transaction，流程定義為：

```text
1. 建立 graphVersion
2. SQLite transaction 寫入該 version evidence
3. Neo4j 寫 staging graph
4. 驗證 Neo4j counts/weight/reasonCode
5. 批次抽樣對帳 SQLite evidence
6. Promote Neo4j active manifest
7. Promote SQLite manifest
8. 清理 retired graph/evidence
```

失敗處理：

- 步驟 2～6 失敗：刪除該 staging graphVersion 的 Neo4j 與 SQLite evidence。
- Process crash：Host 啟動時由 manifest reconciliation 找出非 active orphan version 並清理。
- Active manifest 永遠只指向已通過雙儲存驗證的版本。
- Cleanup 必須以 projectId + graphVersion 精確限定。

### 2.4 CommunityReport

#### 2.4.1 三層社群

| tier | 來源 | 預期數量 | 用途 |
|---|---|---|---|
| C0 | Released menu hierarchy 的頂層模組，不要求 LinkAddress | 8～25 | Global Search 第一層漏斗 |
| C1 | 可執行葉子 Feature + custom-report/approval/schedule | ≥650 | 功能定位與 Local Search anchor |
| C2 | 未被 C1 覆蓋的節點，Leiden 或 deterministic label propagation | ≤100 | 排程、批次、非菜單模組 |

#### 2.4.2 C1 閉包

- 最大 depth = 4。
- 最大 member = 60。
- 允許關係：
  `ROUTES_TO/TRIGGERS/DISPATCHES_TO/HANDLES/CALLS/READS/WRITES`。
- 非 CALLS Edge：`weight >= 0.70`。
- CALLS Edge：`weight >= 0.60` 且 `confidence != inferred`。
- shared 節點即使 weight 低於一般門檻，也可由已驗證 direct edge 收錄一次，然後停止外擴。
- 不允許透過 shared node 把多個業務社群串成超大社群。
- member 觸頂時記錄 `truncated=true`，不得靜默截斷。

#### 2.4.3 C2

- 只處理未被 C1 覆蓋且非應清理孤立節點。
- GDS Leiden 可用時使用 Leiden。
- GDS 不可用時使用 deterministic label propagation。
- memberCount < 3 的 C2 丟棄，但其成員仍保留為可搜尋 unresolved node。

#### 2.4.4 Community Property

| 屬性 | 說明 |
|---|---|
| `communityId` | 穩定 ID |
| `tier` | C0 / C1 / C2 |
| `parentCommunityId` | C1 指向 C0 |
| `title` / `summary` | deterministic template；可被 AI 版本覆蓋 |
| `summaryState` | template / queued / ai-ready / failed |
| `memberIdsJson` / `memberCount` | 有界成員資料 |
| `topTables` / `topEntryPoints` | 各最多 5 個 |
| `cacheKey` | member digest + prompt version |

節點 `communityId` 指向主要 C1/C2。多 C1 時取距離最近者；同距離依 communityId
排序確保 deterministic。其他 membership 記於 `attributes.alsoInCommunities`，最多 5 個。

#### 2.4.5 Summary 產生策略

結構索引完成時：

- C0/C1/C2 全部立即建立 deterministic template。
- Graph 立即可問答。
- 不等待任何 LLM。

背景 AI：

- C0 自動排入背景潤飾。
- C1 第一次被 Global/Local Search 命中時按需排入；可另對高使用率 C1 預熱。
- C2 預設只使用 template，除非使用者問題命中後按需排入。
- 同一 cacheKey 不重複生成。
- AI 失敗保留 template，索引狀態仍為可用。
- UI 進度分開顯示「結構索引可用」與「AI 摘要背景補充中」。

---

## 3. 檢索端規格

### 3.1 Local Search

維持 V3 預算：

```text
SeedLimit = 12
MaximumNodes = 80
MaximumEdges = 120
MaximumDepth = 3
NeighborsPerNode = 50
```

修改：

1. Edge score 讀 Neo4j `weight`。
2. shared node 收錄、不外擴。
3. Seed tie-breaker 順序：
   - BM25 分數；
   - 問題 intent 的 role relevance；
   - non-shared 優先；
   - exact name/alias match；
   - 最後才使用 degree，且只在相同 state/role 內比較。
4. degree 不得讓共用 helper/hub 壓過精確業務名稱。
5. selected node/edge 決定後，一次呼叫 `ReadByEntities` 批次取得 SQLite evidence。
6. SourceEvidenceReader 讀取實際檔案；SQLite evidence 提供 method、artifact、line 與理由。
7. Context Compiler 必須保留：
   - certain/probable/inferred 分區；
   - 完整 method pair 的有界顯示；
   - source 讀取成功與否；
   - 未支援動態路徑聲明。

### 3.2 AnalyzeImpact

- shared node 輸出折疊句。
- 不列出 shared node 的全部 consumer。
- N 使用不重複 `sharedConsumerCount`。
- 直接影響與間接影響依 hop 分區。
- probable/inferred 路徑不得和 certain 路徑混為「已確認」。
- Table/Column 類問題必須提示 dynamic SQL、外部報表與 trigger 索引缺口。

### 3.3 Global Search

兩層漏斗：

```text
問題
→ Neo4j indexed C0 term scoring，選 1～2 個模組
→ 只在其 C1 children + 全部 C2 候選中比對
→ 取得 deterministic/AI summary
```

不得每次呼叫 `ListCommunityReportsAsync` 把全部 650+ C1 載入記憶體後排序。
Neo4j 應新增可依 graphVersion/tier/parentCommunityId 查詢的 index。

### 3.4 Evidence Hydration

```text
Graph Retrieval
→ 選定最多 80 nodes / 120 edges
→ 蒐集 entity ids
→ 一次 SQLite batch query
→ 合併 top property + full evidence
→ SourceEvidenceReader
→ Context Compiler
```

Evidence hydration timeout 時：

- Graph 結構仍可回答；
- 明確標示「完整溯源 evidence 暫時不可用」；
- 不得把代表性 top method 說成完整方法清單。

---

## 4. 索引與增量更新

### 4.1 支援副檔名

```text
.cs .java .js .jsx .ts .tsx .sql .aspx
```

加入 `.aspx` 時必須同步修改：

- GraphIndexingService SupportedExtensions；
- FileSystemWatcher extension filter；
- LanguageForExtension（aspx → frontend）；
- Artifact manifest；
- source fallback safe text extension；
- extractor DI registration；
- extractor/indexer version。

### 4.2 DB Fingerprint

Fingerprint 至少涵蓋：

- sys.objects：P/V/FN/TF/IF 的 schema/name/type/modify_date；
- sys.foreign_keys：FK name、parent/referenced object、modify_date；
- sys.sql_expression_dependencies 的穩定 dependency key；
- sys.sql_modules definition hash 或 modify_date；
- menu hierarchy 與可執行 Feature 過濾結果；
- approval、schedule、batch、report、CSV 等既有 metadata。

任何 fingerprint 子查詢失敗：

- 本次禁止 no-op；
- manifest 標示 Partial 或 Stale；
- 不可沿用「資料庫未變」假設。

### 4.3 Body Delta

V4 body-delta 仍可保留，前提：

- C# method body 變更時重算完整 C# graph，不只重算單檔。
- 非 C#、ASPX、DB fingerprint、route、signature、extractor/schema 變更一律 full。
- V4 extractor ID 不得繼續硬編碼 `csharp-roslyn-v3`。
- C# CALLS、degree、shared、community 必須重新組裝。
- E2 action→page 使用 deterministic page ID，使既有 ASPX node 可與新 C# edge 合併。
- 必須有 body-delta 與 clean full snapshot digest 相同的測試。

### 4.4 效能與記憶體

- C# Parse 平行度不超過 CPU 核數且上限可設定。
- 不可同時持有不再使用的 source text、SyntaxTree、SemanticModel 大型集合。
- Compilation 依 project boundary 處理並及早釋放。
- 每階段記錄耗時、檔案數、節點數、邊數與 peak working set。
- SQLite evidence 批次 transaction。
- Neo4j node/edge batch 大小沿用可調設定。

---

## 5. 實作 TODO List

### Phase 0 — 基線與保護

- [ ] **T0.1** 保存 V3 FBL 全量索引基線：commit/snapshot、硬體、冷/熱機、總耗時、各 extractor 耗時、peak RAM、node/edge/evidence 大小。
- [ ] **T0.2** 建立 GraphRAG SQLite table allowlist 與非 GraphRAG 設定表 hash/row-count 保護。
- [ ] **T0.3** 固定 edge precision 抽樣工具與 Q1～Q5 query fixture。

### Phase 1 — 資料模型與 Evidence Store

- [ ] **T1.1** GraphEdge 新增 Weight/Confidence/EvidenceCount/ReasonCode/TopArtifact/TopLine/SourceMethod/TargetMethod。
- [ ] **T1.2** GraphNode 新增 Degree/CommunityId；新增 GraphRoles.Function、GraphSharedNodeThresholds、ReasonCode 常數。
- [ ] **T1.3** GraphIdentity 新增 FrontendPageEntry 與 SqlFunction。
- [ ] **T1.4** SQLite 新增 graph_evidence；實作 `IGraphEvidenceStore.WriteBatch/ReadByEntities/DeleteByVersion`。
- [ ] **T1.5** Neo4j 改寫 node/edge property；移除 evidenceJson/sourceId/targetId relationship property。
- [ ] **T1.6** 新增 `(graphVersion, communityId)`、`(graphVersion, tier, parentCommunityId)` 查詢 index。
- [ ] **T1.7** Publish 整合 SQLite staging、抽樣對帳、失敗清理與 Host 啟動 orphan reconciliation。
- [ ] **T1.8** GraphAnswerContext 改為批次 evidence hydration；不得產生 N+1 SQLite query。

### Phase 2 — 抽取器

- [ ] **T2.1** Menu Feature leaf filter 與 Menu Hierarchy query 分離。
- [ ] **T2.2** PluginReport Base64 dispatch；解碼失敗只記 diagnostics。
- [ ] **T2.3** sys.objects 全量 SP/function；sys.foreign_keys；sys dependencies。
- [ ] **T2.4** 讀取 sys.sql_modules.definition 並以 ScriptDom 判讀 READS/WRITES。
- [ ] **T2.5** CSharp extractor 移除 marker；實作 syntax inventory、semantic CALLS、reachable deep attributes。
- [ ] **T2.6** 抽取 named View metadata； unresolved/ambiguous 不建 E2。
- [ ] **T2.7** 新增 AspxGraphExtractor，掃描全部合法 `.aspx`，解析 script include 與固定 route。
- [ ] **T2.8** GraphRAGModule 註冊 AspxGraphExtractor；SupportedExtensions/Watcher/Language mapping 同步更新。
- [ ] **T2.9** Frontend extractor 支援單複數 page/view/screen 與相對 ES import。
- [ ] **T2.10** 所有 extractor 完成 reasonCode 映射；未映射 publish fail。

### Phase 3 — Assembler 與 Community

- [ ] **T3.1** Edge 合併：evidence 去重、confidence 彙總、代表 method/artifact、weight。
- [ ] **T3.2** Degree/shared 計算；只清理低價值孤立節點，高價值孤立標 unresolved。
- [ ] **T3.3** C0 使用完整 Released hierarchy；C1 使用 executable Feature。
- [ ] **T3.4** C1 relation-aware threshold、shared 收錄後停止、member cap/truncated。
- [ ] **T3.5** C2 Leiden/fallback；小社群丟棄但節點保留搜尋。
- [ ] **T3.6** communityId deterministic 回填與 alsoInCommunities。
- [ ] **T3.7** 所有 tier 立即產生 deterministic template。
- [ ] **T3.8** AI Summary 改為 C0 自動、C1/C2 按需、cacheKey 去重與降級。

### Phase 4 — 檢索

- [ ] **T4.1** EdgeWeight hardcode 改讀 edge.weight。
- [ ] **T4.2** Local BFS shared 不外擴；seed tie-breaker 防 hub 偏誤。
- [ ] **T4.3** AnalyzeImpact shared 折疊與 confidence 分區。
- [ ] **T4.4** Global Search 改 Neo4j C0→C1/C2 server-side 漏斗。
- [ ] **T4.5** selected entity evidence 單次 batch hydrate。
- [ ] **T4.6** visualization/evidence API 改查 SQLite。

### Phase 5 — Fingerprint、版本與增量

- [ ] **T5.1** CurrentIndexerVersion/SchemaVersion/extractor IDs 提升至 V4。
- [ ] **T5.2** DB fingerprint 加 P/V/FN/TF/IF、FK、dependency、module definition。
- [ ] **T5.3** body-delta hardcoded extractor ID 修正，並驗證與 clean full digest 相同。
- [ ] **T5.4** `.aspx` source manifest、watcher 與 fallback 相容。
- [ ] **T5.5** 刪除 marker、evidenceJson、舊 community builder 與 dead code。

### Phase 6 — 測試與報告

- [ ] **T6.1** Extractor、weight、shared、C1 closure、evidence hydration、crash reconciliation 單元測試。
- [ ] **T6.2** Full/no-op/body-delta/publish failure 整合測試。
- [ ] **T6.3** FBL 全量索引與 A/B/C/D 指標。
- [ ] **T6.4** A15 edge precision 抽樣。
- [ ] **T6.5** Q1～Q5 完整 retrieve→hydrate→source→prompt 人工驗收。
- [ ] **T6.6** 產生結構化驗收報告並完成清理。

---

## 6. 驗收標準

### 6.1 圖結構與覆蓋率

| # | 指標 | 門檻 |
|---|---|---|
| A1 | menu-feature 節點數 | 等於 executable leaf filter SQL 筆數 |
| A2 | active/resolved menu-feature 孤立率 | <5%；unresolved 另列，不得建假邊降低孤立率 |
| A3 | 低價值 generic Code degree=0 | 0；保留的高價值 degree=0 必須 state=unresolved 並列報表 |
| A4 | procedure + function 節點 | ≥250 |
| A5 | FK DEPENDS_ON | ≥440 |
| A6 | PluginReport DISPATCHES_TO | ≥24，stub 必須 unresolved |
| A7 | frontend-page | ≥600 |
| A8 | named View 連通率 | ≥預先盤點「存在且可解析 unique action/view pair」的 90%；目標值約 ≥550 |
| A9 | 另類基金 Feature→page→frontend module | hop≤4 且路徑存在 |
| A10 | 利息收入報表→ReportKernel→Data | 路徑存在 |
| A11 | TSX reachable module | ≥預先盤點可由相對 import 解析 baseline 的 90%；目標值約 ≥500 |
| A12 | 所有邊有 weight/confidence/reasonCode | 100% |
| A13 | Neo4j 無 evidenceJson/sourceId/targetId relationship property | 100% |
| A14 | RMDAL namespace Code | ≥500，且抽樣可連至 CALLS 或 Data relation |
| A15 | Edge precision | certain 隨機抽樣正確率 ≥95%；probable ≥85% |

A15 抽樣至少 100 條，覆蓋：

```text
CALLS
READS
WRITES
ROUTES_TO
DISPATCHES_TO
```

每筆記錄 source、target、reasonCode、artifact、人工判定與重現方式。

### 6.2 社群

| # | 指標 | 門檻 |
|---|---|---|
| B1 | C0 數量 | 8～25 |
| B2 | C1 數量 | ≥650 |
| B3 | C1 member | 平均 3～60；單一不得 >60；truncated 必須可見 |
| B4 | C2 | ≤100 且每個 member≥3 |
| B5 | Feature/EntryPoint/connected Code communityId 覆蓋率 | ≥90% |
| B6 | C1 parentCommunityId 指向有效 C0 | 100% |
| B7 | shared 不形成跨功能超大社群 | 抽樣 shared node，不得被用作 C1 外擴中介 |

### 6.3 儲存與效能

| # | 指標 | 門檻 |
|---|---|---|
| C1 | Neo4j relationship property 體積 | 較 V3 evidenceJson 下降 ≥80% |
| C2 | SQLite/Neo4j evidence 對帳 | 抽樣 100 entity 一致率 100% |
| C3 | FBL full index | ≤同機同條件 V3 ×1.5；報告必附 V3 秒數與 peak RAM |
| C4 | no-op | 無變更不重建 |
| C5 | Local Search P95 | 固定 Q1～Q5 + 15 題擴充集，warmup 5 次後各跑 10 次，P95≤2 秒 |
| C6 | Evidence hydration | 單次 batch；不得出現 selected entity 數量等比例 SQL query 次數 |
| C7 | Community 結構可用時間 | Graph publish 後立即可用，不等待 AI Summary |
| C8 | AI Summary | 失敗或排隊不得把 Project index 狀態改成 unavailable |

### 6.4 問答品質

固定題目：

| # | 題目 | 期望定位 |
|---|---|---|
| Q1 | 另類基金基本資料維護的畫面欄位要加一個欄位，要改哪些檔案？ | ASPX/JS + Controller + 相關 Data |
| Q2 | 利息收入增減分析報表數字不對，可能問題在哪？ | ReportKernel + READS/WRITES evidence |
| Q3 | tblPosition105 加欄位會影響哪些功能？ | reverse READS/WRITES + FK + shared 折疊 |
| Q4 | 公告管理儲存流程改驗證，前端要不要跟著改？ | tsx→component→Controller/Action |
| Q5 | 排程抓 Bloomberg 匯率在哪個模組？ | C2 + scheduled-task |

每題至少記錄：

- 命中的 Seed；
- Graph path；
- SQLite evidence；
- 實際 source snippet；
- confirmed/probable/inferred；
- 缺口；
- 人工 1～5 分。

五題至少 4 題達 4 分以上，且不得有錯誤檔案／方法被宣稱為 certain。

### 6.5 清理與資料安全

| # | 項目 | 門檻 |
|---|---|---|
| D1 | Neo4j | 只保留最後成功 active graphVersion |
| D2 | SQLite evidence | 無 retired/orphan version |
| D3 | 非 GraphRAG 設定表 | schema/row count/hash 未改變 |
| D4 | 暫存檔 | 報告以外全部清除 |
| D5 | Publish failure | 舊 active graph 仍可查，staging/evidence 可自動回收 |

---

## 7. 結構化測試報告

報告：

```text
docs/reports/graphrag-v4-acceptance-{yyyyMMdd-HHmm}.md
```

必須包含：

```markdown
# GraphRAG V4 驗收報告
- 執行時間 / 執行人 / commit hash
- FBL snapshot / DB / Neo4j / SQLite
- CPU / RAM / 冷熱機條件
- V3 baseline 與 V4 實測

## 1. 圖結構與覆蓋率（A1～A14）
## 2. Edge Precision（A15）
## 3. 社群（B1～B7）
## 4. 儲存與效能（C1～C8）
## 5. 問答品質（Q1～Q5）
## 6. 清理與安全（D1～D5）
## 7. 未通過項與處置
## 8. 已知限制
```

自動化腳本：

```text
scripts/dev/graphrag-v4-acceptance.ps1
```

腳本只能：

- Cypher over HTTP 讀取；
- SQL Server 唯讀查詢；
- SQLite 唯讀驗證；
- 清理時只使用 GraphRAG allowlist。

任何 FAIL 都必須附重現查詢與處置決定。除明確由產品負責人接受的已知限制外，
A/B/C/D 必須全數通過才可合併。

---

## 8. 風險與已知限制

1. **Roslyn 記憶體與時間**  
   9,638 檔全量 inventory 會增加解析成本。跨 Type certain CALLS 又需要 SemanticModel，
   因此必須依 project boundary、按需取得並記錄 peak memory；不能以 syntax 猜測換速度。

2. **雙儲存一致性**  
   SQLite 與 Neo4j 沒有 distributed transaction，必須依 staging version、manifest reconciliation
   與 orphan cleanup 達成最終一致。

3. **Evidence query latency**  
   完整 evidence 移 SQLite 後，若逐 Edge 查詢會造成 N+1。只允許 batch hydration。

4. **ASPX 動態 server expression**  
   只解析固定字串 `JSPath`／`AbsolutePath`。動態拼接忽略，不建立猜測邊。

5. **`View()` 無參呼叫**  
   V4 初版仍不處理；A8 baseline 若顯示 named View 不足，再另案加入 action-name convention。

6. **SQL dynamic/cross-database/synonym**  
   sys dependency 與 ScriptDom 都可能漏掉，回答必須聲明限制。

7. **shared 門檻**  
   10/10/20 為初值，只能透過驗收 Q3 與 B7 調整常數，不改架構。

8. **Community member cap**  
   大量觸頂時先檢查錯誤 Edge 與 relation-specific threshold，不直接提高 60。

9. **AI Summary 背景速度**  
   AI 不再對 650+ C1 全量自動執行。Template 永遠先可用，C1/C2 按需。

10. **非目標呼叫鏈**  
    WCF、MQ、RealTimeService、Reflection、dynamic dispatch 仍可能讓高價值節點 unresolved，
    因此不得把 degree=0 當成「沒有業務價值」的充分條件。

---

## 9. 定案條件

本 V1 SPEC 在以下項目獲得確認後才改為「已定案」：

1. C# CALLS 採 semantic resolution，不接受 syntax-only certain edge。
2. Weight 公式移除 crossNamespace/sharedTarget penalty。
3. Menu hierarchy 與 executable Feature 分離。
4. SQLite Evidence 採 batch hydration 與 crash reconciliation。
5. DB fingerprint 涵蓋 V4 新增 metadata。
6. Community AI Summary 採 C0 自動、C1/C2 按需。
7. A15 Edge Precision 納入強制驗收。

定案後才開始 Phase 1 實作；若未定案，不得以節點數驗收取代關係精度驗收。
