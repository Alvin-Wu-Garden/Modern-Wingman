# Modern Wingman — VS Code 偵錯與啟動手冊

## 目錄

1. [前置需求](#1-前置需求)
2. [VS Code 組態說明](#2-vs-code-組態說明)
3. [偵錯 Agent Service 與啟動 Wingman（推薦）](#3-偵錯-agent-service-與啟動-wingman推薦)
4. [附加到已執行的 Agent Service](#4-附加到已執行的-agent-service)
5. [設定中斷點](#5-設定中斷點)
6. [聊天室訊息的呼叫鏈](#6-聊天室訊息的呼叫鏈)
7. [常見問題排除](#7-常見問題排除)

---

## 1. 前置需求

### 安裝 VS Code 擴充套件

開啟 VS Code 擴充套件面板（`Ctrl+Shift+X`），搜尋並安裝：

| 擴充套件 | 用途 |
|---------|------|
| **C# Dev Kit** (`ms-dotnettools.csdevkit`) | .NET debug 支援（必裝） |
| **C#** (`ms-dotnettools.csharp`) | 語言支援 |

---

## 2. VS Code 組態說明

「執行與偵錯」面板提供下列組態（定義於 `.vscode/launch.json`）：

| 配置名稱 | 適用情境 |
|---------|---------|
| **偵錯 Agent Service + Wingman** | 編譯並偵錯 .NET Agent Service，同時用 `start-all.ps1 -DesktopOnly` 啟動 Tauri/Vite 桌面程式 |
| **偵錯 Agent Service** | 只由 VS Code 編譯並偵錯 .NET 服務 |
| **附加至 Agent Service（已在執行）** | 從程序清單附加到已由 `start-all.ps1` 啟動的 `AgentService` |
| **啟動 Wingman（start-all.ps1）** | 只執行 `start-all.ps1 -DesktopOnly` |
| **啟動 Agent Service（start-all.ps1）** | 只執行 `start-all.ps1 -AgentOnly` |
| **啟動全部（start-all.ps1）** | 執行不帶參數的 `start-all.ps1`，開啟 Agent Service 與 Wingman |

---

## 3. 偵錯 Agent Service 與啟動 Wingman（推薦）

> 使用此方法時，請先關閉其他已執行的 Agent Service，避免 API 連接埠衝突。

**步驟：**

1. 按 `Ctrl+Shift+D` 開啟「執行與偵錯」面板

2. 在頂部下拉選單選擇 **「偵錯 Agent Service + Wingman」**

3. 按 `F5` 啟動

VS Code 會自動執行以下步驟：
- 編譯 `AgentService.csproj`
- 啟動 Agent Service 並附加 .NET debugger
- 呼叫 `scripts/dev/start-all.ps1 -DesktopOnly`
- 自動選擇可用的 Vite 開發連接埠並開啟 Tauri Wingman 視窗

若只需要其中一個程序，可改選 **「偵錯 Agent Service」** 或
**「啟動 Wingman（start-all.ps1）」**。

---

## 4. 附加到已執行的 Agent Service

> 使用此方法不需停止服務，適合服務已在運行時臨時加入 debug。

**步驟：**

1. 從「執行與偵錯」選擇 **「啟動全部（start-all.ps1）」**，或在終端機執行：

   ```powershell
   .\scripts\dev\start-all.ps1
   ```

2. 按 `Ctrl+Shift+D` 開啟「執行與偵錯」面板

3. 在頂部下拉選單選擇 **「附加至 Agent Service（已在執行）」**

4. 按 `F5`，在程序清單中選擇 `AgentService.exe`

除了偵錯面板，也可以按 `Ctrl+Shift+P` 執行 **Tasks: Run Task**，再選擇：

- `start: agent-service`
- `start: wingman`
- `start: all`

---

## 5. 設定中斷點

在程式碼行號左側點一下即可設定中斷點（紅點 🔴）。

按 `F5` 後，當程式執行到該行會自動暫停，可以：

| 快捷鍵 | 功能 |
|--------|------|
| `F5` | 繼續執行 |
| `F10` | 單步執行（不進入函式） |
| `F11` | 單步進入（進入函式內部） |
| `Shift+F11` | 跳出目前函式 |
| `Ctrl+Shift+F5` | 重新啟動 |
| `Shift+F5` | 停止偵錯 |

---

## 6. 聊天室訊息的呼叫鏈

當使用者在聊天室送出訊息時，程式的執行順序如下。建議依序在以下位置設定中斷點：

### ① HTTP 入口（最先到達）

**檔案：** `apps/agent-service/src/Host/RestEndpoints/ConversationEndpoints.cs`

**位置：** `SendMessage` 方法（`private static async Task SendMessage(...)`）

```
POST /api/conversations/{id}/messages
↓
ConversationEndpoints.SendMessage()   ← 在此設中斷點，可查看完整 request 內容
```

在此可以查看：
- `request.UserMessage`：使用者輸入的訊息
- `request.ProjectId`：關聯的專案
- `request.AgentMode`：Agent 模式（Plan / Auto / FullAuto）

---

### ② 建立執行記錄

同一個檔案往下，`run.Status = RunStatus.Running` 附近可查看 Run 物件的完整內容。

---

### ③ AI 串流推論

**檔案：** `apps/agent-service/src/Infrastructure/AgentFramework/WingmanChatAgent.cs`

**位置：** `RunStreamingAsync` 方法

```
WingmanChatAgent.RunStreamingAsync()  ← 在此設中斷點，可查看送給 LLM 的 Prompt
```

在此可以查看：
- `effectiveMessage`：組合後的完整 Prompt（含 workspace context）
- `profile`：使用的 AI 供應商設定
- `history`：對話歷史

---

## 7. 常見問題排除

### Q: 找不到「偵錯 Agent Service」選項？

**A:** 確認已安裝 **C# Dev Kit** 擴充套件，並用 `Ctrl+Shift+D` 開啟「執行與偵錯」面板（不是編輯器右上角的播放按鈕）。

---

### Q: Build 失敗，提示 DLL 被其他程序鎖定？

**A:** 已有 AgentService 在執行，先停止它：
```powershell
Stop-Process -Name "AgentService" -Force
```

---

### Q: 一直出現 `Neo4j.Driver.ServiceUnavailableException`？

**A:** 開發時不需要 Neo4j，在 `appsettings.Development.json` 已設定停用：
```json
{
  "Neo4jLifecycle": {
    "Mode": "disabled"
  }
}
```
若仍出現，確認是否使用最新 build（重新按 `F5`）。

---

### Q: Debugger 在已被 `catch` 的例外上暫停（如測試中的預期例外）？

**A:** 在「執行與偵錯」面板底部的 **BREAKPOINTS** 區塊，取消勾選
**「Common Language Runtime Exceptions」**。

---

### Q: 附加模式找不到 AgentService 程序？

**A:** 確認 `start-all.ps1` 已成功啟動，用以下指令確認：
```powershell
Get-Process -Name "AgentService"
```

若是從 `dotnet run` 啟動而只看到 `dotnet.exe`，請在 VS Code 的程序選擇器中搜尋
`AgentService.dll` 的命令列。
