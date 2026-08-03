# GraphRAG V4 破壞性重構規格書 V1.2.1（定案候選）

> 狀態：**定案候選，待產品負責人確認後供實作使用**
> 版本：V1.2.1
> 日期：2026-07-30
> 前身：`graphrag-v4-refactor-spec_V1.2.md`
>
> **版本歷史**
>
> | 版本 | 日期 | 說明 |
> |---|---|---|
> | V4 草稿 | 2026-07-29 | 依 FBL V3 實測問題提出破壞性重構 |
> | V1 | 2026-07-29 | 修正 Semantic CALLS、weight、menu hierarchy、孤立節點、Evidence、AI Summary 與 Edge Precision |
> | V1.1 | 2026-07-29 | 加入 Synthetic Compilation、invocation-digest、記憶體降級與 2.5× 效能上限 |
> | V1.2 | 2026-07-29 | 修正 stale graph、MSBuild 權威順序、條件編譯、雙儲存 promote、Recall 與分級效能門檻 |
> | **V1.2.1（本版）** | 2026-07-30 | 統一 Compilation Auto 流程、DB failure gate、雙儲存狀態機、previous version 生命週期、搜尋 seed、Community Top-K、AI Summary 進度及正規化效能驗收 |
>
> 本文件確認後為 GraphRAG V4 唯一實作依據。
> V3 未被本文件推翻的行為（staging/atomic publish、no-op fast path、
> DPAPI 憑證、manifest 對帳）一律保留。

---

## 0. 重構動機與定案決策

### 0.1 FBL V3 實測問題

量測環境：

```text
Neo4j graphVersion: 9c017d…
SQL Server: FBL_SPV_SIT
Source: D:\FBL_Release_Trunk
Date: 2026-07-29
```

| # | 問題 | 實測證據 |
|---|---|---|
| P1 | 大型 repo marker 過濾砍掉 DAL | 9,638 個 .cs 只有 3,036 個入選；2,283 個 RMDAL/RMQuery/Provider 被排除 |
| P2 | Code:type 大量孤立 | 5,096 個 type 中 3,600 個 degree=0 |
| P3 | Menu Feature 雜訊 | 1,013 個 menu-feature 中 539 個孤立、642 個 inactive；可執行 Menu Item 約 698 筆 |
| P4 | SP/function 覆蓋不足 | Graph 僅 16 個 procedure；DB 有 109 SP + 105 TVF + 48 scalar function |
| P5 | FK 未利用 | DB 463 條 FK；Graph Data→Data DEPENDS_ON 僅 19 條 |
| P6 | Neo4j Evidence 過重 | relationship evidenceJson 共約 16 MB，單邊最大 85 KB |
| P7 | Community 不可用 | primary 3 個超大社群；secondary 1,930 個碎片 |
| P8 | PluginReport 假連通 | Plugin menu 全部停在共用 MenuIndex，未解 Base64 FQN |
| P9 | ASPX 未索引 | 638 個 .aspx 未進 Graph |
| P10 | Named View 未抽取 | 650 處 `View("x")` 沒有 action→page 關係 |
| P11 | TSX 覆蓋不足 | 562 個 tsx 只有 151 個入圖，沒有 ES import 邊 |
| P12 | 自然語言與技術名稱斷裂 | aliasesText 有欄位但缺少可驗收的 Alias、Method、Column seed 規則 |
| P13 | AI Summary 阻塞感 | 缺少有界背景佇列、可觀察進度與優先權規則 |

### 0.2 產品決策

| 決策 | 定案內容 |
|---|---|
| Graph 粒度 | 維持 Feature / EntryPoint / Code / Data 四種 Node；不建 Method/Column Node |
| Edge 類型 | 維持九種 Edge，不加入 CFG/DFG/TESTS |
| 檢索 | 維持 deterministic retrieval；本版不做 Agentic tool loop |
| 向量 | 不做 Embedding；使用 BM25、Alias、精確技術 token 與 Graph traversal |
| Shared | 使用獨立 `isShared`；影響分析折疊為「另影響 N 個共用元件使用者」 |
| ExtJS | 不做 xtype/component hierarchy |
| Community | deterministic template 立即可用；AI Summary 背景按需 |
| C# Compilation | `CompilationMode=Auto`；MSBuild 成功的 Project 為 authoritative，僅失敗 Project 走 Synthetic |
| MSBuild/Synthetic 比對 | 正常索引不雙跑；只允許離線抽樣 Audit，不參與 Publish |
| Body delta | 任何 C# body 變更都重抽完整 C# Graph，不用 invocation-digest 跳過 |
| DB failure | 已設定 Live DB 時，必要查詢失敗不得 Promote 不完整 Graph |
| Publish | 正常穩定狀態只留 active；Publish/Reconciliation 期間可暫留 previous |
| 效能 | 同時量 Wall-clock、每千檔吞吐、每 MB 吞吐與 Peak RAM；倍率僅為其中一項 |

### 0.3 精度與可用性原則

1. Syntax-only 不得建立 `certain` 跨 Type CALLS。
2. 真實 MSBuild Compilation 成功時，不得被 Synthetic 覆蓋。
3. Synthetic 是 per-project/per-batch fallback，不建立單一跨全 Repository 假 Compilation。
4. 未啟用的 `#if` 分支不建立猜測 CALLS。
5. Graph-affecting body 變更不得沿用舊 C# Edge。
6. Edge 驗收同時量 Precision 與 Recall。
7. 覆蓋率不得透過無證據 Edge 達成。
8. 高價值 degree=0 程式保留為 unresolved，不假裝不存在。
9. 只要舊 active graph 完整，新索引 Partial/Failed 就不得取代它。
10. 搜尋 seed 必須能由業務名稱、Type、Method、Route、Table、Column、SP 或 Function 命中。
11. AI Summary 不得阻塞 Graph publish、Local Search 或使用者問答。

---

## 1. 範圍、非目標與資料保護

### 1.1 本次範圍

1. Repository/solution/project discovery。
2. C# 全量 Type inventory 與 project-aware semantic CALLS。
3. ASPX、Named View、ES import、PluginReport Base64 鏈路。
4. SQL Server SP/function/view/table/FK/module dependency。
5. Evidence 從 Neo4j 移至 SQLite，問答時批次 hydration。
6. Deterministic Search Metadata 與 Query Normalization。
7. C0/C1/C2 Community。
8. shared traversal 與影響折疊。
9. DB fingerprint、版本化 publish、crash reconciliation。
10. deterministic template 與有界背景 AI Summary。
11. 覆蓋率、Precision、Recall、問答品質、效能與清理驗收。

### 1.2 非目標

- ❌ Method／Column 級 Node。
- ❌ Embedding／Vector Search。
- ❌ Agentic 多輪工具迴圈。
- ❌ ExtJS component hierarchy。
- ❌ WCF／RealTimeService／MQ 跨進程呼叫鏈。
- ❌ Reflection/dynamic 的猜測 CALLS。
- ❌ CFG/DFG、完整資料流、TESTS Edge。
- ❌ 索引所有條件編譯組合。
- ❌ 以無 Evidence synthetic edge 增加連通率。
- ❌ 強制要求所有舊式 Project 都能被 MSBuildWorkspace 載入。
- ❌ invocation-digest 跳過 C# semantic re-extraction。
- ❌ 正常 Full Index 對同一 Project 同時跑 MSBuild 與 Synthetic。

### 1.3 資料保護與破壞性授權

- V4 首次索引全量重建，不做 V3 graph migration。
- 只允許清除 GraphRAG 自有 Neo4j graphVersion 與 SQLite GraphRAG tables。
- PAT、OAuth、使用者設定、對話與其他非 GraphRAG table 不得清除或改 schema。
- 測試清理必須使用 table allowlist。
- 驗收前保存受保護 table：
  - schema hash；
  - row count；
  - 不輸出明文的 deterministic content hash。
- 驗收後三者必須一致。

---

## 2. Repository Discovery 與 C# Compilation

### 2.1 固定執行順序

```text
Phase 0: Repository / Solution / Project Discovery
→ Phase 1: Project-aware Syntax Inventory
→ Phase 2: Semantic CALLS Resolution
→ Phase 3: Reachable Deep Attributes
```

禁止先以統一 ParseOptions parse 全 Repository，之後才載入 MSBuild metadata。

### 2.2 Phase 0：Repository / Project Discovery

索引開始時先解析：

- `.sln`；
- `.csproj`；
- Project source membership；
- Project reference；
- metadata reference；
- `DefineConstants`；
- `LangVersion`／ParseOptions；
- CompilationOptions；
- linked source file；
- 無法對應 Project 的 loose source。

每個 source 建立一或多個 `CompilationContext`：

```text
contextId
projectPathHash
sourceMembership
parseOptionsHash
defineConstantsHash
compilationMode
```

規則：

- `contextId` 不含絕對路徑。
- linked source 可屬於多個 context。
- Node identity 仍依 logical source declaration 穩定。
- 同一 source 在不同 context 產生的 Edge Evidence 必須帶 `contextId`。
- 不同 context 解析到不同 target 時，不得互相覆蓋；Confidence 依各自 Evidence 彙總。

### 2.3 CompilationMode

```text
Auto
MSBuildOnly
SyntheticOnly
```

預設：

```text
CompilationMode = Auto
```

| Mode | 行為 |
|---|---|
| Auto | 每個 Project 先嘗試 MSBuild；失敗 Project 才 Synthetic |
| MSBuildOnly | MSBuild 失敗的 Project 不建 semantic CALLS，只記 gap |
| SyntheticOnly | 測試／診斷使用；必須標記非真實 build |

正常 Publish 禁止對成功 MSBuild Project 再跑 Synthetic。

可另提供離線：

```text
SemanticAuditCompareSampleRate = 0..1
```

Audit：

- 不在使用者 Full Index 主路徑執行。
- 不影響 Graph Edge。
- 只產出 MSBuild/Synthetic mismatch 報告。

### 2.4 Phase 1：Project-aware Syntax Inventory

- 掃描所有未被排除的 `.cs`。
- Project source 使用 Phase 0 取得的 ParseOptions。
- Loose source 使用明確記錄的 fallback ParseOptions。
- 建立：
  - Type declaration inventory；
  - namespace；
  - public route/action candidate；
  - base type syntax；
  - invocation candidate；
  - named View candidate；
  - TaskName candidate；
  - Method name inventory；
  - conditional directive metadata。
- Generated/bin/obj/packages 沿用排除規則。
- Designer/generated source：
  - 不作高價值搜尋 seed；
  - 若其宣告是 semantic resolution 必要 target，可保留 inventory；
  - 不得因檔名為 `*.Designer.cs` 就讓手寫 partial type 失去解析能力。
- Phase 1 不建立跨 Type CALLS。

### 2.5 Phase 2：Semantic CALLS Resolution

每個 CompilationContext 使用：

```text
1. MSBuild Project Compilation（Auto/MSBuildOnly 且成功）
2. Project-scoped Synthetic Compilation（Auto fallback）
3. Batch-scoped Synthetic Compilation（Project boundary 不可得）
4. 有界 repository heuristic（只能 probable）
5. 無法唯一解析 → 不建 Edge
```

#### MSBuild

- 成功載入的 Project result 為 authoritative。
- 使用真實 source/reference/project reference/DefineConstants/ParseOptions。
- 該 Project 不再進 Synthetic 主流程。
- Load failure：
  - 不使整體索引直接失敗；
  - Auto 模式改走 Synthetic；
  - diagnostics 記 failure 類型，不記敏感路徑或憑證。

#### Project-scoped Synthetic

至少加入：

```text
該 Project source SyntaxTree
可安全取得的 direct project source dependency
mscorlib / System.* / netstandard runtime metadata
allowUnsafe = true
```

規則：

- 不將 75 個 Project 的所有 source 強制放入同一 Compilation。
- `GetSymbolInfo` 唯一解析到 repository source symbol，且 method name/arity 相容時，可建立 `certain`。
- ambiguous/error candidate 不得標 certain。
- 缺失 external business DLL 不建 certain。
- Project boundary 無法取得時才改 batch fallback。

#### Heuristic probable

必須同時滿足：

1. Receiver 可由 parameter/field/property/local declaration syntax 指向明確 type name。
2. 全域 Type Inventory 只有一個相容 namespace/type candidate。
3. Candidate Type 存在相同 method name。
4. Argument count 與可見 method declaration相容。
5. 不存在同名多 candidate、dynamic 或 reflection。

reasonCode：

```text
naming-convention
```

只憑 method name 或 receiver variable 名稱不得建 Edge。

### 2.6 Phase 3：Reachable Deep Attributes

Root：

- Feature-linked Controller；
- Route-bearing Controller Action；
- Scheduled Task；
- Report Plugin；
- 高價值 business-service/repository；
- Frontend Page。

沿 HANDLES/CALLS 展開，為可達 Type 補有界：

```text
methodNames
baseTypes
constructorDependencies
routeNames
taskNames
```

Method 不建 Node，但 `methodNames` 必須進入 Search Metadata。

### 2.7 條件編譯

- MSBuild Project 使用該 Project `DefineConstants`，只抽 active branch。
- Synthetic 使用 Phase 0 可解析到的 project constants。
- Loose source 使用設定的 fallback symbol set；預設空集合。
- Disabled `#if` 不建立 inferred CALLS。
- Node/Evidence details 記：

```text
conditionalCompilationPresent = true
activeSymbolSet = [...]
compilationContextId = ...
```

- 回答涉及該檔時提示「其他條件編譯組合未索引」。
- 本版不為所有 symbol set 重複 parse。

### 2.8 Confidence

| 情境 | 行為 | Confidence |
|---|---|---|
| MSBuild unique source symbol | 建 CALLS | certain |
| Project-scoped Synthetic unique source symbol，無 relevant ambiguity | 建 CALLS | certain |
| Batch Synthetic unique source symbol，context 完整 | 建 CALLS | certain |
| Repository heuristic 符合 §2.5 | 建 CALLS | probable |
| Cross-batch heuristic 符合 §2.5 | 建 CALLS | probable |
| 重複 type／overload ambiguity | 不建 Edge | — |
| 缺失 external reference | 不建 Edge | — |
| Dynamic/reflection/expression tree | 不建 Edge | — |
| Disabled conditional branch | 不建 Edge | — |

### 2.9 記憶體預算與降級

Preflight：

```text
source file count
total source bytes
SyntaxTree count
current process working set
available physical memory
configured MaxWorkingSetBytes
```

預設可用增量預算：

```text
min(
  configured MaxWorkingSetBytes（預設 8 GB） - current process working set,
  available physical memory × 0.60
)
```

若結果小於安全下限，直接使用較小 batch，不先建立大 Compilation。

降級順序：

```text
1. MSBuild/csproj Project boundary
2. Solution project directory
3. Namespace root
4. 頂層目錄
```

分批時：

- 保留全域 Type Inventory。
- 批內使用 SemanticModel。
- 跨批只依 §2.5 建 probable。
- diagnostics 記 batch strategy、批次數、跨批 probable 數。

Runtime Guard：

- 接近預算時取消尚未開始的 semantic batch。
- 釋放 Compilation/SourceText 後以更小 batch 重試。
- 不因降級建立 inferred/certain 猜測 Edge。
- Peak Working Set 不得高於驗收預算。

### 2.10 Body Delta

| 變更 | 行為 |
|---|---|
| source/DB/extractor/schema 皆無變更 | no-op |
| C# body 改變，declaration surface 不變 | 重抽完整 C# Graph；沿用安全的非 C# fragment |
| C# declaration/route/signature 改變 | full C# re-extraction |
| ASPX/Frontend/SQL 檔改變 | 重跑對應 extractor；無法證明局部安全時 full |
| DB fingerprint 改變 | 重跑 live DB extractor |
| Schema/indexer/extractor version 改變 | full |

任何 body-delta 結果必須與 clean full snapshot digest 一致。

---

## 3. Graph 資料模型

### 3.1 Node Kind

```text
Feature
EntryPoint
Code
Data
```

### 3.2 Neo4j Node Property

| Property | Type | 說明 |
|---|---|---|
| id | string | `{kind}:{source}:{logicalKey}`，跨執行穩定 |
| projectId | string | 專案 ID |
| graphVersion | string | Immutable Graph version |
| kind | string | 四種 kind |
| role | string | GraphRoles 白名單 |
| name | string | 顯示名稱 |
| searchableText | string | Deterministic Search Metadata |
| aliasesText | string | 去重別名 |
| language | string | business/csharp/frontend/sql |
| state | string | active/inactive/unresolved |
| isShared | bool | 拓撲共用元件旗標 |
| filePath/startLine/endLine | nullable | 主要 source |
| attributesJson | string | 有界屬性 |
| degree | int | Assembler 計算 |
| communityId | string? | primary C1/C2 |
| evidenceRef | string | Node ID；SQLite 查詢鍵 |

`state` 與 `isShared` 不得互相替代。

### 3.3 Feature 與 Menu Hierarchy

#### 可執行 Menu Item

本版不宣稱一定是 strict leaf；只要 Released 且有 LinkAddress 即為可執行 Menu Item：

```sql
SELECT ID, Parent, Name, LinkAddress
FROM dbo.tblMenuMap
WHERE Released = 1
  AND ISNULL(LinkAddress, '') <> ''
  AND ID NOT LIKE '88%';
```

#### Menu Hierarchy

```sql
SELECT ID, Parent, Name, Released, LinkAddress
FROM dbo.tblMenuMap
WHERE Released = 1
  AND ID NOT LIKE '88%';
```

- LinkAddress 空的父選單不建 Feature。
- 父選單保留在 hierarchy model，供 C0/C1 parent mapping。
- A1 驗收名稱統一使用「可執行 Menu Item」。

Feature roles：

```text
menu-feature
custom-report
approval-feature
schedule
batch-report
```

### 3.4 EntryPoint

| role | 規則 |
|---|---|
| controller-action | Feature、Route、frontend-url 或確認 call chain 指向的 public action |
| frontend-page | 所有合法 `.aspx`，不限 `Views/**` |
| scheduled-task | TaskName 常值 |

未被 Feature 指向但有公開 Route：

```text
state=unresolved
```

完全無 route/feature/frontend/call evidence 的 action 收入 Controller
`attributes.actions` 有界清單，不建獨立 Node。

Frontend Page attributes：

```text
viewName
controllerFolder
includedScripts
```

### 3.5 Code

roles：

```text
controller
business-service
repository
data-model
report-plugin
type
module
migration
frontend-module
```

可清除 degree=0：

- 無業務角色 generic type；
- DTO/value object；
- generated 殘留；
- 無 import/URL/page/edge 的 frontend module。

不得只因 degree=0 清除：

- controller；
- business-service；
- repository；
- report-plugin；
- scheduled-task 相關 Type；
- 有明確可搜尋名稱與 source evidence 的高價值 Type。

保留者：

```text
state=unresolved
```

### 3.6 Frontend Module

- IsPage 支援 `view/views/page/pages/screen/screens`。
- 解析相對 ES import。
- 補 `.ts/.tsx/.js/.jsx/index.*`。
- npm import 忽略。
- page/module import 可達 component 必須入圖。
- import path 正規化，大小寫與 Windows separator 行為固定。

### 3.7 Data

| role | 來源 |
|---|---|
| table/view | sys catalog |
| procedure | sys.objects type=P |
| function | sys.objects type=FN/TF/IF |
| report-template/csv-format/custom-enum | live DB |

`.sql` 檔只補 source evidence；DB object 不因 Repository 缺 `.sql` 而缺 Node。

Column 不建 Node，但下列資料必須有界加入 Table/View 的 Search Metadata 與 Evidence：

```text
columnName
dataType
nullable
primaryKey
foreignKey
```

### 3.8 ID

```text
entry:page:{normalizedRelativePath}
data:sql:{db}/{schema}/function/{name}
```

- `/` 分隔。
- 不含絕對路徑。
- 大小寫規則固定。
- 跨執行穩定。

### 3.9 Shared

初始門檻：

| Node | 條件 |
|---|---|
| frontend module | HANDLES/CALLS 不重複來源 ≥10 |
| EntryPoint | ROUTES_TO 不重複 Feature ≥10 |
| csharp Type | CALLS 不重複來源 Type ≥20 |

行為：

1. 設 `isShared=true`，不改寫 `state`。
2. Local Search/C1 收錄 shared 本身。
3. 不以 shared 作為下一層展開起點。
4. 不用 weight 懲罰使 shared 無法收錄。
5. `attributes.sharedConsumerCount` 保存不重複 consumer。
6. AnalyzeImpact 折疊，不展開全部 consumer。

---

## 4. Deterministic Search Metadata

### 4.1 目的

使用者問題在進入 Graph traversal 前，必須能以業務詞或技術詞命中候選 Node。
本層不使用 Embedding，也不新增 Method/Column Node。

### 4.2 Node Search Metadata

#### Feature

```text
Menu 名稱
功能代碼
LinkAddress token
父層 Menu 名稱
custom report/approval/schedule 名稱
```

#### EntryPoint

```text
Controller/Action
Route
HTTP verb
View name
ASPX path token
TaskName
```

#### Code

```text
Type short/qualified name
CamelCase/PascalCase 拆詞
methodNames（有界）
baseTypes
constructor dependency type names
report plugin FQN
```

#### Data

```text
database/schema/object name
table/view/procedure/function
column names（有界）
FK related table
DB metadata description（若可安全取得）
```

### 4.3 Alias 規則

Alias 來源只能是 deterministic fact：

- Menu／功能名稱；
- Route／URL token；
- Type/Method 拆詞；
- DB object/column；
- 設定檔內人工維護的 business alias；
- 已確認的中英文術語表。

禁止由 LLM 直接寫入 active Graph Alias。

Alias 正規化：

```text
Unicode normalize
trim
case fold
separator fold
camel/pascal split
中英文標點移除
去重
```

### 4.4 Query Normalization

Query 產生：

```text
原句 token
精確 identifier
camel/pascal split token
中英文 Alias 展開
URL/Route token
Table/Column/SP token
```

限制：

- Alias 展開最多 20 個 token。
- 不得產生 Graph 中不存在的 Entity ID。
- Exact identifier/name/alias match 排在 generic BM25 前。
- 搜尋失敗必須回報 missing seed，不得假裝 Graph 已完整回答。

---

## 5. Edge 模型

### 5.1 Edge Kind

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

### 5.2 Neo4j Relationship Property

| Property | 說明 |
|---|---|
| id | SHA-256(source\0kind\0target) |
| graphVersion | 版本 |
| weight | 預計算排序權重 |
| confidence | certain/probable/inferred |
| evidenceCount | 去重 Evidence 數 |
| reasonCode | 代表性 reason |
| topArtifact/topLine | 代表 source |
| sourceMethod/targetMethod | 代表 method pair |
| evidenceRef | Edge ID；SQLite 查詢鍵 |

刪除：

```text
evidenceJson
sourceId relationship property
targetId relationship property
```

記憶體 GraphEdge 仍保留 SourceId/TargetId。

### 5.3 Weight

```text
weight = clamp(base(kind) × confidenceFactor × evidenceBonus, 0.05, 1.0)
```

| Kind | Base |
|---|---:|
| ROUTES_TO | 1.00 |
| HANDLES | 0.95 |
| TRIGGERS | 0.95 |
| DISPATCHES_TO | 0.90 |
| WRITES | 0.90 |
| READS | 0.85 |
| MAPS_TO | 0.85 |
| CALLS | 0.75 |
| DEPENDS_ON | 0.70 |

| Confidence | Factor |
|---|---:|
| certain | 1.00 |
| probable | 0.90 |
| inferred | 0.75 |

Evidence bonus：

```text
去重 evidenceCount >=3 → 1.05
否則 → 1.00
```

不使用 crossNamespaceFactor/sharedTargetFactor。
Weight 只排序，不證明 Edge 正確性。

### 5.4 Confidence 彙總

- 任一 certain Evidence → certain。
- 否則任一 probable → probable。
- 否則 inferred。
- Evidence 依 source/artifact/line/reasonCode/contextId 去重。
- 同一檔案被重複掃描不得提高 Evidence bonus。

### 5.5 ReasonCode

```text
roslyn-invocation
roslyn-route
roslyn-view-result
roslyn-task-name
scriptdom-read
scriptdom-write
scriptdom-exec
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

- 未映射 ReasonCode 的 Edge 不得 Publish。
- 禁止自由文字 ReasonCode。
- 新增 ReasonCode 必須同步改 SPEC、常數、測試與報告。

### 5.6 Edge 規則

| # | Edge | 規則 | ReasonCode |
|---|---|---|---|
| E1 | Feature → Code DISPATCHES_TO | PluginReport URL Base64 解 FQN；不存在建 unresolved stub | menu-link-base64 |
| E2 | Action → Page DISPATCHES_TO | `View("name")`/`View("name",model)` | roslyn-view-result |
| E3a | Page → Frontend HANDLES | `JSPath`/`AbsolutePath` 固定 script path | aspx-script-include |
| E3b | Page/Frontend → Action ROUTES_TO | 固定 `/Controller/Action/` URL | frontend-url |
| E4 | Frontend → Frontend CALLS | 相對 ES import | es-import |
| E5 | Child Table → Parent Table DEPENDS_ON | sys.foreign_keys | fk-constraint |
| E6 | SQL Module → Table/View READS/WRITES | sys dependency + ScriptDom | sys-dependency/scriptdom-* |
| E7 | SQL Module → SQL Module DEPENDS_ON | EXEC、View、Function、SP dependency | sys-dependency/scriptdom-exec |
| E8 | Code → Code CALLS | §2 Semantic/Heuristic | roslyn-invocation/naming-convention |

#### Named View

解析：

```text
Views/{Controller}/{viewName}.aspx
Views/Shared/{viewName}.aspx
```

找不到不建 Edge，記 diagnostics。

#### ASPX/Frontend

- `.js` 固定路徑使用 E3a。
- `/Controller/Action/` 使用 E3b。
- 動態 GetUIData 只連到可確認 action，不解析動態回傳 script。
- 雙向路徑必須使用 visited set 防循環。

#### SQL

1. sys.objects 建 object inventory。
2. sys.sql_expression_dependencies 找 object dependency candidate。
3. sys.sql_modules.definition 交給 ScriptDom。
4. SELECT → READS。
5. INSERT/UPDATE/DELETE/MERGE → WRITES。
6. EXEC/Function/View/Procedure reference → DEPENDS_ON。
7. 無法分類：
   - 不得 certain；
   - 可 probable DEPENDS_ON；
   - 只有明確只讀語境才 probable READS。
8. Dynamic SQL、synonym、cross DB、temp table 記 known gap。

---

## 6. SQLite Evidence Store 與雙儲存 Publish

### 6.1 Evidence Schema

```sql
CREATE TABLE IF NOT EXISTS graph_evidence (
    project_id    TEXT    NOT NULL,
    graph_version TEXT    NOT NULL,
    entity_id     TEXT    NOT NULL,
    entity_type   TEXT    NOT NULL CHECK (entity_type IN ('node', 'edge')),
    seq           INTEGER NOT NULL,
    source        TEXT    NOT NULL,
    confidence    TEXT    NOT NULL,
    artifact      TEXT    NOT NULL,
    reason        TEXT    NOT NULL,
    reason_code   TEXT    NOT NULL,
    start_line    INTEGER,
    end_line      INTEGER,
    details_json  TEXT,
    PRIMARY KEY (project_id, graph_version, entity_type, entity_id, seq)
);

CREATE INDEX IF NOT EXISTS ix_graph_evidence_lookup
ON graph_evidence (project_id, graph_version, entity_type, entity_id);
```

### 6.2 Publish State Schema

Publish 狀態必須持久化，不得只存在記憶體：

```sql
CREATE TABLE IF NOT EXISTS graph_publish_manifests (
    project_id         TEXT NOT NULL,
    graph_version      TEXT NOT NULL,
    evidence_state     TEXT NOT NULL,
    publish_state      TEXT NOT NULL,
    is_current         INTEGER NOT NULL DEFAULT 0,
    requires_reconcile INTEGER NOT NULL DEFAULT 0,
    created_at         TEXT NOT NULL,
    updated_at         TEXT NOT NULL,
    error_code         TEXT,
    PRIMARY KEY (project_id, graph_version)
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_graph_publish_manifest_current
ON graph_publish_manifests(project_id)
WHERE is_current = 1;
```

允許值：

```text
evidence_state:
  writing
  ready
  failed

publish_state:
  staging
  neo4j-promoted
  active
  reconciliation-required
  failed
  retired
```

`ProjectIndexManifest.Status` 仍表示索引品質：

```text
Indexing / Fresh / Partial / Failed / Stale
```

兩者不得混用：

```text
IndexManifestStatus = 內容是否完整
GraphPublishState = 雙儲存發布進度
```

### 6.3 Store Contract

```text
WriteBatch
ReadByEntities
DeleteByVersion
CountByEntities
SetEvidenceState
GetPublishManifest
SetPublishState
ListReconciliationCandidates
```

規則：

- Node 與 Edge ID 必須同時傳 `entity_type`。
- Node 每個 entity 最多 40 筆 Evidence。
- Edge 每個 entity 最多 40 筆 Evidence row。
- Type-level CALLS 的完整 method pairs 可在一筆聚合 `details_json` 保存，但必須：
  - deterministic sort；
  - 有最大 2,000 pairs；
  - 超過時保存 `methodPairOverflowCount`；
  - top method 不能宣稱是完整清單。
- WriteBatch 使用單一 transaction + prepared statement。
- ReadByEntities 一次查 selected Node/Edge IDs。
- 禁止 N+1。

### 6.4 DB Fingerprint 與 Promote Gate

Live DB 設定分三種狀態：

```text
not-configured
configured-ready
configured-unavailable
```

必要 DB 查詢：

- sys.objects；
- sys.foreign_keys；
- sys.sql_expression_dependencies；
- sys.sql_modules；
- Menu Hierarchy；
- Executable Menu Item；
- approval/schedule/batch/report metadata。

規則：

| 情境 | Index Attempt | 是否 Promote |
|---|---|---|
| DB 未設定 | 依 source 結果 Fresh/Partial | 可以；標 DB not-configured |
| DB 已設定且必要查詢全部成功 | 依完整結果 | 可以 |
| DB 已設定且任一必要查詢失敗，有舊 active | Partial/Stale | **不可以**；保留舊 active |
| DB 已設定且任一必要查詢失敗，沒有舊 active | Partial/Stale | 不建立可用 active；UI 顯示 source-only/unavailable |
| 非必要 description/comment 查詢失敗 | Partial + gap | 可依 policy Promote，但不得影響 object/dependency 完整性 |

禁止以缺失 DB fragment 的 Snapshot 取代上一版完整 active。

若要沿用上一版 DB fragment，必須另有明確 `ReuseVerifiedDbFragment` 實作與 digest
一致測試；本版預設不沿用。

### 6.5 Publish 流程

```text
1. 建立 graphVersion 與 graph_publish_manifest(staging/writing)
2. SQLite transaction 寫 Evidence
3. 對帳 Evidence 數量與 entity reference
4. 標 evidence_state=ready
5. Neo4j 寫 staging graph
6. 驗證 Neo4j counts/weight/reasonCode/evidenceRef
7. 驗證 DB Promote Gate
8. Promote Neo4j active pointer，同時保存 previous pointer
9. 標 publish_state=neo4j-promoted
10. Promote SQLite graph manifest current
11. 標 publish_state=active，requires_reconcile=0
12. 再次驗證 active Graph/Evidence
13. 清 previous/retired/orphan version
```

Source of Truth：

- 問答 active graphVersion 以 Neo4j active pointer 為準。
- SQLite current manifest 必須與 Neo4j active 一致。
- Evidence 必須是該 version 的 `evidence_state=ready`。
- 任一不一致在 reconciliation 完成前不得開放該 Project 問答。

### 6.6 Failure 與 Version Retention

| 失敗點 | 行為 |
|---|---|
| 1～7 | 不切換 active；刪新 Evidence/Neo4j staging；舊 active 保留 |
| 8 前 crash | 舊 active 有效；新 version orphan |
| 8 成功、10 前失敗 | previous 暫留；標 reconciliation-required；啟動時回復或完成 promote |
| 10 成功、11 前失敗 | 依兩邊 version 冪等補 active 狀態 |
| 11 成功、13 失敗 | 問答可用；下次 reconciliation 清 retired |
| Active Evidence 缺失或不 ready | 關閉該 Project 問答；若 previous 完整則回復 previous |

Version retention：

```text
正常穩定狀態：只保留 active
Publish/Reconciliation：允許 active + previous + staging
完成 step 12 後：清 previous pointer 與舊版
```

Cleanup 必須精確限定：

```text
projectId + graphVersion
```

### 6.7 Startup Reconciliation

Host 啟動：

```text
讀 Neo4j active/previous
→ 讀 SQLite current/publish/evidence state
→ 分類一致、可完成 promote、可 rollback、不可修復
→ 冪等修復
→ 清 orphan
→ 只啟用通過驗證的 Project 問答
```

Reconciliation 不得：

- 修改非 GraphRAG table；
- 猜測缺失 Evidence；
- 將 Partial DB Snapshot Promote 成 active；
- 因單一 Project 故障阻止其他健康 Project 問答。

---

## 7. Community

### 7.1 Tier

| Tier | 來源 | 數量 | 用途 |
|---|---|---:|---|
| C0 | Released Menu Hierarchy 頂層 | 8～25 | Global Search 模組漏斗 |
| C1 Anchor | Executable Feature/custom report/approval/schedule | 等於合格 anchor 數 | 功能起點 |
| C1 Resolved Report | Anchor 閉包 member 3～60 | 依解析結果 | 可摘要功能閉包 |
| C1 Unresolved | Anchor 閉包 member 1～2 | 分列 | 可搜尋但不假裝完整 |
| C2 | 未被 C1 覆蓋的節點 | ≤100 reports | 非菜單模組 |

### 7.2 C1 閉包

允許：

```text
ROUTES_TO
TRIGGERS
DISPATCHES_TO
HANDLES
CALLS
READS
WRITES
```

限制：

```text
depth <= 4
memberCount <= 60
非 CALLS weight >= 0.70
CALLS weight >= 0.60 且 confidence != inferred
```

Shared：

- certain/probable direct edge 可收錄。
- 即使低於一般門檻仍可收錄一次。
- 收錄後停止，不從 shared 外擴。
- 不得用 shared 串接多個 C1。

Member 觸頂：

```text
truncated=true
truncatedMemberCount=...
```

不得靜默截斷。

### 7.3 C2 Projection

C2 只對未被任何 C1 Anchor 覆蓋且未清理的 Node 執行。

Projection Edge：

```text
CALLS confidence != inferred
HANDLES
ROUTES_TO
DISPATCHES_TO
TRIGGERS
READS
WRITES
MAPS_TO
DEPENDS_ON
```

規則：

- 使用 Edge weight。
- shared 可成為 member，但不作跨 community bridge。
- GDS Leiden 可用時使用固定 seed/config。
- 否則 deterministic label propagation。
- member<3 不建立 C2 CommunityReport。
- Node 仍可搜尋並可標 unresolved。
- Algorithm name/config/seed 寫入 report attributes。

### 7.4 Community Property

```text
communityId
tier
parentCommunityId
title
summary
summaryState
memberIdsJson
memberCount
topTables
topEntryPoints
cacheKey
truncated
```

主要 Community：

- 距離 Feature anchor 最近。
- 同距離依 communityId 字典序。
- 其他 membership 存 `alsoInCommunities`，最多 5。
- 超過 5 時存 `alsoInCommunityOverflowCount`。

### 7.5 Deterministic 與 AI Summary

| 階段 | 行為 |
|---|---|
| Graph publish | C0/C1/C2 template 全部立即建立 |
| C0 AI | Publish 後背景預熱 |
| C1 AI | 第一次命中時排入 |
| C2 AI | 第一次命中時排入 |
| AI 失敗 | 保留 template，summaryState=failed |

Summary State：

```text
template
queued
running
ai-ready
failed
```

AI Summary 不得使 Project index unavailable。

### 7.6 Background Queue

最低實作：

```text
Per-project concurrency = 1
Global concurrency = 2
cacheKey 去重
bounded queue
單筆 timeout
最多 2 次 retry
Graph index / Local Search / Answer 優先於 Summary
```

重啟後：

- `queued/running` 視為可重新排程。
- 已有相同 cacheKey 的 `ai-ready` 不重跑。
- `failed` 依 retry policy，不進無限迴圈。

Progress API 最少回傳：

```json
{
  "projectId": "...",
  "total": 25,
  "queued": 8,
  "running": 1,
  "completed": 14,
  "failed": 2,
  "percent": 60,
  "structuralIndexAvailable": true
}
```

UI 右下角分開顯示：

```text
結構索引可用
AI 摘要背景補充中 15/25
```

---

## 8. 檢索

### 8.1 Seed Search

```text
SeedLimit = 12
```

排序：

1. exact identifier/name/alias。
2. intent role relevance。
3. BM25 score。
4. non-shared。
5. 相同 state/role 內才以 degree tie-break。

不得讓 hub/helper 因 degree 高壓過精確業務或技術命中。

Seed 可以由：

```text
Feature name/code
Type/Method
Route/View/ASPX
Table/Column
SP/Function
Alias
```

命中。

### 8.2 Local Search

```text
MaximumNodes = 80
MaximumEdges = 120
MaximumDepth = 3
NeighborsPerNode = 50
```

BFS：

- 讀 Edge weight。
- shared 收錄、不外擴。
- visited key 至少包含 Node ID 與 traversal direction。
- 雙向 Page/Action 路徑不得震盪。
- probable/inferred 必須保留 Confidence。

### 8.3 Evidence Hydration

```text
選定 <=80 Nodes / <=120 Edges
→ 建立 <=200 (entityType, entityId)
→ 一次 ReadByEntities
→ 合併 top property + SQLite Evidence
→ SourceEvidenceReader
→ Context Compiler
```

Context 分區：

```text
certain
probable
inferred
source read failed
known gaps
```

Hydration timeout：

- 可用 Graph 結構回答。
- 聲明完整溯源暫時不可用。
- 不把 top method 說成完整清單。

### 8.4 AnalyzeImpact

- depth 1～2：直接影響。
- depth 3～4：間接影響。
- shared consumer 折疊。
- probable/inferred 不得稱為已確認。
- Table/Column 問題提示 dynamic SQL、external report、trigger、cross DB gap。
- Method/Column seed 由 Search Metadata 找到所屬 Type/Table，再沿 Graph 展開。

### 8.5 Global Search

```text
Question
→ Neo4j server-side C0 scoring，Top 1～2
→ 每個 C0 的 C1 server-side scoring，Top 12
→ C2 server-side scoring，Top 5
→ template/AI summary
→ 套用 Community Context Token Budget
```

禁止：

- 將全部 C1 載入記憶體排序。
- 將某個 C0 的全部 C1 children 放入 Prompt。
- 每題固定載入全部 C2。

Neo4j index：

```text
(graphVersion, communityId)
(graphVersion, tier, parentCommunityId)
Community title/summary full-text index
```

### 8.6 Missing Seed

若 Seed Search 沒有可信候選：

- 回傳 `missingSeed=true`。
- 列出已嘗試 token/alias 類型。
- 可執行一次 deterministic lexical fallback。
- 仍找不到時明確說明索引缺口。
- 不建立猜測 Entity/Edge。

---

## 9. 索引管線與版本

### 9.1 副檔名

```text
.cs .java .js .jsx .ts .tsx .sql .aspx
```

`.aspx` 必須同步加入：

- SupportedExtensions；
- FileSystemWatcher；
- LanguageForExtension(frontend)；
- Artifact manifest；
- safe source fallback；
- extractor DI；
- extractor/indexer version。

### 9.2 DB Fingerprint

至少包含：

- sys.objects：U/V/P/FN/TF/IF schema/name/type/modify_date；
- sys.foreign_keys；
- sys.sql_expression_dependencies；
- sys.sql_modules definition hash；無 VIEW DEFINITION 時記 capability gap；
- Menu Hierarchy；
- Executable Menu Item；
- approval/schedule/batch/report/CSV metadata。

任何必要子查詢失敗：

- 禁止 no-op；
- manifest Partial/Stale；
- 依 §6.4 禁止 Promote；
- 不假設 DB 未變。

### 9.3 Version

```text
CurrentIndexerVersion = graphrag-v4
GraphAssembler.SchemaVersion = 4.0
```

- 各 extractor ID/version +1。
- Search Metadata version 必須納入 indexer digest。
- Alias glossary version 必須納入 indexer digest。
- V3 Graph 全部失效重建。
- Body-delta 不得硬編碼 V3 extractor ID。
- 刪除 evidenceJson、marker、舊 Community builder dead code。

### 9.4 Full Index Stage Metrics

```text
repository discovery
MSBuild metadata load
syntax inventory
MSBuild compilation
synthetic fallback
semantic resolution
search metadata
frontend/aspx
sql source
sql live DB
assemble
community template
SQLite evidence
Neo4j staging/publish
reconciliation
peak working set
```

每階段記：

```text
elapsed
source file count
source bytes
node/edge/evidence count
fallback count
warning/error count
```

---


## 10. 實作 TODO

### Phase 0 — Baseline 與保護

- [ ] **T0.1** 記錄 V3 commit/snapshot、硬體、冷熱機、階段耗時、Peak RAM、Node/Edge/Evidence 大小；確認 `FullIndexAbsoluteBudgetMinutes`。
- [ ] **T0.2** 建立 GraphRAG table allowlist；保存其他 table schema hash、row count、content hash。
- [ ] **T0.3** 建立 Named View、TSX import、Method seed、Column seed baseline。
- [ ] **T0.4** 建立 A15 Edge Precision fixture。
- [ ] **T0.5** 建立 A16 CALLS Golden Recall fixture，至少 100 組 internal caller→callee。
- [ ] **T0.6** 建立 S1～S6 Search Seed fixture 與 Q1～Q8 問答 golden set。
- [ ] **T0.7** 記錄 V3 processed files/bytes，供正規化 throughput 比較。

### Phase 1 — Model、Evidence 與 Publish

- [ ] **T1.1** GraphNode 新增 Degree/CommunityId/EvidenceRef/IsShared；State 移除 shared。
- [ ] **T1.2** GraphEdge 新增 Weight/Confidence/EvidenceCount/ReasonCode/TopArtifact/TopLine/SourceMethod/TargetMethod/EvidenceRef。
- [ ] **T1.3** 新增 GraphRoles.Function、SharedThreshold、ReasonCode constants。
- [ ] **T1.4** GraphIdentity 新增 FrontendPageEntry、SqlFunction。
- [ ] **T1.5** 新增 graph_evidence、graph_publish_manifests 與索引。
- [ ] **T1.6** IGraphEvidenceStore 實作 batch read/write/delete/count/state。
- [ ] **T1.7** Neo4j 移除 evidenceJson/sourceId/targetId relationship property。
- [ ] **T1.8** Publish 實作 writing→ready→neo4j-promoted→active 狀態機。
- [ ] **T1.9** 實作 previous 暫留、rollback、startup reconciliation、orphan cleanup。
- [ ] **T1.10** Host 只為 reconciliation 成功的 Project 啟用問答。
- [ ] **T1.11** Context Compiler 批次 hydrate Node/Edge，禁止 N+1。
- [ ] **T1.12** Method pair 聚合、2,000 上限與 overflowCount。

### Phase 2 — Discovery、Compilation 與 Extractors

- [ ] **T2.1** Repository/Solution/Project Discovery，先於 Syntax parse。
- [ ] **T2.2** `CompilationMode=Auto|MSBuildOnly|SyntheticOnly`，預設 Auto。
- [ ] **T2.3** Project-aware ParseOptions/DefineConstants/linked source context。
- [ ] **T2.4** 全量 Syntax Inventory，不建跨 Type CALLS。
- [ ] **T2.5** MSBuild authoritative per-project Compilation。
- [ ] **T2.6** Project-scoped Synthetic fallback；禁止正常主路徑雙跑。
- [ ] **T2.7** Batch Synthetic、全域 inventory 與嚴格 cross-batch probable。
- [ ] **T2.8** Conditional active/disabled branch 與 coverage diagnostics。
- [ ] **T2.9** Memory preflight、project-boundary batching、runtime retry。
- [ ] **T2.10** Menu Item 與 Hierarchy query 分離。
- [ ] **T2.11** PluginReport Base64 FQN dispatch。
- [ ] **T2.12** SQL SP/function/FK/table/module inventory。
- [ ] **T2.13** ScriptDom READS/WRITES 與 module-to-module DEPENDS_ON。
- [ ] **T2.14** Named View metadata。
- [ ] **T2.15** AspxGraphExtractor 與 extension/watcher/language/DI registration。
- [ ] **T2.16** Frontend page folders + relative ES import。
- [ ] **T2.17** E3a/E3b 分離及所有 ReasonCode mapping。
- [ ] **T2.18** DB Promote Gate；必要 DB query 失敗不 Promote。

### Phase 3 — Search Metadata、Assembler 與 Community

- [ ] **T3.1** Feature/EntryPoint/Code/Data Deterministic Search Metadata。
- [ ] **T3.2** Camel/Pascal split、Alias glossary、Query Normalization。
- [ ] **T3.3** Method/Column metadata 有界加入 searchableText/Evidence。
- [ ] **T3.4** Exact identifier/name/alias 優先排序。
- [ ] **T3.5** Edge Evidence 去重、Confidence、Weight、top Evidence。
- [ ] **T3.6** Degree/isShared/consumer count。
- [ ] **T3.7** 分級孤立清理，高價值孤立 unresolved。
- [ ] **T3.8** C0、C1 Anchor/Resolved/Unresolved、C2 projection。
- [ ] **T3.9** Community deterministic ID、primary/alsoIn/overflow。
- [ ] **T3.10** Template immediate。
- [ ] **T3.11** AI Summary bounded queue、cacheKey dedupe、retry、priority。
- [ ] **T3.12** Summary progress API 與右下角 UI 進度提示。

### Phase 4 — Retrieval

- [ ] **T4.1** EdgeWeight hardcode 改讀 property。
- [ ] **T4.2** Seed search 支援業務、Type、Method、Route、Table、Column、SP/Function。
- [ ] **T4.3** shared/visited/direction/tie-break。
- [ ] **T4.4** Global Search C0 Top2、每 C0 C1 Top12、C2 Top5、Token Budget。
- [ ] **T4.5** AnalyzeImpact fold/confidence。
- [ ] **T4.6** Batch hydration。
- [ ] **T4.7** Evidence/visualization API 改 SQLite。
- [ ] **T4.8** Missing seed 與唯一一次 lexical fallback。

### Phase 5 — Indexing 與 Version

- [ ] **T5.1** V4 indexer/schema/extractor/search metadata/alias version。
- [ ] **T5.2** DB fingerprint 擴充。
- [ ] **T5.3** 保守 body-delta：C# body 變更重抽完整 C# Graph。
- [ ] **T5.4** Body-delta 與 clean full digest 一致測試。
- [ ] **T5.5** `.aspx` manifest/watcher/fallback。
- [ ] **T5.6** Full Index stage metrics、processed files/bytes/throughput。
- [ ] **T5.7** 刪除 V3 dead code，grep 驗證。

### Phase 6 — Tests 與 Acceptance

- [ ] **T6.1** E1～E8、Weight、Confidence、Search、Shared、Community、Evidence 單元測試。
- [ ] **T6.2** Auto/MSBuild/Synthetic/fallback 測試；正常路徑驗證不雙跑。
- [ ] **T6.3** Project-aware ParseOptions 與 conditional branch 測試。
- [ ] **T6.4** Memory preflight/batch fallback/Peak RAM 測試。
- [ ] **T6.5** DB failure no-promote 測試。
- [ ] **T6.6** Full/no-op/body-delta/publish failure/crash reconciliation 整合測試。
- [ ] **T6.7** A1～A18。
- [ ] **T6.8** B1～B9。
- [ ] **T6.9** C1～C12。
- [ ] **T6.10** S1～S6。
- [ ] **T6.11** Q1～Q8。
- [ ] **T6.12** D1～D6 與驗收報告。

---

## 11. 驗收

### 11.1 Graph 覆蓋率與精度

| # | 指標 | 門檻 |
|---|---|---|
| A1 | menu-feature | 等於 Executable Menu Item SQL |
| A2 | active/resolved menu 孤立率 | <5%；unresolved 分列，不得假連 |
| A3 | 低價值 Code degree=0 | 0；高價值 unresolved 附清單 |
| A4 | procedure+function | ≥250 |
| A5 | FK DEPENDS_ON | ≥440 |
| A6 | PluginReport dispatch | ≥24；stub unresolved |
| A7 | frontend-page | ≥600 |
| A8 | Named View | ≥T0.3 baseline 90%，目標約550 |
| A9 | 另類基金 Feature→Page→JS | hop≤4 |
| A10 | 利息收入 ReportKernel→Data | 路徑存在 |
| A11 | TSX reachable | ≥T0.3 baseline 90%，目標約500 |
| A12 | Edge weight/confidence/reasonCode/evidenceRef | 100% |
| A13 | Neo4j 無 evidenceJson/sourceId/targetId relationship property | 100% |
| A14 | RMDAL Code | ≥500，抽樣有 CALLS/Data relation |
| A15 | Edge Precision | certain≥95%；probable≥85% |
| A16 | Internal CALLS Golden Recall | certain+probable 命中≥90% |
| A17 | SQL Module→Module | Golden fixture Recall≥90%，Precision≥95% |
| A18 | E3 ReasonCode | script include/frontend URL 抽樣100%正確 |

> **A5 canonical 計數註記**：門檻 `≥440` 計算抽取出的 FK constraint
> Evidence facts，不計算去重後的 canonical edge 數。多個 constraint 若具有相同
> `source + DEPENDS_ON + target`，必須合併為一條 canonical edge 並保留多筆 Evidence；
> 不得為了湊足 edge 數破壞 §3.8 的穩定 ID 與 §5.2 的 evidenceCount 語意。

#### A15

- certain、probable 各至少 50 條。
- 依 CALLS/READS/WRITES/ROUTES_TO/DISPATCHES_TO/DEPENDS_ON 分層抽樣。
- probable target 錯誤仍算錯誤。
- 非目標情境只能在抽樣前依 fixture 排除。
- 每筆記 source/target/reasonCode/artifact/人工判定。

#### A16

- Phase 2 前建立至少 100 組 internal caller→callee。
- Golden pair 來源：
  - Roslyn 真實 project navigation；
  - 人工確認 source；
  - 既有可重現流程。
- Dynamic/reflection/WCF/MQ 可預先標 non-goal。
- 實作後不得因 miss 才改 non-goal。
- certain+probable Recall≥90%。
- Miss 分類：
  - project load；
  - missing reference；
  - conditional；
  - batching；
  - extractor bug。

### 11.2 Community

| # | 指標 | 門檻 |
|---|---|---|
| B1 | C0 | 8～25 |
| B2 | C1 Anchor | 等於合格 Feature/custom/approval/schedule anchor |
| B3 | C1 Resolved member | 3～60 |
| B4 | C1 Unresolved | member 1～2，100% 標 unresolved |
| B5 | C2 | ≤100 reports 且 member≥3 |
| B6 | connected Feature/EntryPoint/Code primary communityId | ≥90% |
| B7 | C1 parent 指向 C0 | 100% |
| B8 | shared 不作跨社群中介 | 抽樣100% |
| B9 | C2 reproducibility | 相同 snapshot/config 產生相同 membership digest |

> **C0 fallback 註記**：C0 優先且主要來自 Released Menu Hierarchy 頂層。
> Custom report、approval、schedule、batch 等合格 anchor 若確實沒有 Released Menu
> 路徑，允許依固定 role 建立 `unmapped-anchor-role` fallback C0，使 C1 parent
> invariant 可成立。Fallback 的 `source`、`rootKey` 必須明確標記，不得冒充既有選單，
> 且仍計入 B1 的 8～25 總上限。

### 11.3 Search Seed

| # | 查詢 | 期望 |
|---|---|---|
| S1 | 中文業務名稱 | 命中對應 Feature/EntryPoint |
| S2 | `BondTradeService` | 命中 Code Type |
| S3 | `ProcessLogin` 等 Method | 命中包含該 Method 的 Code Type |
| S4 | `SettlementDate`／交割日 | 命中含該 Column/alias 的 Data/Code |
| S5 | `/Controller/Action` | 命中 Action/Frontend |
| S6 | SP/Function 名稱 | 命中 Data Module |

門檻：

```text
Top-5 Recall >= 90%
Exact identifier/name/alias Top-1 >= 95%
```

### 11.4 Storage、Publish 與效能

| # | 指標 | 門檻 |
|---|---|---|
| C1 | Neo4j relationship property size | 較 V3 evidenceJson 降低≥80% |
| C2 | SQLite/Neo4j Evidence count 對帳 | 全量 entity reference count 100%；內容抽樣100 entity |
| C3 | Full Index wall-clock | 可比 scope 依倍率；不可比 scope 標 N/A 並改驗絕對時間 |
| C4 | Normalized throughput | 報告 files/min、MB/min；不得較可比 V3 下降>50%而無核准 |
| C5 | Peak Working Set | ≤preflight budget；無 OOM/process crash |
| C6 | no-op | 無變更不重建 |
| C7 | Local Retrieval P95 | ≤2秒 |
| C8 | Hydration | 單次 batch，不得 N+1 |
| C9 | Community template | Graph active 後立即可用 |
| C10 | AI failure | 不使 index/answer unavailable |
| C11 | DB failure | 必要查詢失敗不得取代舊 active |
| C12 | Reconciliation | failure matrix 全部可冪等恢復 |

#### C3 Wall-clock 分級

| 倍率 | 判定 |
|---|---|
| ≤1.5× | PASS target |
| >1.5× 且 ≤2.0× | PASS with warning |
| >2.0× 且 ≤2.5× | CONDITIONAL；需產品負責人核准 |
| >2.5× | FAIL |

只有下列條件同時成立時，V3/V4 scope 才可直接比較倍率：

```text
processed file count 差異 <= 10%
processed source bytes 差異 <= 10%
source snapshot / DB / hardware / cold-warm 條件相同
```

若任一條件不成立：

- C3 倍率記為 `N/A（scope not comparable）`，不能填 PASS 或 FAIL。
- 改以 T0.1 前由產品負責人確認的 `FullIndexAbsoluteBudgetMinutes` 驗收。
- C4 normalized throughput 與 C5 Peak Working Set 仍為強制門檻。
- `FullIndexAbsoluteBudgetMinutes` 未填不得開始正式驗收。

倍率必須同時附：

```text
V3/V4 processed file count
processed source bytes
files/min
MB/min
每階段 elapsed
冷/熱機
```

不得單獨以倍率宣稱效能改善或退化。

Local Retrieval 計時範圍：

```text
Query Normalization
Seed Search
Graph Traversal
SQLite Hydration
不含 LLM generation
```

測試：

- S1～S6、Q1～Q8 加 15 題擴充集。
- warmup 5 次。
- 每題 10 次。
- P95≤2秒。

### 11.5 AI Summary 背景進度

| # | 指標 | 門檻 |
|---|---|---|
| AI1 | Publish 阻塞 | AI call 數=0 |
| AI2 | Queue concurrency | per-project≤1、global≤2 |
| AI3 | cacheKey dedupe | 重複工作=0 |
| AI4 | Progress API | queued/running/completed/failed/total 正確 |
| AI5 | UI | 結構可用與 AI 進度分開顯示 |
| AI6 | Failure | template 保留，無無限 retry |

### 11.6 問答品質

| # | 問題 | 期望 |
|---|---|---|
| Q1 | 另類基金畫面加欄位要改哪些檔案？ | ASPX/JS + Controller + Data |
| Q2 | 利息收入增減分析數字不對？ | ReportKernel + READS/WRITES |
| Q3 | tblPosition105 加欄位影響？ | Reverse Data + FK + shared fold |
| Q4 | 公告管理驗證改動前端影響？ | TSX→component→Action |
| Q5 | Bloomberg 匯率排程在哪？ | C2 + scheduled-task |
| Q6 | 登入流程是什麼？ | Method seed→Controller→Service→Repository/Data |
| Q7 | 債券交易流程是什麼？ | Alias/Feature→Page/Action→Code→Data |
| Q8 | 修改 SettlementDate 會影響哪裡？ | Column seed→Table→Code/SQL→Job/Report，附 gap |

每題保存：

```text
normalized query
seed
graph path
hydrated evidence
source snippet
certain/probable/inferred
known gaps
人工 1～5
```

門檻：

- 至少 7/8 題達 4 分。
- Q6～Q8 不得 missing seed。
- 不得將錯誤檔案、方法或資料物件標 certain。

### 11.7 Cleanup 與 Safety

| # | 指標 | 門檻 |
|---|---|---|
| D1 | 穩定狀態 Neo4j | 只保留 active；previous/staging=0 |
| D2 | Publish/Reconcile 期間 | active+previous+staging 符合狀態機 |
| D3 | SQLite | 穩定狀態無 retired/orphan Evidence |
| D4 | 非 GraphRAG table | schema/row count/content hash 不變 |
| D5 | 暫存檔 | 報告外清除 |
| D6 | Publish failure | 舊 active 可查；新 staging 可回收或回復 |

---

## 12. 驗收報告

位置：

```text
docs/reports/graphrag-v4-acceptance-{yyyyMMdd-HHmm}.md
```

格式：

```markdown
# GraphRAG V4 驗收報告

- 執行時間 / 執行人 / commit hash
- Source/DB/Neo4j/SQLite
- CPU/RAM/冷熱機
- CompilationMode
- MSBuild 成功/失敗 Project 數
- Synthetic/batch fallback 數
- DB Promote Gate 狀態

## 1. A1～A18 Coverage/Precision/Recall
## 2. B1～B9 Community
## 3. S1～S6 Search Seed
## 4. C1～C12 Storage/Publish/Performance
## 5. AI1～AI6 Background Summary
## 6. Q1～Q8 Answer Quality
## 7. D1～D6 Cleanup/Safety
## 8. Stage Metrics/Normalized Throughput/Peak RAM
## 9. Diagnostics 統計
## 10. FAIL/Warning/Conditional 處置
## 11. Known Gaps
```

驗收腳本：

```text
scripts/dev/graphrag-v4-acceptance.ps1
```

規則：

- 一般驗證只執行唯讀 Cypher、SQL、SQLite query。
- 清理只使用 GraphRAG allowlist。
- 自動填寫可計算項目；A15/A16/A17/S/Q 含 fixture 或人工確認。
- 任何 FAIL 不得合併。
- C3 Conditional 必須取得產品負責人明確核准。

---

## 13. 風險與已知限制

1. **MSBuildWorkspace 可能無法載入舊式 Project**  
   Auto 模式會 per-project fallback；A16 必須呈現 Recall 損失。

2. **Synthetic 不等於真實 Build**  
   外部 DLL、Project reference 或 constants 可能缺失；不得覆蓋 MSBuild result。

3. **Linked source 有多個 CompilationContext**  
   Evidence 必須保留 contextId，不能以最後一次結果覆寫。

4. **全量 C# Semantic 成本高**  
   以 Project boundary、記憶體預算及正規化 throughput 管理。

5. **條件編譯只索引 active branch**  
   其他 symbol set 不索引，回答需聲明。

6. **Body Delta 保守重抽 C#**  
   日常修改仍有成本，但避免 stale CALLS。

7. **SQLite/Neo4j 無 distributed transaction**  
   依持久化狀態機、previous version 與 startup reconciliation 保證。

8. **必要 DB query 失敗會阻止新 Graph Promote**  
   這是保護上一版完整索引的預期行為。

9. **ASPX 動態 expression**  
   只解析固定字串。

10. **Dynamic SQL/cross DB/synonym**  
    可能漏 Data Edge，回答必須標 gap。

11. **WCF/MQ/Reflection 非目標**  
    高價值孤立保留 unresolved。

12. **Method/Column 不建 Node**  
    依 Search Metadata 命中所屬 Type/Table；須由 S3/S4/Q6/Q8 驗收。

13. **Shared 門檻為初值**  
    只依 Q3/B8 調整常數。

14. **AI Summary 是附加能力**  
    Template 永遠先可用；背景 Queue 不得搶占問答。

---

## 14. 定案條件

本 V1.2.1 在以下事項獲產品負責人確認後改為「已定案」：

1. `CompilationMode=Auto`，先 Project Discovery，再 project-aware parse。
2. MSBuild 成功 Project 不在正常索引重跑 Synthetic。
3. Synthetic 只用於 failed Project/batch fallback。
4. 必要 DB query 失敗不得 Promote 不完整 Graph。
5. Publish state 以 SQLite 持久化，支援冪等 reconciliation。
6. previous version 只在 Publish/Reconciliation 期間保留。
7. `state` 與 `isShared` 分離。
8. Method/Column/Alias 必須能成為 deterministic search seed。
9. Global Search 使用 C0/C1/C2 Top-K 與 Token Budget。
10. C1 Anchor、Resolved、Unresolved 分開驗收。
11. E3a/E3b ReasonCode 分離，加入 SQL Module-to-Module dependency。
12. AI Summary 使用有界 Queue 並提供右下角進度。
13. C3 同時附正規化 throughput 與 Peak RAM。
14. A/S/Q/C/D/AI 全數達成後才完成 V4。

確認後才能開始 Phase 1；不得以 V1.1/V1.2 已存在為由跳過本版精度、
搜尋、發布一致性或效能條款。
