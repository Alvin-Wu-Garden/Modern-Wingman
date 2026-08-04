# Modern Wingman 專案健康度重構驗收報告

日期：2026-08-04
分支：`codex/project-health-refactor`
基準 HEAD：`f7e045a239e144a250bbe6edf9f95982ce590ff6`

## 1. 實作摘要

本次依 `docs/modern-wingman-project-health-refactor-spec.md` 完成主要 P0／P1／P2
重構，目標是降低專案問答等待、避免 Graph／Neo4j 阻斷、減少原始碼工具 I/O、改善
Provider／Model 與 Knowledge Graph UI，並維持既有 REST／SSE、PAT、GraphRAG V4、
Jira fixture 與唯讀專案工具契約。

### 已完成範圍

- Phase 1：固定 `4173`、`5002`、`17688`；啟動／停止改用 ownership manifest、PID、
  StartTime 與執行檔路徑驗證，移除 `D:\CargoTarget` 依賴。
- Phase 2：專案問答加入 Graph Ready／Stale／Unavailable；Graph 探測與問答隔離；
  Graph 失效時仍提供原始碼工具；`done` 不等待標題生成。
- Phase 3：檔案 catalog、原始碼與 Roslyn 快取、取消、in-flight dedup、工具硬上限、
  Neo4j query concurrency 4、BFS layer batching。
- Phase 4：Provider API 合併狀態，移除前端 key-status N+1；模型清單取消、錯誤重試與
  cache；Provider／Model picker 窄視窗改善。
- Phase 5：Graph 初始節點上限 200、載入更多、窄視窗 overlay、Markdown table theme
  token、Progress 勾選／折疊、Community Summary toast 成功／失敗／關閉行為。
- Phase 6：限制 CORS、TLS revocation check、Tauri CSP、`AllowedHosts` loopback、
  Development log level、清理空殼 package 與 tracked generated artifacts，更新 README
  與 debug guide，加入 tracked core regression project。

## 2. 自動化驗收結果

以下命令均以順序執行，避免同一個 AgentService 輸出檔被平行 compiler lock 住：

| Gate | 結果 |
|---|---|
| `dotnet build apps/agent-service/AgentService.csproj --configuration Debug` | PASS；0 warning、0 error |
| `dotnet test apps/agent-service/tests/AgentService.CoreRegression/AgentService.CoreRegression.csproj --configuration Debug --no-restore` | PASS；2/2 |
| `dotnet test apps/UnitTests/AgentService.UnitTests.csproj --configuration Debug --no-restore` | PASS；200/200 |
| `pnpm typecheck` | PASS；contracts、ui-kit、desktop |
| `pnpm --filter @modern-wingman/desktop build` | PASS；Vite 既有大 chunk warning，未新增 error |
| `cargo check --manifest-path apps/desktop/src-tauri/Cargo.toml` | PASS |
| JSON parse：launch、tasks、appsettings、Tauri config | PASS |
| PowerShell AST：啟動／停止／Graph acceptance scripts | PASS |
| `git diff --check` | PASS；僅有 Git 的 LF／CRLF 警告 |

## 3. 尚未能在本工作階段安全自動化的驗收

以下項目需要使用者正在使用的 FBL Graph、Neo4j 與實際 Tauri 視窗，不能以靜態建置
結果代替：

1. RT1～RT6 的實際 F5／Shift+F5 全生命週期與重複啟動測試；目前 `4173` 與 Neo4j
   listener 已有使用中的程序，因此未強制停止未知或使用者程序。
2. QA1～QA10 的端對端專案問答，包括 `140078資料流`、Graph unavailable fallback、
   title 延遲與 watcher overflow。
3. 696 個菜單鏈路與現有 Graph Golden 的精度／wall-clock 比對；本分支沒有重新產生
   FBL Graph acceptance fixture，避免在不明確資料版本上宣稱精度通過。
4. PF1～PF10 的同機器五次 warm-up／median／p95 量測；目前完成的是邏輯與 bounded
   cache／concurrency 實作，未虛構量測數字。
5. UI1～UI8 與 800x600、1024x768、1280x800、1920x1080 三主題人工 smoke，以及
   Tauri production CSP smoke。

## 4. 產物與測試資料

- `artifacts/graphrag-v4-quality/` 的 tracked JSON／TRX／diagnostic 產物已移除，並以
  `/artifacts/` 忽略後續生成檔。
- `apps/agent-service/tests/AgentService.CoreRegression/` 為可由乾淨 clone 還原的核心
  回歸測試；測試資料只建立在工作區 `temp` 下並於 finally 清理。
- `temp/jira-samples/` 保留，因為它是 Development 設定使用的功能性 Jira fixture。
- `temp/validation-bin/` 與 `temp/validation-obj/` 是前次本機驗證殘留的 ignored 目錄，
  不會進入 Git；可在停止所有本機測試程序後由使用者手動刪除。

## 5. Code Review 結論

- 沒有新增第三方 runtime dependency。
- 沒有改變 PAT／Credential 儲存格式或把密鑰寫入 log／artifact。
- Project tools 仍限制在專案根目錄、唯讀，並有檔案大小、行數、取消與單輪工具上限。
- Graph 只作為可降級證據來源；原始碼工具仍可在 Graph unavailable 時回答。
- 新增／修改的核心邏輯皆附繁體中文說明；已刪除無效 root `lint` 宣告、空殼 package、
  舊啟動參數文件與 tracked generated artifacts。

結論：自動化建置、型別、核心回歸、既有 200 個本機測試與靜態安全檢查均通過；上述
環境依賴驗收需在使用者確認可中斷目前服務並提供實際 Graph 版本後，才能宣告 Phase 7
的端對端精度／UI／效能 gates 全部通過。
