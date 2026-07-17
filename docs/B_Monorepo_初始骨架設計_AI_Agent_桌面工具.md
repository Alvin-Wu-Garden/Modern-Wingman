
# AI Agent 桌面工具 Monorepo 初始骨架設計（B版）

> 目標：提供一份可以直接開工的 **Monorepo 初始骨架設計文件**，用於實作一款類似 Claude Code / OpenAI Codex 的桌面型 AI Agent 工具。  
> 技術主軸：**React (TypeScript) + Tauri 2 + Microsoft Agent Framework 1.9.0 (C# / .NET 10)**  
> 通訊架構：**React UI ↔ Tauri IPC ↔ Rust（gRPC Client）↔ Agent Service（gRPC Server）**  
> 通訊優先序：gRPC → WebSocket → SSE → HTTP（依可用性 fallback）  
> 前端框架：**Shadcn/ui + Tailwind CSS + Zustand**  
> 模型供應商：**OpenAI / Anthropic / Azure OpenAI / Custom Endpoint（BYOK）**  
> Node.js 版本：**24.13.0**  
> 文件用途：作為開發團隊建立 repo、初始化專案、分工協作與定義契約的起始藍圖。

---

## 1. 文件資訊

- **文件名稱**：AI Agent 桌面工具 Monorepo 初始骨架設計
- **版本**：v1.0
- **文件類型**：Implementation Skeleton Specification
- **適用階段**：專案初始化 / MVP 開工前
- **主要讀者**：
  - 軟體架構師
  - 前端工程師
  - .NET 工程師
  - DevOps / Build 工程師
  - 技術主管

---

## 2. 文件目標

本文件的目的是定義：

1. **Monorepo 目錄結構**
2. **各專案與套件的責任邊界**
3. **跨層契約（Contracts）與事件設計位置**
4. **初始化順序與開發流程**
5. **建議命名規範與工程規範**
6. **第一版 MVP 交付所需最小骨架**

這份文件重點不是「架構理念」，而是「**工程團隊如何真的把 repo 建起來並開始寫程式**」。

---

## 3. Monorepo 採用原因

相較於一開始就拆成多個 repo，Monorepo 對此專案更適合，原因如下：

1. **前後端契約會頻繁變動**  
   AI Agent 產品在前期會快速調整事件格式、Approval payload、Run DTO、Session schema。放在同一個 repo 便於同步更新。

2. **桌面殼、前端、Agent Service 的耦合度高**  
   Tauri Desktop、React UI、C# Agent Service 彼此之間會共享大量命名、流程與版本。

3. **共享資產明顯存在**  
   包含 DTO、Event schema、Prompt 模板、政策設定、測試 fixtures、MCP presets。

4. **適合 MVP 快速迭代**  
   先做單一版本控制、單一 release cadence，比一開始做多 repo 更務實。

---

## 4. 頂層目錄結構

```text
modern-wingman/
├─ apps/
│  ├─ desktop/                       # React + Tauri 桌面應用
│  ├─ agent-service/                 # C# / .NET / MAF 本機 Agent Runtime
│  └─ docs-site/                     # 選配：內部文件站 / Storybook / API docs
│
├─ packages/
│  ├─ contracts/                     # 共用 DTO / Event schema / 型別契約
│  ├─ ui-kit/                        # 共用 React UI 元件
│  ├─ prompt-assets/                 # 系統提示詞 / 模板 / policy prompt
│  ├─ skills/                        # 內建 task playbooks / skills 定義
│  ├─ mcp-presets/                   # MCP server 預設設定 / manifest
│  └─ config/                        # 共用 lint / tsconfig / prettier 設定
│
├─ infra/
│  ├─ dev/                           # 本地開發環境腳本
│  ├─ ci/                            # CI/CD pipeline 設定
│  ├─ packaging/                     # 安裝包 / 簽章 / 自動更新設定
│  └─ observability/                 # OTel / dashboards / exporter config
│
├─ schemas/
│  ├─ api/                           # OpenAPI / JSON Schema / protobuf
│  ├─ policy/                        # approval policy schema
│  ├─ events/                        # event schema 定義
│  └─ tauri/                         # capabilities / command manifest schema
│
├─ examples/
│  ├─ demo-workspace/                # 測試 repo
│  ├─ sample-prompts/
│  └─ sample-mcp-servers/
│
├─ scripts/
│  ├─ bootstrap/
│  ├─ lint/
│  ├─ release/
│  ├─ smoke-tests/
│  └─ repo-tools/
│
├─ .editorconfig
├─ .gitignore
├─ README.md
├─ pnpm-workspace.yaml
└─ global.json                       # .NET SDK pinning
```

---

## 5. 專案與套件責任說明

## 5.1 `apps/desktop`

### 目標
承載桌面端產品體驗，包含：
- 聊天介面
- Session 管理
- Diff 顯示
- Approval 操作
- Tool timeline
- 設定頁
- MCP 管理頁

### 技術組成
- React + TypeScript + Vite
- Tauri 2
- Shadcn/ui + Tailwind CSS（UI 元件庫）
- Zustand（全域狀態管理）
- Node.js 24.13.0

### 不負責的事情
- 不直接執行 shell
- 不直接操作高風險 filesystem
- 不直接保存敏感憑證
- 不負責 Agent 核心工作流邏輯

---

## 5.2 `apps/agent-service`

### 目標
作為本機 Agent Runtime，負責：
- 接收 UI 發來的任務
- 執行 Agent loop
- 協調工具
- 管理 session / runs / memory
- 執行 approval policy
- 輸出事件流

### 技術組成
- .NET 10
- ASP.NET Core Minimal API
- Microsoft Agent Framework 1.9.0（`Microsoft.Agents.AI` 系列套件）
- Grpc.AspNetCore（gRPC Server）
- OpenTelemetry
- SQLite

### 不負責的事情
- 不處理前端 UI 狀態
- 不直接承擔桌面視窗與系統匣管理

---

## 5.3 `packages/contracts`

### 目標
集中管理前後端共用契約：
- Request / Response DTO
- SSE / WS Event schema
- Approval payload
- Diff artifact metadata
- 型別與驗證規則

### 設計原則
- 所有前後端共用的結構都要先進入 contracts
- 前端使用的型別為 **Tauri IPC payload types**（非 HTTP DTO），前端不直接呼叫 Agent Service
- C# Agent Service 的 gRPC DTO 由 protobuf 生成，在 C# 端自行維護，需與本套件語義對齊
- 前端與 C# 端之間以本套件的 TypeScript 定義為溝通語言
- 契約版本變動需要記錄 changelog

---

## 5.4 `packages/ui-kit`

### 目標
收納可重複使用的 UI 元件：
- Chat message components
- Timeline items
- Form controls
- Split panels
- Dialogs / Drawers / Tabs
- Diff panel container

### 原則
- 僅放通用元件
- 與業務流程緊密耦合的元件放回 `apps/desktop/src/features/*`

---

## 5.5 `packages/prompt-assets`

### 內容建議
- system prompts
- safety instructions
- plan mode prompt
- execute mode prompt
- git commit drafting prompt
- code review prompt
- repo summary prompt

### 原則
- Prompt 當作版本化資產管理
- 不把 prompt 文案硬寫在程式邏輯中

---

## 5.6 `packages/skills`

### 內容建議
- 任務流程定義（例如：修測試、產生變更摘要、建立 commit message）
- 常用任務策略與模板
- Workflow presets

### 目的
未來可把 skills 視為半結構化功能模組，而非散落於程式碼中的 prompt + tool steps。

---

## 5.7 `packages/mcp-presets`

### 內容建議
- 預設 MCP server 連線範本
- server manifest
- local / remote MCP profiles
- capability annotations

### 目的
降低使用者手動配置 MCP server 的門檻。

> **MVP 注意**：MVP 階段本套件僅建立目錄佔位，不實作實際功能。MCP 完整支援排入 Phase 5。

---

## 6. `apps/desktop` 詳細骨架

```text
apps/desktop/
├─ src/
│  ├─ app/
│  │  ├─ router/
│  │  ├─ store/
│  │  ├─ providers/
│  │  ├─ bootstrap/
│  │  └─ layout/
│  │
│  ├─ features/
│  │  ├─ chat/
│  │  │  ├─ components/
│  │  │  ├─ hooks/
│  │  │  ├─ store/
│  │  │  └─ api/
│  │  │
│  │  ├─ sessions/
│  │  ├─ workspaces/
│  │  ├─ approvals/
│  │  ├─ diff-viewer/
│  │  ├─ timeline/
│  │  ├─ logs/
│  │  ├─ mcp-manager/
│  │  ├─ settings/
│  │  └─ onboarding/
│  │
│  ├─ components/
│  │  ├─ common/
│  │  ├─ layout/
│  │  ├─ forms/
│  │  └─ feedback/
│  │
│  ├─ services/
│  │  ├─ agent-api/
│  │  ├─ stream-client/
│  │  ├─ tauri-bridge/
│  │  └─ telemetry/
│  │
│  ├─ hooks/
│  ├─ lib/
│  ├─ types/
│  ├─ styles/
│  └─ main.tsx
│
├─ src-tauri/
│  ├─ src/
│  │  ├─ commands/
│  │  ├─ supervisor/
│  │  ├─ security/
│  │  ├─ keychain/
│  │  ├─ process/
│  │  └─ main.rs
│  │
│  ├─ capabilities/
│  │  ├─ main-window.json
│  │  ├─ privileged-window.json
│  │  ├─ settings-window.json
│  │  └─ updater.json
│  │
│  ├─ icons/
│  ├─ tauri.conf.json
│  └─ Cargo.toml
│
├─ package.json
├─ tsconfig.json
└─ vite.config.ts
```

### 設計重點
- `features/` 使用業務功能切分，而不是只用 pages/components 切。
- `services/tauri-bridge` 是**主要通訊層**，所有對 Agent Service 的操作均透過 Tauri `invoke()` 發起，不直接呼叫 HTTP/gRPC。
- `services/agent-api` 封裝 Tauri `invoke()` 呼叫（對應 Agent Service gRPC 方法），提供 TypeScript 友善介面。
- `services/stream-client` 封裝 Tauri `listen()` 事件訂閱，接收 Rust 轉發的 gRPC streaming 事件。
- `src-tauri/capabilities` 必須獨立管理，避免所有視窗共用過大權限。

---

## 7. `apps/agent-service` 詳細骨架

```text
apps/agent-service/
├─ src/
│  ├─ Host/
│  │  ├─ Program.cs
│  │  ├─ Api/
│  │  │  ├─ RunsEndpoints.cs
│  │  │  ├─ SessionsEndpoints.cs
│  │  │  ├─ WorkspacesEndpoints.cs
│  │  │  ├─ ApprovalsEndpoints.cs
│  │  │  └─ McpEndpoints.cs
│  │  ├─ Streaming/
│  │  ├─ Middleware/
│  │  └─ DependencyInjection/
│  │
│  ├─ Application/
│  │  ├─ Runs/
│  │  ├─ Sessions/
│  │  ├─ Workspaces/
│  │  ├─ Approvals/
│  │  ├─ Artifacts/
│  │  ├─ Policies/
│  │  └─ Telemetry/
│  │
│  ├─ Domain/
│  │  ├─ Agents/
│  │  ├─ Tools/
│  │  ├─ Events/
│  │  ├─ Sessions/
│  │  ├─ Workspaces/
│  │  └─ ValueObjects/
│  │
│  ├─ Infrastructure/
│  │  ├─ AgentFramework/
│  │  │  ├─ Harness/
│  │  │  ├─ Workflows/
│  │  │  ├─ Mcp/
│  │  │  ├─ Memory/
│  │  │  └─ Providers/
│  │  │
│  │  ├─ ToolRuntime/
│  │  │  ├─ FileTools/
│  │  │  ├─ ShellTools/
│  │  │  ├─ GitTools/
│  │  │  ├─ WebTools/
│  │  │  └─ McpTools/
│  │  │
│  │  ├─ Persistence/
│  │  │  ├─ Sqlite/
│  │  │  ├─ FileStore/
│  │  │  └─ Migrations/
│  │  │
│  │  ├─ Security/
│  │  │  ├─ Policies/
│  │  │  ├─ Allowlist/
│  │  │  ├─ Secrets/
│  │  │  └─ WorkspaceTrust/
│  │  │
│  │  └─ Observability/
│  │     ├─ Logging/
│  │     ├─ Metrics/
│  │     └─ Tracing/
│  │
│  └─ Contracts/
│     ├─ Requests/
│     ├─ Responses/
│     ├─ Events/
│     └─ Dtos/
│
├─ tests/
│  ├─ UnitTests/
│  ├─ IntegrationTests/
│  ├─ ContractTests/
│  └─ E2ETests/
│
├─ appsettings.json
├─ appsettings.Development.json
└─ AgentService.sln
```

### 設計重點
- `Host/Api` 改為 **gRPC Service 實作層**（`Grpc.AspNetCore`），同時保留 HTTP health/management endpoints。
- `Host/Api` 內的 `.proto` 定義與 protobuf 生成程式碼由 C# 專案自行維護（不放共用 `schemas/`）。
- `Application/` 放 use cases（每個 Run 對應一個 **MAF Workflow 實例**）。
- `Domain/` 放核心模型，避免直接耦合 MAF 或資料庫。
- `Infrastructure/AgentFramework` 作為 MAF 1.9.0 adapter 層，**`Workflows/` 是 Phase 1 必要組成**。
- `Infrastructure/AgentFramework/Providers` 管理多 provider 初始化（OpenAI / Anthropic / Azure OpenAI / Custom BYOK）。
- `Infrastructure/ToolRuntime` 封裝所有本機工具實作。
- `Contracts/` 可以和 `packages/contracts` 對齊命名，但 C# 端需有本地 DTO（由 protobuf 生成）。

---

## 8. `packages/contracts` 詳細骨架

```text
packages/contracts/
├─ src/
│  ├─ api/
│  │  ├─ runs.ts
│  │  ├─ sessions.ts
│  │  ├─ workspaces.ts
│  │  ├─ approvals.ts
│  │  └─ mcp.ts
│  │
│  ├─ events/
│  │  ├─ run-events.ts
│  │  ├─ message-events.ts
│  │  ├─ tool-events.ts
│  │  ├─ approval-events.ts
│  │  ├─ diff-events.ts
│  │  ├─ artifact-events.ts
│  │  └─ trace-events.ts
│  │
│  ├─ schemas/
│  │  ├─ zod/
│  │  └─ json-schema/
│  │
│  ├─ enums/
│  ├─ utils/
│  └─ index.ts
│
├─ package.json
├─ tsconfig.json
└─ README.md
```

### 設計重點
- `api/` 定義 **Tauri IPC invoke payload types**（request / response，非 HTTP DTO）。
- `events/` 定義 **Tauri event streaming payload types**（gRPC events 由 Rust 轉發至前端的格式）。
- `schemas/` 放 runtime validation（Zod），確保事件 payload 在接收時被驗證。
- C# gRPC protobuf 型別與此套件並行維護，需保持語義對齊。

---

## 9. `schemas/` 建議內容

### `schemas/api/`
- OpenAPI YAML/JSON（HTTP health / management endpoints 用）
- 若未來要用 codegen，可在此集中管理
- **注意**：gRPC `.proto` 定義由 `apps/agent-service` 的 C# 專案自行維護，不放此處

### `schemas/events/`
- JSON Schema for SSE / WebSocket events
- 方便做 contract tests

### `schemas/policy/`
- approval policy schema
- workspace trust schema
- model permission schema

### `schemas/tauri/`
- Tauri capabilities 對應結構說明
- command exposure rules

---

## 10. 命名規範

## 10.1 目錄與檔名- TypeScript 檔案使用 `kebab-case.ts`
- React component 檔案使用 `PascalCase.tsx`
- C# 類別使用 `PascalCase.cs`
- API endpoint 檔案使用 `FeatureEndpoints.cs`

## 10.2 DTO 命名
- Request：`CreateRunRequest`
- Response：`CreateRunResponse`
- Event：`RunStartedEvent`
- Artifact：`DiffArtifactDto`
- Approval：`ApprovalDecisionRequest`

## 10.3 ID 命名
- workspace：`ws_xxx`
- session：`sess_xxx`
- run：`run_xxx`
- approval：`apv_xxx`
- artifact：`art_xxx`

## 10.4 Tauri Command 命名
- Tauri command（Rust side）：`snake_case`，例如 `create_run`、`cancel_run`、`get_sessions`
- TypeScript wrapper（`services/tauri-bridge`）：`camelCase`，例如 `createRun()`、`cancelRun()`
- Tauri event（Rust emit → React listen）：`kebab-case`，例如 `run-event`、`stream-delta`

---

## 11. 初始 API 契約骨架

## 11.1 gRPC / REST API（前端透過 Tauri IPC 存取）

> **架構說明**：前端不直接呼叫以下 API。React 透過 Tauri IPC `invoke()` 發出請求，Rust gRPC Client 轉發至 Agent Service。以下為 Agent Service 對外服務定義，以 REST 格式呈現供閱讀。

### Runs
- `POST /runs`
- `GET /runs/{runId}`
- `POST /runs/{runId}/cancel`
- `GET /runs/{runId}/stream`

### Sessions
- `GET /sessions`
- `POST /sessions`
- `GET /sessions/{sessionId}`
- `GET /sessions/{sessionId}/history`

### Workspaces
- `GET /workspaces`
- `POST /workspaces`
- `GET /workspaces/{workspaceId}`
- `POST /workspaces/{workspaceId}/trust`

### Approvals
- `GET /approvals/pending`
- `POST /approvals/{approvalId}/decision`

### MCP
- `GET /mcp/servers`
- `POST /mcp/servers/register`
- `POST /mcp/servers/test-connection`

---

## 11.2 SSE Event 類型

### 最小必要事件
- `run.created`
- `run.started`
- `message.delta`
- `message.completed`
- `tool.started`
- `tool.stdout`
- `tool.completed`
- `approval.requested`
- `approval.resolved`
- `diff.available`
- `run.completed`
- `run.failed`

### 建議 Event Payload 共同欄位
```json
{
  "eventId": "evt_001",
  "eventType": "tool.started",
  "runId": "run_001",
  "sessionId": "sess_001",
  "timestamp": "2026-06-09T10:00:00Z",
  "data": {}
}
```

---

## 12. 初始資料庫骨架

## 12.1 SQLite 資料表建議

### `workspaces`
- `id`
- `name`
- `root_path`
- `trust_level`
- `created_at`
- `updated_at`

### `sessions`
- `id`
- `workspace_id`
- `title`
- `created_at`
- `updated_at`

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

---

## 13. Tauri 權限矩陣骨架

## 13.1 建議 capabilities 分拆

### `main-window.json`
允許：
- 基本視窗行為
- 受控的 dialog 開啟
- 與 Agent Service 通訊必要 command

### `settings-window.json`
允許：
- 讀取本機設定
- 儲存 model provider 設定
- 測試 Agent Service 狀態

### `privileged-window.json`
僅必要時建立：
- 安全地處理高敏感管理功能
- 不對主視窗開放

### `updater.json`
- 自動更新流程專用能力

---

## 14. 開發初始化順序

## Phase 0：建立 Repo 基礎
1. 建立 Monorepo 根目錄（`modern-wingman/`）
2. 設定 `pnpm-workspace.yaml`
3. 設定 `.editorconfig`、`.gitignore`
4. 加入 `global.json` 鎖定 .NET 10 SDK
5. 加入 `.nvmrc` 或 `package.json engines` 鎖定 Node.js 24.13.0
6. 建立基礎 README

## Phase 1：建立最小可跑骨架
1. 初始化 `apps/desktop`（React + Vite + Tauri 2 + Shadcn/ui + Tailwind CSS + Zustand）
2. 初始化 `apps/agent-service`（.NET 10 + ASP.NET Core + MAF 1.9.0 + Grpc.AspNetCore 骨架）
3. 建立 `packages/contracts`（Tauri IPC payload types）
4. 確認 desktop 能啟動並顯示首頁
5. 確認 agent-service gRPC health check 可通（Rust gRPC client 能連線）
6. 初始化 **MAF Workflow 骨架**（`Infrastructure/AgentFramework/Workflows/`）
7. 設定多 Provider 初始化（OpenAI provider 作為預設可注入）

## Phase 2：打通第一條鏈路
1. 實作 gRPC `RunService.CreateRun`
2. 實作 gRPC `RunService.StreamRun`（server-side streaming）
3. Rust gRPC Client 接收 stream，透過 `app_handle.emit()` 轉發 Tauri 事件
4. React 可透過 Tauri `invoke` 送出訊息，並透過 `listen` 收到模擬事件
5. 顯示 tool timeline 與 message stream

## Phase 3：加入真實工具能力
1. File tools
2. Approval 流程
3. Shell tools（受控）
4. Session persistence

## Phase 4：加入工作區與 diff
1. Workspace model
2. Diff preview
3. Artifact storage
4. Trust level 管理

## Phase 5：加入 MCP 與 Git
1. MCP presets
2. Git tools
3. Settings UI
4. 日誌與 tracing

---

## 15. MVP 必要檔案清單

## 15.1 Root
- `README.md`
- `pnpm-workspace.yaml`
- `.editorconfig`
- `.gitignore`
- `global.json`

## 15.2 Desktop
- `src/main.tsx`
- `src/app/router/index.tsx`
- `src/features/chat/components/ChatPanel.tsx`
- `src/features/timeline/components/RunTimeline.tsx`
- `src/services/agent-api/client.ts`
- `src/services/stream-client/sse-client.ts`
- `src-tauri/src/main.rs`
- `src-tauri/capabilities/main-window.json`
- `src-tauri/tauri.conf.json`

## 15.3 Agent Service
- `src/Host/Program.cs`
- `src/Host/Api/RunsEndpoints.cs`
- `src/Host/Streaming/SsePublisher.cs`
- `src/Application/Runs/CreateRunHandler.cs`
- `src/Infrastructure/ToolRuntime/FileTools/ReadFileTool.cs`
- `src/Infrastructure/ToolRuntime/ShellTools/RunCommandTool.cs`
- `src/Infrastructure/Security/Policies/DefaultApprovalPolicy.cs`
- `src/Infrastructure/Persistence/Sqlite/AppDbContext.cs`

## 15.4 Contracts
- `src/api/runs.ts`
- `src/events/run-events.ts`
- `src/events/tool-events.ts`
- `src/events/approval-events.ts`

---

## 16. 測試策略骨架

## 16.1 前端
- 單元測試：hooks、stream reducers、approval UI
- 元件測試：chat panel、timeline、approval dialog
- E2E：從送出 prompt 到顯示 timeline

## 16.2 Agent Service
- Unit tests：policy、tool adapters、DTO mapping
- Integration tests：runs endpoint、SSE streaming、SQLite persistence
- Contract tests：與 `packages/contracts` 對齊
- E2E：對 demo workspace 執行簡單修檔任務

---

## 17. CI / CD 骨架建議

## 17.1 CI 檢查項目
- Node.js 版本驗證（需 24.13.0）
- TypeScript type check
- ESLint
- .NET 10 build
- .NET test
- gRPC 服務健康檢查
- contract validation
- schema validation
- basic smoke test

## 17.2 Release 流程
- Desktop version bump
- Agent service version bump
- 產出桌面安裝包
- 簽章（正式環境）
- 釋出 release notes

---

## 18. 第一版分工建議

## 前端工程師
- Chat UI
- Session Sidebar
- Timeline / Diff / Approval UI
- Settings / MCP 管理頁

## .NET 工程師
- Runs API
- SSE Publisher
- Session persistence
- Tool Runtime
- Approval policy

## 桌面 / 平台工程師
- Tauri capabilities
- Agent Service supervisor
- Keychain / OS integration
- Packaging / updater

## 架構 / 技術主管
- 契約治理
- 權限矩陣
- 命名規範
- 里程碑與風險控管

---

## 19. 建議的 Git Commit 與 Branch 策略

### Branch 命名
- `feat/desktop-chat-ui`
- `feat/agent-runs-endpoint`
- `feat/contracts-run-events`
- `chore/ci-bootstrap`
- `fix/approval-dialog-state`

### Commit 建議
- `feat(desktop): add chat panel skeleton`
- `feat(agent-service): add runs endpoint`
- `feat(contracts): define run event schema`
- `chore(repo): setup pnpm workspace`

---

## 20. 第一版開工清單（直接可執行）

### Day 1
- 建 Monorepo 根目錄（`modern-wingman/`）
- 建 `apps/desktop`（React + Vite + Tauri 2 + Shadcn/ui + Tailwind CSS + Zustand）
- 建 `apps/agent-service`（.NET 10 + MAF 1.9.0 + gRPC server skeleton）
- 建 `packages/contracts`（Tauri IPC payload types）

### Day 2
- Desktop 首頁跑起來
- Agent service gRPC health check 跑起來
- Rust gRPC client（Tonic）能連線 Agent Service
- Tauri IPC bridge 初始化（`services/tauri-bridge/` + `services/agent-api/`）

### Day 3
- gRPC `CreateRun` / `StreamRun` 打通
- MAF Workflow 骨架（Run → Workflow 實例）
- Rust 接收 gRPC stream → Tauri emit → React `listen()` 收到事件
- 前端顯示模擬 message delta

### Day 4
- File read tool
- Approval modal
- Timeline 顯示 tool started/completed
- MAF Workflow 加入 Tool executor node

### Day 5
- Session persistence（SQLite 初始化）
- 多 provider 設定（OpenAI / Anthropic 可切換）
- 基本 MVP demo 完成

---

## 21. 後續擴充預留點

1. `packages/contracts` 預留 WebSocket event types
2. `packages/skills` 預留 workflow presets
3. `packages/mcp-presets` 預留 provider-specific config
4. `infra/observability` 預留 OTel collector 設定
5. `apps/agent-service/Infrastructure/AgentFramework/Workflows` 預留 subagents / background jobs

---

## 22. 驗收標準（B版文件完成後）

若依本文件建置骨架，應達成以下狀態：

- Repo 結構已完整建立
- Desktop / Agent Service / Contracts 可獨立啟動與編譯
- 有清楚的前後端契約位置
- 有最小 API 與事件骨架
- 有可持續擴充的目錄設計
- 工程團隊可開始平行開發

---

## 23. 結語

這份 B 版文件的目標，是把「架構想法」落成「工程骨架」。

當你完成這份文件對應的 repo 初始化後，團隊就不會停留在抽象討論，而是可以直接開始：

- 建 UI
- 建 Agent API
- 定義事件
- 接工具
- 實作 Approval
- 做 MVP demo

建議下一步立即補的文件有：

1. **API 契約詳細版**
2. **Event Schema 詳細版**
3. **Tauri Capabilities 權限矩陣**
4. **Approval Policy 詳規**
5. **MCP 接入規格**

這樣你的專案就能從「規劃階段」進入真正的「可開發階段」。
