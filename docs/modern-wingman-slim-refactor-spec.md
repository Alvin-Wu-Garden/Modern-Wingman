# Modern Wingman 大型瘦身重構規格

## 1. 文件目的

本文件固定 Modern Wingman 未發布版本的大型破壞性重構邊界。重構的首要目標不是增加功能，而是讓產品只保留三個清楚、可維護的核心能力：

1. 一般對話：不綁定專案，可自由問答。
2. 專案解析：只回答專案問題，並根據 GraphRAG 證據指出應修改的檔案、原因與建議做法，不直接修改程式碼。
3. Marketplace：管理 Agent Skill 與 MCP Server，並部署到明確支援的外部 Agent。

本次允許破壞性 API、資料庫 schema 與本機資料變更。Modern Wingman 尚未正式發布，因此不保留舊版相容層、雙軌實作或未使用的 migration dead code。

## 2. 固定產品決策

### 2.1 支援平台

- 只正式支援 Windows 10／11 x64。
- Secret 使用 Windows DPAPI CurrentUser 保護。
- 不為 Linux／macOS 保留尚未驗證的抽象或分支。

### 2.2 一般對話

- 不綁定專案。
- 不讀取 GraphRAG。
- 不修改檔案。
- 不執行 Shell、Build、Test。
- 不執行 Git／SVN 寫入。
- 不使用 Coding Workflow、Approval、Changeset、Worktree、Subagent 或 Schedule。
- 支援多個對話、附件、語音輸入、Markdown 與串流回答。
- Provider／Model 必須可在共用輸入 UI 中切換。
- Modern Wingman 可載入 instruction-only Agent Skill。
- Modern Wingman 不執行 Skill script，也不直接執行 Marketplace MCP Tool。

### 2.3 專案解析

- 專案可以有多個對話。
- 專案對話與一般對話共用 Conversation、Message、SSE、錯誤處理、附件 UI 與訊息元件。
- 每個專案問題都必須先從 GraphRAG 取得證據。
- 純架構問題直接解釋。
- Bug 或新需求只回答：
  - 建議修改的檔案、Symbol、Route、SQL 或資料庫物件。
  - 修改原因。
  - 建議做法。
  - 可能影響。
- 不直接修改專案檔案。
- 每個回答必須附上可追溯來源。
- 附件只補充當次問題，不寫入 GraphRAG。
- 保留目前投資交易／風控系統導向的索引與 AI community enrichment 流程。
- 結構圖譜發布成功後即可問答；AI 業務摘要在背景繼續產生。
- 畫面顯示「索引可用 · 業務摘要生成中 23/80」，不得把摘要尚未完成顯示成「部分索引可用」。
- 保留 Neo4j Browser 類型的唯讀知識圖譜檢視器。

### 2.4 專案資料庫

- 專案右鍵選單提供「設定資料庫連線」。
- 每個專案最多一個主要資料庫。
- 支援 SQL Server：
  - SQL 帳號密碼。
  - Windows Integrated Security。
- 支援 SQLite：
  - 使用檔案選擇器。
  - 固定 ReadOnly mode。
- 正式執行只讀取專案設定，不再讀取 `WINGMAN_GRAPHRAG_SQLSERVER_CONNECTION`。
- 連線字串以 DPAPI 加密保存，不得寫入 log、Graph、manifest 或錯誤訊息。
- 已設定資料庫但 preflight 失敗時，本次索引失敗並保留上一版成功圖譜。
- 未設定資料庫時允許 source-only 索引，coverage 必須明確標示 database not configured。
- 索引所有 schema、View、Stored Procedure 與 Function。
- 資料列只索引選單、設定、列舉、排程等必要 metadata table，並限制筆數與欄位長度。
- 不索引交易、客戶、部位、金額等業務資料列。

### 2.5 Marketplace

- 只保留一套 .NET Marketplace。
- Artifact 只支援 Agent Skill 與 MCP Server。
- 移除 Wingman Plugin、Function、Hook 與 plugin runtime。
- 移除 Rust Legacy Skills、Library、MCP 與其 SQLite schema。
- 因產品尚未發布，不遷移舊安裝資料，直接清空。
- 部署使用實體複製，不使用 junction 或 symlink。
- 保留 Global／Project scope、部署前預覽、內容 hash、衝突確認與可恢復備份。
- 移除時只移除 Modern Wingman 有紀錄且 hash 相符的部署內容。
- 支援以下十二個 target：
  1. Codex
  2. Claude Code
  3. GitHub Copilot
  4. Cursor
  5. VS Code
  6. Cline
  7. Roo Code
  8. Kilo Code
  9. Gemini CLI
  10. OpenCode
  11. Antigravity
  12. Grok
- GitHub Copilot target 表示個人／CLI／Cloud Agent scope。
- VS Code target 表示 workspace scope，Skill 使用 VS Code 支援的專案路徑，MCP 使用 `.vscode/mcp.json`。
- Codex CLI 與 Codex IDE extension 共用一個 Codex target。

### 2.6 VCS

- 保留本機資料夾、Git clone／update、SVN checkout／update。
- 移除 Agent Git commit、push、branch 寫入。
- 移除 Agent SVN commit。
- 移除 Worktree、Shadow Git、Snapshot workspace 與 protected-ref 寫入流程。
- VCS 認證仍使用 DPAPI。

### 2.7 明確維持現狀

- CORS 維持目前 AllowAnyOrigin／AllowAnyMethod／AllowAnyHeader。
- HttpClient 維持停用憑證撤銷檢查。
- 本次不導入 CopilotKit。
- 本次不把投資系統索引 Prompt 通用化。

## 3. 目標架構

```text
Desktop App
├─ AppShell
├─ General Conversations
├─ Projects
│  ├─ Project Conversations
│  ├─ Index Status
│  ├─ Database Connection
│  └─ Knowledge Graph Viewer
├─ Marketplace
└─ Settings
        │
        ▼
Single REST + SSE Agent Service
├─ Conversation Service
│  ├─ General answer
│  └─ Project GraphRAG answer
├─ GraphRAG
├─ Project Database Source
├─ Provider Runtime
├─ Speech
└─ Marketplace
```

不再存在第二套 gRPC Run transport、第二套專案 QA API、Rust Marketplace persistence 或 Coding Agent orchestration。

## 4. Conversation 契約

Conversation 增加 nullable `ProjectId`：

```json
{
  "id": "conversation-id",
  "projectId": "project-id-or-null",
  "title": "對話標題",
  "createdAt": "timestamp",
  "updatedAt": "timestamp"
}
```

- `ProjectId == null`：一般對話。
- `ProjectId != null`：專案對話。
- 所有訊息使用 `POST /api/conversations/{id}/messages`。
- 建立專案對話時由後端驗證 Project 存在。
- 專案刪除時 cascade 刪除 Conversation 與 Message。
- SSE 回傳解析後的 Provider／Model、文字 token、usage 與完成／錯誤狀態。
- 可追溯來源直接包含在專案回答的 Markdown 中；索引進度由既有專案狀態 API 輪詢，
  不再為同一份狀態維護第二套 SSE 契約。

## 5. 前端共用元件

- 保留並共用 `MessageComposer`。
- 移除只轉傳 props 的 `ChatInput`。
- 建立單一 `ConversationPane`，共用：
  - Message list。
  - Markdown。
  - Streaming。
  - 自動捲動。
  - 空白狀態。
  - Provider／Model picker。
  - Attachment。
  - Speech-to-Text。
  - Cancel、Retry 與錯誤顯示。
- `ChatPage` 不再兼任整個 App Shell。
- 若只有單一 Tauri 視窗與單一路徑，移除只有 `/` 的 React Router。
- 所有 Agent Service URL 由單一 API client 設定提供。

## 6. 必須移除的功能與程式碼

### 6.1 專案功能

- Impact Analysis UI、API、model 與 GraphRAG method。
- Data Intelligence UI、API、glossary、runtime database plugin 與 validators。
- AGENTS.md UI、API、GraphRAG generator 與相關 type。
- Change Intent Classifier。
- Clarification Planner。
- Change Analysis Session 與 SQLite store。
- Change Brief、Evidence Pack、Implementation Plan。

### 6.2 Coding Agent

- Agent Mode。
- Explore／Plan／Code／Verify Workflow。
- Approval。
- Changeset 與 hunk restore／accept。
- Apply Patch、Delete File、Run Command、Build、Test。
- Git commit／push／branch Agent tool。
- SVN commit Agent tool。
- Run Workspace Lifecycle。
- Git Worktree、SVN Shadow Git、Snapshot workspace。
- Agent Workbench 與 Run Timeline。
- Agent Schedule。
- Subagent。

### 6.3 傳輸與擴充

- gRPC Health／Run service、proto 與 package。
- Rust Skills／Library／MCP command、SQLite 與 dependencies。
- Wingman Plugin runtime。
- Marketplace plugin installation/configuration tables。
- Legacy Skills UI。

### 6.4 診斷

- 複雜 Audit CSV export、facets、eval、tool approval audit。
- 保留最小模型請求錯誤、索引錯誤與必要診斷。

## 7. 資料清理

- `apps/wingman_dev.db` 可以完全清空並以新 schema 重建。
- 移除淘汰資料表後執行 `VACUUM`。
- 刪除未被程式、建置、測試或正式 migration 引用的根目錄 SQL dump 與手動資料匯出。
- 不保留讀取舊 schema 的 compatibility code。
- 專案刪除時清除：
  - Project row。
  - Database secret。
  - Conversation／Message。
  - Index manifest／diagnostics。
  - Neo4j 專案 graph、community 與 summary。
  - 暫存附件。
- 永遠不得刪除：
  - 原始碼目錄。
  - Git／SVN working copy。
  - 外部 SQL Server／SQLite。
  - 其他 Agent 的 Marketplace 部署。

## 8. 中文註解與維護性規則

- 新增或實質改寫的 public class、public method、重要 private workflow 必須有完整繁體中文註解。
- 註解解釋責任、資料邊界、安全限制與失敗行為，不重述語法。
- 不為每個 class 建立一個 interface。
- Interface 只保留在 LLM、Graph store、Repository、Database source、Secret、Speech、外部 Agent target 等真正邊界。
- Request／Response record 優先與單一 use case 放在同一檔案。
- 先刪除功能再決定是否拆檔，禁止以新增大量小檔案掩蓋大型類別問題。
- 不保留被 feature flag 永久關閉的 dead code。

## 9. 驗收

### 9.1 編譯與自動測試

- `.NET` build 無 warning-as-error 回歸。
- 全部有效單元測試通過。
- Desktop TypeScript typecheck 通過。
- Desktop production build 通過。
- Rust/Tauri build 通過。
- 移除功能的測試與 fixture 一併刪除，不留下只測死碼的測試。

### 9.2 一般對話

- 可建立、切換、重新命名及刪除多個對話。
- 可切換 Provider／Model。
- Attachment 與 Speech 正常。
- 不出現專案、Agent Mode、Approval、Changeset 或 Workspace UI。
- 不會呼叫 GraphRAG 或寫入本機專案。

### 9.3 專案解析

- 每個專案可建立多個對話。
- 回答包含檔案、Symbol、Route、SQL、Table／Procedure 等 citation。
- 問投資系統的庫存資料流能取得入口到資料庫的完整證據路徑。
- Bug／新需求只提出修改建議，不修改檔案。
- 結構索引完成即可問答。
- AI 摘要背景狀態顯示正確。
- 查看知識圖譜仍可使用唯讀 Cypher、圖形、表格與 raw view。

### 9.4 資料庫

- SQL Server SQL Auth 成功。
- SQL Server Integrated Security 成功。
- SQLite ReadOnly 成功。
- 密碼不出現在 API response、log、Graph 或 manifest。
- 連線失敗保留上一版成功 graph。
- 未設定資料庫可完成 source-only 索引。

### 9.5 Marketplace

- 十二個 target 都有明確 descriptor 與相容性測試。
- Skill／MCP 可預覽、部署、重新部署及移除。
- 只對已確認公開設定格式的 target／scope 開放 MCP；其餘 target 明確顯示不相容，不偽造成功。
- 部署只使用實體複製。
- 不會刪除非 Modern Wingman 管理的檔案。
- Rust Legacy Marketplace 不再編譯、啟動或建立資料表。

### 9.6 真實系統與 Neo4j 清理

- 使用真實投資系統原始碼與 SQL Server 完成端到端索引。
- 驗證 `tblMenuMap → Controller → Service／Business Logic → SQL／Procedure → Table`。
- 驗證中文「庫存」問題可對應 Inventory／Holdings／Position 等程式碼。
- 真實 Neo4j 黃金問題必須覆蓋 Menu、Route、前端、Controller、Data 與 READS／WRITES，
  且「關於庫存，給我解釋整個資料流是怎麼運行的？」的 coverage 必須為 100%。
- 每次測試檢索後刪除該次測試建立的 Neo4j 專案資料。
- 最終交付前確認沒有殘留測試 manifest、node、edge 或 community。
- 不刪除使用者正式專案的成功索引，除非該索引明確屬於本次測試。
