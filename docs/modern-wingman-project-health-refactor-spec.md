# Modern Wingman 專案健康度、效能與可維護性重構規格 V1.0

> 文件狀態：實作候選版
> 建立日期：2026-08-04
> 適用分支基準：`main` / `f7e045a`
> 適用產品：Modern Wingman Windows 桌面版
> 主要目標：修正已盤點的流程、效能、UI、啟動、測試與目錄結構問題，不新增產品功能

---

## 0. 文件目的與決策摘要

本規格把 2026-08-04 對 Modern Wingman 的只讀盤點結果，轉換為可直接實作、Code Review 與驗收的工程契約。

本次重構的目的不是擴大 GraphRAG、增加新的 Agent 框架或重做整套 UI，而是讓目前已有功能具備以下品質：

1. VS Code 按一次 Run and Debug 可啟動全部必要服務，停止 Debug 後可關閉本次啟動的全部服務。
2. 專案解析在 Neo4j 暫時不可用時，仍能使用唯讀原始碼工具回答可回答的問題。
3. 使用者送出問題後，不因索引 Catch-up、標題生成、Provider 狀態查詢或同步檔案掃描而長時間無回應。
4. GraphRAG 保留既有 FBL 投資系統 Schema、索引精度、696 個菜單功能鏈路與背景 Community AI Summary。
5. 知識圖譜頁面在 Tauri 最小視窗下不跑版，Markdown 表格與進度提示清楚可讀。
6. 專案結構維持簡單，移除失效設定、文件、產物與無呼叫者程式，不建立第二套實作。
7. 乾淨 clone 至少能執行一套納入版控的核心回歸驗證；本機完整測試仍可保留於既有位置。
8. 所有修改都遵守 `docs/開發、程式碼生成與測試模式固定指令.txt`。

### 0.1 本規格固定的產品決策

- Modern Wingman 仍只正式支援 Windows 10／11 x64。
- 一般對話不讀取專案 GraphRAG。
- 專案解析只提供唯讀分析，不修改被分析專案原始碼。
- GraphRAG 仍限定 FBL 投資交易系統，不恢復 Java 或通用多語言索引器。
- GraphRAG V4 的 Node、Edge、Evidence、Community 與 Publish Schema 不在本次重做。
- Community deterministic summary 必須隨主索引發布；Community AI Summary 維持背景補強。
- 不導入 CopilotKit、第二套 Agent Runtime、第二套 Workflow Engine 或新的圖資料庫。
- 不建立跨機器分散式快取、訊息佇列或獨立 Search Service。
- 不保留新舊雙軌流程；新流程驗收通過後應刪除舊程式與舊設定。

### 0.2 與既有規格的優先順序

本規格只覆蓋以下專案健康度議題：

- 啟動、停止與 Port 管理。
- 專案問答的可用性與降級流程。
- 原始碼工具與 Graph Retrieval 效能。
- Provider／Model 載入流程。
- UI 響應式、Markdown 與進度呈現。
- 檔案、模組、測試、產物、文件與安全設定。

Graph 抽取精度與資料模型仍以 `docs/graphrag-v4-refactor-spec_V1.2.1.md` 為準；產品瘦身範圍仍以 `docs/modern-wingman-slim-refactor-spec.md` 為準。

若既有規格與本規格在以下三點衝突，以本規格為準：

1. 專案解析不再因 Neo4j 暫時不可用而完全拒絕回答；可降級使用原始碼工具。
2. CORS、TLS 憑證撤銷檢查與 Tauri CSP 不再固定維持寬鬆設定，改為本機桌面應用所需的最小權限。
3. 雖然 `apps/UnitTests` 可繼續作為不納入版控的本機完整測試專案，但核心 Smoke／Contract 回歸測試必須可由乾淨 clone 執行。

---

## 1. 現況基準與已確認問題

### 1.1 2026-08-04 基準驗證

盤點時工作區為乾淨的 `main`，與 `origin/main` 同步，HEAD 為 `f7e045a`。

| 驗證 | 基準結果 | 說明 |
|---|---:|---|
| Agent Service Debug build | PASS | 0 warning、0 error |
| 本機 `apps/UnitTests` | PASS | 200/200；但該專案未納入版控 |
| pnpm typecheck | PASS | contracts、ui-kit、desktop |
| Desktop production build | PASS with warning | 主要 chunk 超過 1 MB |
| Rust `cargo check` | PASS | Tauri 可編譯 |
| `pnpm lint` | FAIL | Workspace 宣告 lint，但沒有 package 提供 lint script |
| Git working tree | CLEAN | 本次盤點未修改產品程式 |

### 1.2 P0：交付與回歸驗證問題

#### P0-1 核心測試不可由乾淨 clone 重現

- `apps/UnitTests` 被根目錄 `.gitignore` 排除。
- 既有驗收腳本直接引用 `apps/UnitTests/AgentService.UnitTests.csproj` 與其中 Fixture。
- 本機能通過 200 項測試，不代表 CI 或另一台電腦可以重現。
- 目前未見可替代的完整 CI workflow。

#### P0-2 驗收產物與來源界線不清

- `artifacts` 與已追蹤的 `temp` 內存在測試報告、診斷 JSON 與本機絕對路徑。
- 大型診斷檔會增加 clone、diff 與 Code Review 成本。
- 測試結果檔不是產品原始碼，不應成為長期 source of truth。

### 1.3 P1：功能與流程問題

#### P1-1 Neo4j 成為專案問答的單點阻斷

目前專案對話在建立工具與執行 `RunStreamingAsync` 前，先要求 Neo4j 可用與索引版本一致。Neo4j 未啟動、啟動逾時或暫時失聯時，直接回傳錯誤；`search_project_text`、`read_project_file_range` 與 `find_csharp_symbol` 沒有機會執行。

#### P1-2 第一輪回答被對話標題生成阻塞

主要回答已完成後，伺服器仍等待最多 10 秒的標題生成，再送出最終完成事件。標題是次要 metadata，不應延遲主要回答。

#### P1-3 問答 fallback 可能重掃並雜湊整個專案

當檔案 watcher 未註冊或狀態不足以判定增量異動時，問答路徑可能呼叫 `CatchUpAsync`，遍歷並計算所有支援檔案的 SHA-256。大型 FBL 專案不應在每個問題前承擔此成本。

#### P1-4 原始碼工具在 Request Thread 執行同步大量 I/O

全文搜尋雖回傳 `Task`，但內部同步列舉目錄與讀檔；C# Symbol 搜尋每次重新解析所有 `.cs`；高行號片段讀取會從第一行逐行跳過。多次 Agent Tool Call 會線性放大延遲。

#### P1-5 Provider／Model Picker 有同步等待與 N+1 請求

Provider 清單取得時，後端存在 async-over-sync 與重複 SQLite 讀取；前端取得清單後再逐 Provider 查 Key Status，切換 Provider 又查 Model。這使一般對話與專案對話初次進入時都可能長時間空白。

#### P1-6 Debug Port 與文件不一致

目前實際使用 Vite `4173`、Agent REST `5002`、Neo4j Bolt `17688`，但 launch profile 仍有 `5200`／`7264`，README 仍有 gRPC `5001` 與 Vite `1420`，腳本另檢查未被目前程式設定使用的 `17475`。

#### P1-7 Knowledge Graph 與 Markdown UI 存在可重現的版面風險

- 知識圖譜左右面板在 800px 最小視窗同時開啟時，主圖寬度不足。
- Graph 預設一次載入 1000 節點，容易造成圖形渲染卡頓與節點重疊。
- Markdown table header 有固定深色，但 table cell 沒有明確文字色，造成主題下低對比。
- Community Summary 失敗提示可永久停留並遮住右下角內容。

### 1.4 P2：維護性、效能與安全問題

- Graph path BFS 對每個展開節點個別查 Neo4j，形成 N+1 round trip。
- Query Planner 單輪可同時送出較多 Neo4j Query Variant，缺少明確的全域併發上限。
- 完整索引完成後可能執行強制 Full GC，造成 UI／服務短暫停頓。
- Managed Neo4j 固定配置較高記憶體，且 cold start 最長等待 90 秒。
- `KnowledgeGraphPage.tsx`、`Neo4jGraphStore.cs`、`GraphRetrievalService.cs` 等檔案責任過重。
- `src/{Application,Domain,Host,Infrastructure}` 與 `modules/{GraphRAG,Marketplace}` 邊界未形成一致規則。
- `stop-all.ps1` 含機器專屬的 `D:\CargoTarget\...` 絕對路徑。
- CORS 允許所有 Origin／Method／Header。
- 多個 HttpClient 預設停用憑證撤銷檢查。
- Tauri CSP 為 `null`。
- Development logging 使用 `AgentService: Trace`，正常開發時過度吵雜。
- Repository 追蹤大量 bundled runtime；其中哪些屬於官方不可拆 bundle、哪些是多餘平台資產尚未形成清單。
- README、V3 註解、gRPC 描述與 Java 舊規格未同步清理。
- `packages/config`、`mcp-presets`、`prompt-assets`、`skills` 等 package 需確認是否只是空殼。

---

## 2. 範圍、非目標與保護條件

### 2.1 本次實作範圍

1. VS Code launch/tasks 與 `start-all.ps1`／`stop-all.ps1`。
2. Agent Service 啟動設定、REST Port、Neo4j managed lifecycle。
3. 專案 Conversation endpoint、GraphRAG readiness、工具建立與回答串流。
4. Project Analysis 唯讀工具的檔案列舉、全文搜尋、Symbol 搜尋、片段讀取與單輪工具預算。
5. Graph Retrieval Query Variant 併發與 BFS 鄰居查詢。
6. Provider／Model API 與前端 Picker 初始化流程。
7. Knowledge Graph Viewer、Markdown renderer、Community Summary toast 與專案頁響應式版面。
8. 大型檔案責任拆分，但不改變公開產品行為。
9. 測試、驗收腳本、產物忽略規則、README 與失效設定。
10. 本機桌面架構必要的 CORS、TLS、CSP 與 log hardening。

### 2.2 明確非目標

- 不修改 FBL GraphRAG Node／Edge／Evidence Schema。
- 不改寫 FBL 696 個菜單的抽取器業務規則。
- 不重跑或重新定義既有 Graph Precision／Recall Golden 標準。
- 不新增 Java、Python、Go 等語言索引支援。
- 不新增新的 LLM Provider。
- 不新增 OAuth 功能。
- 不修改 Marketplace 功能契約。
- 不修改 JIRA 功能契約，除非受 GraphRAG service API 內部重構影響。
- 不修改被分析的 FBL 專案檔案或資料庫資料。
- 不把 Neo4j 改成其他資料庫。
- 不導入 Redis、Elasticsearch、OpenSearch、消息佇列或分散式 Worker。
- 不為未來 Web Server 部署建立多租戶、登入、Session 或遠端部署架構。
- 不以微服務方式拆分 Agent Service。

### 2.3 必須保護的既有功能

- 一般對話、多對話、串流、附件、Markdown、語音輸入。
- Provider 與 Model 選擇、PAT 與 Credential 安全儲存。
- 專案新增、刪除、VCS 更新與專案資料庫設定。
- FBL 完整索引、增量索引、active graph version 與上一版成功圖譜保留。
- 696 個菜單功能鏈路與既有 GraphRAG Golden 驗收。
- Community deterministic summary、AI Summary queue、進度與完成後停止輪詢。
- 知識圖譜檢視、搜尋、篩選、鄰居展開與 Inspector。
- JIRA、Marketplace、Settings 與 Copilot runtime 驗證。
- SQLite 中人工問答使用的既有 PAT／Credential。

### 2.4 資料安全限制

- FBL SQL Server 帳密只允許執行唯讀查詢，不得 INSERT、UPDATE、DELETE、MERGE、DDL 或執行可能寫入資料的 Stored Procedure。
- 測試不得將真實帳密、Token、PAT、Connection String 或 Authorization Header 寫入原始碼、log、snapshot 或 artifact。
- SQLite Schema 變更不得刪除既有 PAT／Credential。
- Neo4j 整合測試若需要清空資料，只能連到本機明確的測試資料庫；不得清空人工測試正在使用的圖。
- 測試建立的檔案只能位於根目錄 `temp`，結束後必須清除。

---

## 3. 目標執行架構

### 3.1 服務與固定 Port

本機開發只保留以下必要 Listener：

| 服務 | 位址 | 啟動者 | 停止責任 |
|---|---|---|---|
| Vite Dev Server | `127.0.0.1:4173` | VS Code preLaunch task | postDebug task |
| Agent Service REST/SSE | `127.0.0.1:5002` | VS Code .NET debug | VS Code stopAll |
| Neo4j Bolt | `127.0.0.1:17688` | Agent Service managed runtime | Agent Service graceful shutdown／stop task fallback |
| Neo4j Browser HTTP | Neo4j 實際設定值 | Neo4j managed runtime | 同 Neo4j process |

規則：

- 移除目前沒有正式設定來源的 `17475` 檢查，除非實作時確認它就是 Neo4j Browser 的有效 configured port；若確認需要，必須由同一份 Neo4j options 產生，不得只存在腳本常數。
- `launchSettings.json`、`appsettings.json`、Vite、Tauri、VS Code tasks、README 必須一致。
- Frontend 的 Agent API Base URL 必須只有一個定義來源；`speech.ts` 不得再自行硬編碼另一份。
- 正式 Tauri build 不啟動 Vite，也不得依賴 4173。

### 3.2 單一 Debug 流程

VS Code 的 Run and Debug 只顯示一個使用者入口：

```text
Modern Wingman（全端 Debug）
```

流程固定為：

```text
preLaunch
  ├─ 清理「上一輪由 Modern Wingman 啟動但異常殘留」的程序
  ├─ 驗證固定 Port 無未知程序占用
  └─ 啟動 Vite 並等待 readiness

compound debug
  ├─ 啟動 Agent Service debugger
  └─ 啟動 Tauri desktop debugger

stop
  ├─ VS Code stopAll 結束兩個 debugger
  └─ postDebug 清理由本輪 manifest 記錄的 Vite／Neo4j 子程序
```

限制：

- 不以程序名稱直接殺掉所有 `dotnet`、`node`、`java` 或 `cargo`。
- 只能終止 manifest 中記錄 PID、StartTime 與 executable path 均符合的程序。
- 不得硬編碼 `D:\CargoTarget` 或特定使用者路徑。
- 未知程序占用 Port 時，應顯示 PID、ProcessName、ExecutablePath，然後停止啟動；不得擅自終止。
- 重複按 Debug 不得產生第二個 Vite、Agent Service 或 Managed Neo4j。

### 3.3 Readiness 分層

應區分三種 readiness，不再以 Neo4j 決定整個 App 是否 Online：

| 狀態 | 意義 | 可使用功能 |
|---|---|---|
| AppReady | Desktop 可開啟、Agent REST 可回應 | 一般對話、設定、專案管理 |
| GraphReady | Neo4j 可連線且 active graph 可用 | GraphRAG、知識圖譜瀏覽 |
| ProjectToolsReady | 專案路徑存在且可唯讀存取 | 原始碼搜尋、Symbol、讀檔 |

Frontend 應針對功能顯示局部錯誤，不得因 Graph 尚未 Ready 就把整個專案解析頁顯示為 `Failed to fetch`。

---

## 4. 專案問答流程重構

### 4.1 目標流程

每個專案問題固定依序執行：

```text
1. 驗證 Conversation 與 Project
2. 立即建立 SSE 串流並送出 preparing 狀態
3. 取得索引狀態快照，不做全專案雜湊
4. 判斷可用資料來源
5. 建立本輪唯讀工具
6. 執行 Agent RunStreamingAsync
7. 串流回答與工具進度
8. 立即送出 done
9. 非阻塞更新對話標題
```

### 4.2 Graph 使用模式

Graph 狀態分為：

#### Ready

- active graph version 與 manifest 一致。
- 提供 Graph Search、Path Trace、Source Search、Read File、Find Symbol。
- 回答可將 Graph 證據標示為「已索引事實」。

#### StaleButUsable

- 有上一版成功圖譜，但 watcher 已偵測到檔案異動。
- Graph 仍可作導航候選，最新原始碼工具負責確認。
- 回答必須指出「圖譜版本可能落後目前 Working Copy」。
- 問答不得同步等待完整 Catch-up。

#### Unavailable

- Neo4j 未啟動、啟動逾時、連線失敗或沒有 active graph。
- 仍建立 Source Search、Read File、Find Symbol 工具。
- 回答必須清楚標示「本輪未取得知識圖譜證據」。
- 只有當問題明確要求 Graph-only 資訊且原始碼工具無法回答時，才回報資訊不足。

### 4.3 索引與問答隔離

- 一般問題不得觸發 full index。
- watcher 已記錄 changed files 時，背景增量索引可開始，但不得阻塞當次問題。
- watcher 不可用時，只允許做低成本狀態判斷，不得自動 SHA-256 全專案。
- Full Catch-up 只能由以下入口觸發：
  - 使用者按「開始索引」或「重新索引」。
  - VCS update 完成後的明確 background job。
  - watcher overflow／失效且系統顯示需要重新同步。
- Catch-up 與問答同時執行時，問答使用上一版成功圖譜與目前檔案，不等待 publish。

### 4.4 回答完成與標題生成

- SSE `done` 必須在主要回答、usage 與必要持久化完成後立即送出。
- 對話標題生成不屬於回答完成條件。
- 標題生成失敗、逾時或取消不得把成功回答標示為失敗。
- 標題完成後可透過既有 Conversation reload 或輕量事件更新；不建立第二套持久連線。
- App shutdown 時未完成的標題工作可以取消，不影響回答內容。

### 4.5 Agent Tool 使用預算

本輪工具必須保存已呼叫的 normalized arguments 與結果摘要，用於避免重複呼叫。

預設限制：

| 工具類型 | 單輪軟上限 | 單輪硬上限 |
|---|---:|---:|
| Graph seed/search | 2 | 4 |
| Graph path/neighbor | 3 | 6 |
| Source text search | 4 | 8 |
| Find symbol | 3 | 6 |
| Read file range | 8 | 16 |
| 全部唯讀工具合計 | 16 | 32 |

規則：

- 完全相同參數不得重複執行，直接重用本輪結果。
- 同一檔案相鄰或重疊的 read range 應合併。
- Search 結果已提供足夠候選時，應先 Read／Verify，不再換同義詞重複搜尋。
- 達軟上限後，Agent instruction 必須要求先整理證據缺口。
- 達硬上限後停止工具呼叫，以現有證據回答並列出未確認項目。
- 不把每一步內部推理文字呈現給使用者，只呈現可驗證的工作進度與工具結果摘要。

---

## 5. 原始碼工具效能設計

### 5.1 Project File Catalog

每個 active project 維護一份輕量、記憶體內的檔案目錄：

```text
RelativePath
Extension
Length
LastWriteTimeUtc
```

規則：

- Catalog 不保存完整檔案內容。
- 初次建立只列舉檔案 metadata，不計算全部 SHA-256。
- FileSystemWatcher 事件增量更新 Catalog。
- watcher overflow 時標記 catalog stale，由明確背景同步重建。
- Project 刪除或 Agent Service 停止時釋放 Catalog。
- 不將 Catalog 新增到 SQLite；本次只需要 process memory。
- 若已有可重用的 watcher snapshot，直接擴充現有類別，不另建第二套 watcher。

### 5.2 Source Text Search

- 使用 Catalog 過濾副檔名、排除目錄與檔案大小後才開檔。
- 讀檔不得在 ASP.NET Request Thread 上執行長時間同步掃描。
- 採 bounded concurrency；預設同時最多 4 個檔案讀取工作。
- 每個檔案命中數、總命中數與總掃描 bytes 都必須有上限。
- CancellationToken 必須能中止列舉與讀取。
- 本輪相同 normalized query 使用記憶體結果快取，Conversation 結束即釋放。
- 不新增外部 Search Server；不依賴開發機 Codex 附帶的 `rg.exe`。

### 5.3 C# Symbol Search

查詢優先順序：

1. 先查已發布 Graph／索引中的 CodeClass、Symbol 與 aliases。
2. Graph 無命中或不可用時，使用 Catalog 對候選檔名與文字做縮小。
3. 只解析候選 `.cs`，不得每次解析全專案所有 C# 檔案。
4. 同一檔案的 SyntaxTree 可依 `Path + LastWriteTimeUtc + Length` 暫存於 process memory。
5. watcher 偵測檔案變更時，使該檔案 cache 失效。

本次不導入完整 Roslyn Workspace、Language Server 或新的持久化 Symbol DB；若既有 Graph 已提供足夠 Symbol，應優先重用。

### 5.4 File Range Read

- 保留單次最多 2000 行的既有上限。
- 讀取結果必須包含實際檔案路徑、start line、end line 與每行行號。
- 重疊範圍由本輪 cache 合併。
- 超過允許檔案大小或非文字檔時拒絕讀取並回傳明確原因。
- 讀取前後檢查 Project Root，防止 `..`、junction 或 symbolic link 逃逸。

### 5.5 Graph Retrieval 併發

- Query Rewrite 仍只在 deterministic query 無有效 seed 時啟動。
- LLM query variants 維持既有最大值，但執行 Neo4j 查詢時最多 4 個 concurrent query。
- Query variant 必須 normalized 去重後才執行。
- BFS 鄰居展開優先改成每層批次查詢，不為每個節點單獨 round trip。
- 保留既有 max depth、max nodes、max context characters 與 relation whitelist。
- 不因效能優化改變 Graph score、confidence、evidence 或 community 規則。

### 5.6 記憶體與 GC

- 移除一般索引完成路徑中的強制同步 Full GC，交由 .NET GC 自行回收。
- 若實測證明大型 FBL full index 必須主動 compact，僅能在：
  - publish 完成；
  - 沒有 active conversation；
  - 沒有 UI request；
  - 記憶體超過經基準驗證的安全門檻；
  才執行一次，並記錄 pause duration。
- 不為此新增通用 Memory Manager。

---

## 6. Provider 與 Model 載入流程

### 6.1 Provider API

Provider list response 應一次包含 Picker 初始化所需的本機狀態：

```json
{
  "id": "github-copilot",
  "displayName": "GitHub Copilot",
  "isConfigured": true,
  "isAvailable": true,
  "statusMessage": null
}
```

規則：

- 列表 endpoint 不得由前端再逐 Provider 呼叫 key-status。
- 後端同一 request 不得重複查詢 Provider settings。
- 移除 `.GetAwaiter().GetResult()` 等 async-over-sync。
- 不在 Provider list 時呼叫遠端模型服務。

### 6.2 Model List

- 只為目前選中的 Provider 載入 Model。
- 切換 Provider 時取消前一個尚未完成的請求。
- 同一 Provider model list 可使用簡單 process-memory cache，建議 TTL 5 分鐘。
- 使用者完成登入、更新 API Key 或按重新整理時，使該 Provider cache 失效。
- 遠端模型服務失敗時保留 Picker，顯示可重試錯誤；不得讓整個 Composer 消失。
- 不把不同使用者 Credential 或完整 response 寫入 log。

### 6.3 UI 初始狀態

- Provider list 載入期間顯示 skeleton 或明確 loading，不顯示空白區域。
- 已儲存的 Provider／Model 先顯示本機值，再背景驗證可用性。
- Picker 錯誤不得阻止使用者閱讀既有對話。
- 一般對話與專案對話共用同一個 ProviderModelPicker 行為。

---

## 7. Knowledge Graph 與對話 UI

### 7.1 Knowledge Graph 初始載入

- 初始查詢預設最多 200 個節點。
- 初始邊數由後端依節點上限與既有安全係數限制。
- 使用者可明確選擇載入更多，但單次 UI request 不超過 1000 個節點。
- 點擊節點的鄰居展開單次最多新增 100 個節點。
- 搜尋應先取得小型結果，再以選取節點為中心展開，不因輸入關鍵字重載完整圖。
- 696 個菜單鏈路驗收使用 server-side query／分批遍歷，不要求瀏覽器同時渲染全部鏈路。

### 7.2 響應式面板

視窗行為固定為：

| Content width | Filter | Inspector | Graph |
|---:|---|---|---|
| `>= 1280px` | 可同時固定顯示 | 可同時固定顯示 | 剩餘空間 |
| `960–1279px` | 同時最多開一側 | 同時最多開一側 | 至少 55% |
| `800–959px` | Drawer／Overlay | Drawer／Overlay | 主區滿寬 |

驗收規則：

- Tauri 最小視窗 `800x600` 不得出現整頁水平捲軸。
- Toolbar 可換行或局部水平捲動，但主要動作不得被裁切。
- 開啟 Inspector 不得把 Canvas 壓縮成不可操作寬度。
- Modal／Drawer 必須可由 Escape、關閉按鈕與 backdrop 關閉。
- Resize handle 只能在固定面板模式顯示。

### 7.3 Markdown

- Table header、cell、border、hover 與 inline code 必須使用 theme token，不得依賴未安裝的 Tailwind Typography class。
- Light、Dark、Glass 三種主題下，一般文字與背景對比至少 4.5:1。
- Table 必須有容器水平捲動，不得撐破訊息卡片。
- 長檔案路徑、Symbol、URL 與 code 必須換行或在局部容器捲動。
- 不移除既有 code copy、syntax block、link 與 Markdown 功能。

### 7.4 工作進度與 Community Summary Toast

- 每個完成步驟顯示勾選狀態。
- 全部完成後進度區可折疊，預設保留一行「已完成 N 個步驟」。
- 失敗步驟顯示錯誤與重試，不得永遠顯示為進行中。
- Community AI Summary 成功後 4 秒內自動隱藏或進入完成摘要。
- 失敗狀態必須有明確關閉按鈕；可保留直到使用者關閉，但不得遮住 Composer。
- Toast 在窄視窗改為置中、限制最大寬度並避開底部輸入框。
- Polling 在 completed、failed、project change、component unmount 時停止。

### 7.5 UI 檔案拆分

`KnowledgeGraphPage.tsx` 只保留頁面 orchestration 與主要狀態協調。可依現有責任最多拆出下列四類元件：

1. Toolbar／Search。
2. Filter Panel。
3. Inspector／Details。
4. Canvas 與 selection adapter（若目前尚未獨立）。

限制：

- 優先重用既有元件，不為每個小區塊建立檔案。
- 不新增第二套 Graph state store。
- 不因拆檔引入 Redux、XState、MobX 或新的狀態框架。
- 若一個 extracted component 只有單一簡單 JSX 片段且無獨立責任，應留在原檔。

---

## 8. 後端結構與檔案規則

### 8.1 固定模組邊界

本次不搬動整個 Solution，採下列簡單規則：

```text
apps/agent-service/src
├─ Domain              純資料與核心規則
├─ Application         非基礎設施用例
├─ Infrastructure      SQLite、HTTP、Provider、外部整合
└─ Host                REST/SSE、DI、啟動

apps/agent-service/modules
├─ GraphRAG             FBL 索引、Neo4j、Retrieval、Project analysis tools
└─ Marketplace          Marketplace feature module
```

- GraphRAG 專屬型別不得散落新增到 Host／Infrastructure，除非是 API adapter 或跨模組 consumer。
- Host 不直接實作 Graph query、檔案掃描或 Provider persistence。
- `GraphRAGModule` 保持唯一 GraphRAG composition root。
- 不新增只有一個實作且沒有外部隔離價值的 Interface。
- 不新增新的 `.csproj` 來拆 GraphRAG。

### 8.2 大型類別拆分原則

- `Neo4jGraphStore` 允許維持 partial class，但每個 partial 只放一種責任：Schema、Publish、Retrieval、Community、Visualization。
- 共用低階 Neo4j query helper 只保留一份。
- `GraphRetrievalService` 應把 Query Planning 與結果組裝維持在同一 service；只在確有獨立測試價值時拆出純函式 helper。
- `ProjectAnalysisTools` 可保留單一 tool factory，但 file catalog／cache 若具有獨立生命週期可建立一個 concrete service。
- 拆分後必須刪除舊方法，不建立 forwarding wrapper 維持內部相容。

### 8.3 程式碼規模限制

本次不是以行數作唯一品質指標，但實作必須遵守：

- 產品程式新增檔案原則上不超過 12 支；超過時必須在 Code Review 說明每支檔案的獨立責任。
- 不新增超過 2 個新的 Interface；每個 Interface 必須符合外部基礎設施隔離或兩個實作條件。
- 不新增新的 NuGet／npm／Cargo 套件，除非現有能力無法安全完成且先取得明確同意。
- 不新增 feature flag framework；各 phase 以 Git commit 為回滾單位。
- 不建立 Compat、Legacy、V2、新舊雙軌類別；已替代的舊程式直接刪除。
- 重要 class、public method、非直覺效能與安全邏輯必須有清楚的繁體中文註解。
- 註解說明「為什麼」與限制，不逐行翻譯程式碼。

### 8.4 空目錄與 package

- 檢查 `packages/config`、`mcp-presets`、`prompt-assets`、`skills` 的實際 import、build 與 runtime consumer。
- 沒有 consumer、source 與發布用途的 package 應刪除，不保留空殼 roadmap。
- 有實際資產用途但不需要 build 的 package，應移出 pnpm package scope，改為清楚命名的 assets／templates 目錄。
- Root `pnpm` script 只宣告實際可執行命令。

---

## 9. 測試、產物與版本控制

### 9.1 測試策略

保留兩層測試：

#### Tracked core regression

乾淨 clone 必須具備一套納入版控的最小測試，至少覆蓋：

- 專案問答 Ready／StaleButUsable／Unavailable 三種 Graph 狀態。
- Neo4j unavailable 時仍建立 source tools 並進入 Agent streaming。
- 回答完成不等待 title generation。
- Provider list 不發生重複 setting query 的 contract。
- Tool call 去重與硬上限。
- File range root escape 防護。
- Markdown／Knowledge Graph 關鍵元件 typecheck 與 build。
- Debug process manifest 的純函式或腳本 dry-run 驗證。

此測試可放在既有 tracked `apps/agent-service/tests` 邊界，不複製整套本機 UnitTests。若需要 `.csproj`，只建立一個最小 tracked test project。

#### Local exhaustive regression

- `apps/UnitTests` 可依既有決策維持本機完整測試且不納入版控。
- 驗收腳本若偵測不到本機 UnitTests，必須標示 `SKIPPED: local exhaustive tests unavailable`，不得宣告完整驗收通過。
- 完成正式驗收時，本機 200 項既有測試仍必須執行並通過。

### 9.2 Frontend 驗證

- Root `pnpm lint` 必須二選一：
  - 建立實際可執行的 lint script；或
  - 移除誤導的 root lint script，並以 typecheck／build 作固定 gate。
- 本次不為了形式新增新的 lint 套件；若專案已無 linter，採第二種。
- 必須執行 desktop typecheck 與 production build。
- Build chunk warning 必須記錄；本次只處理由本次改動造成或可低風險 lazy-load 的大型 chunk，不全面重做 bundling。

### 9.3 Artifacts 與 temp

- `artifacts` 只存本機或 CI 產物，預設不納入版控。
- Golden input fixture 若是規格 source of truth，移到 tracked tests fixtures，保持小型、可 review、無絕對路徑。
- `.trx`、build output、test-bin、runtime diagnostics、含機器路徑 JSON 不得納入版控。
- 根目錄 `temp` 與 `apps/temp` 不保存 tracked runtime 檔案；若無必要應移除 `apps/temp`。
- 驗收後執行 `git grep`，不得在 tracked test artifact 中找到 `D:\Modern-Wingman` 或 `C:\Users\...`。

### 9.4 Bundled tools

- `apps/agent-service/tools/copilot-cli/win32-x64` 若為官方完整 runtime bundle，不得任意刪除內部檔案。
- 先建立實際 runtime consumer 與平台清單，再判斷是否有可安全移除的重複 bundle。
- 若只能整包保留，應記錄來源版本、SHA-256、授權與升級方式。
- 不把開發期間下載 cache、測試 proxy、其他平台 bundle 新增到 Git。
- 本次 bundled tools 瘦身是 P2，不得阻塞 P0／P1 功能修正。

---

## 10. 安全、CORS、TLS、CSP 與 Logging

### 10.1 Local-only Binding

- Agent Service、Vite、Neo4j Bolt 與 Neo4j Browser 必須綁定 loopback。
- `AllowedHosts` 不使用 `*`，應限制本機 Host。
- 啟動 log 明確顯示實際 bind address，但不得顯示 Credential。

### 10.2 CORS

開發模式只允許實際需要的來源，例如：

```text
http://127.0.0.1:4173
http://localhost:4173
Tauri production WebView 的實際 origin
```

- 不再使用 AllowAnyOrigin。
- Method 與 Header 只允許目前 API 使用範圍。
- 實作前必須透過 Tauri dev／production smoke test 確認 production origin，不得憑猜測硬編碼。

### 10.3 TLS 憑證撤銷檢查

- HttpClient 預設啟用 `CheckCertificateRevocationList`。
- 若企業 Proxy 環境確實阻擋，可提供既有設定系統中的 Development-only override；預設不得關閉。
- 關閉時 log warning，但不得包含 URL query 中的 secret。

### 10.4 Tauri CSP

- 依實際使用的 Agent API、圖片、語音與 WebView 資源建立最小 CSP。
- 不允許任意遠端 script。
- Light／Dark／Glass CSS 所需 inline style 若必須允許，需在註解說明原因。
- CSP 完成後必須執行 Tauri production build smoke test。

### 10.5 Logging

Development 預設：

```text
Default = Information
Microsoft.AspNetCore = Warning 或 Information
AgentService = Debug
```

- `Trace` 只透過明確的短期診斷設定開啟。
- Graph 索引與 Retrieval 保留 stage duration、node count、query count、cache hit、tool count。
- 不記錄完整 Prompt、原始碼全文、PAT、Authorization、資料庫密碼或 Connection String。

---

## 11. 文件與設定同步

### 11.1 必須更新

- `README.md`
- `README_zh-TW.md`
- `docs/debug-guide.md`
- `.vscode/launch.json`
- `.vscode/tasks.json`
- `apps/agent-service/Properties/launchSettings.json`
- `apps/agent-service/appsettings*.json`
- `apps/desktop/vite.config.ts`
- `apps/desktop/src-tauri/tauri.conf.json`
- Frontend API base URL 定義
- 受影響的 GraphRAG／Project Analysis 註解

### 11.2 必須清理的失效內容

- 已移除的 gRPC／Tonic／port 5001 說明。
- 已失效的 Vite port 1420。
- launch profile 中未使用的 5200／7264。
- GraphRAG V3 名稱與已不存在的相容流程。
- 暗示目前仍支援 Java 索引的 active 文件；歷史規格可保留但必須加上「已失效／僅供歷史參考」。
- 不存在或無法執行的 lint／test 指令。
- 特定使用者與特定磁碟的絕對路徑。

### 11.3 預期修改觸點

下表用來協助實作者先找現有責任，不代表每個檔案都必須修改，也不得因為表中列出就機械式建立新檔案。

| 議題 | 優先檢查的現有檔案 |
|---|---|
| Debug／Port／process lifecycle | `.vscode/launch.json`、`.vscode/tasks.json`、`scripts/dev/start-all.ps1`、`scripts/dev/stop-all.ps1` |
| Agent bind／readiness／CORS | `Program.cs`、`ServiceRegistration.cs`、`appsettings*.json`、`launchSettings.json` |
| 專案問答降級／SSE／title | `ConversationEndpoints.cs` 與現有 Conversation service |
| Index status／watcher／catch-up | `GraphIndexWatcher.cs`、`GraphIndexingService.cs` |
| Source tools／tool budget | `ProjectAnalysisTools.cs` 與 Agent instruction 組裝位置 |
| Query concurrency／BFS | `GraphRetrievalService.cs`、`Neo4jGraphStore.Retrieval.cs` |
| Neo4j managed runtime | `Neo4jRuntime.cs`、`Neo4jGraphContracts.cs` |
| Provider／Model | `ModelProviderService.cs`、`ProviderEndpoints.cs`、`ProviderModelPicker.tsx` |
| API base URL | `services/agent-api/client.ts`、`speech.ts` 與其 consumer |
| Knowledge Graph UI | `KnowledgeGraphPage.tsx`、既有 graph components、`services/agent-api/projects.ts` |
| Markdown／進度 | `markdown-renderer.tsx`、`CommunitySummaryProgressToast.tsx`、既有 workflow progress component |
| Tauri security／window | `tauri.conf.json`、`vite.config.ts` |
| 測試／產物／文件 | `.gitignore`、`apps/agent-service/tests`、`scripts/dev/*acceptance*`、README 與 debug guide |

### 11.4 API 與事件契約原則

- 不新增第二套專案問答 endpoint；沿用 Conversation REST + SSE。
- Provider list 可加入 `isConfigured`、`isAvailable`、`statusMessage` 等欄位，屬於 additive contract；前端不得再為初始化逐筆查 key-status。
- 專案回答的現有 status／progress event 應增加可顯示的 context 狀態：Graph ready、Graph stale 或 Graph unavailable。若現有 event 已能表達，直接重用，不新增重複 event type。
- Graph unavailable 是可降級狀態，不等同整個 message request 失敗；最終回答內必須保留資料來源缺口。
- API error response 維持單一格式，至少包含可顯示 message 與可診斷 code；不得把內部 stack trace 或 secret 回傳前端。
- Graph Viewer endpoint 路徑維持不變，只調整安全預設 limit 與分批載入行為。
- 不為內部 refactor 保留舊 DTO adapter；前後端在同一 Phase 一次切換。

---

## 12. 分階段實作計畫

每個 Phase 必須可獨立建置、測試與回滾；前一 Phase 未通過不得開始下一 Phase。允許多 Agent 平行做互不重疊的只讀分析或測試，但同一檔案同一時間只能由一個實作者修改。

### Phase 0 — Baseline 與保護

- [ ] 建立實作分支。
- [ ] 保存 `git status`、HEAD、Port、build、typecheck、cargo check 與本機測試基準。
- [ ] 記錄 FBL 696 menu chain、Graph node／edge count、active version 與 Golden 結果。
- [ ] 記錄一般對話、專案對話、Provider Picker、Graph Viewer 的手動 smoke 結果。
- [ ] 確認 SQLite PAT／Credential 讀取正常。
- [ ] 確認測試只能使用本機 SQLite／Neo4j／temp。

### Phase 1 — Debug、Port 與 Readiness

- [ ] 統一 Port 與 API base URL。
- [ ] 修正 launchSettings 與 README。
- [ ] 移除 stop script 的 `D:\CargoTarget` 硬編碼。
- [ ] 以 PID + StartTime + executable path manifest 管理本輪程序。
- [ ] 移除或正式化 17475。
- [ ] 將 AppReady、GraphReady、ProjectToolsReady 分離。
- [ ] 驗證按一次 Debug／Stop 的完整生命週期。

### Phase 2 — 專案問答降級與串流完成

- [ ] Neo4j Ready／Stale／Unavailable 三態。
- [ ] Neo4j unavailable 時仍建立 source tools。
- [ ] 移除問答 request 中的 full-project Catch-up。
- [ ] `done` 不等待 title generation。
- [ ] 前端顯示 Graph 缺口但不把整個頁面顯示為 Failed to fetch。
- [ ] 補 tracked core regression tests。

### Phase 3 — Tool 與 Retrieval 效能

- [ ] 建立或重用單一 Project File Catalog。
- [ ] 全文搜尋改為 cancellable bounded I/O。
- [ ] Symbol 搜尋先 Graph／candidate narrowing，再解析候選檔。
- [ ] 加入 per-turn query／read cache 與工具去重。
- [ ] 加入單輪軟／硬工具上限。
- [ ] Query Variant concurrency 限制為 4。
- [ ] BFS 改為 layer batch neighbor query。
- [ ] 移除無條件強制 Full GC。

### Phase 4 — Provider／Model 與對話 UI

- [ ] Provider API 一次回傳本機狀態。
- [ ] 移除 async-over-sync 與前端 N+1 key-status。
- [ ] 只載入選中 Provider 的 models，支援取消與簡單 TTL cache。
- [ ] Provider／Model loading、error、retry UI。
- [ ] 確認一般與專案對話共用一致流程。

### Phase 5 — Knowledge Graph／Markdown／Progress UI

- [ ] Graph initial limit 200、顯式 load more、neighbor incremental expansion。
- [ ] 800x600 響應式 drawer／overlay。
- [ ] Markdown table 使用 theme token 並通過三主題對比。
- [ ] Progress 完成勾選與折疊。
- [ ] Community Summary toast success／failure／dismiss 行為。
- [ ] 在必要範圍拆分 KnowledgeGraphPage，無第二套 state。

### Phase 6 — 結構、產物、文件與安全

- [ ] 整理 Neo4jGraphStore partial responsibility。
- [ ] 清理空殼 package、失效設定、V3／gRPC／Java active 說明。
- [ ] 修正 artifacts／temp／absolute path 追蹤。
- [ ] 讓 root scripts 與實際可執行命令一致。
- [ ] 限制 local binding、CORS、TLS revocation 與 CSP。
- [ ] 降低正常 Development log level。
- [ ] 記錄 bundled runtime 保留／移除決策。

### Phase 7 — 全面驗收與 Code Review

- [ ] 執行第 13 節全部自動驗收。
- [ ] 執行第 14 節人工驗收。
- [ ] 獨立 Reviewer 檢查流程、效能、UI、安全、死碼與中文註解。
- [ ] 清除測試資料、Neo4j 測試圖、temp 與背景程序。
- [ ] 確認 PAT／Credential 未被修改。
- [ ] 產出完整驗收報告與 Known Gaps。

---

## 13. 自動測試與效能驗收

### 13.1 固定建置 Gate

下列命令必須全部成功，或在實作後改成等價且已更新文件的命令：

```powershell
dotnet build apps/agent-service/AgentService.csproj --configuration Debug
dotnet test <tracked-core-regression-project> --configuration Debug
dotnet test apps/UnitTests/AgentService.UnitTests.csproj --configuration Debug
pnpm typecheck
pnpm --filter @modern-wingman/desktop build
cargo check --manifest-path apps/desktop/src-tauri/Cargo.toml
git diff --check
```

規則：

- 本機 UnitTests 不存在時，不能宣告完整驗收通過。
- Build warning 必須列入報告；本次新增 warning 視為 FAIL。
- 不得只執行受影響的少量測試就宣告全部通過。

### 13.2 啟動與停止 Gate

| ID | 驗收情境 | 通過條件 |
|---|---|---|
| RT1 | 無服務執行時按 F5 | 只啟動一個 Vite、一個 Agent Service、一個 Tauri、一個 Managed Neo4j |
| RT2 | 服務已由上一輪正常停止 | Port 4173、5002、17688 可再次使用 |
| RT3 | 上一輪異常殘留且 manifest 可驗證 | 只清理 Modern Wingman 擁有的殘留程序 |
| RT4 | 未知程序占用 5002 | 不殺未知程序；顯示 PID、名稱與路徑並停止啟動 |
| RT5 | 按 Stop | 本輪服務全部停止，無 orphan listener |
| RT6 | 重複按 F5 | 不產生第二份 listener，不出現 address already in use |
| RT7 | 換工作區／Cargo target | stop 流程不依賴 `D:\CargoTarget` |

### 13.3 專案問答 Gate

| ID | 情境 | 通過條件 |
|---|---|---|
| QA1 | Graph Ready，問 `140078資料流` | 執行 Graph retrieval 並回傳可追溯鏈路 |
| QA2 | Graph Ready，追問 Controller 原始碼 | Agent 可呼叫 read tool，回傳路徑與行號 |
| QA3 | Neo4j 停止，問明確 Symbol | 不回 503；使用 source tools 回答並標示 Graph unavailable |
| QA4 | Graph stale，Working Copy 已修改 | 使用舊圖導航、最新檔案確認，標示 stale risk |
| QA5 | 無 active graph、專案路徑存在 | 仍進入 RunStreamingAsync，不因 Graph 前置條件中止 |
| QA6 | 第一輪回答完成、標題服務延遲 10 秒 | `done` 不等待標題；回答維持成功 |
| QA7 | Agent 重複呼叫相同 search | 實際 I/O 只執行一次，進度不重複列出 |
| QA8 | 工具到達硬上限 | 停止工具呼叫，回答列出確認與未知，不進入循環 |
| QA9 | watcher 正常且專案無異動 | 問答前不遍歷並 SHA-256 全專案 |
| QA10 | watcher overflow | 顯示需同步／排程背景 Catch-up，不阻塞當次可回答問題 |

### 13.4 Graph 精度不回歸 Gate

- 696 個有效菜單功能仍可找到完整既有鏈路。
- GraphRAG V4 A／B／S／C／Q 既有 Golden 不得低於目前已通過結果。
- Node／Edge canonical ID 與 active version publish 行為不變。
- Community deterministic summary 數量與 membership 不得無原因改變。
- AI Summary 背景佇列不得阻塞主索引可用狀態。
- 若效能修改造成結果差異，必須逐筆說明是 bug fix 或 regression；未釐清前不得接受。

### 13.5 效能 Gate

效能測試必須在同一台機器、同一份 FBL 專案、同一 Graph version 下比較修改前後；每項至少 warm-up 1 次、正式量測 5 次，報告 median 與 p95。

| ID | 指標 | 目標 |
|---|---|---|
| PF1 | 無異動專案問題前置掃描 | 不做全專案 SHA-256；前置本機處理 median < 500 ms |
| PF2 | Provider list 本機 API | warm median < 300 ms，且單一 HTTP request |
| PF3 | Model list cache hit | median < 100 ms |
| PF4 | 相同工具參數重複呼叫 | 第二次不做磁碟／Neo4j I/O |
| PF5 | Source search thread usage | 無長時間同步阻塞 request thread；取消後 1 秒內停止 |
| PF6 | Query Variant Neo4j concurrency | 同時最多 4 |
| PF7 | Graph initial render payload | 預設節點 <= 200 |
| PF8 | Desktop production chunk | 不得比基準增加 10% 以上；若下降則記錄 |
| PF9 | Full index correctness | 結果不回歸；wall clock 不得比基準慢 10% 以上 |
| PF10 | Incremental changed-file index | 不重新處理未變更的全部檔案 |

Provider／LLM 外部網路延遲須與本機處理時間分開記錄，不以網路時間判定本機效能失敗。

### 13.6 安全 Gate

- Agent Service 不監聽 `0.0.0.0` 或 LAN IP。
- 非允許 Origin 的 CORS preflight 被拒絕。
- Tauri dev 與 production build 均能連 Agent API。
- TLS revocation check 預設開啟。
- CSP 啟用後一般對話、專案對話、圖片、Markdown、Graph Canvas、語音仍可用。
- Log、Exception、artifact 不包含 PAT、Authorization 或資料庫密碼。
- Project file tool 無法讀取 Project Root 外檔案。

### 13.7 結構與版本控制 Gate

| ID | 驗收項目 | 通過條件 |
|---|---|---|
| ST1 | Root scripts | 每個宣告的 build／typecheck／lint／test 指令都可執行，或已移除失效指令 |
| ST2 | Tracked regression | 乾淨 clone 不依賴 ignored `apps/UnitTests` 也能執行核心 Smoke／Contract tests |
| ST3 | Generated artifacts | `.trx`、test-bin、runtime diagnostics 與大檔測試輸出不再納入版控 |
| ST4 | Absolute paths | Tracked source／fixture／doc 不含特定使用者的 build artifact 絕對路徑；必要文件範例除外且必須明確標示 |
| ST5 | Empty packages | 無 consumer 的 package 已刪除；保留者有清楚用途 |
| ST6 | Dead code | 重構後無失效 DI、無呼叫者 wrapper、新舊雙軌 class 或無效設定 |
| ST7 | Documentation | README、debug guide、launch、tasks、Port、REST transport 描述一致 |
| ST8 | File count | 新增產品檔案／Interface 符合第 8.3 節限制，超出者有 Reviewer 明確核准 |

### 13.8 UI 自動與人工結合 Gate

| ID | 驗收項目 | 通過條件 |
|---|---|---|
| UI1 | Minimum viewport | 800x600 無整頁水平捲軸或主要操作裁切 |
| UI2 | Graph panels | 窄視窗使用 drawer／overlay，canvas 保持可操作 |
| UI3 | Graph payload | 初始節點 <= 200，load more 與 neighbor expansion 可用 |
| UI4 | Markdown table | Light／Dark／Glass header 與 cell 對比清楚，局部可橫向捲動 |
| UI5 | Progress | 完成步驟有勾選，全數完成後可折疊 |
| UI6 | Community toast | Success 可消失；Failure 可關閉；兩者不遮 Composer |
| UI7 | Provider picker | 載入、成功、失敗、重試與窄視窗 dropdown 均可用 |
| UI8 | Localized failures | Graph 錯誤只影響 Graph 區域，不使整個 Projects page 顯示 Failed to fetch |

---

## 14. 人工 UI 與端對端驗收

### 14.1 視窗尺寸

至少測試：

```text
800x600
1024x768
1280x800
1920x1080
```

每個尺寸都驗證：

- App sidebar、Project sidebar、Header、Composer 不重疊。
- Provider／Model dropdown 完整可見。
- Knowledge Graph toolbar、filter、inspector、canvas 可操作。
- Modal、Drawer、Toast 不超出視窗。
- Message table、code block、長路徑不撐破訊息卡。

### 14.2 主題

Light、Dark、Glass 各測試：

- Markdown heading、paragraph、list、table、inline code、code block、link。
- User／Agent message 對比。
- Graph node label、edge label、selected state、hover state。
- Loading、success、warning、error、disabled 狀態。

### 14.3 對話體驗

- 新對話進入時 Provider／Model 立即顯示已儲存值或 loading skeleton。
- 專案問題送出後 500ms 內看到第一個本機工作狀態事件。
- 工具開始、完成、命中數、讀取行數可見。
- 已完成工作進度有勾選並可折疊。
- 取消回答可停止 LLM 與後續工具呼叫。
- 標題延遲不影響答案完成。

### 14.4 Graph Viewer

- 搜尋 `140078` 可看到命中與少量完整鏈路。
- 搜尋菜單、Endpoint、Controller、SQL object 都能選取並展開。
- 初始不渲染整張大圖。
- Inspector 內容與圖選取同步。
- Neo4j 停止時只顯示 Graph 區域錯誤，不使整個 Projects page Failed to fetch。

### 14.5 Debug 體驗

- VS Code Run and Debug 只有一個主要入口。
- F5 完成後 Desktop、Agent、Vite、Neo4j 狀態可判讀。
- Stop 後工作管理員與 Port 檢查沒有本輪 orphan。
- 再次 F5 不需人工到工作管理員結束程序。

---

## 15. Code Review 固定檢查表

Reviewer 必須逐項確認：

### 15.1 流程與正確性

- [ ] Graph unavailable 不再阻斷 source-only answer。
- [ ] Graph stale 回答有版本警示。
- [ ] 問答不觸發同步 full Catch-up。
- [ ] title generation 不阻塞 `done`。
- [ ] 工具上限、取消與去重有效。
- [ ] 696 menu chain 與 Golden 無回歸。

### 15.2 效能

- [ ] 沒有新增 `.GetAwaiter().GetResult()`／`.Result`／`.Wait()`。
- [ ] 沒有在 request path 無限制 `Directory.EnumerateFiles`。
- [ ] 沒有無上限 `Task.WhenAll`。
- [ ] 沒有每節點一查的新增 N+1。
- [ ] cache 有明確生命週期與 invalidation。
- [ ] 沒有為效能引入額外服務或資料庫。

### 15.3 UI

- [ ] 800x600 無整頁水平捲動。
- [ ] 三主題表格文字可讀。
- [ ] Toast 可關閉且不遮 Composer。
- [ ] 完成進度可折疊。
- [ ] Graph initial payload 受限。
- [ ] 沒有建立第二套 Graph state。

### 15.4 結構與註解

- [ ] 新增檔案與 Interface 數量符合限制。
- [ ] 每個新增 class／public method 有必要且清楚的繁體中文註解。
- [ ] 沒有逐行翻譯式註解。
- [ ] 沒有 Compat／Legacy 雙軌殘留。
- [ ] 移除失效 using、DI、設定、腳本、文件與測試。
- [ ] 沒有未使用程式碼與沒有 consumer 的 package。

### 15.5 安全與資料

- [ ] PAT／Credential 未被刪除、覆寫或輸出。
- [ ] SQL Server 僅執行唯讀查詢。
- [ ] 測試 Neo4j 與人工 Graph 隔離。
- [ ] Project file path 不能逃逸 root。
- [ ] CORS、TLS、CSP 經 Tauri 實測。
- [ ] 測試資料與 temp 已清除。

---

## 16. 回滾與失敗處理

- 每個 Phase 使用獨立 commit；不得把全部重構壓成單一巨大 commit。
- 不建立永久雙軌 feature flag；Phase 驗收失敗時回滾該 Phase commit。
- GraphRAG publish 仍保留上一版成功 active version；新索引失敗不得刪除上一版。
- Provider API contract 若改變，前後端必須在同一 Phase 一起修改與驗收。
- Port／launch 修改失敗時，回滾完整 Phase 1，不留下半套 task 或 script。
- UI 拆分發生 regression 時可回滾 UI commit，不影響後端流程修正。
- Security hardening 若遇企業環境相容問題，必須保留 loopback 限制；只能針對已證明的 origin／proxy 問題做最小修正。
- 測試失敗、取消或逾時時，仍須停止背景程序並清理測試資料。

---

## 17. 驗收報告格式

完成實作後必須產出一份報告，至少包含：

```text
1. Branch、HEAD、實作日期
2. Phase 0–7 完成狀態
3. 修改／新增／刪除檔案與用途
4. 新增 class、interface、method、設定與實際呼叫者
5. 破壞性變更與已刪除舊流程
6. Build、tracked tests、local UnitTests、typecheck、desktop build、cargo check
7. RT1–RT7 結果
8. QA1–QA10 結果
9. ST1–ST8 結構與版本控制結果
10. UI1–UI8 與四種視窗尺寸結果
11. Graph 696 menu chain 與既有 Golden 結果
12. PF1–PF10 修改前後 median／p95
13. Light／Dark／Glass 主題結果
14. CORS、TLS、CSP、path traversal、secret scan 結果
15. SQLite PAT／Credential 保留結果
16. Neo4j／temp／背景程序清理結果
17. Bundled tools 與 repository artifact 處置
18. Known Gaps、未驗證項目與人工確認項目
```

不得以「應該正常」、「理論上通過」或「已遵循最佳實踐」代替實際結果。

---

## 18. Definition of Done

只有同時符合以下條件，才可宣告本規格完成：

1. Phase 0–7 全部完成或有使用者明確接受的排除項目。
2. Agent Service、Desktop、Tauri 全部可建置。
3. Tracked core regression 與本機完整 UnitTests 全數通過。
4. VS Code F5／Stop 可重複執行且沒有 Port 衝突或 orphan process。
5. Neo4j unavailable 時，專案 Agent 仍可執行 source-only answer。
6. 問答前不再重掃並雜湊整個未異動專案。
7. 主要回答完成不等待 title generation。
8. Provider／Model Picker 不再使用 N+1 key-status 初始化。
9. 原始碼工具具備取消、bounded concurrency、去重與本輪 cache。
10. Graph Query Variant 與 BFS 不再產生無上限併發／N+1。
11. 696 個菜單功能鏈路與 GraphRAG Golden 不回歸。
12. 800x600 與三種主題 UI 驗收通過。
13. Markdown table、工作進度與 Community Toast 問題修正。
14. Port、README、launch、tasks、appsettings、Vite、Tauri 設定一致。
15. 沒有新增未使用程式碼、空殼 package、失效相容層或不必要套件。
16. 關鍵 class、method、效能與安全邏輯具有清楚的繁體中文註解。
17. Tracked artifacts 不含 build output、本機絕對路徑或 secret。
18. CORS、TLS、CSP、loopback 與 path root 防護驗收通過。
19. SQLite 既有 PAT／Credential 完整保留。
20. 測試產生的 SQLite 資料、Neo4j 測試圖、temp 與背景程序已清除。
21. 驗收報告清楚列出所有實際執行結果、Known Gaps 與未驗證項目。

---

## 19. 最終實作原則

本規格的完成標準不是新增更多程式，而是讓 Modern Wingman 的現有流程更短、更快、更穩定、更容易維護。

實作時必須持續遵守：

```text
先修阻斷流程
→ 再消除重複 I/O
→ 再改善 UI
→ 最後整理結構、文件與安全
```

任何新增抽象、檔案、快取、設定或背景工作，都必須能直接對應本規格中的已確認問題與驗收項目。若現有類別可以用少量修改完成，就不得建立第二套架構。
