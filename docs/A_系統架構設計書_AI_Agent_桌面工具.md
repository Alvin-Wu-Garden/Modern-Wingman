
# AI Agent 桌面工具系統架構設計書（A版）

> 目標：打造一款類似 Claude Code / OpenAI Codex 的桌面型 AI Agent 工具。  
> 技術主軸：**React (TypeScript) + Tauri 2 + Microsoft Agent Framework 1.9.0 (C# / .NET 10)**  
> 通訊架構：**React UI ↔ Tauri IPC ↔ Rust（gRPC Client）↔ Agent Service（gRPC Server）**  
> 通訊優先序：gRPC → WebSocket → SSE → HTTP（依可用性 fallback）  
> 前端框架：**Shadcn/ui + Tailwind CSS + Zustand**  
> 模型供應商：**OpenAI / Anthropic / Azure OpenAI / Custom Endpoint（BYOK）**  
> Node.js 版本：**24.13.0**  
> 文件用途：作為產品/架構/開發團隊的**系統設計基準文件**。

---

## 1. 文件資訊

- **文件名稱**：AI Agent 桌面工具系統架構設計書
- **版本**：v1.0
- **文件類型**：Architecture Design Specification
- **適用階段**：MVP → V2 演進
- **主要讀者**：
  - 軟體架構師
  - 前端工程師
  - .NET / Agent Runtime 工程師
  - DevOps / Infra 工程師
  - 產品經理 / 技術主管

---

## 2. 專案目標

本專案要建立一套桌面型 AI Agent 工具，具備以下能力：

1. **對話式操作與任務執行**
   - 使用自然語言描述需求
   - Agent 依需求自動規劃並執行步驟

2. **本機開發工作區操作**
   - 讀取與修改專案檔案
   - 搜尋程式碼與關鍵字
   - 顯示 diff / patch
   - 呼叫 shell 指令
   - 協助 Git 工作流

3. **可控的高權限操作**
   - 對 shell、檔案寫入、網路、MCP 工具進行權限管控
   - 支援 approval / consent 機制

4. **多工具與多代理協作**
   - 支援多種工具（file、shell、git、web、MCP）
   - 後續支援 subagents / background agents

5. **可觀測與可維運**
   - 提供完整事件流、trace、run log、approval history
   - 支援日後進行問題排查與行為審計

---

## 3. 設計原則

### 3.1 核心原則

1. **Least Privilege（最小權限）**  
   前端 UI 不直接持有高權限能力；高權限能力集中在受控後端執行。

2. **Separation of Concerns（職責分離）**  
   UI、桌面橋接、Agent Runtime、Tool Runtime、Policy Engine、Persistence 各自分層。

3. **Provider Agnostic（模型供應商中立）**  
   不將系統設計綁死在單一模型供應商，保留 OpenAI / Anthropic / Azure OpenAI / Foundry 等擴充能力。

4. **Auditability（可審計）**  
   所有高風險操作必須可追溯，至少留下 request、decision、executor、result。

5. **Extensibility（可擴充）**  
   後續可加入更多 MCP Server、更多工具、更多 Agent Workflow，而不需大幅重構。

6. **Human-in-the-loop（人在迴圈中）**  
   對高風險工具與破壞性操作，預設需要使用者批准。

---

## 4. 系統範圍

### 4.1 In Scope（納入範圍）

- 桌面應用程式（**Windows Only，MVP 範疇**）
- React UI + Tauri Desktop Shell
- 本機 Agent Service（C# / .NET）
- Session / Run / Event Stream
- Tool Runtime（File / Shell / Git / MCP）
- Approval / Policy 機制
- 本機持久化（SQLite + File Artifacts）
- Telemetry / Logging / Tracing

### 4.2 Out of Scope（第一版不納入）

- 多人即時協作
- 雲端託管版 SaaS 控制台
- 完整 Marketplace / Plugin Store
- 行動裝置版本
- 超大型企業 IAM / SSO 深度整合（可在後期擴充）

---

## 5. 邏輯架構總覽

```text
┌─────────────────────────────────────────────────────────────────┐
│                      Desktop App (Tauri)                       │
│                                                                 │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │ React + TypeScript UI                                    │  │
│  │ - Chat / Command input                                   │  │
│  │ - Task tree / Plan panel                                 │  │
│  │ - Diff viewer / File explorer                            │  │
│  │ - Approval modal                                         │  │
│  │ - Session list / Workspace selector                      │  │
│  │ - Tool event timeline / Logs / Trace                     │  │
│  │ - MCP server manager / Settings                          │  │
│  └───────────────────────────────────────────────────────────┘  │
│                    │                                            │
│                    │ Tauri IPC (low-privilege only)             │
│                    ▼                                            │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │ Tauri Core (Rust)                                        │  │
│  │ - Window / Tray / Notifications                          │  │
│  │ - Secure OS integration                                  │  │
│  │ - Credential bridge / Keychain wrapper                   │  │
│  │ - Launch & supervise local Agent Service                 │  │
│  │ - Policy gate for privileged operations                  │  │
│  └───────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
                     │
                     │ gRPC（主力）/ WebSocket / SSE / HTTP（依優先序 fallback）
                     ▼
┌─────────────────────────────────────────────────────────────────┐
│                Local Agent Service (C# / .NET / MAF)           │
│                                                                 │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │ Application Layer                                        │  │
│  │ - Session orchestration                                  │  │
│  │ - Approval workflows                                     │  │
│  │ - Task / Todo / Plan-Execute mode                        │  │
│  │ - Event streaming                                        │  │
│  └───────────────────────────────────────────────────────────┘  │
│                                                                 │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │ Agent Layer (MAF)                                        │  │
│  │ - Harness Agent                                          │  │
│  │ - Subagents / Background agents                          │  │
│  │ - Workflows / handoff / concurrent tasks                 │  │
│  │ - Context compaction / memory                            │  │
│  │ - MCP client / MCP server                                │  │
│  └───────────────────────────────────────────────────────────┘  │
│                                                                 │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │ Tool Layer                                               │  │
│  │ - File tools                                             │  │
│  │ - Shell tools                                            │  │
│  │ - Git tools                                              │  │
│  │ - Search / Fetch / Web tools                             │  │
│  │ - Enterprise tools                                       │  │
│  └───────────────────────────────────────────────────────────┘  │
│                                                                 │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │ Infra Layer                                              │  │
│  │ - SQLite / File Artifacts                                │  │
│  │ - Repo index / vector store（後期）                      │  │
│  │ - OpenTelemetry / logs / metrics / traces                │  │
│  │ - Policy engine / allowlist / secrets                    │  │
│  └───────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

---

## 6. 模組責任劃分

## 6.1 UI 層（React + TypeScript）

### 主要責任
- Chat 對話介面
- Session / Workspace 管理
- Tool Event Timeline 顯示
- Diff Viewer / File Preview
- Approval Modal
- MCP Server 管理
- 設定頁（模型、權限、工作區、API Key 等）

### 技術組成
- React + TypeScript + Vite
- Tauri 2（桌面殼）
- Shadcn/ui + Tailwind CSS（UI 元件）
- Zustand（全域狀態管理）
- Tauri IPC（`invoke` / `listen`）作為**唯一對外通訊方式**，不直接呼叫 Agent Service HTTP/gRPC

### 不應承擔的責任
- 直接執行 shell
- 直接存取敏感檔案
- 直接保存 secrets
- 直接呼叫高風險 MCP 工具

### 建議頁面模組
- `ChatPage`
- `WorkspacePage`
- `SessionSidebar`
- `RunTimelinePanel`
- `ApprovalDialog`
- `DiffPreviewPanel`
- `McpServerSettingsPage`
- `ModelProviderSettingsPage`

---

## 6.2 Tauri Core（Rust）

### 主要責任
- 作為 WebView 與本機系統之間的安全橋接層
- 啟動與監控 Agent Service 生命週期
- 處理桌面級能力：
  - 視窗管理
  - 系統通知
  - 系統匣
  - 原生檔案選擇器
  - 安全地存取 OS credential store（若需要）
- 限制前端可使用的權限範圍

### 建議原則
- 只提供必要的 Tauri command
- 使用 capability / scope 嚴格定義 command 暴露範圍
- 不將 shell / arbitrary file access 直接暴露給前端

---

## 6.3 Agent Service（C# / .NET 10 / MAF 1.9.0）

### 主要責任
- 管理 Session 與 Run 生命週期
- 執行 Agent loop（每個 Run 對應一個 **MAF Workflow 實例**）
- 決策何時使用哪些工具
- 管理 context / memory / todo
- 協調 workflow / background jobs / subagents
- 執行 approval policy
- 透過 **gRPC streaming** 輸出 event stream 給 Rust 層

### 技術組成
- .NET 10
- ASP.NET Core Minimal API
- Microsoft Agent Framework 1.9.0（`Microsoft.Agents.AI` 系列套件）
- Grpc.AspNetCore（gRPC Server，`.proto` 由 C# 專案自行維護）
- MAF Workflows（**Phase 1 必要組成**，Run = Workflow instance）
- MAF 多 Provider 支援（OpenAI / Anthropic / Azure OpenAI / Custom BYOK）
- OpenTelemetry
- SQLite

### 子模組

#### 1. Session Manager
- 建立 session
- 載入歷史對話
- 附加工作區 metadata
- 存取記憶資料

#### 2. Run Orchestrator
- 接收使用者輸入
- 建立 run 上下文
- 每個 Run 對應一個 **MAF Workflow 實例**，由 Workflow 驅動 Agent loop
- 控制工具呼叫與中斷恢復

#### 3. Approval Manager
- 對高風險操作產生 approval request
- 等待前端回傳 approval decision
- 寫入 audit log

#### 4. Tool Runtime
- 執行 file / shell / git / MCP 工具
- 套用白名單 / 黑名單規則
- 收集輸出並發送事件

#### 5. Event Stream Publisher
- 持續將 token / tool / diff / approval / run status 透過 **gRPC server-side streaming** 回傳
- Rust 層接收 gRPC stream 後，透過 `app_handle.emit()` 轉發 Tauri 事件至前端

#### 6. Memory / Artifact Storage
- 保存 todo
- 保存 summary / compacted context
- 保存產出檔案與 patch

---

## 6.4 Tool Layer

### 工具類型

#### File Tools
- `read_file`
- `write_file`
- `edit_file`
- `list_directory`
- `search_files`
- `show_diff`

#### Shell Tools
- `run_command`
- `run_test`
- `build_project`
- `lint_project`

#### Git Tools
- `git_status`
- `git_diff`
- `git_branch_list`
- `git_commit_draft`

#### MCP Tools
- 從第三方 MCP Server 動態載入
- 需標註 capability 與風險等級

#### Web / Retrieval Tools（可選）
- `web_search`
- `fetch_url`
- `search_docs`

### 工具設計原則
- 所有工具要有統一輸入/輸出格式
- 所有工具都必須可被 policy engine 攔截
- 所有工具呼叫都要留下可觀測紀錄

---

## 7. 資料流與事件流設計

## 7.1 主要資料流

### 使用者發起一次任務
1. 使用者在 UI 輸入指令
2. 前端透過 **Tauri IPC `invoke`** → Rust gRPC Client → gRPC `CreateRun` → Agent Service
3. Agent Service 建立 run context，初始化 **MAF Workflow 實例**
4. MAF Workflow 驅動 Agent 開始規劃與執行
5. Agent 若需工具，交由 Tool Runtime 執行
6. 若工具需批准，先發送 approval request（gRPC stream event → Rust → Tauri emit → 前端）
7. 使用者批准後，前端透過 **Tauri IPC `invoke`** 回傳決策，Rust 轉 gRPC → Agent Service 繼續執行
8. 結果、diff、日誌、token 持續透過 **gRPC server-side streaming → Rust `app_handle.emit()` → 前端 `listen()`**
9. 最終結果落地到 session 與 artifacts

---

## 7.2 事件模型（Event Types）

### Run Events
- `run.created`
- `run.started`
- `run.completed`
- `run.failed`
- `run.cancelled`

### Message Events
- `message.delta`
- `message.completed`

### Tool Events
- `tool.started`
- `tool.stdout`
- `tool.stderr`
- `tool.completed`
- `tool.failed`

### Approval Events
- `approval.requested`
- `approval.resolved`
- `approval.timed_out`

### Diff / Artifact Events
- `diff.available`
- `artifact.created`

### Task / Todo Events
- `todo.updated`
- `plan.updated`

### Trace Events
- `trace.linked`
- `telemetry.warning`

---

## 8. API 契約草案

## 8.1 建立 Run

> **架構說明**：前端不直接呼叫 Agent Service HTTP/gRPC。所有操作均透過 Tauri IPC（`invoke` / `listen`）與 Rust 層溝通，由 Rust gRPC Client 統一對接 Agent Service。以下 API 規格為 **Agent Service gRPC 服務定義**，以 REST 格式呈現供閱讀。

### `POST /runs`（對應 gRPC `RunService.CreateRun`）

#### Request 範例
```json
{
  "workspaceId": "ws_001",
  "sessionId": "sess_001",
  "userMessage": "請幫我分析這個 repo 的錯誤並修正測試",
  "mode": "auto",
  "selectedTools": ["file", "shell", "git", "mcp"],
  "selectedMcpServers": ["github", "filesystem"],
  "modelProfile": "anthropic-coding",
  "approvalMode": "default"
}
```

#### Response 範例
```json
{
  "runId": "run_001",
  "streamUrl": "/runs/run_001/stream",
  "status": "created"
}
```

---

## 8.2 串流事件

### `GET /runs/{runId}/stream`

使用 **SSE** 回傳事件流。

#### 事件範例
```text
event: tool.started
data: {"runId":"run_001","tool":"grep","message":"正在搜尋測試失敗原因"}
```

```text
event: approval.requested
data: {
  "approvalId":"apv_001",
  "type":"shell_command",
  "riskLevel":"medium",
  "command":"dotnet test",
  "reason":"需要執行測試以驗證修改"
}
```

---

## 8.3 Approval 決策

### `POST /approvals/{approvalId}/decision`

#### Request 範例
```json
{
  "decision": "approve_once",
  "comment": "允許執行這次測試命令"
}
```

#### 合法 decision
- `approve_once`
- `approve_always`
- `reject`
- `edit_then_approve`

---

## 8.4 Session API

### `GET /sessions`
### `POST /sessions`
### `GET /sessions/{sessionId}`
### `GET /sessions/{sessionId}/history`

---

## 8.5 Workspace API

### `GET /workspaces`
### `POST /workspaces`
### `GET /workspaces/{workspaceId}`
### `POST /workspaces/{workspaceId}/trust`

---

## 8.6 MCP API

### `GET /mcp/servers`
### `POST /mcp/servers/test-connection`
### `POST /mcp/servers/register`

---

## 9. Approval Policy 規格

## 9.1 風險分級

### Level 0（自動允許）
- 讀取工作區內檔案
- grep / glob / list directory
- 查詢 session memory
- Git read-only 查詢

### Level 1（首次詢問，可記住）
- 工作區內寫檔
- 執行白名單測試指令
- 呼叫只讀型 MCP tools

### Level 2（每次都問）
- 工作區外寫檔
- 需要網路的 shell / fetch
- 會改動外部系統狀態的 MCP tools

### Level 3（預設拒絕）
- 危險刪除指令
- 修改系統設定
- 匯出 secrets / tokens
- 執行未知外部 binary

---

## 9.2 Approval Request 結構

```json
{
  "approvalId": "apv_001",
  "runId": "run_001",
  "category": "shell_command",
  "riskLevel": "level_2",
  "summary": "Agent 需要執行 dotnet restore 並存取網路",
  "details": {
    "command": "dotnet restore",
    "requiresNetwork": true,
    "workingDirectory": "D:/repo/sample-app"
  },
  "choices": [
    "approve_once",
    "approve_always_for_workspace",
    "reject"
  ]
}
```

---

## 10. 安全模型

## 10.1 邊界定義

### Boundary A：Web UI
- 視為低信任層
- 不能直接觸碰 shell/file system/secrets

### Boundary B：Tauri Core
- 提供受控命令
- 負責本機能力橋接
- 僅暴露白名單能力

### Boundary C：Agent Service
- 所有高權限操作的實際執行者
- 需受 policy engine 管理

---

## 10.2 Secrets 管理

### 原則
- API key 不儲存在前端 localStorage
- 優先使用 OS credential store 或受保護的本機存放機制
- Agent Runtime 讀取 secrets 時要可審計

### 建議
- Windows Credential Manager / Keychain / Secret Service
- 開發模式下可用 `.env.local`，但正式版不建議

---

## 10.3 Workspace Trust Model

工作區需要明確信任狀態：

- `trusted`
- `untrusted`
- `read_only`

### 規則建議
- `untrusted`：不可直接寫檔 / 不可執行 shell
- `read_only`：只允許查詢與分析
- `trusted`：可依 approval policy 執行高權限操作

---

## 11. 持久化設計

## 11.1 SQLite 資料表建議

### `sessions`
- `id`
- `workspace_id`
- `title`
- `created_at`
- `updated_at`
- `last_model_profile`

### `runs`
- `id`
- `session_id`
- `status`
- `mode`
- `started_at`
- `ended_at`

### `messages`
- `id`
- `session_id`
- `role`
- `content`
- `created_at`

### `tool_calls`
- `id`
- `run_id`
- `tool_name`
- `status`
- `input_json`
- `output_json`
- `started_at`
- `ended_at`

### `approvals`
- `id`
- `run_id`
- `category`
- `risk_level`
- `decision`
- `created_at`
- `resolved_at`

### `artifacts`
- `id`
- `run_id`
- `type`
- `path`
- `metadata_json`

### `workspace_settings`
- `workspace_id`
- `trust_level`
- `policy_json`
- `preferred_model`

---

## 11.2 Artifact 儲存建議

本機檔案系統儲存：

```text
.artifacts/
├─ sessions/
├─ runs/
├─ diffs/
├─ patches/
├─ exports/
└─ memory/
```

---

## 12. 可觀測性設計

## 12.1 必要觀測資料

- 每次 Run 的起訖時間
- 使用的模型與 provider
- token 使用量
- 每個 tool call 的耗時與結果
- approval request / decision 紀錄
- 錯誤堆疊與 exception metadata
- diff / patch 產物路徑

## 12.2 Trace 關聯

建議每個 run 建立統一的：
- `traceId`
- `runId`
- `sessionId`
- `workspaceId`

便於跨層追蹤：
- UI 顯示
- Agent Service 追蹤
- Tool Runtime 日誌
- External Provider 請求

---

## 13. 非功能需求（NFR）

## 13.1 安全性
- 高風險操作需有 approval
- Secrets 不明文存於前端
- 可設定工作區信任等級

## 13.2 效能
- UI 對串流事件的延遲需低於 300ms 感知級
- 一般工具呼叫事件需即時呈現
- 大檔案操作需可分段處理

## 13.3 可維護性
- 前後端契約明確
- 模組化工具註冊
- 支援新增 provider / MCP server / workflows

## 13.4 可測試性
- Tool Runtime 可 mock
- Approval policy 可單元測試
- Event schema 可 contract test

---

## 14. 里程碑規劃

## Milestone 1：基礎 MVP
- 基本 chat UI（Shadcn/ui + Tailwind CSS）
- Local Agent Service 啟動（gRPC server）
- **MAF Workflow 骨架**（每個 Run = 一個 Workflow 實例）
- **MAF Provider 初始化**（OpenAI / Anthropic 可切換）
- gRPC streaming → Rust `app_handle.emit()` → 前端 `listen()` 事件鏈路
- file read/write
- shell execution（受控）
- approval modal
- session persistence

## Milestone 2：Coding Agent 可用版
- todo / plan panel
- diff preview
- git 工具
- MCP client
- workspace trust model
- 基本 telemetry

## Milestone 3：進階 Agent 能力
- subagents
- background jobs
- workflow checkpoint / resume
- repo indexing
- policy rule presets

## Milestone 4：企業強化
- 多 provider 管理
- hosted / remote agent mode
- team sync
- enterprise audit export

---

## 15. 風險與對策

## 15.1 三語言棧複雜度
### 風險
- React + Rust + C# 增加開發與部署複雜度

### 對策
- 嚴格定義邊界
- React ↔ Tauri ↔ Agent Service 使用固定契約
- 優先讓 Rust 專注在桌面橋接，不承擔業務複雜度

## 15.2 權限模型設計不完整
### 風險
- 可能造成高風險操作誤執行

### 對策
- 一開始就定義 approval matrix
- 高風險工具預設 deny
- 所有例外決策要有審計

## 15.3 Agent 行為不可觀測
### 風險
- 難以 debug 與追責

### 對策
- 第一版就實作事件流與 traces
- 對 tool call / approval / run 都做記錄

---

## 16. 開發順序建議

### Phase 1
1. `POST /runs`
2. `GET /runs/{id}/stream`
3. 基本 chat UI
4. Session persistence

### Phase 2
1. File tools
2. Approval modal
3. Shell execution
4. Tool timeline

### Phase 3
1. Diff viewer
2. Git tools
3. Workspace trust model
4. MCP client

### Phase 4
1. Todo / Plan panel
2. Memory compaction
3. Background jobs
4. Observability dashboard

---

## 17. 驗收標準（Definition of Done）

### MVP 驗收標準
- 使用者可在桌面 app 中建立 session
- 可對某個本機 repo 下達分析 / 修改需求
- Agent 可讀取檔案、執行白名單 shell、提出 diff
- 高風險動作必須跳出 approval
- UI 可即時看見 run timeline / logs / diff
- session、run、approval、artifacts 可被保存

---

## 18. 後續建議文件

接下來建議補完以下文件：

1. **Monorepo 初始骨架設計（B版文件）**
2. **API 契約明細文件**
3. **Event Schema 文件**
4. **Approval Policy 詳規**
5. **MCP Server / Client 接入指南**
6. **Tauri Capabilities 權限矩陣文件**
7. **Observability 與日誌規格文件**

---

## 19. 結語

這份架構設計書的核心目標不是只做出「能聊天的桌面 App」，而是建立一套：

- **可操作本機開發工作區**
- **可控風險與權限**
- **可擴充工具與 MCP 生態**
- **可觀測、可維護、可演進**

的 AI Agent 平台基礎。

若本文件作為開發基線，建議下一步直接配套輸出：
- **Monorepo 初始骨架設計（B）**
- **API 契約草案**
- **事件模型 Schema**

以便工程團隊立即進入實作。
