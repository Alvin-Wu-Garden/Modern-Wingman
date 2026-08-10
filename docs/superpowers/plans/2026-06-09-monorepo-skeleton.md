# Modern Wingman — Monorepo 骨架建立計畫（Phase 0 + Phase 1）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 從零建立 `modern-wingman` Monorepo，完成 apps/desktop、apps/agent-service、packages/contracts 三個專案的初始骨架，使三者皆可獨立編譯並啟動，並打通 Tauri 啟動流程至「顯示首頁」。

**Architecture:**
- React + TypeScript + Vite + Tauri 2（桌面殼）作為前端應用
- .NET 10 + ASP.NET Core + MAF 1.9.0 + gRPC 作為 Agent Service
- pnpm workspaces 管理 monorepo；Rust（透過 Tauri）僅負責桌面橋接，不承擔業務邏輯

**Tech Stack:**
- Node.js 24.13.0 / pnpm（最新穩定）/ TypeScript 5
- React 18 / Vite / Tauri 2 / Shadcn-ui / Tailwind CSS v4 / Zustand
- .NET 10 / ASP.NET Core Minimal API / MAF 1.9.0 / Grpc.AspNetCore
- Rust（stable，透過 rustup）/ Tonic（gRPC client for Rust）

---

## 前置工具安裝狀態

| 工具 | 狀態 | 說明 |
|------|------|------|
| Node.js 24.13.0 | ✅ | 已安裝 |
| npm 11.6.2 | ✅ | 已安裝 |
| .NET 10.0.201 | ✅ | 已安裝 |
| Git 2.47.1 | ✅ | 已安裝 |
| Visual Studio 2022 | ✅ | Rust build tools 可用 |
| WebView2 148.x | ✅ | Tauri 依賴 |
| pnpm | ❌ | 需透過 `npm i -g pnpm` 安裝 |
| Rust / rustup | ❌ | 需下載 rustup-init.exe 安裝 |
| Tauri CLI | ❌ | Rust 裝好後執行 `pnpm add -g @tauri-apps/cli` |

---

## 檔案結構總覽（Phase 0 + Phase 1 完成後）

```
modern-wingman/
├── .editorconfig
├── .gitignore
├── .nvmrc                              # 24.13.0
├── global.json                         # .NET 10 SDK pinning
├── pnpm-workspace.yaml
├── package.json                        # root（private, scripts）
├── README.md
├── 工作日誌.md                          # 實作日誌
│
├── apps/
│   ├── desktop/                        # React + Vite + Tauri 2
│   │   ├── src/
│   │   │   ├── main.tsx
│   │   │   ├── App.tsx
│   │   │   ├── app/
│   │   │   │   ├── router/index.tsx
│   │   │   │   ├── store/index.ts
│   │   │   │   └── providers/AppProviders.tsx
│   │   │   ├── features/
│   │   │   │   └── chat/
│   │   │   │       └── components/ChatPage.tsx  # 首頁佔位
│   │   │   ├── services/
│   │   │   │   ├── tauri-bridge/index.ts        # Tauri invoke/listen 封裝
│   │   │   │   ├── agent-api/client.ts          # invoke-based agent 呼叫
│   │   │   │   └── stream-client/index.ts       # Tauri listen-based stream
│   │   │   └── styles/globals.css              # Tailwind directives
│   │   ├── src-tauri/
│   │   │   ├── src/
│   │   │   │   ├── main.rs
│   │   │   │   └── commands/mod.rs             # Tauri commands 骨架
│   │   │   ├── capabilities/
│   │   │   │   └── main-window.json
│   │   │   ├── tauri.conf.json
│   │   │   └── Cargo.toml
│   │   ├── index.html
│   │   ├── package.json
│   │   ├── tsconfig.json
│   │   ├── tsconfig.app.json
│   │   ├── tsconfig.node.json
│   │   ├── vite.config.ts
│   │   ├── components.json             # Shadcn/ui config
│   │   └── tailwind.config.ts
│   │
│   └── agent-service/                  # .NET 10 + MAF 1.9.0 + gRPC
│       ├── src/
│       │   ├── Host/
│       │   │   ├── Program.cs
│       │   │   ├── GrpcServices/
│       │   │   │   └── HealthGrpcService.cs    # 第一個 gRPC service
│       │   │   ├── Protos/
│       │   │   │   └── health.proto            # gRPC health check proto
│       │   │   └── DependencyInjection/
│       │   │       └── ServiceRegistration.cs
│       │   ├── Application/
│       │   │   └── .gitkeep
│       │   ├── Domain/
│       │   │   └── .gitkeep
│       │   └── Infrastructure/
│       │       └── AgentRuntime/
│       │           └── .gitkeep
│       ├── appsettings.json
│       ├── appsettings.Development.json
│       └── AgentService.csproj
│
├── UnitTests/                          # local-only, not versioned
│   └── AgentService.UnitTests.csproj
│
├── packages/
│   ├── contracts/                      # Tauri IPC payload types
│   │   ├── src/
│   │   │   ├── api/
│   │   │   │   └── runs.ts
│   │   │   ├── events/
│   │   │   │   └── run-events.ts
│   │   │   └── index.ts
│   │   ├── package.json
│   │   └── tsconfig.json
│   ├── ui-kit/
│   │   ├── src/index.ts
│   │   └── package.json
│   ├── prompt-assets/
│   │   └── package.json
│   ├── skills/
│   │   └── package.json
│   ├── mcp-presets/
│   │   └── package.json
│   └── config/
│       ├── eslint-base.js
│       ├── tsconfig-base.json
│       └── package.json
│
├── schemas/
│   ├── api/.gitkeep
│   ├── events/.gitkeep
│   ├── policy/.gitkeep
│   └── tauri/.gitkeep
│
├── examples/
│   ├── demo-workspace/.gitkeep
│   ├── sample-prompts/.gitkeep
│   └── sample-mcp-servers/.gitkeep
│
├── infra/
│   ├── dev/.gitkeep
│   ├── ci/.gitkeep
│   ├── packaging/.gitkeep
│   └── observability/.gitkeep
│
└── scripts/
    ├── bootstrap/
    │   └── check-tools.ps1             # 工具版本檢查腳本
    └── dev/
        └── start-all.ps1               # 一鍵啟動開發環境
```

---

## Task 1：安裝前置工具（pnpm + Rust + Tauri CLI）

**Files:** 無檔案變更，僅工具安裝

- [ ] **Step 1.1：安裝 pnpm**

```powershell
npm install -g pnpm
pnpm --version
```

預期輸出：`9.x.x`（或更新版本）

⚠️ **若失敗（內網阻擋 npm registry）**：手動下載 pnpm standalone exe  
  → https://github.com/pnpm/pnpm/releases/latest → `pnpm-win-x64.exe`  
  → 重新命名為 `pnpm.exe` 並放入 `C:\Windows\System32\` 或 PATH 路徑

- [ ] **Step 1.2：安裝 Rust（rustup）**

下載並執行：https://win.rustup.rs/x86_64  
安裝選項選 **1（預設）**，完成後重開 PowerShell。

```powershell
rustc --version
cargo --version
```

預期輸出：`rustc 1.8x.x (stable)`

⚠️ **若失敗（內網阻擋）**：
  → 從 https://forge.rust-lang.org/infra/other-installation-methods.html  
  → 下載 `rust-1.xx.x-x86_64-pc-windows-msvc.msi` 離線安裝包

- [ ] **Step 1.3：安裝 Tauri CLI**

```powershell
pnpm add -g @tauri-apps/cli
pnpm tauri --version
```

預期輸出：`tauri-cli 2.x.x`

⚠️ **若 pnpm add 失敗**：使用 `npm install -g @tauri-apps/cli`

---

## Task 2：建立 Monorepo 根目錄結構

**Files:**
- Create: `modern-wingman/.gitignore`
- Create: `modern-wingman/.editorconfig`
- Create: `modern-wingman/.nvmrc`
- Create: `modern-wingman/global.json`
- Create: `modern-wingman/pnpm-workspace.yaml`
- Create: `modern-wingman/package.json`
- Create: `modern-wingman/README.md`

- [ ] **Step 2.1：初始化 git repo**

```powershell
cd "D:\My Folder\Dev\Modern Wingman"
git init
git branch -m main
```

預期輸出：`Initialized empty Git repository in ...`

- [ ] **Step 2.2：建立 .gitignore**

內容（已建立於 Task 執行時，見 Step 2.2 程式碼區塊）

- [ ] **Step 2.3：建立其餘根設定檔**（.editorconfig、.nvmrc、global.json、pnpm-workspace.yaml、package.json）

- [ ] **Step 2.4：建立佔位目錄**（apps、packages、schemas、examples、infra、scripts 子目錄及 .gitkeep）

- [ ] **Step 2.5：初始 commit**

```powershell
git add .
git commit -m "chore(repo): initialize monorepo skeleton"
```

---

## Task 3：初始化 packages/contracts

**Files:**
- Create: `packages/contracts/package.json`
- Create: `packages/contracts/tsconfig.json`
- Create: `packages/contracts/src/index.ts`
- Create: `packages/contracts/src/api/runs.ts`
- Create: `packages/contracts/src/events/run-events.ts`

- [ ] **Step 3.1：建立 contracts package 檔案**
- [ ] **Step 3.2：安裝 contracts 套件依賴**

```powershell
cd "D:\My Folder\Dev\Modern Wingman"
pnpm install
```

- [ ] **Step 3.3：確認 TypeScript 編譯無錯誤**

```powershell
cd packages/contracts
pnpm tsc --noEmit
```

預期：無錯誤輸出

- [ ] **Step 3.4：Commit**

```powershell
git add packages/contracts
git commit -m "feat(contracts): add initial Tauri IPC payload types"
```

---

## Task 4：初始化 apps/agent-service

**Files:**
- Create: `apps/agent-service/AgentService.csproj`
- Create: `apps/agent-service/src/Host/Program.cs`
- Create: `apps/agent-service/src/Host/Protos/health.proto`
- Create: `apps/agent-service/src/Host/GrpcServices/HealthGrpcService.cs`
- Create: `apps/agent-service/src/Host/DependencyInjection/ServiceRegistration.cs`
- Create: `apps/agent-service/appsettings.json`
- Create: `apps/agent-service/appsettings.Development.json`

- [ ] **Step 4.1：建立 .csproj 並加入 NuGet 套件**

```powershell
cd "D:\My Folder\Dev\Modern Wingman\apps\agent-service"
dotnet new web -n AgentService --no-restore
```

⚠️ **若 NuGet restore 失敗（內網）**：先執行建立，後續手動 `dotnet restore --source <內部 NuGet 來源>`

- [ ] **Step 4.2：加入必要 NuGet 套件**

```powershell
dotnet add package Grpc.AspNetCore --version 2.67.0
dotnet add package Microsoft.Agents.AI --version 1.9.0
dotnet add package Microsoft.Agents.AI.OpenAI --version 1.9.0
dotnet add package Microsoft.Agents.AI.Anthropic --version 1.9.0
dotnet add package OpenTelemetry.Extensions.Hosting --version 1.10.0
dotnet add package Microsoft.EntityFrameworkCore.Sqlite --version 9.0.0
```

⚠️ **若套件安裝失敗（內網）**：逐一記錄失敗套件，人工介入設定內部 NuGet source 後繼續

- [ ] **Step 4.3：撰寫 health.proto 與 Program.cs**
- [ ] **Step 4.4：確認 .NET 編譯成功**

```powershell
dotnet build
```

預期輸出：`Build succeeded.`

- [ ] **Step 4.5：啟動並確認 gRPC health endpoint 存活**

```powershell
dotnet run
```

預期：應用啟動，控制台顯示 gRPC server 監聽 `http://localhost:5000`

- [ ] **Step 4.6：Commit**

```powershell
git add apps/agent-service
git commit -m "feat(agent-service): add .NET 10 + gRPC + MAF 1.9.0 skeleton"
```

---

## Task 5：初始化 apps/desktop（React + Vite + Tauri 2）

**Files:**
- Create: `apps/desktop/` （透過 Tauri CLI 建立）
- Modify: `apps/desktop/src/` （加入 Shadcn/ui + Tailwind + Zustand）
- Create: `apps/desktop/src/services/tauri-bridge/index.ts`
- Create: `apps/desktop/src/services/agent-api/client.ts`
- Create: `apps/desktop/src/services/stream-client/index.ts`
- Create: `apps/desktop/src/features/chat/components/ChatPage.tsx`

- [ ] **Step 5.1：建立 Tauri 專案骨架**

```powershell
cd "D:\My Folder\Dev\Modern Wingman\apps"
pnpm create tauri-app desktop --template react-ts --manager pnpm --no-open
```

⚠️ **若 create tauri-app 失敗（內網）**：手動建立目錄結構，詳見 Task 5 附錄

- [ ] **Step 5.2：安裝 Tailwind CSS v4**

```powershell
cd desktop
pnpm add tailwindcss @tailwindcss/vite
```

- [ ] **Step 5.3：安裝 Shadcn/ui 依賴**

```powershell
pnpm add class-variance-authority clsx tailwind-merge lucide-react
pnpm add -D @types/node
```

- [ ] **Step 5.4：初始化 Shadcn/ui**

```powershell
pnpm dlx shadcn@latest init
```

選項：Style = Default, Base color = Neutral, CSS variables = yes

- [ ] **Step 5.5：安裝 Zustand 和 React Router**

```powershell
pnpm add zustand react-router-dom @tanstack/react-query
```

- [ ] **Step 5.6：撰寫服務層骨架**（tauri-bridge、agent-api、stream-client）
- [ ] **Step 5.7：撰寫 ChatPage 首頁佔位元件**
- [ ] **Step 5.8：設定 Router 與 AppProviders**
- [ ] **Step 5.9：確認 Desktop 開發模式可啟動**

```powershell
pnpm tauri dev
```

預期：Tauri 視窗開啟，顯示 ChatPage 首頁

- [ ] **Step 5.10：Commit**

```powershell
git add apps/desktop
git commit -m "feat(desktop): add React + Vite + Tauri 2 + Shadcn/ui + Zustand skeleton"
```

---

## Task 6：建立 packages 佔位套件與工具腳本

**Files:**
- Create: `packages/ui-kit/package.json`
- Create: `packages/ui-kit/src/index.ts`
- Create: `packages/config/package.json`
- Create: `packages/config/tsconfig-base.json`
- Create: `scripts/bootstrap/check-tools.ps1`
- Create: `scripts/dev/start-all.ps1`

- [ ] **Step 6.1：建立 ui-kit、config、prompt-assets、skills、mcp-presets 佔位套件**
- [ ] **Step 6.2：建立 check-tools.ps1（工具版本檢查）**
- [ ] **Step 6.3：建立 start-all.ps1（一鍵啟動開發環境）**
- [ ] **Step 6.4：執行 pnpm install（根目錄，連結所有 workspace）**

```powershell
cd "D:\My Folder\Dev\Modern Wingman"
pnpm install
```

預期：所有 workspace packages 被識別並連結

- [ ] **Step 6.5：Commit**

```powershell
git add packages scripts
git commit -m "chore(packages): add placeholder packages and dev scripts"
```

---

## Task 7：建立工作日誌與驗收確認

**Files:**
- Create: `工作日誌.md`（即時更新）

- [ ] **Step 7.1：確認三個核心單元皆可獨立編譯**

```powershell
# contracts
cd packages/contracts && pnpm tsc --noEmit

# agent-service
cd apps/agent-service && dotnet build

# desktop
cd apps/desktop && pnpm tsc --noEmit
```

- [ ] **Step 7.2：確認 pnpm workspace 所有套件被識別**

```powershell
cd "D:\My Folder\Dev\Modern Wingman"
pnpm list -r --depth=0
```

- [ ] **Step 7.3：最終 Commit**

```powershell
git add 工作日誌.md
git commit -m "docs: add implementation work log"
```

---

## 驗收標準

完成 Phase 0 + Phase 1 後，應達到以下狀態：

- [ ] `pnpm install`（根目錄）無錯誤
- [ ] `packages/contracts` 可 TypeScript 編譯
- [ ] `apps/agent-service` 可 `dotnet build` 成功
- [ ] `apps/agent-service` 可 `dotnet run` 啟動 gRPC server
- [ ] `apps/desktop` 可 `pnpm tauri dev` 啟動，顯示首頁
- [ ] `apps/ui-kit`、`packages/prompt-assets`、`packages/skills`、`packages/mcp-presets` 佔位套件存在
- [ ] 根目錄 `git log` 有清晰 commit 紀錄
- [ ] `工作日誌.md` 記錄每個步驟執行結果

---

## 下一份計畫（Phase 2）

Phase 0+1 完成後，下一份計畫為：
**`2026-06-09-grpc-chain.md`** — 打通 gRPC 全鏈路（Tauri IPC ↔ Rust gRPC Client ↔ Agent Service ↔ MAF Workflow）
