# Modern Wingman — VS Code 偵錯與啟動手冊

本專案目前只有一個推薦的全端 Debug 組態：`Modern Wingman（全端 Debug）`。
它由 VS Code 啟動 Agent Service 與 Tauri，前置 task 只清理可驗證的舊程序、建置 Vite
相依服務並啟動 Vite；按下停止後會執行同一套 ownership-aware 清理流程。

## 1. 固定本機服務

| 服務 | 位址 | 啟動責任 |
|---|---|---|
| Vite | `http://127.0.0.1:4173` | `scripts/dev/start-all.ps1` |
| Agent Service REST／SSE | `http://127.0.0.1:5002` | VS Code `dotnet` debugger |
| Neo4j Bolt | `bolt://127.0.0.1:17688` | Agent Service managed runtime（需要時啟動） |
| Tauri Desktop | 無固定 TCP port | VS Code `node-terminal` debugger |

Neo4j Browser 的 HTTP port 由 Neo4j 自己的設定管理，不應在 Debug task 內另外猜測或
重複啟動。若固定 port 被其他程式占用，前置 task 會顯示 PID、程序名稱與路徑並停止，
不會靜默換 port 或誤殺未知程序。

## 2. 前置需求

請先安裝：

- .NET 10 SDK
- Node.js 24 與 pnpm
- Rust stable、Cargo
- VS Code 的 C# Dev Kit（`ms-dotnettools.csdevkit`）與 C# 擴充套件

## 3. 推薦的全端 Debug 流程

1. 關閉其他正在使用 `4173`、`5002` 或 `17688` 的本專案程序。
2. 在 VS Code 按 `Ctrl+Shift+D` 開啟「執行與偵錯」。
3. 選擇唯一的 `Modern Wingman（全端 Debug）`。
4. 按 `F5`。

前置 task 會執行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\dev\start-all.ps1 -SkipBuild
```

接著 VS Code 會啟動可掛中斷點的 Agent Service 與 Tauri。Vite 已就緒後才會繼續，
所以前端不會在服務尚未準備好時先開啟。

按 `Shift+F5` 停止時，`modern-wingman: stop all` 只會清理本輪 runtime manifest、
固定 workspace 執行檔與已驗證的 managed Neo4j launcher；未知程序會保留並提示，避免
影響其他應用程式。

## 4. 手動啟動相依服務

只有在不需要 VS Code debugger 時才直接執行：

```powershell
.\scripts\dev\start-all.ps1
```

停止：

```powershell
.\scripts\dev\stop-all.ps1
```

若只想驗證啟動前清理與 Vite，可使用 `-SkipBuild`；不要再使用舊的
`-DesktopOnly`、`-AgentOnly` 或多組 launch profile。

## 5. 設定中斷點與聊天呼叫鏈

### HTTP／SSE 入口

檔案：`apps/agent-service/src/Host/RestEndpoints/GeneralConversationEndpoints.cs` 或 `ProjectConversationEndpoints.cs`

方法：`SendMessage`

```text
POST /api/conversations/{id}/messages
  ↓
ConversationEndpoints.SendMessage
  ↓
短時間探測 Graph（Ready／Stale／Unavailable）
  ↓
GraphRAG prompt，或 source-only fallback
  ↓
AgentRuntime.RunStreamingAsync
```

### Agent 串流推論

檔案：`apps/agent-service/src/Infrastructure/AgentRuntime/AgentRuntime.cs`

方法：`RunStreamingAsync`

專案問答會額外提供下列唯讀工具：

- `search_project_graph`
- `trace_project_graph_paths`
- `search_project_text`
- `find_csharp_symbol`
- `read_project_file_range`

Graph 不可用時仍會建立這些工具，Agent 必須區分已確認事實、合理推論與未知缺口。

## 6. 常見問題

### Port 已被占用

先查詢程序，不要直接殺掉未知 PID：

```powershell
Get-NetTCPConnection -State Listen -LocalPort 4173,5002,17688 |
  Select-Object LocalPort,OwningProcess
Get-Process -Id <PID> | Select-Object Id,ProcessName,Path,StartTime
```

若程序是上一輪 Modern Wingman，先執行：

```powershell
.\scripts\dev\stop-all.ps1
```

若不是本 workspace 的程序，請先停止該應用程式或改由擁有者處理；本專案不會自動誤殺。

### 建置顯示 DLL 被鎖定

確認沒有另一個 AgentService 或 debugger session 正在執行，再按 `Shift+F5` 後重試。
必要時先用上面的 `stop-all.ps1`，不要在工作管理員盲目終止所有 `dotnet.exe`。

### Graph 無法連線

這不應阻斷專案問答。畫面會顯示 Graph 警告，Agent 仍可使用原始碼工具；需要查看
跨節點鏈路時，再啟動 managed Neo4j 並重新檢查索引狀態。

### 想查看目前實際 port

```powershell
Get-NetTCPConnection -State Listen |
  Where-Object LocalAddress -in @('127.0.0.1','::1') |
  Sort-Object LocalPort |
  Select-Object LocalAddress,LocalPort,OwningProcess
```

所有本機開發設定請以 `launch.json`、`tasks.json`、`launchSettings.json`、
`appsettings.json` 與 `vite.config.ts` 為準；本文件不保留已移除的 gRPC、5001、1420、
5200、7264 或舊版多組啟動流程。
