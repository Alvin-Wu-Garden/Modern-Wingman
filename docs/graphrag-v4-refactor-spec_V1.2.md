# GraphRAG V4 破壞性重構規格書 V1.2（定案候選）

> 狀態：**定案候選，待產品負責人確認後供實作使用**
> 版本：V1.2
> 日期：2026-07-29
> 前身：`graphrag-v4-refactor-spec_V1.1.md`
>
> **版本歷史**
>
> | 版本 | 日期 | 說明 |
> |---|---|---|
> | V4 草稿 | 2026-07-29 | 依 FBL V3 實測問題提出破壞性重構 |
> | V1 | 2026-07-29 | 修正 Semantic CALLS、weight、menu hierarchy、孤立節點、Evidence、AI Summary 與 Edge Precision |
> | V1.1 | 2026-07-29 | 加入 Synthetic Compilation、invocation-digest、記憶體降級與 2.5× 效能上限 |
> | **V1.2（本版）** | 2026-07-29 | 修正 invocation-digest stale graph 風險、MSBuild/Synthetic 權威順序、條件編譯、記憶體預算、雙儲存 promote、CALLS Recall 與分級效能門檻 |
>
> 本文件尚需產品負責人確認。確認後，本文件才是 V4 唯一實作依據。
> V3 未被本文件推翻的行為（staging/atomic publish、no-op fast path、
> DPAPI 憑證、manifest 對帳）一律保留。

---

## 0. 重構動機

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
| P3 | Menu Feature 雜訊 | 1,013 個 menu-feature 中 539 個孤立、642 個 inactive；可執行葉子約 698 筆 |
| P4 | SP/function 覆蓋不足 | Graph 僅 16 個 procedure；DB 有 109 SP + 105 TVF + 48 scalar function |
| P5 | FK 未利用 | DB 463 條 FK；Graph Data→Data DEPENDS_ON 僅 19 條 |
| P6 | Neo4j Evidence 過重 | relationship evidenceJson 共約 16 MB，單邊最大 85 KB |
| P7 | Community 不可用 | primary 3 個超大社群；secondary 1,930 個碎片 |
| P8 | PluginReport 假連通 | Plugin menu 全部停在共用 MenuIndex，未解 Base64 FQN |
| P9 | ASPX 未索引 | 638 個 .aspx 未進 Graph |
| P10 | Named View 未抽取 | 650 處 `View("x")` 沒有 action→page 關係 |
| P11 | TSX 覆蓋不足 | 562 個 tsx 只有 151 個入圖，沒有 ES import 邊 |

### 0.2 產品決策

| 決策 | 定案內容 |
|---|---|
| Graph 粒度 | 維持 Feature / EntryPoint / Code / Data 四種 Node；不建 Method Node |
| Edge 類型 | 維持九種 Edge，不加入 CFG/DFG/TESTS |
| 檢索 | 維持 deterministic retrieval；本版不做 Agentic tool loop |
| 向量 | 不做 Embedding，維持 BM25 + Graph traversal |
| Shared | 影響分析折疊為「另影響 N 個共用元件使用者」 |
| ExtJS | 不做 xtype/component hierarchy |
| Community | deterministic template 立即可用；AI Summary 背景按需 |
| C# Compilation | Synthetic Compilation 是必要 fallback；MSBuildWorkspace 成功載入時，其 project result 為 authoritative |
| Body delta | V4 第一版採保守策略；任何 C# body 變更都重抽完整 C# Graph，不用 invocation-digest 跳過 |
| 效能 | 目標 ≤1.5×；1.5～2.0×警告；2.0～2.5×需核准；>2.5×失敗 |

### 0.3 V1.2 精度原則

1. Syntax-only 不得建立 `certain` 跨 Type CALLS。
2. 真實 MSBuild Compilation 成功時，不能被 Synthetic 結果覆蓋。
3. 未啟用的 `#if` 分支不建立猜測 CALLS。
4. Graph-affecting body 變更不得因不完整 digest 而沿用舊 Edge。
5. Edge 驗收同時量 Precision 與 Recall。
6. 覆蓋率指標不得透過建立無證據 Edge 達成。
7. 高價值 degree=0 程式保留為 unresolved，不假裝不存在。

---

## 1. 範圍與非目標

### 1.1 本次範圍

1. C# 全量 Type inventory 與 semantic CALLS。
2. ASPX、Named View、ES import、PluginReport Base64 鏈路。
3. SQL Server SP/function/FK/module dependency。
4. Evidence 從 Neo4j 移至 SQLite，問答時批次 hydration。
5. C0/C1/C2 Community。
6. shared traversal 與影響折疊。
7. DB fingerprint、版本化 publish、crash reconciliation。
8. deterministic template 與按需 AI Summary。
9. 覆蓋率、Precision、Recall、效能與清理驗收。

### 1.2 非目標

- ❌ Method 級 Node。
- ❌ Embedding／Vector Search。
- ❌ Agentic 多輪工具迴圈。
- ❌ ExtJS component hierarchy。
- ❌ WCF／RealTimeService／MQ 跨進程呼叫鏈。
- ❌ Reflection/dynamic 的猜測 CALLS。
- ❌ CFG/DFG、完整資料流、TESTS Edge。
- ❌ 索引所有條件編譯組合。
- ❌ 以無 evidence synthetic edge 增加連通率。
- ❌ 強制要求 MSBuildWorkspace 成功；失敗時必須安全 fallback。
- ❌ invocation-digest 跳過 C# semantic re-extraction；列為未來最佳化。

### 1.3 資料保護與破壞性授權

- V4 首次索引全量重建，不做 V3 graph migration。
- 只允許清除 GraphRAG 自有 Neo4j graphVersion 與 SQLite graph tables。
- PAT、OAuth、使用者設定、對話與其他非 GraphRAG table 不得清除或改 schema。
- 測試清理必須使用 table allowlist。
- 驗收前保存受保護 table：
  - schema hash；
  - row count；
  - 不輸出明文的 deterministic content hash。
- 測試後三者都必須一致。

---

## 2. C# Semantic Compilation 策略

### 2.1 三階段流程

```text
Phase 1: 全量 Syntax Inventory
→ Phase 2: Semantic CALLS Resolution
→ Phase 3: Reachable Deep Attributes
```

#### Phase 1：全量 Syntax Inventory

- 掃描所有未被排除的 `.cs`。
- 使用固定 LanguageVersion；MSBuild project 可用時沿用其 ParseOptions。
- 建立：
  - Type declaration inventory；
  - namespace；
  - public route/action candidate；
  - base type syntax；
  - invocation candidate；
  - named View candidate；
  - TaskName candidate。
- Generated/designer/bin/obj/packages 等沿用排除規則。
- 不取得 SemanticModel。
- 不建立跨 Type CALLS。
- 不使用 invocation-digest 判斷「可以沿用舊 CALLS」。

#### Phase 2：Semantic CALLS Resolution

依下列權威順序：

```text
1. MSBuildWorkspace project Compilation（成功載入時）
2. Synthetic Compilation（必要 fallback）
3. 有界 repository heuristic（只能 probable）
4. 無法唯一解析 → 不建 Edge
```

#### Phase 3：Reachable Deep Attributes

從下列 root 出發：

- Feature-linked Controller；
- Route-bearing Controller Action；
- Scheduled Task；
- Report Plugin；
- 高價值 business-service/repository root；
- Frontend Page。

沿 HANDLES/CALLS 展開，只為可達 Type 補：

```text
methods
baseTypes
constructorDependencies
```

清單必須有上限，不存完整 source。

### 2.2 MSBuildWorkspace 規則

- `EnableMSBuildEnhancement` 可配置，預設可維持 false。
- 啟用後，每個成功載入的 Project 使用真實：
  - Project source membership；
  - metadata reference；
  - project reference；
  - DefineConstants；
  - ParseOptions；
  - CompilationOptions。
- 該檔案屬於成功載入的 Project 時，MSBuild semantic result 為 authoritative。
- MSBuild 與 Synthetic 結果一致：建立正常 Edge。
- MSBuild 與 Synthetic 不一致：
  - 使用 MSBuild 結果；
  - 寫入 `SEMANTIC_RESULT_MISMATCH` diagnostics；
  - 不把 Synthetic 結果另建 Edge。
- Project load 失敗：
  - 不使整體索引失敗；
  - 該 Project source 改走 Synthetic；
  - diagnostics 記錄 load failure 類型，不記敏感路徑或憑證。

### 2.3 Synthetic Compilation

Synthetic 是必要 fallback，至少加入：

```text
所有已通過排除規則的 source SyntaxTree
mscorlib / System.* / netstandard 等可取得的 runtime metadata reference
allowUnsafe = true
```

規則：

- Synthetic 不假裝等同真實 build。
- `GetSymbolInfo` 唯一解析到 repository source symbol，且 method name/arity 相容時，
  可建立 `certain`。
- Compilation 對該 invocation 有 ambiguous/error candidate 時，不得標 certain。
- 外部 business DLL 缺失造成 symbol 無法解析時，不建 certain Edge。
- Synthetic 結果不能覆蓋成功的 MSBuild Project 結果。

### 2.4 Heuristic probable CALLS

只有同時滿足以下條件，才可建立 probable：

1. Receiver 可由 parameter/field/property/local declaration syntax 指向明確 type name。
2. Repository 全域 Type Inventory 只有一個相容 namespace/type candidate。
3. Candidate Type 存在相同 method name。
4. Argument count 與可見 method declaration 相容。
5. 不存在同名多 candidate、dynamic 或 reflection。

reasonCode：

```text
naming-convention
```

只憑 method name 或只憑 receiver variable 名稱不得建 Edge。

### 2.5 條件編譯

- MSBuild Project 成功時，使用該 Project 的 DefineConstants，只抽 active branch。
- Synthetic fallback 使用明確記錄的 default ParseOptions，只抽 active branch。
- Disabled `#if` text 不會產生 InvocationExpressionSyntax，因此不建 inferred CALLS。
- 發現 conditional directives 時，Node/Evidence details 記：

```text
conditionalCompilationPresent = true
activeSymbolSet = [...]
```

- 回答涉及該檔時，Context Compiler 提示「其他條件編譯組合未索引」。
- 本版不為每組 symbol set 重複 parse。

### 2.6 Confidence 分類

| 情境 | 行為 | confidence |
|---|---|---|
| MSBuild unique source symbol | 建 CALLS | certain |
| Synthetic unique source symbol，無 relevant ambiguity | 建 CALLS | certain |
| Repository heuristic 同時滿足 §2.4 | 建 CALLS | probable |
| 重複 type declaration | 不建 Edge，記 diagnostics | — |
| 缺失 external reference | 不建 Edge，記 diagnostics | — |
| Dynamic/reflection/expression tree | 不建 Edge，記 diagnostics | — |
| Multiple candidate/overload ambiguity | 不建 Edge，記 diagnostics | — |
| Disabled conditional branch | 不建 Edge，記 coverage gap | — |
| Cross-batch heuristic | 僅滿足 §2.4 才建 | probable |

### 2.7 記憶體預算與降級

#### 預算

建立 Compilation 前先估算：

```text
source file count
total source bytes
SyntaxTree count
available physical memory
configured MaxWorkingSetBytes
```

預設預算：

```text
min(configured MaxWorkingSetBytes（預設 8 GB）, available physical memory × 0.60)
```

不得等到已超出 8GB 才開始降級。

#### 降級順序

```text
1. MSBuild/csproj Project boundary
2. Solution project directory
3. Namespace root
4. 最後才使用頂層目錄
```

分批時：

- 保留全域 Type Inventory。
- 批內使用 SemanticModel。
- 跨批只依 §2.4 建 probable。
- diagnostics 記錄 batch strategy、批次數與跨批 probable 數。

#### Runtime Guard

- 執行中接近記憶體預算時，取消尚未開始的 semantic batch。
- 釋放 Compilation/SourceText 後以較小 batch 重試。
- 不允許因降級建立 inferred/certain 猜測 Edge。
- Peak Working Set 寫入驗收報告。

### 2.8 Body Delta

V4 第一版採保守策略：

| 變更 | 行為 |
|---|---|
| 無 source/DB/extractor/schema 變更 | no-op |
| 只有 C# body 改變，declaration surface 不變 | 重抽完整 C# Graph；沿用可安全保留的非 C# fragment |
| C# declaration/route/signature 改變 | full C# re-extraction |
| ASPX/Frontend/SQL 檔改變 | 重跑對應 extractor；無法證明局部安全時 full |
| DB fingerprint 改變 | 重跑 live DB extractor |
| Schema/indexer/extractor version 改變 | full |

任何 body-delta 結果必須與 clean full snapshot digest 一致。

未來若要引入 graph-affecting-body-digest，必須另立 SPEC，至少涵蓋：

- receiver declaration；
- normalized invocation；
- constant arguments；
- named View；
- route/API/SQL constant；
- object creation；
- conditional symbol set。

---

## 3. Graph 資料模型

### 3.1 Node

四種 kind：

```text
Feature
EntryPoint
Code
Data
```

#### 3.1.1 Neo4j Node Property

| Property | Type | 說明 |
|---|---|---|
| id | string | `{kind}:{source}:{logicalKey}`，跨執行穩定 |
| projectId / graphVersion | string | 沿用 |
| kind | string | 四種 kind |
| role | string | GraphRoles 白名單 |
| name | string | 顯示名稱 |
| searchableText | string | BM25 去噪內容 |
| aliasesText | string | 別名 |
| language | string | business/csharp/frontend/sql |
| state | string | active/inactive/unresolved/shared |
| filePath/startLine/endLine | nullable | 主要 source |
| attributesJson | string | 有界屬性 |
| degree | int | Assembler 計算 |
| communityId | string? | 主要 C1/C2 |
| evidenceRef | string | Node id；SQLite 查詢鍵 |

### 3.2 Feature

#### 可執行葉子 Feature

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

Hierarchy 與 Feature 分開：

- LinkAddress 空的父選單不建 Feature。
- 父選單仍保留在 hierarchy model，供 C0/C1 parent mapping。

| role | 規則 |
|---|---|
| menu-feature | executable leaf |
| custom-report | V3 report template + menu mapping |
| approval-feature | 沿用 live DB |
| schedule | 沿用 live DB |
| batch-report | 沿用 live DB |

### 3.3 EntryPoint

| role | 規則 |
|---|---|
| controller-action | 保留 Feature 指向、Route、frontend-url 或確認 call chain 的 public action |
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

### 3.4 Code

roles 沿用 V3並新增必要細分：

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

#### degree=0 清理

可清除：

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

保留者標：

```text
state=unresolved
```

### 3.5 Frontend Module

- IsPage 支援：`view/views/page/pages/screen/screens`。
- 解析相對 ES import。
- 補 `.ts/.tsx/.js/.jsx/index.*`。
- npm import 忽略。
- page/module import 可達的 component 必須入圖。
- import path 必須正規化，大小寫與 Windows separator 行為固定。

### 3.6 Data

| role | 來源 |
|---|---|
| table/view | sys catalog |
| procedure | sys.objects type=P |
| function | sys.objects type=FN/TF/IF |
| report-template/csv-format/custom-enum 等 | live DB |

`.sql` 檔只補 source evidence；DB object 不因 Repository 缺 `.sql` 而缺 Node。

### 3.7 ID

```text
entry:page:{normalizedRelativePath}
data:sql:{db}/{schema}/function/{name}
```

路徑：

- `/` 分隔；
- 不含絕對路徑；
- 大小寫規則固定；
- 跨執行穩定。

### 3.8 Shared

初始門檻：

| Node | 條件 |
|---|---|
| frontend module | HANDLES/CALLS 不重複來源 ≥10 |
| EntryPoint | ROUTES_TO 不重複 Feature ≥10 |
| csharp Type | CALLS 不重複來源 Type ≥20 |

行為：

1. Local Search/C1 收錄 shared 本身。
2. 不以 shared 作為下一層展開起點。
3. 不透過 weight 懲罰使 shared 無法被收錄。
4. `attributes.sharedConsumerCount` 保存不重複 consumer。
5. AnalyzeImpact 折疊，不展開全部 consumer。

---

## 4. Edge 模型

### 4.1 Edge Kind

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

### 4.2 Neo4j Relationship Property

| Property | 說明 |
|---|---|
| id | SHA-256(source\0kind\0target) |
| graphVersion | 版本 |
| weight | 預計算排序權重 |
| confidence | certain/probable/inferred |
| evidenceCount | 去重 evidence 數 |
| reasonCode | 代表性 reason |
| topArtifact/topLine | 代表 source |
| sourceMethod/targetMethod | 代表 method pair，非完整清單 |

刪除：

```text
evidenceJson
sourceId relationship property
targetId relationship property
```

記憶體 GraphEdge 仍保留 SourceId/TargetId。

### 4.3 Weight

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

不使用：

- crossNamespaceFactor；
- sharedTargetFactor。

Weight 只排序，不證明 Edge 正確性。

### 4.4 Confidence 彙總

- 任一 certain evidence → certain。
- 否則任一 probable → probable。
- 否則 inferred。
- evidence 依 source/artifact/line/reasonCode 去重。
- 同一檔案被重複掃描不得提高 evidence bonus。

### 4.5 ReasonCode

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

- 未映射 reasonCode 的 Edge 不得 publish。
- 禁止以自由文字 reasonCode 逃避 validation。
- 新增 ReasonCode 必須同步改 SPEC、常數、測試與報告。

### 4.6 Edge 規則

| # | Edge | 規則 | ReasonCode |
|---|---|---|---|
| E1 | Feature → Code DISPATCHES_TO | PluginReport URL Base64 解 FQN；不存在建 unresolved stub；解碼失敗只 diagnostics | menu-link-base64 |
| E2 | Action → Page DISPATCHES_TO | `View("name")`/`View("name",model)`；Controller folder→Shared | roslyn-view-result |
| E3 | Page → Frontend HANDLES / Page → Action ROUTES_TO | `JSPath`/`AbsolutePath` 固定字串 | aspx-script-include |
| E4 | Frontend → Frontend CALLS | 相對 ES import | es-import |
| E5 | Child Table → Parent Table DEPENDS_ON | sys.foreign_keys | fk-constraint |
| E6 | SQL Module → Table READS/WRITES | sys dependency + sys.sql_modules + ScriptDom | sys-dependency/scriptdom-* |
| E7 | Code → Code CALLS | §2 Semantic/Heuristic 規則 | roslyn-invocation/naming-convention |

#### E2

解析：

```text
Views/{Controller}/{viewName}.aspx
Views/Shared/{viewName}.aspx
```

找不到不建 Edge，記 diagnostics。

#### E3

- `.js` 固定路徑 → HANDLES frontend module。
- `/Controller/Action/` → ROUTES_TO action。
- 動態 GetUIData 只連到 action，不解析回傳 script。
- E2/E3 形成雙向路徑時，BFS 必須用 visited set 防循環。

#### E6

1. sys.objects 建 object inventory。
2. sys.sql_expression_dependencies 找 dependency candidate。
3. sys.sql_modules.definition 交給 ScriptDom。
4. 證實 SELECT → READS。
5. 證實 INSERT/UPDATE/DELETE/MERGE → WRITES。
6. 無法分類：
   - 不得 certain；
   - 可 probable DEPENDS_ON；
   - 只有明確只讀語境才 probable READS。
7. Dynamic SQL、synonym、cross DB、temp table 記 known gap。

---

## 5. SQLite Evidence Store

### 5.1 Schema

```sql
CREATE TABLE IF NOT EXISTS graph_evidence (
    project_id    TEXT    NOT NULL,
    graph_version TEXT    NOT NULL,
    entity_id     TEXT    NOT NULL,
    entity_type   TEXT    NOT NULL,
    seq           INTEGER NOT NULL,
    source        TEXT    NOT NULL,
    confidence    TEXT    NOT NULL,
    artifact      TEXT    NOT NULL,
    reason        TEXT    NOT NULL,
    reason_code   TEXT    NOT NULL,
    start_line    INTEGER,
    end_line      INTEGER,
    details_json  TEXT,
    PRIMARY KEY (project_id, graph_version, entity_id, seq)
);

CREATE INDEX IF NOT EXISTS ix_graph_evidence_lookup
ON graph_evidence (project_id, graph_version, entity_id);
```

### 5.2 Store Contract

```text
WriteBatch
ReadByEntities
DeleteByVersion
CountByEntities
```

規則：

- 每 entity 最多 40 筆。
- WriteBatch 使用單一 transaction + prepared statement。
- ReadByEntities 一次查 selected entity ids。
- 禁止 N+1。
- details_json 保存完整 method pairs。
- Neo4j top method 只做預覽。

### 5.3 Publish

```text
1. 建立 graphVersion
2. SQLite transaction 寫 evidence
3. Neo4j 寫 staging graph
4. 驗證 Neo4j counts/weight/reasonCode
5. 對帳 SQLite evidence
6. 將 SQLite version 標記 evidence-ready
7. Promote Neo4j active pointer
8. Promote SQLite manifest metadata
9. 清理 retired version
```

#### Source of Truth

- 問答時 active graphVersion 以 Neo4j active pointer 為準。
- Evidence query 使用該 active graphVersion 直接查 graph_evidence。
- SQLite manifest 是索引狀態 metadata，不是 evidence 是否存在的唯一判斷。

#### 失敗處理

| 失敗點 | 行為 |
|---|---|
| 2～6 | 刪除新 graphVersion 的 SQLite evidence 與 Neo4j staging |
| 7 前 crash | 舊 Neo4j active 仍有效；新 version 視為 orphan 清理 |
| 7 成功、8 失敗 | 新 Evidence 已 evidence-ready；標 reconciliation-required；問答可依 Neo4j activeVersion 讀 Evidence |
| 8 成功、9 失敗 | 不影響問答；下次 reconciliation 清 retired |
| 新 active Evidence 不完整 | 在開始提供問答前回復舊 Neo4j active pointer，並清理新 version |

Host 啟動流程：

```text
Neo4j/SQLite manifest reconciliation
→ 驗證 active evidence-ready
→ 回復或補 promote
→ 清 orphan
→ 啟用問答端點
```

Cleanup 必須精確限定：

```text
projectId + graphVersion
```

---

## 6. Community

### 6.1 Tier

| Tier | 來源 | 數量 | 用途 |
|---|---|---:|---|
| C0 | Released Menu Hierarchy 頂層，不要求 LinkAddress | 8～25 | Global Search 模組漏斗 |
| C1 | Executable Feature/custom report/approval/schedule | ≥650 | 功能閉包 |
| C2 | 未被 C1 覆蓋的節點 | ≤100 | 非菜單模組 |

### 6.2 C1 閉包

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

- 由 certain/probable direct edge 可收錄。
- 即使低於該 Edge Kind 的一般門檻，仍可收錄一次。
- 收錄後停止，不從 shared 外擴。
- 不得用 shared 串接多個 C1。

Member 觸頂：

```text
truncated=true
```

不得靜默截斷。

### 6.3 C2

- 對未被 C1 覆蓋且未清理的 Node 執行。
- GDS Leiden 可用時使用 Leiden。
- 否則 deterministic label propagation。
- member<3 的社群不建立 CommunityReport。
- Node 本身仍可搜尋並可標 unresolved。

### 6.4 Community Property

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
```

主要 Community：

- 距離 Feature anchor 最近。
- 同距離依 communityId 字典序。
- 其他 membership 存 `alsoInCommunities`，最多 5。

### 6.5 Summary

| 階段 | 行為 |
|---|---|
| Graph publish | C0/C1/C2 全部建立 deterministic template，立即可問答 |
| C0 AI | 全部背景預熱 |
| C1 AI | 第一次命中時排入 |
| C2 AI | 第一次命中時排入，預設 template |
| AI 失敗 | 保留 template，summaryState=failed |

AI Summary 不得使 Project index unavailable。

UI 分開顯示：

```text
結構索引可用
AI 摘要背景補充中
```

---

## 7. 檢索

### 7.1 Local Search

```text
SeedLimit = 12
MaximumNodes = 80
MaximumEdges = 120
MaximumDepth = 3
NeighborsPerNode = 50
```

排序：

1. BM25 score。
2. Intent role relevance。
3. non-shared。
4. exact name/alias match。
5. 相同 state/role 內才以 degree tie-break。

不得讓 hub/helper 因 degree 高壓過業務精確命中。

BFS：

- 讀 Edge weight。
- shared 收錄、不外擴。
- visited key 至少包含 Node ID 與 traversal direction。
- 遇 E2/E3 雙向路徑不得震盪。

### 7.2 Evidence Hydration

```text
選定 <=80 Nodes / <=120 Edges
→ 蒐集 <=200 entity ids
→ 一次 ReadByEntities
→ 合併 top property + full evidence
→ SourceEvidenceReader
→ Context Compiler
```

Context 必須分區：

```text
certain
probable
inferred
source read failed
known gaps
```

Hydration timeout：

- 仍可用 Graph 結構回答。
- 明確聲明完整溯源暫時不可用。
- 不把 top method 說成完整清單。

### 7.3 AnalyzeImpact

- depth 1～2：直接影響。
- depth 3～4：間接影響。
- shared consumer 折疊。
- probable/inferred 不得稱為已確認。
- Table 問題提示 dynamic SQL、external report、trigger、cross DB gap。

### 7.4 Global Search

```text
Question
→ Neo4j server-side C0 scoring，取 1～2
→ 該 C0 的 C1 children + 全部 C2
→ template/AI summary
```

禁止每次將全部 C1 載入記憶體排序。

Neo4j index：

```text
(graphVersion, communityId)
(graphVersion, tier, parentCommunityId)
```

---

## 8. 索引管線

### 8.1 副檔名

```text
.cs .java .js .jsx .ts .tsx .sql .aspx
```

`.aspx` 必須同步加入：

- SupportedExtensions；
- FileSystemWatcher；
- LanguageForExtension（frontend）；
- Artifact manifest；
- safe source fallback；
- extractor DI；
- extractor/indexer version。

### 8.2 DB Fingerprint

至少包含：

- sys.objects：U/V/P/FN/TF/IF schema/name/type/modify_date；
- sys.foreign_keys；
- sys.sql_expression_dependencies；
- sys.sql_modules definition hash 或 modify_date；
- Menu Hierarchy；
- Executable Feature filter；
- approval/schedule/batch/report/CSV metadata。

任何子查詢失敗：

- 禁止 no-op；
- manifest Partial/Stale；
- 不假設 DB 未變。

### 8.3 Version

```text
CurrentIndexerVersion = graphrag-v4
GraphAssembler.SchemaVersion = 4.0
```

- 各 extractor ID/version +1。
- V3 Graph 全部失效重建。
- Body-delta 不得硬編碼 `csharp-roslyn-v3`。
- 刪除 evidenceJson、marker、舊 Community builder dead code。

### 8.4 階段效能記錄

每次 Full Index 記錄：

```text
scan
hash/fingerprint
syntax inventory
MSBuild load
synthetic compilation
semantic resolution
frontend/aspx
sql source
sql live DB
assemble
community template
SQLite evidence
Neo4j publish
peak working set
```

---

## 9. 實作 TODO

### Phase 0 — Baseline

- [ ] **T0.1** 記錄 V3 commit/snapshot、硬體、冷熱機、各階段耗時、peak RAM、Node/Edge/Evidence 大小。
- [ ] **T0.2** 建立 GraphRAG table allowlist；保存其他 table schema hash、row count、content hash。
- [ ] **T0.3** 建立 Named View 與 TSX import baseline。
- [ ] **T0.4** 建立 A15 Edge Precision fixture。
- [ ] **T0.5** 建立 A16 CALLS Golden Recall fixture，至少 100 組 internal caller→callee。

### Phase 1 — Model / Evidence

- [ ] **T1.1** GraphNode 新增 Degree/CommunityId/EvidenceRef；GraphEdge 新增 Weight/Confidence/EvidenceCount/ReasonCode/TopArtifact/TopLine/SourceMethod/TargetMethod。
- [ ] **T1.2** 新增 GraphRoles.Function、SharedThreshold、ReasonCode constants。
- [ ] **T1.3** GraphIdentity 新增 FrontendPageEntry、SqlFunction。
- [ ] **T1.4** 新增 graph_evidence 與 IGraphEvidenceStore WriteBatch/ReadByEntities/DeleteByVersion/CountByEntities。
- [ ] **T1.5** Neo4j 移除 evidenceJson/sourceId/targetId relationship property，寫新 property/index。
- [ ] **T1.6** Publish 實作 evidence-ready、reconciliation-required、rollback/recovery。
- [ ] **T1.7** Host 啟動完成 reconciliation 後才啟用 Project 問答。
- [ ] **T1.8** Context Compiler 批次 hydrate，禁止 N+1。

### Phase 2 — Extractors

- [ ] **T2.1** Menu Feature 與 Hierarchy query 分離。
- [ ] **T2.2** PluginReport Base64 FQN dispatch。
- [ ] **T2.3** sys.objects SP/function、FK、dependency、sys.sql_modules ScriptDom。
- [ ] **T2.4** C# Phase 1 全量 Syntax Inventory，不建 CALLS。
- [ ] **T2.5** MSBuildWorkspace optional authoritative project compilation。
- [ ] **T2.6** Synthetic fallback 與 §2.3 validation。
- [ ] **T2.7** Heuristic probable 嚴格符合 §2.4。
- [ ] **T2.8** Conditional compilation active branch 與 coverage diagnostics。
- [ ] **T2.9** Memory preflight、project-boundary batching、runtime retry。
- [ ] **T2.10** Named View metadata。
- [ ] **T2.11** AspxGraphExtractor 與 DI/extension/watcher/language registration。
- [ ] **T2.12** Frontend page folders + relative ES import。
- [ ] **T2.13** 所有 Edge ReasonCode 映射，未映射 publish fail。

### Phase 3 — Assembler / Community

- [ ] **T3.1** Edge evidence 去重、confidence、weight、top evidence。
- [ ] **T3.2** Degree/shared/consumer count。
- [ ] **T3.3** 分級孤立清理，高價值孤立 unresolved。
- [ ] **T3.4** C0 hierarchy、C1 relation-aware closure、C2 Leiden/fallback。
- [ ] **T3.5** Community deterministic ID、primary/alsoIn。
- [ ] **T3.6** Template immediate。
- [ ] **T3.7** C0 AI 預熱、C1/C2 按需。

### Phase 4 — Retrieval

- [ ] **T4.1** EdgeWeight hardcode 改讀 property。
- [ ] **T4.2** shared/visited/direction/tie-break。
- [ ] **T4.3** Global server-side C0→C1/C2。
- [ ] **T4.4** AnalyzeImpact fold/confidence。
- [ ] **T4.5** Batch hydration。
- [ ] **T4.6** Evidence/visualization API 改 SQLite。

### Phase 5 — Indexing / Version

- [ ] **T5.1** V4 indexer/schema/extractor version。
- [ ] **T5.2** DB fingerprint 擴充。
- [ ] **T5.3** 保守 body-delta：C# body 變更重抽完整 C# Graph。
- [ ] **T5.4** Body-delta 與 clean full digest 一致測試。
- [ ] **T5.5** `.aspx` manifest/watcher/fallback。
- [ ] **T5.6** 刪除 V3 dead code，grep 驗證。

### Phase 6 — Tests / Acceptance

- [ ] **T6.1** E1～E7、weight、confidence、shared、Community、Evidence 單元測試。
- [ ] **T6.2** MSBuild/Synthetic agreement/disagreement/fallback 測試。
- [ ] **T6.3** Conditional compilation active/disabled branch 測試。
- [ ] **T6.4** Memory preflight/batch fallback 測試。
- [ ] **T6.5** Full/no-op/body-delta/publish failure/crash reconciliation 整合測試。
- [ ] **T6.6** A1～A16。
- [ ] **T6.7** B1～B7。
- [ ] **T6.8** C1～C8。
- [ ] **T6.9** Q1～Q5。
- [ ] **T6.10** D1～D5 與報告。

---

## 10. 驗收

### 10.1 Graph 覆蓋率與精度

| # | 指標 | 門檻 |
|---|---|---|
| A1 | menu-feature | 等於 executable leaf SQL |
| A2 | active/resolved menu孤立率 | <5%；unresolved 分列，不得假連 |
| A3 | 低價值 Code degree=0 | 0；高價值 unresolved 附清單 |
| A4 | procedure+function | ≥250 |
| A5 | FK DEPENDS_ON | ≥440 |
| A6 | PluginReport dispatch | ≥24；stub unresolved |
| A7 | frontend-page | ≥600 |
| A8 | Named View | ≥T0.3 baseline 90%，目標約550 |
| A9 | 另類基金 Feature→Page→JS | hop≤4 |
| A10 | 利息收入 ReportKernel→Data | 路徑存在 |
| A11 | TSX reachable | ≥T0.3 baseline 90%，目標約500 |
| A12 | Edge weight/confidence/reasonCode | 100% |
| A13 | Neo4j 無 evidenceJson/sourceId/targetId relationship property | 100% |
| A14 | RMDAL Code | ≥500，抽樣有 CALLS/Data relation |
| A15 | Edge Precision | certain≥95%；probable≥85% |
| A16 | Internal CALLS Golden Recall | certain+probable 命中 ≥90% |

#### A15

- certain 與 probable 各抽至少 50 條。
- 覆蓋 CALLS/READS/WRITES/ROUTES_TO/DISPATCHES_TO。
- probable target 錯誤仍算錯誤。
- 非目標情境只能在抽樣前依 golden fixture 明確排除，不能驗收後剔除失敗案例。
- 每筆記 source/target/reasonCode/artifact/人工判定。

#### A16

- T0.5 在 Phase 2 前建立至少 100 組 internal caller→callee。
- Golden pair 來源：
  - Roslyn 真實 project navigation；
  - 人工確認 source；
  - 既有可重現流程。
- Dynamic/reflection/WCF/MQ 可在 fixture 建立時標記 non-goal，不進 denominator。
- 實作完成後不得因未命中才把 pair 改為 non-goal。
- certain+probable 命中率 ≥90%。
- Miss 分類：
  - project load；
  - missing reference；
  - conditional；
  - batching；
  - extractor bug。

### 10.2 Community

| # | 指標 | 門檻 |
|---|---|
| B1 | C0 | 8～25 |
| B2 | C1 | ≥650 |
| B3 | C1 member | 3～60；超過不得發生；truncated 可見 |
| B4 | C2 | ≤100 且 member≥3 |
| B5 | connected Feature/EntryPoint/Code communityId | ≥90% |
| B6 | C1 parent 指向 C0 | 100% |
| B7 | shared 不作跨社群中介 | 抽樣 100% 符合 |

### 10.3 Storage / Performance

| # | 指標 | 門檻 |
|---|---|
| C1 | Neo4j relationship property size | 較 V3 evidenceJson 降低≥80% |
| C2 | SQLite/Neo4j Evidence 對帳 | 抽樣100 entity，100% |
| C3 | Full Index | 依分級門檻 |
| C4 | no-op | 無變更不重建 |
| C5 | Local Search P95 | ≤2秒 |
| C6 | Hydration | 單次 batch，不得 N+1 |
| C7 | Community template | Graph publish 後立即可用 |
| C8 | AI failure | 不使 index unavailable |

#### C3 分級

| 倍率 | 判定 |
|---|---|
| ≤1.5× | PASS（目標） |
| >1.5× 且 ≤2.0× | PASS with warning；報告需附階段分析 |
| >2.0× 且 ≤2.5× | CONDITIONAL；需效能分析與產品負責人明確核准 |
| >2.5× | FAIL |

基線必須同機、同 source snapshot、同 DB、同冷/熱機條件。

Local Search：

- Q1～Q5 + 15 題擴充集。
- warmup 5 次。
- 每題 10 次。
- P95 ≤2秒。

### 10.4 問答品質

| # | 問題 | 期望 |
|---|---|---|
| Q1 | 另類基金畫面加欄位要改哪些檔案？ | ASPX/JS + Controller + Data |
| Q2 | 利息收入增減分析數字不對？ | ReportKernel + READS/WRITES |
| Q3 | tblPosition105 加欄位影響？ | Reverse Data + FK + shared fold |
| Q4 | 公告管理驗證改動前端影響？ | TSX→component→Action |
| Q5 | Bloomberg 匯率排程在哪？ | C2 + scheduled-task |

每題保存：

```text
seed
graph path
hydrated evidence
source snippet
certain/probable/inferred
known gaps
人工 1～5
```

至少 4/5 題達 4 分；不得將錯誤檔案或方法標為 certain。

### 10.5 Cleanup / Safety

| # | 指標 | 門檻 |
|---|---|
| D1 | Neo4j | 只保留最後 active version |
| D2 | SQLite | 無 retired/orphan evidence |
| D3 | 非 GraphRAG table | schema/row count/content hash 不變 |
| D4 | 暫存檔 | 報告外清除 |
| D5 | Publish failure | 舊 active 可查，新 staging 可回收或回復 |

---

## 11. 驗收報告

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
- V3 baseline vs V4
- 是否使用 MSBuild
- 是否觸發 memory fallback

## 1. A1～A14 Coverage
## 2. A15 Edge Precision
## 3. A16 CALLS Recall
## 4. B1～B7 Community
## 5. C1～C8 Storage/Performance
## 6. Q1～Q5 Answer Quality
## 7. D1～D5 Cleanup/Safety
## 8. Diagnostics 統計
## 9. FAIL/Warning/Conditional 處置
## 10. Known Gaps
```

驗收腳本：

```text
scripts/dev/graphrag-v4-acceptance.ps1
```

規則：

- 一般驗證只執行唯讀 Cypher、SQL、SQLite query。
- 清理只使用 GraphRAG allowlist。
- A/B/C/D 自動填寫；A15/A16/Q 含人工或 golden fixture。
- 任何 FAIL 不得合併。
- C3 Conditional 必須取得產品負責人明確核准。

---

## 12. 風險與已知限制

1. **Synthetic 不等於真實 Build**  
   外部 DLL、DefineConstants 與 Project reference 可能缺失。成功 MSBuild Project result
   優先；Synthetic 只 fallback。

2. **MSBuildWorkspace 可能無法載入舊專案**  
   失敗不得阻止索引，但會降低 certain CALLS coverage；A16 必須反映。

3. **全量 C# Semantic 成本**  
   9,638 檔可能超過 V3 兩倍。C3 分級與每階段量測不可省略。

4. **記憶體降級會降低 Recall**  
   跨 batch 只能 probable，需在報告列出 fallback 與 A16 miss。

5. **條件編譯只索引 active branch**  
   其他 symbol set 不索引，回答需聲明。

6. **Body Delta 保守重抽 C#**  
   日常 C# 修改仍有成本，但避免 stale CALLS。Digest 最佳化延後。

7. **SQLite/Neo4j 無 distributed transaction**  
   依 evidence-ready、activeVersion 與 startup reconciliation 保證。

8. **ASPX 動態 expression**  
   只解析固定字串。

9. **Dynamic SQL/cross DB/synonym**  
   可能漏掉 Data Edge。

10. **WCF/MQ/Reflection 非目標**  
    高價值孤立保留 unresolved。

11. **Shared 門檻為初值**  
    只依 Q3/B7 調整常數。

12. **AI Summary 是附加能力**  
    Template 永遠先可用，C1/C2 不全量預熱。

---

## 13. 定案條件

本 V1.2 在以下事項獲得產品負責人確認後改為「已定案」：

1. V4 第一版不採 invocation-digest 跳過 C# semantic re-extraction。
2. MSBuild 成功時為 authoritative，Synthetic 為 fallback。
3. Conditional compilation 只索引 active branch。
4. 記憶體降級先依 Project boundary。
5. Publish step 7/8 failure 依 reconciliation 規則處理。
6. A16 CALLS Recall 為強制驗收。
7. C3 採 Target/Warning/Conditional/Fail 四級門檻。

確認後才能開始 Phase 1；不得以「V1.1 已定案」為由跳過 V1.2 的精度與一致性條款。
