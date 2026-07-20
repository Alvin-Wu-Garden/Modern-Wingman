# AI Framework 升級驗證

此專案使用 Microsoft Agent Framework 1.13.0 與 GitHub Copilot Adapter 1.13.0-rc1。
執行下列命令可驗證鎖定的相依圖、Release 建置、單元測試，以及 Windows publish 是否攜帶 bundled Copilot CLI：

```powershell
.\scripts\verify-ai-framework-upgrade.ps1
```

當 Neo4j 與 nopCommerce benchmark 環境已提供時，以下命令會把原本可選的外部驗收改為必要條件：

```powershell
.\scripts\verify-ai-framework-upgrade.ps1 -RequireExternalAcceptance
```

它需要 `WINGMAN_TEST_NEO4J_URI`、`WINGMAN_BENCHMARK_NEO4J_URI` 與
`WINGMAN_BENCHMARK_NOPCOMMERCE_ROOT`。各 Neo4j 帳號、密碼與 database 仍可依既有測試的
環境變數設定；請只在本機或受保護的 CI Secret 中設定，勿寫入設定檔或提交至版本控制。

此命令不會讀取 PAT、`gh auth`、本機 Copilot OAuth 或環境變數中的 AI 金鑰。
它的目標是安全地驗證離線可重現的部分；真實供應商驗證必須由操作者明確提供測試帳號與短期金鑰。

## 必要的人工 smoke test

1. 在未設定任何 PAT／API Key 的乾淨使用者設定下啟動桌面程式。Copilot 狀態必須是 `not_configured`，且不得啟動 Copilot CLI。
2. 設定一組具 Copilot 使用權的 fine-grained PAT，驗證狀態、模型清單、串流文字、Tool approval，以及移除 PAT 後 runtime 停止。
3. 設定一組 OpenAI API Key，以最小提示詞驗證串流、取消、Tool call、429／timeout 的錯誤提示。
4. 關閉最後一個對話與整個桌面程式，確認沒有遺留 Agent Service 或 Copilot 子程序。

## Copilot SDK 相容性限制

`Microsoft.Agents.AI.GitHub.Copilot 1.13.0-rc1` 的已編譯組件參考
`GitHub.Copilot.SDK` assembly `1.0.0.0`。較新的 SDK（例如 NuGet 1.0.7）使用不同的
強式簽名／assembly version，會造成 C# 編譯錯誤。因此本專案讓 Adapter 以 transitive
方式鎖定 SDK 1.0.0；等待 Adapter 發布針對新版 SDK 編譯的版本後，才可安全升級。

目前專案隨附的 Copilot CLI 是 `1.0.65`，而 SDK 1.0.0 的 NuGet target 預設對應 CLI
`1.0.57`。`verify-ai-framework-upgrade.ps1` 會驗證 publish 後的 binary 能執行並回報版本，
但 PAT 驗證、模型清單與串流 Tool call 的真實 smoke test 仍是這個 RC 組合的必要 release
gate；在該驗收通過前，不應宣稱 SDK／CLI 的跨版本二進位相容性已被證實。
