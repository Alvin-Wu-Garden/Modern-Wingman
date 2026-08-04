# Modern Wingman

> AI Agent Desktop Tool — Windows-first, Tauri 2 + .NET 10 + MAF 1.9.0

[繁體中文說明 →](./README_zh-TW.md)

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Desktop Shell | Tauri 2 (Rust) |
| Frontend | React 19 + TypeScript + Vite |
| UI | Shadcn/ui + Tailwind CSS v4 + Zustand |
| Agent Service | .NET 10 + ASP.NET Core + MAF 1.9.0 |
| Communication | Tauri WebView ↔ local REST/SSE Agent Service |
| AI Providers | OpenAI / Anthropic / Azure OpenAI / Custom BYOK |

---

## Prerequisites

| Tool | Required Version | Install |
|------|-----------------|---------|
| Node.js | 24.13.0 | https://nodejs.org |
| pnpm | ≥ 11.0.0 | `npm install -g pnpm` |
| .NET SDK | 10.x | https://dotnet.microsoft.com/download |
| Rust (rustup) | stable (≥ 1.80) | https://rustup.rs |
| Tauri CLI | 2.x | `pnpm add -g @tauri-apps/cli` |
| WebView2 | any | pre-installed on Windows 11; [download](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) for Windows 10 |

Run the tool check script to verify your environment:

```powershell
.\scripts\bootstrap\check-tools.ps1
```

---

## Getting Started

### 1. Install dependencies

```powershell
pnpm install
```

### 2. Configure Agent Service

Copy the development settings and fill in your API key:

```powershell
# appsettings.Development.json is already present — edit it directly
notepad apps\agent-service\appsettings.Development.json
```

Minimum required config:

```json
{
  "AgentService": {
    "ActiveProfileId": "openai-byok"
  }
}
```

> Set API keys via environment variables — never hardcode them:
> ```powershell
> $env:OPENAI_API_KEY = "sk-..."
> $env:ANTHROPIC_API_KEY = "sk-ant-..."
> ```
> Available profile IDs: `copilot-default`, `openai-byok`, `anthropic-byok`, `azure-openai-byok`, `azure-foundry-byok`, `custom-byok`

> **Note:** `appsettings.Development.json` is excluded from git (`.gitignore`). Never commit API keys.

### 3. Start development services

**Option A — VS Code full Debug (recommended)**

Choose `Modern Wingman（全端 Debug）` in VS Code Run and Debug and press `F5`.
The pre-launch task starts Vite; VS Code starts the debuggable Agent Service and Tauri.

The fixed local endpoints are Vite `4173`, Agent REST/SSE `5002`, and Neo4j Bolt `17688`.
Press `Shift+F5` to run the matching stop task.

For a service-only smoke test (without debugger), use:

```powershell
.\scripts\dev\start-all.ps1
```

Stop it with:

```powershell
.\scripts\dev\stop-all.ps1
```

**Option B — Manual (two terminals)**

Terminal 1 — Agent Service:

```powershell
cd apps\agent-service
dotnet run
# REST/SSE API listens on http://127.0.0.1:5002
```

Terminal 2 — Desktop:

```powershell
cd apps\desktop
pnpm tauri dev
# Vite dev server: http://127.0.0.1:4173
# Tauri window opens automatically
```

Do not use the removed `-AgentOnly` or `-DesktopOnly` flags.

> **First run:** Cargo will compile all Rust dependencies. This takes **5–10 minutes**. Subsequent runs are fast.

---

## Building for Production

```powershell
# Build Agent Service
cd apps\agent-service
dotnet publish -c Release

# Build Desktop (produces installer in apps/desktop/src-tauri/target/release/bundle/)
cd apps\desktop
pnpm tauri build
```

---

## Workspace Structure

```
modern-wingman/
├── apps/
│   ├── desktop/        # Tauri 2 + React 19 + Vite (frontend + Rust shell)
│   └── agent-service/  # .NET 10 + ASP.NET Core + MAF 1.9.0 + REST/SSE
├── packages/
│   ├── contracts/      # Shared Tauri IPC payload types & event schemas
│   ├── ui-kit/         # Shared React UI components
│   ├── config/         # Shared TypeScript / ESLint configuration
│   ├── prompt-assets/  # Prompt templates
│   ├── skills/         # Agent skill definitions
│   └── mcp-presets/    # MCP server presets (Phase 5)
├── schemas/            # API / event / policy / Tauri schema placeholders
├── scripts/
│   ├── bootstrap/      # check-tools.ps1 — verify dev environment
│   └── dev/            # start-all.ps1 — one-command dev startup
└── docs/               # Architecture docs & implementation plans
```

---

## Architecture Overview

```
React UI
  │  Tauri WebView (local REST/SSE)
  ▼
Rust (Tauri shell)
  │  local desktop host
  ▼
Agent Service (.NET 10)
  │  MAF 1.9.0 Workflows
  ▼
AI Provider  (OpenAI / Azure OpenAI / Anthropic / Custom BYOK)
```

Streaming tokens flow through the local SSE endpoint:
`Agent Service → REST/SSE → Tauri WebView → React`

---

## Development Workflow

```powershell
# Type-check all packages
pnpm -r typecheck

# Build .NET service only
cd apps\agent-service && dotnet build

# TypeScript check for desktop only
cd apps\desktop && pnpm typecheck
```

---

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| `tauri dev` hangs on first run | Cargo is compiling — wait 5–10 min |
| `esbuild` build script warning from pnpm | Benign; run `pnpm approve-builds` in an interactive terminal to silence it |
| `dotnet run` fails with port conflict | Stop the previous Modern Wingman debug session and verify `127.0.0.1:5002` is free; do not kill unknown processes |
| Tauri window blank / white screen | Ensure Vite is ready on `127.0.0.1:4173` before Tauri connects |
