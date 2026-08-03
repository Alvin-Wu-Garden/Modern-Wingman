# GraphRAG V4 破壞性重構規格書 V1.1（已定案）

> 狀態：**已定案，供實作使用**
> 版本：V1.1
> 日期：2026-07-29
> 作者：產品負責人＋GitHub Copilot 技術審查
>
> **版本歷史**
> | 版本 | 日期 | 說明 |
> |---|---|---|
> | V4 草稿 | 2026-07-29 | 初版，修正 V3 實測問題 |
> | V1 | 2026-07-29 | 修正 semantic CALLS 精度、weight 公式、hierarchy 分離、孤立清理、AI Summary 策略、A15 精度驗收 |
> | **V1.1（本版）** | 2026-07-29 | 定案以下三項：(1) C3 門檻放寬至 ≤2.5×；(2) body-delta 改為 invocation-digest 判斷；(3) Synthetic Compilation 為主、MSBuildWorkspace 可選；確認使用單一大 Compilation 策略；補充重複型別/條件編譯/缺失 reference/跨 Compilation 的精度標示規則；A15 精度驗收範圍明確化 |
>
> **本文件為唯一實作依據。**
> 嚴禁參考其他 doc（`graphrag-refactor-spec.md`、
> `graphrag-v4-refactor-spec.md`、`graphrag-v4-refactor-spec_V1.md`、
> `Modern Wingman System Prompt.md` 等）。
> 若本文件與程式碼現況衝突，以本文件為準。
> V3 未被本文件推翻的行為（staging/atomic publish、no-op fast path、
> DPAPI 憑證、manifest 對帳）一律保留。

---

## 0. 重構動機（實測數據佐證）

以下數據來自 2026-07-29 對 FBL 專案的實際量測
（Neo4j 版本 `9c017d…`、SQL Server `FBL_SPV_SIT`、原始碼 `D:\FBL_Release_Trunk`）：

| # | 問題 | 實測證據 |
|---|---|---|
| P1 | 大型 repo 檔名 marker 過濾器砍掉 DAL 層 | 9,638 個 .cs 只有 3,036 個（31.5%）命中 marker；被排除的 2,283 個屬於 RMDAL/RMQuery/Provider 等資料存取層 |
| P2 | Code:type 節點大量孤立 | 5,096 個 type 節點中 3,600 個（71%）度數為 0，純雜訊 |
| P3 | 菜單雜訊 | 1,013 個 menu-feature 中 539 個（53%）孤立、642 個 inactive；過濾條件可將 1,497 筆縮至 698 筆 |
| P4 | SP 幾乎沒抽到 | 圖中 Data:procedure 只有 16 個；DB 實際有 109 SP + 105 TVF + 48 scalar function |
| P5 | FK 未利用 | DB 有 463 條 FK；圖中 Data→Data DEPENDS_ON 只有 19 條 |
| P6 | 邊 evidence 冗餘 | evidenceJson 平均 717 B/邊、P95 2,864 B、最大 85 KB、全圖 16 MB |
| P7 | 社群不可用 | primary 只有 3 個超大社群；secondary 有 1,930 個碎片（平均 2-3 成員） |
| P8 | PluginReport 菜單假連通 | 39 個 plugin 菜單全部 ROUTES_TO 同一個入口；Base64 payload 未解碼，與 `*_ReportKernel` 類斷鏈 |
| P9 | .aspx 完全未索引 | 638 個 .aspx 不在掃描副檔名內；menu→frmalternativefund.js 路徑數 = 0 |
| P10 | `return View("x")` 未抽 | 650 處 named View 呼叫沒有對應邊 |
| P11 | React tsx 覆蓋不足 | 562 個 tsx 只有 151 個入圖；IsPage 只認複數資料夾；無 import 邊 |

### 0.1 已定案的產品決策

| 決策 | 內容 |
|---|---|
| 共用元件影響分析 | 折疊為「另影響 N 個共用元件使用者」，不展開明細 |
| ExtJS hierarchy | 不做，維持每檔案一個前端模組節點 |
| 檢索架構 | 維持 deterministic single-pass；不導入 Agentic tool loop |
| Community summary | deterministic template 立即可用；AI 潤飾為背景按需，不阻塞問答 |
| Semantic Compilation | 單一大 Synthetic CSharpCompilation；MSBuildWorkspace 可選增強 |
| C# CALLS 精度 | 唯一 symbol resolution → certain；僅 repo 內唯一同名型別 → probable；多候選不建邊 |
| C3 效能門檻 | 全量索引 ≤ V3 基線 × 2.5 |
| Body-delta 觸發條件 | invocation-digest（跨 type invocation candidate 清單 hash）改變才觸發語意重算 |

---

## 1. 範圍與非目標

### 1.1 範圍

1. 節點抽取規則重寫（四種 kind 不變，抽取條件全面修訂）。
2. 邊 payload 瘦身：evidence 移至 SQLite，Neo4j 保留結構化小欄位＋預計算 weight。
3. 新增三類鏈路：ASPX 前端頁面、PluginReport Base64 動態分派、FK/SP/function 資料庫依賴。
4. C0/C1/C2 三層階層社群。
5. 檢索端改讀 edge weight、處理 shared 折疊、批次補載 evidence。
6. Community deterministic template 立即產生，AI Summary 改為分層按需。
7. DB fingerprint 補 FK/function/module dependency。
8. 覆蓋率、精度、效能、清理與結構化報告驗收。

### 1.2 非目標（明確不做）

- ❌ Method 級節點（方法名保留在 Edge 代表欄位與 SQLite evidence）
- ❌ Embedding／向量檢索（維持 BM25）
- ❌ Agentic 多輪工具迴圈
- ❌ ExtJS component hierarchy
- ❌ WCF／RealTimeService／MQ 跨進程呼叫鏈
- ❌ CFG/DFG、完整變數資料流、TESTS 邊
- ❌ 為通過節點/邊數驗收而建立無 evidence 的 synthetic edge
- ❌ MSBuildWorkspace 強制依賴（可選增強，不是主路徑）

### 1.3 破壞性授權與資料保護

- Modern Wingman 尚未發佈：允許不向後相容。graphVersion 直接換代，不寫 V3→V4 遷移。
- V4 首次索引全量重建。
- 測試期間：可清除 GraphRAG 自有的 Neo4j graphVersion 與 SQLite graph tables。
- **PAT、OAuth、使用者設定、對話與其他非 GraphRAG 資料表一律不得清除或改 schema。**
- 清理腳本必須使用 GraphRAG table allowlist，不得使用「除 PAT 外全部刪除」類黑名單。

---

## 2. C# Semantic Compilation 策略（V1.1 新增章節）

### 2.1 主策略：單一大 Synthetic CSharpCompilation

```
CSharpCompilation.Create(
    assemblyName: "GraphRagSynthetic",
    syntaxTrees: [ 所有已通過 ShouldIgnore/LooksGenerated 篩選的 .cs SyntaxTree ],
    references: [ mscorlib / System.* / netstandard 基本 metadata reference ],
    options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                 allowUnsafe: true, reportSuppressedDiagnostics: false)
)
```

- **只加基本 runtime reference**，不嘗試解析外部業務 DLL（PlugIn_Report_FBL.dll、Telerik 等）。
- 外部 symbol 解析失敗是預期行為，不中斷 Compilation。
- Compilation 建立後按需取得 SemanticModel（`compilation.GetSemanticModel(tree)`），不預先全部取得。
- 整個 Compilation 建立完成後才開始 Phase 2 invocation resolution，不交錯進行。

### 2.2 MSBuildWorkspace 可選增強

- 僅在 `CSharpGraphExtractorOptions.EnableMSBuildEnhancement = true`（預設 false）時啟用。
- 啟用時，用 MSBuildWorkspace 取得的 Compilation 與 Synthetic Compilation **並行驗證**：
  若兩者 symbol resolution 結果不同，以 Synthetic 為準，差異記入 diagnostics。
- MSBuildWorkspace load 失敗不中斷索引，降級回 Synthetic。

### 2.3 解析品質標示規則（invocation candidate 的分類）

| 情境 | 處理方式 | confidence |
|---|---|---|
| Symbol resolution 唯一成功 | 建立 CALLS，reasonCode = `roslyn-invocation` | `certain` |
| repo 內唯一同名型別（heuristic） | 建立 CALLS，reasonCode = `naming-convention` | `probable` |
| **重複型別**（同 namespace/name 宣告在多個 SyntaxTree） | 不建邊；diagnostics 記錄 ambiguous symbol | — |
| **條件編譯**（invocation 在 `#if` 分支內，preprocessor 未定義） | 建立 CALLS；confidence = `inferred`；reasonCode = `roslyn-invocation`；detail 標 `conditionalCompilation=true` | `inferred` |
| **缺失 reference**（外部 DLL symbol 無法解析） | 不建邊；diagnostics 記錄 external reference miss | — |
| **跨 Compilation 呼叫**（啟用多批次時的跨批邊界） | 本版單一 Compilation 無此情境；若未來改分批，跨批 CALLS 降為 `probable` | — |
| Dynamic call / reflection / expression tree | 不建邊；diagnostics 記錄 | — |
| 多 candidate（overload/interface ambiguity） | 不建邊；diagnostics 記錄 | — |
| Generated code（`ShouldIgnore`/`LooksGenerated` 判定） | 整個檔案跳過，不產生任何節點或邊 | — |

### 2.4 Invocation Digest（body-delta 觸發條件）

Invocation digest 定義：對單一 `.cs` 檔案做 **syntax-only** 掃描（不建 semantic model），
收集所有 `InvocationExpressionSyntax` 的：

```
{ ReceiverTypeName, MethodName, ArgumentCount, LineNumber }
```

將上述清單排序後取 SHA-256，作為該檔案的 **invocation-digest**。

Body-delta 觸發規則：

| 變更類型 | 觸發條件 | 行為 |
|---|---|---|
| 只有 method body 內的非 invocation 程式碼改變（字串、條件、局部變數等） | invocation-digest 不變 | delta（跳過語意重算，沿用既有 graph） |
| 新增/刪除/改名跨 type 呼叫 | invocation-digest 改變 | 觸發完整 C# semantic 重算 |
| 宣告面（class/method 簽名）改變 | declaration-surface hash 改變（既有機制） | 觸發完整 C# semantic 重算 |
| ASPX、DB fingerprint、route、extractor/schema 版本改變 | 獨立判斷 | 觸發完整對應 extractor 重算 |

**體驗說明**：工程師只改 method body 裡的邏輯（90% 的日常修改）不會觸發昂貴的語意重算；
只有新增/移除跨類別呼叫才觸發，符合「改了什麼才重算什麼」的直覺。

### 2.5 記憶體與效能約束

- SyntaxTree 建立後盡快重用，不多次 parse 同一檔案。
- Phase 1（全量 syntax inventory）完成後，無法再使用的 SyntaxTree 允許 GC。
- `CSharpCompilation` 在 Phase 2 與 Phase 3 保持存活；Phase 3 完成後釋放。
- 平行度：`Environment.ProcessorCount`（可由 `CSharpGraphExtractorOptions.MaxParallelism` 覆蓋）。
- peak working set 必須在 T0.1 基線與 V4 全量索引時各量測一次並記入驗收報告（C3）。
- 若 peak RAM 超過 8 GB，自動降級為「按頂層目錄分批」策略並記入 diagnostics；
  分批策略下跨批 CALLS 降為 probable，不視為 bug。

---

## 3. 資料模型規格

### 3.1 節點（GraphNode）

四種 kind 不變：`Feature`、`EntryPoint`、`Code`、`Data`。

#### 3.1.1 共通 Neo4j Node Property

| 屬性 | 型別 | 說明 |
|---|---|---|
| `id` | string | 確定性 ID：`{kind}:{source}:{logicalKey}`，跨執行穩定 |
| `projectId` / `graphVersion` | string | 沿用 V3 |
| `kind` | string | Feature / EntryPoint / Code / Data |
| `role` | string | GraphRoles 白名單 |
| `name` | string | 人類可讀短名稱 |
| `searchableText` | string | BM25 去噪文字 |
| `aliasesText` | string | 別名串接 |
| `language` | string | business / csharp / frontend / sql |
| `state` | string | `active` / `inactive` / `unresolved` / `shared` |
| `filePath` / `startLine` / `endLine` | string?/int? | 主要原始碼位置 |
| `attributesJson` | string | 有界結構化屬性，含新增欄位 |
| `degree` | int | **新增**：Assembler 計算總度數 |
| `communityId` | string? | **新增**：主要 C1/C2 社群 ID，O(1) 讀取 |
| `evidenceRef` | string | **變更**：SQLite 查詢鍵（= node id），不再存 evidenceJson |

#### 3.1.2 Feature 抽取規則

**可執行葉子功能過濾（Feature 節點用）：**

```sql
SELECT ID, Parent, Name, LinkAddress
FROM dbo.tblMenuMap
WHERE Released = 1
  AND ISNULL(LinkAddress, '') <> ''
  AND ID NOT LIKE '88%'
```

**Menu Hierarchy 查詢（C0/C1 階層用，與 Feature 過濾分開）：**

```sql
SELECT ID, Parent, Name, Released, LinkAddress
FROM dbo.tblMenuMap
WHERE Released = 1
  AND ID NOT LIKE '88%'
```

Hierarchy rows 只用於建立 C0/C1 parent mapping；`LinkAddress` 為空的父選單
不建 Feature 節點，但不因 Feature 過濾條件而從 Hierarchy 中消失。

| role | 來源 | 規則 |
|---|---|---|
| `menu-feature` | tblMenuMap | 只取可執行葉子（上方 SQL）；`attributes.parentMenuId` 保存 Parent |
| `custom-report` | report template + 菜單 regex | 沿用 V3 |
| `approval-feature` / `schedule` / `batch-report` | live DB | 沿用 V3 |

#### 3.1.3 EntryPoint 抽取規則

| role | 來源 | 規則 |
|---|---|---|
| `controller-action` | Roslyn | 保留：被 Feature 指向、有可解析 Route、被 frontend-url 指向、有確認 HANDLES/CALLS 鏈的 public action。未被 Feature 指向但有 Route 的 action 標 `state=unresolved`；完全無 route/feature/frontend/call evidence 者收進 Controller `attributes.actions` 有界清單 |
| `frontend-page` | AspxGraphExtractor | 所有未排除目錄內的 `.aspx` 建節點；name = 去副檔名檔名；attributes 含 `viewName`、`controllerFolder`、`includedScripts` |
| `scheduled-task` | Roslyn TaskName 常值 | 沿用 V3 |

#### 3.1.4 Code 抽取規則

##### C# Type：三階段 Roslyn

**階段一：全量 Syntax Inventory（syntax-only，所有 .cs 檔）**

- 建立全部 .cs 的 SyntaxTree（已排除 generated、designer、`bin/obj/packages` 等）。
- 收集 type declaration inventory、namespace、base type syntax、public route/action candidate。
- 收集每個檔案的 **invocation-digest**（§2.4）。
- **不建立任何 CALLS 邊**（syntax-only 無法保證解析正確性）。

**階段二：Semantic CALLS Resolution（單一大 Synthetic Compilation）**

- 建立 §2.1 所定義的單一大 CSharpCompilation。
- 對每個 invocation candidate 取 SemanticModel，依 §2.3 規則分類：
  - unique symbol → `certain` CALLS
  - repo 內唯一同名 → `probable` CALLS（reasonCode = `naming-convention`）
  - 其餘（多候選/動態/缺失 reference）→ 不建邊，記 diagnostics
- 同一 `(SourceId, TargetId)` 對若產生多筆 evidence，記入 evidenceCount，不重複建邊。

**階段三：Reachable Deep Attributes**

- 從 EntryPoint、Feature-linked Controller、scheduled-task、report-plugin 與高價值 repository root 出發。
- 沿 HANDLES/CALLS 展開可達 Type。
- 只對可達 Type 補完整 `methods`/`baseTypes`/`constructorDependencies`（有界清單，不存原始碼）。

##### degree=0 孤立清理分級

**允許清除（assembler 最後刪除）：**
- Generic type / DTO / value object（無業務角色且無任何關係）
- 確認為 generated code 的殘留節點
- 無任何關係的 frontend module

**不得只因 degree=0 刪除（保留並標 `state=unresolved`）：**
- controller / business-service / repository / report-plugin / scheduled-task 相關 Type
- 可搜尋名稱明確且有 source evidence 的高價值程式
- *說明：WCF/MQ/Reflection 等非目標鏈路會造成合法的孤立；degree=0 不等於沒有業務價值*

##### Frontend Module

- `IsPage` 資料夾辨識包含：`view/views/page/pages/screen/screens`（單複數都認）。
- 解析相對路徑 ES import（`import x from './…'` 或 `'../…'`），補 `.ts/.tsx/.js/.jsx/index.*` 副檔名。
- npm package import 一律忽略。
- 可由 page/module import 鏈到達的 component 必須入圖（解 P11 的 411 個被丟棄 tsx）。
- 無 import、URL、page 或其他關係的 module 由 Assembler 孤立清理（需在上述低價值分級內）。

#### 3.1.5 Data 抽取規則

| role | 來源 | 規則 |
|---|---|---|
| `table` / `view` | sys catalog | 沿用 V3 |
| `procedure` / `function`（新增 role） | **sys.objects 為權威來源** | type IN (`P`,`FN`,`TF`,`IF`) 全量建節點（109 SP + 105 TVF + 48 scalar function）；`.sql` 檔案存在時才補 filePath/行號，不存在不缺節點 |
| 其餘（report-template/csv-format/custom-enum 等） | live DB | 沿用 V3 |

#### 3.1.6 節點 ID 規則（新增部分）

```
entry:page:{normalizedRelativePath}
data:sql:{db}/{schema}/function/{name}
```

路徑規則：正斜線分隔、大小寫正規化、不含絕對路徑、跨執行穩定。

#### 3.1.7 `state=shared` 判定與行為

**自動判定門檻（Assembler 階段，集中於 `GraphSharedNodeThresholds`）：**

| 節點類型 | 判定條件 |
|---|---|
| Code(frontend module) | HANDLES/CALLS 不重複來源數 ≥ 10 |
| EntryPoint | ROUTES_TO 不重複 Feature 來源數 ≥ 10 |
| Code(csharp type) | CALLS 不重複來源 Type 數 ≥ 20 |

**行為：**

1. C1 閉包展開與 Local Search BFS：收錄 shared 節點本身，但**不以 shared 節點作為下一層展開的中介**。
2. shared 節點的 weight 不得因 `state=shared` 而被降低到無法進入結果集。
3. `attributes.sharedConsumerCount` 保存不重複入邊來源數。
4. 影響分析（AnalyzeImpact）輸出：折疊為「另影響 N 個共用元件使用者」，不展開全部 consumer。

---

### 3.2 邊（GraphEdge）

九種 kind 不變：`ROUTES_TO`、`HANDLES`、`CALLS`、`DISPATCHES_TO`、`TRIGGERS`、`READS`、`WRITES`、`MAPS_TO`、`DEPENDS_ON`。

#### 3.2.1 Neo4j Relationship Property（全面替換 V3 evidenceJson）

| 屬性 | 型別 | 說明 |
|---|---|---|
| `id` | string | SHA-256(source\0kind\0target) |
| `graphVersion` | string | 沿用 V3 |
| `weight` | float | 索引時預計算（§3.2.2），供排序 |
| `confidence` | string | `certain` / `probable` / `inferred` |
| `evidenceCount` | int | 去重後 evidence 數 |
| `reasonCode` | string | 封閉清單代碼（§3.2.3） |
| `topArtifact` | string | 代表性檔案或 DB logical key |
| `topLine` | int? | 代表性行號 |
| `sourceMethod` / `targetMethod` | string? | 代表性 method pair（不代表完整清單） |
| ~~`evidenceJson`~~ | — | **刪除**，移至 SQLite |
| ~~`sourceId` / `targetId` property~~ | — | **刪除**（端點由 Neo4j topology 表示） |

#### 3.2.2 Weight 計算公式

```
weight = clamp(
    base(kind) × confidenceFactor × evidenceBonus,
    0.05,
    1.0
)

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
  certain   1.00
  probable  0.90
  inferred  0.75

evidenceBonus:
  去重後 evidenceCount >= 3 → 1.05（封頂 1.0）
  否則 → 1.00
```

**明確不包含（已刪除）：**
- `crossNamespaceFactor`：跨 namespace 往往是最重要的 Controller→Service→DAL 鏈路，不得懲罰。
- `sharedTargetFactor`：shared 由 traversal 規則控制，不得讓節點無法進入結果。

#### 3.2.3 ReasonCode（封閉清單）

```
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

**規則：**
- 所有 extractor 在實作前必須完成 reasonCode 映射表。
- 未映射 reasonCode 的 Edge 不得 publish（ValidationError）。
- 禁止用自由文字 reasonCode 規避白名單。
- 新增值需同步修改本 SPEC。

#### 3.2.4 Confidence 彙總規則

同一 Edge 多份 evidence 合併時：
- 有任何 `certain` evidence → `certain`
- 否則有 `probable` → `probable`
- 否則 → `inferred`
- evidenceCount 須先依 `(source, artifact, line, reasonCode)` 去重再計算。

#### 3.2.5 新增／修訂邊規則

| # | 邊 | 方向 | 規則 | reasonCode |
|---|---|---|---|---|
| E1 | `DISPATCHES_TO` | Feature(menu) → Code(type) | 解析 `/PluginReport/MenuIndex/{base64}` → Base64 解碼 → `{dll}/{FQN}` → 指向 `code:csharp:{FQN}`；找不到時建 `state=unresolved` stub Code 節點並掛邊；解碼失敗只記 diagnostics | `menu-link-base64` |
| E2 | `DISPATCHES_TO` | EntryPoint(action) → EntryPoint(page) | Roslyn 抽 `return View("name")` 與 `View("name", model)`；解析順序：`Views/{Controller}/{name}.aspx` → `Views/Shared/{name}.aspx`；找不到 → 不建邊，記 diagnostics | `roslyn-view-result` |
| E3 | `HANDLES` / `ROUTES_TO` | EntryPoint(page) → Code(frontend) 或 action | 解析 `JSPath("…")` / `AbsolutePath("…")` 固定字串：`.js` 結尾 → `HANDLES` 指向前端模組；`/Controller/Action/` 形式 → `ROUTES_TO` 指向 action；動態 GetUIData 只建到 action 為止 | `aspx-script-include` |
| E4 | `CALLS` | Code(frontend) → Code(frontend) | 相對路徑 ES import，補 `.ts/.tsx/.js/.jsx/index.*`；npm import 忽略 | `es-import` |
| E5 | `DEPENDS_ON` | Data(child table) → Data(parent table) | `sys.foreign_keys` 全量建立，去重 | `fk-constraint` |
| E6 | `READS` / `WRITES` | Data(procedure/function/view) → Data(table) | `sys.sql_expression_dependencies` 產生候選；讀取 `sys.sql_modules.definition` 並以 ScriptDom 判讀 SELECT/INSERT/UPDATE/DELETE/MERGE/EXEC；確認寫入 → `WRITES`；確認讀取 → `READS`；dependency 存在但 definition 無法解析 → `probable DEPENDS_ON` 或 `probable READS`，confidence 不得標 `certain`；ScriptDom 補 `.sql` 行號 | `sys-dependency` / `scriptdom-read` / `scriptdom-write` |
| E7 | `CALLS` | Code → Code | Roslyn 三階段（§3.1.4）；記 `sourceMethod`/`targetMethod` | `roslyn-invocation` / `naming-convention` |

**E3 雙向環說明：** action→page（E2, DISPATCHES_TO）與 page→action（E3, ROUTES_TO）構成雙向路徑。
BFS 的 hopDecay 會壓制往返震盪，不視為 bug；但實作需確認 BFS 不以此為無限迴圈觸發條件。

---

### 3.3 SQLite Evidence Store

```sql
CREATE TABLE IF NOT EXISTS graph_evidence (
    project_id    TEXT    NOT NULL,
    graph_version TEXT    NOT NULL,
    entity_id     TEXT    NOT NULL,   -- node id 或 edge id
    entity_type   TEXT    NOT NULL,   -- 'node' | 'edge'
    seq           INTEGER NOT NULL,
    source        TEXT    NOT NULL,   -- ast / framework / db-metadata / ...
    confidence    TEXT    NOT NULL,
    artifact      TEXT    NOT NULL,
    reason        TEXT    NOT NULL,   -- 繁中說明，僅溯源 UI 顯示用
    reason_code   TEXT    NOT NULL,   -- reasonCode enum
    start_line    INTEGER,
    end_line      INTEGER,
    details_json  TEXT,               -- 完整 method pairs 等補充資料
    PRIMARY KEY (project_id, graph_version, entity_id, seq)
);

CREATE INDEX IF NOT EXISTS ix_graph_evidence_lookup
    ON graph_evidence (project_id, graph_version, entity_id);
```

**規則：**
- 每 entity evidence 上限 40 筆。
- WriteBatch 使用單一 SQLite transaction 與 prepared statement。
- `ReadByEntities` 必須支援一次查多個 entity id（`WHERE entity_id IN (…)`），供批次 hydration。
- **禁止逐 Node/Edge N+1 查詢。**
- `details_json` 保存完整 method pairs；Neo4j `sourceMethod/targetMethod` 只作快速預覽。
- **不得動到 PAT／OAuth／使用者設定等既有資料表。**

#### 3.3.1 跨 Neo4j/SQLite Publish 流程

```
1. 建立新 graphVersion
2. SQLite transaction 寫入該 version evidence（先寫）
3. Neo4j 寫 staging graph
4. 驗證 Neo4j counts / weight / reasonCode
5. 批次抽樣對帳 SQLite evidence（100 entity）
6. Promote Neo4j active manifest
7. Promote SQLite manifest
8. 清理 retired graph/evidence
```

**失敗處理：**
- 步驟 2～6 任一失敗 → 刪除該 graphVersion 的 Neo4j staging 節點與 SQLite evidence。
- Process crash → Host 啟動時 manifest reconciliation 找出 non-active orphan version 並清理。
- Active manifest 永遠只指向雙儲存驗證通過的版本。
- Cleanup 必須以 `(projectId, graphVersion)` 精確限定，不得影響其他 version。

---

### 3.4 CommunityReport（全面重寫）

刪除 V3 的 primary（3 個超大社群）與 secondary（1,930 碎片）機制。

#### 3.4.1 三層社群

| tier | 來源 | 預期數量 | 用途 |
|---|---|---|---|
| **C0 模組層** | tblMenuMap Released hierarchy 的頂層節點（Parent=Root，套用 `ID NOT LIKE '88%'`，不要求 LinkAddress） | 8～25 | Global Search 第一層漏斗 |
| **C1 功能層** | 每個可執行葉子 Feature + custom-report/approval/schedule Feature = 一個社群 | ≥ 650 | Local Search 錨點；「這個功能動到哪些程式與表」 |
| **C2 孤兒補集** | 未被任何 C1 覆蓋的節點；Leiden（GDS 可用時）或 deterministic label propagation；成員數 < 3 的社群丟棄，但其節點仍保留可搜尋 | ≤ 100 | 排程/批次/MQ 類 Global Search |

#### 3.4.2 C1 閉包建立規則

```
從 Feature（錨點）出發，沿以下邊向外展開：
  ROUTES_TO / TRIGGERS / DISPATCHES_TO / HANDLES / CALLS / READS / WRITES

停止條件（任一）：
  (a) depth > 4
  (b) memberCount > 60（設 truncated=true，不得靜默截斷）
  (c) 到達 shared 節點：收錄，但不從 shared 繼續外擴

Weight 門檻：
  非 CALLS 邊：weight >= 0.70
  CALLS 邊：   weight >= 0.60 且 confidence != inferred
```

**shared 節點的特殊邏輯：** 即使 CALLS weight 計算結果低於 0.70，
只要從 Feature 到 shared 節點有直接 certain/probable edge，就收錄該 shared 節點（停止外擴）。
shared 節點不得被用作把多個業務 C1 社群串接成超大社群的中介。

#### 3.4.3 Community 節點屬性

| 屬性 | 說明 |
|---|---|
| `communityId` | C0: `community:menu-root:{menuId}`；C1: `community:menu:{menuId}` 或 `community:feature:{featureId}`；C2: `community:leiden:{sortedMemberDigest}` |
| `tier` | `C0` / `C1` / `C2` |
| `parentCommunityId` | C1 → 所屬 C0；C0/C2 為 null |
| `title` / `summary` | deterministic template 立即產生；AI 版本覆蓋後標 `summaryState=ai-ready` |
| `summaryState` | `template` / `queued` / `ai-ready` / `failed` |
| `memberIdsJson` / `memberCount` | 有界成員清單 |
| `topTables` / `topEntryPoints` | 各最多 5 個代表成員 |
| `cacheKey` | member digest + prompt version，相同則不重生 |

**節點 communityId 回填規則：**
- 每個節點指向主要 C1（距離最近的錨點 Feature）。
- 同距離時依 `communityId` 字典序排序取第一個，確保 deterministic。
- 其他 membership 記入 `attributes.alsoInCommunities`（上限 5）。

#### 3.4.4 Summary 產生策略

| 階段 | 時機 | 行為 |
|---|---|---|
| 結構索引完成 | Graph publish 後立即 | C0/C1/C2 全部建立 deterministic template；Graph 立即可問答 |
| C0 AI 潤飾 | 結構索引完成後自動排入背景 | 數量少（8～25），全部預熱 |
| C1 AI 潤飾 | 按需：Global/Local Search 第一次命中時排入 | 650+ 社群不全量 LLM |
| C2 AI 潤飾 | 按需：使用者問題命中後排入 | 預設只用 template |
| 失敗 | LLM timeout 或 error | 保留 template，summaryState = `failed`，不影響問答 |

UI 應分開顯示「結構索引可用」與「AI 摘要背景補充中」兩種狀態。

---

## 4. 檢索端規格

### 4.1 Local Search

預算維持 V3：

```
SeedLimit = 12
MaximumNodes = 80
MaximumEdges = 120
MaximumDepth = 3
NeighborsPerNode = 50
```

**修改項目：**

1. Edge score 改讀 Neo4j `weight` 屬性（刪除 `EdgeWeight` hardcode 常數表）。
2. BFS 展開遇 `state=shared` 節點：計分收錄，但**不以 shared 作為下一層展開的起點**。
3. Seed tie-breaker 順序（相同 BM25 分數時）：
   - 問題 intent 的 role relevance（Controller/Service/Repo 分別對不同 intent 有不同優先級）
   - non-shared 優先
   - exact name/alias match
   - degree（最後才用，且只在相同 state/role 內比較，不得讓 hub 節點壓過精確業務名稱）
4. selected nodes/edges 決定後，**一次呼叫 `ReadByEntities` 批次取得 SQLite evidence**（禁止 N+1）。
5. SourceEvidenceReader 讀取實際檔案；SQLite evidence 提供完整 method pair、artifact、行號。
6. Context Compiler 輸出需保留：
   - certain/probable/inferred 分區標示
   - 完整 method pair 的有界顯示（不截斷到只剩 `sourceMethod`）
   - source 讀取成功/失敗狀態
   - 已知不支援的動態路徑聲明

### 4.2 AnalyzeImpact

- shared 節點輸出折疊句，N 取 `sharedConsumerCount`。
- 直接影響（depth 1-2）與間接影響（depth 3-4）分區顯示。
- `probable`/`inferred` 路徑不得與 `certain` 路徑混稱為「已確認」。
- Table 相關問題必須提示 dynamic SQL、外部報表、trigger 等索引缺口。

### 4.3 Global Search（兩層漏斗）

```
問題
→ Neo4j C0 community term scoring（server-side，選 1～2 個模組）
→ 在其 C1 children + 全部 C2 候選中比對
→ 取得 deterministic/AI summary
```

**禁止**：把全部 650+ C1 載入記憶體後排序（O(N) 記憶體問題）。
Neo4j 須新增 `(graphVersion, tier, parentCommunityId)` 查詢 index。

### 4.4 Evidence Hydration

```
Graph Retrieval 選定 ≤80 nodes / ≤120 edges
→ 蒐集 entity ids（最多 200 個）
→ 一次 SQLite batch query（ReadByEntities）
→ 合併 Neo4j top property + SQLite full evidence
→ SourceEvidenceReader（讀實際檔案片段）
→ Context Compiler
```

Hydration timeout 時：
- Graph 結構仍可回答。
- 明確標示「完整溯源 evidence 暫時不可用」。
- 不得把代表性 `topMethod` 說成完整方法清單。

---

## 5. 索引管線規格

### 5.1 支援副檔名

```
.cs  .java  .js  .jsx  .ts  .tsx  .sql  .aspx
```

加入 `.aspx` 必須同步修改：
- `GraphIndexingService` SupportedExtensions
- FileSystemWatcher extension filter
- LanguageForExtension（aspx → frontend）
- Artifact manifest
- source fallback safe text extension
- extractor DI registration
- extractor 版本（觸發 fingerprint 失效）

### 5.2 DB Fingerprint（V4 補充）

Fingerprint 須涵蓋：
- `sys.objects`：type IN (U,V,P,FN,TF,IF) 的 schema/name/type/modify_date
- `sys.foreign_keys`：FK name、parent/referenced object id、modify_date
- `sys.sql_expression_dependencies`：穩定 dependency key
- `sys.sql_modules`：definition hash 或 modify_date
- Menu hierarchy 與可執行 Feature 過濾結果
- approval / schedule / batch / report / CSV 等既有 metadata（沿用 V3）

任何 fingerprint 子查詢失敗：
- 本次禁止走 no-op fast path。
- manifest 標示 `Partial` 或 `Stale`。
- 不得沿用「資料庫未變」假設。

### 5.3 Body-delta（invocation-digest 版本）

| 變更類型 | 觸發判斷 | 行為 |
|---|---|---|
| 只有 method body 內的非 invocation 邏輯改變 | invocation-digest 不變 | delta：跳過語意重算 |
| 新增/刪除/改名跨 type 呼叫 | invocation-digest 改變 | 觸發完整 C# semantic 重算 |
| 宣告面（class/method 簽名）改變 | declaration-surface hash 改變（V3 機制） | 觸發完整 C# semantic 重算 |
| ASPX / DB fingerprint / route / extractor 版本改變 | 獨立判斷 | 觸發對應 extractor 重算 |

**驗證要求（T5.3）：** body-delta 模式與 clean full snapshot 的 graph digest 必須相同；
不一致代表 body-delta 遺漏了應觸發重算的場景，為 blocking bug。

### 5.4 版本升級

- `CurrentIndexerVersion` 提升至 V4（格式 `graphrag-v4`）。
- 各 extractor ID/version 全部 +1（確保 V3 索引結果全部失效重建）。
- 刪除：evidenceJson 相關讀寫、marker 清單、舊社群 builder 分支。
- 執行 grep 確認無 dead code 殘留。

---

## 6. 實作 TODO List

> 相依順序：Phase 0 → 1 → 2 → 3 → 4 → 5 → 6
> 標記規則：完成後在 PR 描述引用編號（如 `Implements T2.5`）。

### Phase 0 — 基線與保護

- [ ] **T0.1** 記錄 V3 FBL 全量索引基線（commit/snapshot、冷/熱機條件、耗時、各 extractor 耗時、peak RAM、node/edge/evidence 大小）。
- [ ] **T0.2** 建立 GraphRAG SQLite table allowlist；記錄非 GraphRAG 設定表的 schema hash 與 row count（供 D3 驗收）。
- [ ] **T0.3** 建立 A8/A11 baseline 盤點腳本（可解析的 named View pair 數、可解析的 tsx import chain 數）；此腳本**在 Phase 2 開始前**完成。
- [ ] **T0.4** 建立 edge precision 抽樣工具與 Q1～Q5 query fixture（用於 T6.4/T6.5）。

### Phase 1 — 資料模型與 Evidence Store

- [ ] **T1.1** `GraphModel.cs`：GraphEdge 新增 `Weight`/`Confidence`/`EvidenceCount`/`ReasonCode`/`TopArtifact`/`TopLine`/`SourceMethod`/`TargetMethod`；GraphNode 新增 `Degree`/`CommunityId`；`evidenceRef` 取代 `evidenceJson`；新增 `GraphRoles.Function`、`GraphSharedNodeThresholds`、ReasonCode 封閉清單常數。
- [ ] **T1.2** `GraphIdentity.cs`：新增 `FrontendPageEntry(relativePath)` 與 `SqlFunction(db, schema, name)`。
- [ ] **T1.3** SQLite：manifest store DB 新增 `graph_evidence` 表與 index（§3.3 DDL）；實作 `IGraphEvidenceStore`（WriteBatch / ReadByEntities / DeleteByVersion）。不得動到 PAT 等既有表。
- [ ] **T1.4** `Neo4jGraphStore.cs`：改寫 node/edge property；移除 evidenceJson/sourceId/targetId；新增 `(graphVersion, communityId)` 與 `(graphVersion, tier, parentCommunityId)` index；`ValidateStagingAsync` 改為驗證 `reasonCode`/`weight` 必填＋抽樣核對 SQLite evidence 筆數。
- [ ] **T1.5** Publish/cleanup 整合 evidence store（§3.3.1 流程，含 crash orphan reconciliation）。
- [ ] **T1.6** `GraphAnswerContext`：Context Compiler 改為批次 evidence hydration（禁止 N+1）；分區顯示 certain/probable/inferred。

### Phase 2 — 抽取器

- [ ] **T2.1** `SqlServerGraphExtractor`：Feature 過濾與 hierarchy 查詢分開（§3.1.2）；保留 `attributes.parentMenuId`。
- [ ] **T2.2** `SqlServerGraphExtractor`：新增 `TryParsePluginReportTarget`（E1 邊，Base64 解碼 + FQN 查找 + unresolved stub）。
- [ ] **T2.3** `SqlServerGraphExtractor`：sys.objects 全量 SP/function；sys.sql_modules ScriptDom READS/WRITES 判定（E6）；sys.foreign_keys E5 邊。
- [ ] **T2.4** `CSharpGraphExtractor`：Phase 1 全量 syntax inventory（含 invocation-digest 計算）；Phase 2 單一大 Synthetic Compilation（§2.1/§2.3 所有規則）；Phase 3 reachable deep attributes；刪除 `IsLargeRepositoryCallPathFile` marker 過濾器。
- [ ] **T2.5** `CSharpGraphExtractor`：抽取 named View metadata（供 T2.7 合併成 E2）；unresolved/ambiguous 不建邊。
- [ ] **T2.6** `CSharpGraphExtractor`：記憶體管理（§2.5 約束，peak RAM 量測，自動降級邏輯）。
- [ ] **T2.7** 新增 `AspxGraphExtractor`：掃描 `.aspx`；frontend-page 節點；E3 邊；與 T2.5 的 view-name 中繼資料合併建 E2 邊。
- [ ] **T2.8** `FrontendGraphExtractor`：IsPage 補單複數；E4 es-import；移除「非 page 且無 URL 即丟棄」規則。
- [ ] **T2.9** 所有 extractor 完成 reasonCode 映射表；未映射 reasonCode 的 Edge publish 失敗。

### Phase 3 — Assembler 與社群

- [ ] **T3.1** `GraphAssembler`：邊合併（evidence 去重、confidence 彙總、代表 method/artifact、weight 公式）。
- [ ] **T3.2** `GraphAssembler`：degree 計算；shared 判定與 `attributes.sharedConsumerCount`；分級孤立清理（§3.1.4 規則）。
- [ ] **T3.3** `GraphCommunityBuilder`：C0（hierarchy 頂層）→ C1（executable Feature 閉包，§3.4.2 規則）→ C2（未覆蓋 Leiden/fallback，<3 成員丟棄但節點保留）；communityId deterministic 回填；`alsoInCommunities`；輸出 tier/parentCommunityId。
- [ ] **T3.4** Community deterministic template 立即產生（結構索引完成後立即可問答）。
- [ ] **T3.5** AI Summary 改為 C0 自動、C1/C2 按需（§3.4.4）；cacheKey 去重；失敗保留 template。

### Phase 4 — 檢索端

- [ ] **T4.1** `GraphRetrievalService`：刪除 `EdgeWeight` hardcode → 讀邊 `weight`；BFS shared 不外擴；seed tie-breaker（§4.1）。
- [ ] **T4.2** Global Search 兩層漏斗（§4.3）；Neo4j server-side C0→C1/C2 查詢。
- [ ] **T4.3** AnalyzeImpact shared 折疊、confidence 分區、影響層次分區（§4.2）。
- [ ] **T4.4** Evidence hydration 單次 batch（§4.4）。
- [ ] **T4.5** 溯源/visualization API 改查 SQLite。

### Phase 5 — 版本與清理

- [ ] **T5.1** `CurrentIndexerVersion` 提升至 V4；各 extractor ID/version +1；`.aspx` 完整 manifest 整合（§5.1）。
- [ ] **T5.2** DB fingerprint 補 V4 新增 metadata（§5.2）。
- [ ] **T5.3** body-delta 改為 invocation-digest（§5.3）；驗證與 clean full snapshot digest 相同。
- [ ] **T5.4** 刪除 V3 遺留：evidenceJson 讀寫、marker 清單、舊社群 builder 分支；grep 確認無 dead code。

### Phase 6 — 測試與報告

- [ ] **T6.1** 單元測試：extractor 規則（E1～E7）、weight 公式、shared 判定、C1 閉包（shared 不外擴、truncated）、evidence hydration、crash reconciliation。
- [ ] **T6.2** 整合測試：full/no-op/body-delta（invocation-digest 觸發）/publish failure。
- [ ] **T6.3** FBL 全量索引執行，量測 peak RAM（自動降級臨界確認）。
- [ ] **T6.4** A1～A14 + A15 edge precision 驗收（§7）。
- [ ] **T6.5** B1～B7 社群驗收；Q1～Q5 完整 retrieve→hydrate→source→prompt 人工驗收。
- [ ] **T6.6** C1～C8 儲存與效能驗收。
- [ ] **T6.7** D1～D5 清理與安全驗收。
- [ ] **T6.8** 產生結構化驗收報告（§8 格式）；確認無 FAIL 後合併。

---

## 7. 驗收標準（Acceptance Criteria）

> 驗收環境：`D:\FBL_Release_Trunk` ＋ SQL Server `127.0.0.1,3301` / `FBL_SPV_SIT`。
> 驗收前允許清空 Neo4j（`MATCH (n) DETACH DELETE n`）與 SQLite graph tables（allowlist 限定）。
> PAT 等設定表在驗收全程不得被清除（D3 全程監控）。

### 7.1 圖結構與覆蓋率

| # | 指標 | 門檻 | 驗證方式 |
|---|---|---|---|
| A1 | menu-feature 節點數 | = executable leaf filter SQL 筆數（誤差 0） | count 比對 |
| A2 | active/resolved menu-feature 孤立率 | < 5%（V3 為 53%）；unresolved 另列，不得建假邊降低孤立率 | degree=0 比例 |
| A3 | 低價值 Code degree=0 孤立節點 | = 0；保留的高價值 degree=0 必須 state=unresolved 並附清單 | count |
| A4 | procedure + function 節點數 | ≥ 250 | count |
| A5 | FK DEPENDS_ON 邊數 | ≥ 440 | count by reasonCode |
| A6 | PluginReport DISPATCHES_TO 邊 | ≥ 24；stub 必須 state=unresolved | count |
| A7 | frontend-page 節點數 | ≥ 600 | count |
| A8 | named View 連通率 | ≥ T0.3 baseline 的 90%（目標約 ≥ 550） | count by reasonCode |
| A9 | 另類基金 Feature→page→frontend module | hop ≤ 4，路徑存在（V3 = 0） | path query |
| A10 | 利息收入報表→ReportKernel→Data | 路徑存在 | path query |
| A11 | TSX reachable module 數 | ≥ T0.3 baseline 的 90%（目標約 ≥ 500） | count |
| A12 | 所有邊具備 weight/confidence/reasonCode | 100% | 缺欄位 count = 0 |
| A13 | Neo4j 邊上不存在 evidenceJson/sourceId/targetId 屬性 | 100% | 屬性存在 count = 0 |
| A14 | RMDAL namespace Code 節點數 | ≥ 500，且抽樣可連至 CALLS 或 Data relation | filePath CONTAINS 'rmdal' |
| **A15** | **Edge precision（新增，強制）** | certain 抽樣正確率 ≥ 95%；probable 抽樣正確率 ≥ 85% | 見下方 |

**A15 抽樣規則：**
- 各 confidence 層各隨機抽取 ≥ 50 條，覆蓋 CALLS/READS/WRITES/ROUTES_TO/DISPATCHES_TO。
- **精度範圍**：排除 §2.3 已明確標示為「已知無法 certain」的情境（重複型別/條件編譯/缺失 reference）後計算；這些情境若標為 `probable` 或 `inferred` 不算錯誤。
- 每筆記錄：source/target/reasonCode/artifact/人工判定/重現 Cypher。
- certain 精度 < 95% 視為 FAIL，需找出系統性錯誤來源修復後重測。

### 7.2 社群

| # | 指標 | 門檻 |
|---|---|---|
| B1 | C0 數量 | 8 ～ 25 |
| B2 | C1 數量 | ≥ 650 |
| B3 | C1 成員數 | 平均 3 ～ 60；單一不得 > 60；truncated 必須可見 |
| B4 | C2 數量與成員 | ≤ 100 且每個 ≥ 3 |
| B5 | communityId 覆蓋率 | ≥ 90%（Feature/EntryPoint/connected Code） |
| B6 | C1 parentCommunityId | 100% 有效指向 C0 |
| B7 | shared 不形成跨功能超大社群 | 抽樣 shared node，不得被用作 C1 外擴中介 |

### 7.3 儲存與效能

| # | 指標 | 門檻 |
|---|---|---|
| C1 | Neo4j relationship 屬性體積 | 較 V3（16 MB evidenceJson）下降 ≥ 80% |
| C2 | SQLite/Neo4j evidence 對帳 | 抽樣 100 entity，筆數一致率 100% |
| C3 | FBL 全量索引耗時 | ≤ V3 基線 × **2.5**；報告必附 V3 秒數與 peak RAM |
| C4 | no-op fast path | 無變更重跑 → 不觸發重建 |
| C5 | Local Search P95 | Q1～Q5 + 15 題擴充集，warmup 5 次後各跑 10 次，P95 ≤ 2 秒 |
| C6 | Evidence hydration | 一次 batch；SQL query 次數不得與 selected entity 數量等比例 |
| C7 | Community 結構可用時間 | Graph publish 後立即可問答，不等待 AI Summary |
| C8 | AI Summary 失敗影響 | 失敗不得把 project index 狀態改成 unavailable |

### 7.4 問答品質（人工評分，5 題至少 4 題達 4/5 分以上）

| # | 題目 | 期望定位 |
|---|---|---|
| Q1 | 另類基金基本資料維護的畫面欄位要加一個欄位，要改哪些檔案？ | ASPX/JS ＋ AlternativeFundController ＋ 相關 Data |
| Q2 | 利息收入增減分析報表數字不對，可能問題在哪？ | InterestIncomeAnlz_ReportKernel ＋ READS/WRITES evidence |
| Q3 | tblPosition105 加欄位會影響哪些功能？ | 反向 READS/WRITES ＋ FK ＋ shared 折疊句正確出現 |
| Q4 | 公告管理儲存流程改了驗證邏輯，前端要不要跟著改？ | frmAnnouncement.tsx → component → Announcement/ action |
| Q5 | 排程裡抓 Bloomberg 匯率的功能在哪個模組？ | C2 社群命中 ＋ scheduled-task 節點 |

每題記錄：命中 seed、graph path、SQLite evidence、實際 source snippet、confident/probable/inferred 標示、缺口、1～5 分。
不得有錯誤檔案或方法被宣稱為 certain。

### 7.5 清理與資料安全

| # | 指標 | 門檻 |
|---|---|---|
| D1 | Neo4j 版本 | 只保留最後成功 active graphVersion |
| D2 | SQLite evidence | 無 retired/orphan version 殘留 |
| D3 | 非 GraphRAG 設定表 | schema/row count/hash 與 T0.2 基線完全一致 |
| D4 | 暫存檔 | 報告以外全部清除 |
| D5 | Publish failure 回復 | 失敗後舊 active graph 仍可查；staging/evidence 可自動回收 |

---

## 8. 結構化測試報告格式

報告落點：`docs/reports/graphrag-v4-acceptance-{yyyyMMdd-HHmm}.md`

```markdown
# GraphRAG V4 驗收報告
- 執行時間 / 執行人 / commit hash
- 環境：FBL_Release_Trunk snapshot、FBL_SPV_SIT、Neo4j 版本、SQLite 版本
- 硬體：CPU / RAM / 冷熱機條件
- V3 baseline vs V4 實測對照

## 1. 圖結構與覆蓋率（A1～A14）
| # | 指標 | 門檻 | 實測值 | 判定 | 備註 |
（FAIL 必附原因分析與 Cypher 重現語句）

## 2. Edge Precision（A15）
（抽樣明細表，含每筆 source/target/reasonCode/人工判定）

## 3. 社群（B1～B7）

## 4. 儲存與效能（C1～C8）
（附 V3 baseline 數字）

## 5. 問答品質（Q1～Q5）
（每題：提問、system 回答摘要、人工評分、缺口說明）

## 6. 清理與安全（D1～D5）

## 7. 未通過項與處置
（每項：指標編號、FAIL 原因、處置決定（修復重測 / 已知限制接受））

## 8. 已知限制清單
（WCF/MQ/dynamic SQL/Reflection/條件編譯缺失分支/跨 Compilation…）
```

自動化腳本：`scripts/dev/graphrag-v4-acceptance.ps1`
- 只使用 Cypher over HTTP（唯讀）、sqlcmd 唯讀查詢、SQLite 唯讀驗證。
- 清理操作只使用 GraphRAG allowlist。
- 腳本自動填充所有 A/B/C/D 指標；Q 區人工填寫。

**合併條件：** 所有 A/B/C/D 指標 PASS，且 Q 題 ≥ 4/5 達 4 分以上；
任何 FAIL 需附處置決定並由產品負責人確認才可合併。

---

## 9. 風險與已知限制

1. **Synthetic Compilation 記憶體峰值**
   9,638 檔全量 SyntaxTree + CSharpCompilation 在高記憶體消耗情況下可能超過 8 GB。
   自動降級機制（§2.5）會切換至按頂層目錄分批，此時跨批 CALLS 降為 probable，
   A15 certain 比例可能下降。驗收 A15 時需確認是否觸發了降級。

2. **ASPX server-side 動態表達式**
   只解析固定字串的 `JSPath`/`AbsolutePath`；其他 C# 運算式忽略，不建邊。

3. **`View()` 無參呼叫**
   本版不處理（依 action name 推 view name 的慣例）。若 A8 達標則無需補充。

4. **重複型別（duplicate type declarations）**
   合成 Compilation 中同 namespace+name 的重複型別不建 CALLS（§2.3），
   記入 diagnostics。若重複型別比例過高，可考慮依命名空間前綴分組建多個 Compilation（分批降級）。

5. **SQL dynamic/cross-database/synonym**
   sys dependency 與 ScriptDom 都可能漏，回答必須聲明限制。

6. **shared 門檻（10/10/20）**
   為初值，只能透過驗收 Q3 與 B7 調整 `GraphSharedNodeThresholds`，不改架構。

7. **C1 成員上限 60**
   若大量觸頂，先檢查是否有錯誤 Edge 或 weight 太低的跨模組邊被納入，
   優先調整 relation-specific threshold，不直接提高上限。

8. **WCF/MQ/RealTimeService/Reflection**
   高價值孤立程式標 `state=unresolved` 保留搜尋，不建假邊。回答需聲明此限制。

9. **AI Summary 背景速度**
   C1 650+ 個不全量 LLM 預熱；若使用者問到從未命中的社群，第一次回答使用 template，
   UI 顯示「AI 摘要補充中」。

10. **invocation-digest 與 certain CALLS 的最終一致**
    body-delta 路徑必須通過 T5.3 驗證（與 clean full snapshot digest 相同），
    否則視為 blocking bug。
