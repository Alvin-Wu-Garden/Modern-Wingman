# Modern Wingman — AgentService Debug 教學手冊

## 目錄

1. [前置需求](#1-前置需求)
2. [Debug 模式說明](#2-debug-模式說明)
3. [方法一：從 VS Code 直接啟動（推薦）](#3-方法一從-vs-code-直接啟動推薦)
4. [方法二：附加到已執行的程序](#4-方法二附加到已執行的程序)
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

## 2. Debug 模式說明

專案提供兩種 debug 配置（定義於 `.vscode/launch.json`）：

| 配置名稱 | 適用情境 |
|---------|---------|
| **Debug AgentService** | 由 VS Code 直接編譯並啟動，適合一般開發 debug |
| **Attach to AgentService (已在執行)** | 附加到已由 `start-all.ps1` 啟動的程序，不重新啟動服務 |

---

## 3. 方法一：從 VS Code 直接啟動（推薦）

> 使用此方法時，請確保沒有其他 AgentService 在執行（避免 port 5200 衝突）。

**步驟：**

1. 停止已在執行的服務（若有）：
   ```powershell
   Stop-Process -Name "AgentService" -Force
   ```

2. 按 `Ctrl+Shift+D` 開啟「執行與偵錯」面板

3. 在頂部下拉選單選擇 **「Debug AgentService」**

4. 按 `F5` 啟動

VS Code 會自動執行以下步驟：
- 編譯 `AgentService.csproj`
- 啟動服務（監聽 `http://localhost:5200`）
- 附加 debugger

---

## 4. 方法二：附加到已執行的程序

> 使用此方法不需停止服務，適合服務已在運行時臨時加入 debug。

**步驟：**

1. 先執行 `start-all.ps1` 啟動所有服務：
   ```powershell
   cd scripts\dev
   .\start-all.ps1
   ```

2. 按 `Ctrl+Shift+D` 開啟「執行與偵錯」面板

3. 在頂部下拉選單選擇 **「Attach to AgentService (已在執行)」**

4. 按 `F5`，VS Code 會列出程序清單，選擇 `AgentService` 即可

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

### Q: 找不到「Debug AgentService」選項？

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
