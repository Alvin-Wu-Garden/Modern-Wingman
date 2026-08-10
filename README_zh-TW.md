# Modern Wingman

> AI Agent 桌面工具 — Windows 優先，Tauri 2 + .NET 10 + MAF 1.9.0

[English README →](./README.md)

## 技術棧

| 層次 | 技術 |
|------|------|
| 桌面殼層 | Tauri 2（Rust） |
| 前端 | React 19 + TypeScript + Vite |
| UI 元件 | Shadcn/ui + Tailwind CSS v4 + Zustand |
| Agent 服務 | .NET 10 + ASP.NET Core + MAF 1.9.0 |
| 通訊架構 | Tauri WebView ↔ 本機 REST/SSE Agent Service |
| AI 供應商 | OpenAI / Anthropic / Azure OpenAI / Custom BYOK |

---

## 前置條件

| 工具 | 需求版本 | 安裝方式 |
|------|---------|---------|
| Node.js | 24.13.0 | https://nodejs.org |
| pnpm | ≥ 11.0.0 | `npm install -g pnpm` |
| .NET SDK | 10.x | https://dotnet.microsoft.com/download |
| Rust（rustup） | stable（≥ 1.80） | https://rustup.rs |
| Tauri CLI | 2.x | `pnpm add -g @tauri-apps/cli` |
| WebView2 | 任意版本 | Windows 11 已內建；Windows 10 請[另行下載](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) |

執行以下指令確認環境是否齊全：

```powershell
.\scripts\bootstrap\check-tools.ps1
```

---

## 快速開始

### 1. 安裝相依套件

```powershell
pnpm install
```

### 2. 設定 Agent Service

開啟開發設定檔並填入 API 金鑰：

```powershell
notepad apps\agent-service\appsettings.Development.json
```

最低必要設定如下：

```json
{
  "AgentService": {
    "ActiveProfileId": "openai-byok"
  }
}
```

> 請在 Modern Wingman「設定」頁輸入 API 金鑰。後端會先向供應商驗證，再以加密形式保存於本機；Provider 金鑰不從環境變數讀取。
> 可用的 Profile ID：`copilot-default`、`openai-byok`、`anthropic-byok`、`azure-openai-byok`、`azure-foundry-byok`、`custom-byok`

> **注意：** `appsettings.Development.json` 已列入 `.gitignore`，不會被提交至 Git。請勿將 API 金鑰寫入任何受版控的檔案。

### 3. 啟動開發服務

**方式 A — VS Code 全端 Debug（建議）**

在 VS Code 的「執行與偵錯」選擇 `Modern Wingman（全端 Debug）`，按 `F5`。
前置 task 會啟動 Vite；Agent Service 與 Tauri 由 VS Code 啟動並可掛中斷點。

固定本機端點為 Vite `4173`、Agent REST／SSE `5002`、Neo4j Bolt `17688`。
按 `Shift+F5` 會執行對應的停止 task。

若只需不掛 debugger 的服務 smoke test，可使用：

```powershell
.\scripts\dev\start-all.ps1
```

停止：

```powershell
.\scripts\dev\stop-all.ps1
```

**方式 B — 手動啟動（兩個終端機）**

終端機 1 — Agent Service：

```powershell
cd apps\agent-service
dotnet run
# REST/SSE API 監聽 http://127.0.0.1:5002
```

終端機 2 — 桌面應用：

```powershell
cd apps\desktop
pnpm tauri dev
# Vite 開發伺服器：http://127.0.0.1:4173
# Tauri 視窗將自動開啟
```

請勿再使用已移除的 `-AgentOnly` 或 `-DesktopOnly` 參數。

> **首次執行：** Cargo 需要編譯所有 Rust 相依項目，約需 **5～10 分鐘**。後續啟動將快許多。

---

## 正式建置

```powershell
# 建置 Agent Service
cd apps\agent-service
dotnet publish -c Release

# 建置桌面應用（安裝檔輸出至 apps/desktop/src-tauri/target/release/bundle/）
cd apps\desktop
pnpm tauri build
```

---

## Workspace 結構

```
modern-wingman/
├── apps/
│   ├── desktop/        # Tauri 2 + React 19 + Vite（前端 + Rust 殼層）
│   └── agent-service/  # .NET 10 + ASP.NET Core + MAF 1.9.0 + REST/SSE
├── packages/
│   ├── contracts/      # 共用 Tauri IPC Payload 型別 & 事件 Schema
│   ├── ui-kit/         # 共用 React UI 元件
│   ├── config/         # 共用 TypeScript / ESLint 設定
│   ├── prompt-assets/  # Prompt 範本
│   ├── skills/         # Agent Skill 定義
│   └── mcp-presets/    # MCP Server 預設設定（Phase 5）
├── schemas/            # API / 事件 / 政策 / Tauri 結構定義佔位
├── scripts/
│   ├── bootstrap/      # check-tools.ps1 — 檢查開發環境工具
│   └── dev/            # start-all.ps1 — 一鍵啟動開發環境
└── docs/               # 架構設計文件 & 實作計畫
```

---

## 架構概覽

```
React UI
  │  Tauri WebView（本機 REST/SSE）
  ▼
Rust（Tauri 殼層）
  │  本機桌面 Host
  ▼
Agent Service（.NET 10）
  │  MAF 1.9.0 Workflows
  ▼
AI 供應商（OpenAI / Azure OpenAI / Anthropic / Custom BYOK）
```

串流 Token 透過本機 SSE 回傳：
`Agent Service → REST/SSE → Tauri WebView → React`

---

## 開發常用指令

```powershell
# 對所有套件執行型別檢查
pnpm -r typecheck

# 僅建置 .NET 服務
cd apps\agent-service && dotnet build

# 僅對桌面前端執行 TypeScript 檢查
cd apps\desktop && pnpm typecheck
```

---

## 常見問題排除

| 症狀 | 解法 |
|------|------|
| `tauri dev` 首次卡住不動 | Cargo 正在編譯中，等待 5～10 分鐘即可 |
| pnpm 出現 `esbuild` build script 警告 | 不影響功能；在互動式終端機執行 `pnpm approve-builds` 可消除警告 |
| `dotnet run` 報 Port 衝突 | 先停止上一輪 Modern Wingman Debug，確認 `127.0.0.1:5002` 已釋放；不要終止未知程序 |
| Tauri 視窗空白或白屏 | 確認 Vite 已在 `127.0.0.1:4173` Ready 後再啟動 Tauri |
| 內網無法下載 NuGet / npm 套件 | 設定內部 registry，或手動下載離線套件後重試 |
