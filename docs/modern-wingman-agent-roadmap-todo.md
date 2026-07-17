# Modern Wingman Agent 工具強化 TODO List

> 文件日期：2026-07-11  
> 文件狀態：實作與驗收中  
> 目標：在保留現有多供應商對話、專案解析、知識圖譜、Skills/MCP 管理、離線語音與審計功能的前提下，將 Modern Wingman 強化為可安全執行完整開發工作的本機 Agent 工具。

## 0. 執行原則

- [x] 本文件先供最終審視，未取得「開始實作」指示前不得修改功能程式碼。
- [x] 實作必須沿用現有 React + Tauri + .NET Agent Service + MAF 架構。
- [x] 嚴禁破壞現有對話、供應商、專案解析、Neo4j、Skills、MCP、STT 與 SQLite 功能。
- [x] 每個階段開始前確認受影響模組與資料庫 migration。
- [x] 每個階段完成後執行單元測試、整合測試、前端 typecheck/build 與既有功能回歸測試。
- [x] Migration 必須向前相容，不刪除既有資料，不重建或覆寫現有 `wingman.db`。

## 1. 已確認的產品決策

### 1.1 Agent 模式

- [x] 支援「詢問」模式。
- [x] 支援「規劃」模式。
- [x] 支援「Auto」模式。
- [x] 支援「完全自動」模式。
- [x] 新專案預設使用「規劃」模式。
- [x] 規劃核准後轉入 Auto，不直接轉入完全自動。
- [x] 每個專案保存最後使用的 Agent 模式。
- [x] Agent 模式與工作區、版控、分支、網路狀態必須分開呈現。

### 1.2 版控與工作區

- [x] Git 專案的執行型 Agent Run 預設使用 Git worktree。
- [x] SVN 專案使用 Shadow Git 建立可隔離的 Git worktree。
- [x] 無版控專案可直接工作，也可建立執行前快照。
- [x] 支援多組 Git/Bitbucket/SVN Connection Profile。
- [x] 支援 Git clone、fetch、pull、checkout、branch、commit、push。
- [x] 支援 SVN browse、checkout、switch、update、add、delete、move、commit。
- [x] 不需要建立 Pull Request。
- [x] 不需要 SVN branch/tag 建立功能；先支援選擇與切換既有路徑。
- [x] SVN 專案沒有 externals，不需納入第一階段。
- [x] Bitbucket 為公司內網自架 Bitbucket Server/Data Center。
- [x] Bitbucket 使用 HTTPS、帳號與 Access Token。
- [x] 不需要 SSH Key、Proxy、NTLM、Kerberos。
- [x] 支援每個連線 Profile 個別忽略 SSL 驗證。
- [x] Portable Git 與 SVN CLI 隨 Modern Wingman 發版，不在企業環境自行更新。
- [x] 版控帳號、密碼與 Token 第一階段允許明文存入 `wingman.db`。

### 1.3 Commit/Push 規則

- [x] Auto 模式可自動 commit，push 與 SVN commit 必須核准。
- [x] 完全自動模式可對非受保護分支自動 commit/push 或 SVN commit。
- [x] `main`、`master`、`develop`、`release/*` 預設為 Git 受保護分支。
- [x] SVN `trunk`、`tags/*` 預設為受保護路徑。
- [x] 受保護分支或路徑不得在無核准狀態下寫回遠端。
- [x] Git commit identity 優先使用 Repository 設定，缺少時使用 Connection Profile 設定。
- [x] 支援建立新 Git branch，預設命名格式為 `wingman/<task-slug>`。

### 1.4 網路與 Shell

- [x] Agent 必須具備完整本機開發能力。
- [x] 支援 PowerShell 與受控子行程執行。
- [x] Agent 開發工作允許網路存取，不將「網路開放」誤當成 Agent 模式。
- [x] 即使網路開放，命令、工具、目的地、耗時與結果仍必須可審計。

## 2. 統一 Agent Run 核心

- [x] 統一一般對話、專案解析問答與 Explore -> Plan -> Code -> Verify 工作流的 Run 模型。
- [x] 定義 Thread、Conversation、Run、RunStep、RunEvent、Approval、ChangeSet 的責任與關聯。
- [x] 讓不同 AI Provider 共用同一套 Agent Run 生命週期。
- [x] 移除「一般聊天走 MAF、程式修改只走 Copilot Workflow」造成的能力落差。
- [x] BYOK、OpenRouter、OpenAI、Anthropic、Azure、Custom、Copilot 路徑使用一致的工具與事件介面。
- [x] Run 支援 created、running、waiting_approval、paused、completed、failed、cancelled 狀態。
- [x] Run 支援暫停、繼續、取消與服務重啟後恢復。
- [x] Run 支援從最後一個安全步驟重試。
- [x] Run 支援更換模型後繼續，不重複已完成的有副作用操作。
- [x] 將現有 `run:token`、`run:tool-call`、`run:tool-result`、`run:phase`、`run:plan`、`run:verify` 正式接入桌面端。
- [x] 將背景 fire-and-forget 工作改為可持久化、可追蹤的執行協調機制。

## 3. 四種 Agent 模式與 Policy Engine

### 3.1 模式能力

- [x] 詢問模式：允許讀檔、搜尋、專案圖譜查詢與唯讀版控操作。
- [x] 詢問模式：禁止修改檔案、commit、push、SVN commit 與有副作用工具。
- [x] 規劃模式：允許讀取、分析、環境偵測與產生實作計畫。
- [x] 規劃模式：不得建立 worktree 或開始修改，直到使用者核准計畫。
- [x] 規劃核准後建立隔離工作區並轉入 Auto。
- [x] Auto 模式：允許工作區內修改、建置、測試與低風險命令。
- [x] Auto 模式：允許自動 commit；push 與 SVN commit 必須核准。
- [x] 完全自動模式：允許完整執行非受保護分支工作。
- [x] 完全自動模式：仍遵守受保護分支、工作區邊界與禁止洩漏憑證規則。

### 3.2 權限與核准

- [x] 建立 `IAgentPolicyEngine`，禁止再以 `PermissionHandler.ApproveAll` 作為正式策略。
- [x] 將操作分成 read、write、execute、network、external-side-effect、destructive。
- [x] 建立低、中、高、極高風險分級。
- [x] 支援 approve_once、approve_for_run、approve_for_workspace、reject。（後端契約完成，UI 先支援 once）
- [x] 高風險操作顯示完整命令、工作目錄、原因與可能影響。
- [x] 刪除檔案、資料庫 migration、工作區外寫入、受保護分支 push 必須提高風險等級。
- [x] MCP 有副作用工具也必須通過相同 Policy Engine。
- [x] 核准決策與執行結果寫入 Audit Event。
- [x] 為未來管理員鎖定模式與權限上限保留 Policy Profile 擴充點。

## 4. Tool Runtime

### 4.1 統一工具註冊

- [x] 建立 `IToolRegistry` 與標準 Tool Descriptor。
- [x] Tool Descriptor 包含名稱、說明、輸入 Schema、風險、權限、逾時與來源。
- [x] 所有 Tool Call 經過 Policy Engine、Telemetry 與輸出遮罩。
- [x] 工具執行結果使用統一狀態與錯誤格式。
- [x] 防止模型直接拼接任意命令繞過 Tool Runtime。

### 4.2 第一批內建工具

- [x] `read_file`
- [x] `list_directory`
- [x] `search_files`
- [x] `read_file_range`
- [x] `apply_patch`
- [x] `delete_file`
- [x] `run_command`
- [x] `run_build`
- [x] `run_test`
- [x] `git_status`
- [x] `git_diff`
- [x] `git_branch`
- [x] `git_commit`
- [x] `git_push`
- [x] `svn_status`
- [x] `svn_diff`
- [x] `svn_commit`
- [x] `read_skill`
- [x] `run_skill_script`
- [x] `call_mcp_tool`
- [x] `query_code_graph`
- [x] `analyze_impact`

### 4.3 子行程執行器

- [x] 建立受控 Process Runner，統一執行 PowerShell、Python、Node.js、Git、SVN 與建置工具。
- [x] 限制 working directory 並驗證實際解析路徑。
- [x] 支援 timeout、取消與終止完整子行程樹。
- [x] 限制 stdout/stderr 最大容量並支援串流。
- [x] 禁止跳出互動式視窗。
- [x] 使用環境變數白名單，避免把所有 API Key/Token 傳給子行程。
- [x] 命令與輸出執行敏感資訊遮罩。
- [x] 記錄 executable、arguments hash、cwd、exit code、duration 與 timeout。

## 5. Skills 完整執行能力

### 5.1 Skill 載入與一致性

- [x] 修正 Agent Service 的 SKILL.md frontmatter parser，支援多行 `description: |`、`|-`、`>`、`>-`。
- [x] Skill 中央庫同步後自動通知 Agent Service Refresh，不需重啟服務。
- [x] BYOK 與 Copilot Agent 都能取得完整 SKILL.md，不只看到 Skill 清單。
- [x] Skill 掃描錯誤、版本不相容與缺少 Runtime 要在 UI 顯示。
- [x] 保持 progressive disclosure：初始只載入名稱與描述，需要時再讀全文。

### 5.2 Skill Runtime Manifest

- [x] 定義可選的 `wingman.yaml`，不破壞只有 SKILL.md 的既有 Skill。
- [x] Manifest 支援 Python、Node.js、PowerShell Runtime 類型。
- [x] Manifest 支援 Runtime 版本範圍，例如 Python `>=3.10 <3.13`。
- [x] Manifest 支援 entrypoints、允許腳本、參數 Schema、工作目錄與逾時。
- [x] Manifest 支援 Python requirements/lockfile 與 Node package manager/lockfile 宣告。
- [x] Manifest 支援是否需要網路、是否需要使用者核准與所需環境變數名稱。
- [x] 未提供 Manifest 的 Skill 預設只允許讀取說明，不自動執行任意腳本。

### 5.3 Runtime Resolver

- [x] 建立 `IRuntimeResolver`。
- [x] 優先偵測專案 `.venv/Scripts/python.exe`。
- [x] 偵測 `py` launcher 與系統 Python。
- [x] 偵測專案 Node.js、系統 Node.js 與 Wingman 管理 Runtime。
- [x] 支援 Wingman 管理的 Python/Node.js Runtime 目錄。
- [x] 執行前驗證 Runtime 版本是否符合 Skill Manifest。
- [x] 找不到相容 Runtime 時停止並顯示明確解決方式，不可盲目執行。
- [x] 設定頁顯示可用 Runtime、版本、來源與健康狀態。
- [x] 規劃離線匯入 Runtime 與套件快取能力，符合企業內網環境。
- [x] 決定第一版隨程式打包的 Python/Node.js 預設版本與授權清單。

### 5.4 Skill Script Runner

- [x] 建立 `ISkillScriptRunner` 與 `run_skill_script` Agent Tool。
- [x] 只允許執行已同步至 Modern Wingman Agent 的 Skill。
- [x] 只允許執行 Manifest 宣告的相對路徑。
- [x] 防止 `..`、junction、symlink 造成路徑穿越。
- [x] 依副檔名與 Manifest 選擇 Python、Node.js 或 PowerShell Runtime。
- [x] 支援參數 Schema 驗證，避免直接拼接 shell 字串。
- [x] 支援 dependency preflight 與缺少依賴診斷。
- [x] 安裝依賴前套用 Agent 模式與核准政策。
- [x] 支援 stdout/stderr 串流、timeout、取消與 exit code。
- [x] 將 Skill ID、腳本、Runtime、輸入/輸出 hash 與核准結果寫入審計。
- [x] Skill 風險掃描結果要影響允許的執行策略。

## 6. MCP Runtime 補強

- [x] 將現有 MCP Registry 從設定同步提升為 Modern Wingman 可直接呼叫的 MCP Client Runtime。
- [x] 支援 stdio、SSE、HTTP transport 的實際連線與健康檢查。
- [x] 啟動時或按需探索 MCP tools 與輸入 Schema。
- [x] 將 MCP tools 動態註冊到 `IToolRegistry`。
- [x] 顯示 MCP Server、Tool、read-only/side-effect 風險與連線狀態。
- [x] MCP 呼叫經過 Agent 模式、Policy Engine 與 Approval。
- [x] MCP Server 的 command、args、env 進行路徑與敏感資訊檢查。
- [x] MCP 啟動失敗不得拖垮整個 Agent Run。
- [x] 支援 timeout、取消、重連與 stderr 診斷。
- [x] MCP Tool Call 寫入 `ai_tool_call_logs`。

## 7. Git、Bitbucket 與 SVN

### 7.1 資料模型

- [x] 新增 `vcs_connection_profiles`。
- [x] 新增 `vcs_credentials`。
- [x] 新增 `project_vcs_bindings`，關聯既有 Projects，不重複建立專案主資料。
- [x] 新增 `vcs_protected_refs`。
- [x] 新增 `vcs_operations`。
- [x] 為 Credential 預留 `encryption_scheme`，第一版值為 plaintext。
- [x] API 只回傳 `hasSecret`，不得回傳密碼或完整 Token。
- [x] 對既有 Local Project 保持相容，VCS binding 可為 null。

### 7.2 Connection Profile

- [x] 支援多組 Bitbucket Git Profile。
- [x] 支援多組 SVN Profile。
- [x] 支援 Profile 名稱、Base URL、帳號、Token/密碼與啟用狀態。
- [x] 支援測試連線並保存最後測試狀態與時間。
- [x] 支援每個 Profile 個別設定 SSL 驗證開關。
- [x] 忽略 SSL 時顯示高風險警告並寫入審計。
- [x] 不修改使用者全域 Git/SVN SSL 設定。
- [x] Token 不放入 remote URL、Git config、命令參數或 Log。

### 7.3 Portable Git/SVN

- [x] 封裝 Windows Portable Git。
- [x] 封裝 Windows SVN CLI 與必要 DLL。
- [x] Runtime 選擇順序：設定指定路徑 -> 系統 PATH -> Wingman 內建 Runtime。
- [x] 設定頁顯示實際使用來源與版本。
- [x] Portable Runtime 隨正式版本更新，不自行聯網更新。
- [x] 納入第三方授權與 NOTICE 文件。
- [x] 開發版與正式版驗證 tools 複製與 publish 路徑。

### 7.4 Git 功能

- [x] Bitbucket HTTPS Connection Test。
- [x] 使用 `git ls-remote` 取得遠端分支。
- [x] 支援 clone 指定分支與目的路徑。
- [x] 支援 fetch、pull、checkout、switch。
- [x] 支援建立 `wingman/<task-slug>` 新分支。
- [x] 支援 status、diff、commit、push。
- [x] 使用 AskPass 或安全暫時環境傳遞 Token。
- [x] Commit identity 優先 Repository，其次 Profile。
- [x] 套用受保護分支規則。
- [x] Push 前偵測 remote branch 是否前進，避免非 fast-forward 覆蓋。
- [x] 禁止未經明確策略執行 force push。

### 7.5 Git Worktree

- [x] Agent 修改型 Run 建立專屬 worktree。
- [x] 規劃模式核准前不得建立 worktree。
- [x] 工作區有未提交變更時顯示基準來源選擇。
- [x] 支援以 HEAD 為基準。
- [x] 支援將目前未提交變更帶入隔離工作區。
- [x] Run 保存 source workspace、worktree path、branch 與 base commit。
- [x] 完成後支援套用、保留、放棄或推送。
- [x] 清理前確認變更已套用或使用者已明確放棄。
- [x] 啟動時偵測並處理前次異常中斷留下的 worktree。

### 7.6 SVN 與 Shadow Git

- [x] 支援標準 `trunk/branches/tags` 分支選單。
- [x] 支援非標準 Repository 路徑瀏覽器。
- [x] 支援 checkout、update、switch、status、diff、add、delete、move、commit。
- [x] 建立 SVN 主工作副本，不在其中直接執行 `git init`。
- [x] 建立獨立 Shadow Git Repository，且不得設定 remote。
- [x] Shadow Git baseline 記錄 SVN URL、path 與 revision。
- [x] 從 Shadow Git 建立每個 Agent Run 的 worktree。
- [x] Shadow Git 不納入 `.svn` metadata。
- [x] Agent 完成後將 ChangeSet 套回 SVN 主工作副本。
- [x] 將新增、刪除、重新命名轉換成正確 SVN 操作。
- [x] 保留 SVN properties、binary 檔案與空目錄語意。
- [x] 套用前確認遠端 SVN revision 是否改變。
- [x] Revision 改變時先 update 並執行三方合併。
- [x] 衝突時停止並進入 waiting_approval/conflict 狀態，不可直接覆蓋。
- [x] Auto 模式 SVN commit 必須核准。
- [x] 完全自動模式仍遵守 trunk/tags 受保護規則。

### 7.7 無版控專案

- [x] 支援直接選擇既有本機資料夾，保持現有流程。
- [x] Agent 修改前建立檔案快照或 ChangeSet baseline。
- [x] 支援放棄變更與回復至執行前狀態。
- [x] 對大型專案提供排除資料夾與快照容量上限。
- [x] 無法完整回復時必須在執行前明確提示。

## 8. Changeset、Diff、Checkpoint 與復原

- [x] 每個修改型 Run 建立 baseline。
- [x] 保存新增、修改、刪除、重新命名檔案清單。
- [x] 產生 Unified Diff 與 binary change metadata。
- [x] 每個 Agent 修復迭代建立 checkpoint。
- [x] 支援整個 Run 回復。
- [x] 支援逐檔案或逐 hunk 套用。
- [x] 回復不得覆蓋 Run 開始後由使用者產生的新修改。
- [x] Git worktree 支援保留為 branch。
- [x] SVN Shadow Git 支援將 ChangeSet 套回主工作副本。
- [x] ChangeSet 與驗證結果關聯，清楚顯示哪些修改已通過測試。

## 9. Context Engineering

- [x] 支援 `@file` 加入指定檔案。
- [x] 支援 `@folder` 加入目錄範圍。
- [x] 支援加入目前 Git/SVN diff。
- [x] 支援加入 IDE 選取程式碼的擴充介面。
- [x] 支援文字、圖片與文件附件。
- [x] 專案知識圖譜、Repo Map 與 Impact Analysis 可作為 Agent Context Tool，而非一律塞入 Prompt。
- [x] 支援根目錄與巢狀 AGENTS.md 指示優先順序。
- [x] 顯示目前 Context 來源與估計 token 用量。
- [x] 支援 Context 自動壓縮與摘要。
- [x] 保存壓縮前後關聯，避免重啟後遺失關鍵決策。
- [x] 對網頁、Skill、MCP 回傳與 Repository 文件加入 Prompt Injection 防護標記。

## 10. Provider 韌性與任務完成率

- [x] 將既有 provider timeout、attempt 與 telemetry 資料真正用於重試決策。
- [x] 支援 first-token、idle-stream、total-request 分類重試。
- [x] 支援同模型重試、同供應商換模型與跨供應商 fallback 策略。
- [x] 防止有副作用的 Tool Call 因模型重試而重複執行。
- [x] 顯示實際 resolved provider/model，而不只顯示使用者要求值。
- [x] 建立模型健康度、近期成功率、平均回應時間與 timeout 指標。
- [x] 使用者可在失敗卡片選擇重試、換模型重試或從步驟繼續。
- [x] Provider fallback 與額外花費必須可配置並寫入審計。

## 11. Observability 與企業審計

- [x] 將所有 Agent Run 關聯到 trace ID。
- [x] 將實際 Tool、MCP、Skill、Git、SVN 操作寫入 `ai_tool_call_logs` 或對應審計表。
- [x] 實作 approval_required、approval_result、duration、status 與 sanitized error。
- [x] 記錄 Agent mode、workspace strategy、provider、model、branch/revision。
- [x] 記錄 SSL 驗證是否關閉。
- [x] 記錄 commit/push/SVN commit 前後 revision。
- [x] 建立審計查詢 API。
- [x] 建立審計 UI，可依日期、專案、Run、Provider、Tool、狀態篩選。
- [x] 支援匯出 JSON/CSV，且預設遮罩敏感資訊。
- [x] 設定保留期限與清理策略。
- [x] 補上目前已有資料表但尚未串接的 Tool Call 寫入流程。

## 12. Desktop UI/UX

### 12.1 Agent Workbench

- [x] 將傳統聊天頁提升為 Agent Workbench。
- [x] 左側顯示工作區、專案、Thread 與近期 Run。
- [x] 中央顯示對話、計畫、工具執行與驗證 Timeline。
- [x] 右側顯示 Context、Changed Files、Diff、Terminal Output、Approvals。
- [x] 右側面板可收合，保留小螢幕可用性。
- [x] Agent 模式使用獨立 segmented control：詢問／規劃／Auto／完全自動。
- [x] 工作區、版控、分支與網路使用獨立狀態列，不與模式名稱混合。
- [x] 規劃模式顯示計畫審查卡與「核准並進入 Auto」。
- [x] 顯示 Run 目前階段：Explore、Plan、Code、Verify、Done。

### 12.2 Tool 與 Approval 顯示

- [x] Tool Call 使用可收合卡片，不混入一般 Markdown 回應。
- [x] 命令卡顯示 executable、參數、cwd、耗時、exit code。
- [x] stdout/stderr 支援展開、複製與搜尋。
- [x] Skill 卡顯示 Skill、Runtime、腳本與風險。
- [x] MCP 卡顯示 Server、Tool、輸入摘要與副作用等級。
- [x] Approval 卡顯示原因、風險、影響與核准選項。（目前支援允許一次/拒絕）
- [x] 錯誤卡提供重試、換模型、查看審計與從步驟繼續。

### 12.3 Diff 與完成審查

- [x] Changed Files 列表依新增、修改、刪除、重新命名分組。
- [x] Diff 支援 side-by-side 與 unified view。
- [x] 支援逐檔案／逐 hunk 接受或排除。
- [x] 顯示 Build、Test、Lint 結果。
- [x] 顯示 Commit Message 預覽與編輯。
- [x] Push/SVN Commit 前顯示目標遠端與受保護規則。
- [x] Run 完成頁提供套用、保留、放棄、Commit、Push 等明確動作。

### 12.4 設定頁

- [x] 將長頁面拆成一般、AI 供應商、Agent 與權限、Runtime、版本控制、Skills、MCP、語音、資料與審計分類。
- [x] 新增四種 Agent 模式說明與每專案預設設定。
- [x] 新增 Version Control Connection Profile 管理。
- [x] 新增 Portable Git/SVN Runtime 狀態。
- [x] 新增 Python/Node.js Runtime 狀態。
- [x] 新增 Workspace、worktree、Shadow Git 預設路徑。
- [x] 三種路徑均可設定全域預設，建立專案時可另行覆寫。
- [x] 新增 Commit identity 與受保護分支規則。
- [x] SSL 忽略設定使用明確警告，不做隱藏式全域關閉。

### 12.5 新增專案流程

- [x] 新增專案使用「本機資料夾／Git-Bitbucket／SVN」三個 Tab。
- [x] Git 流程支援 Profile、Repository URL、Branch、新分支與目的路徑。
- [x] SVN 流程支援 Profile、Repository Browser、標準分支與任意路徑。
- [x] Clone/checkout 顯示即時進度、取消與錯誤診斷。
- [x] 成功取得專案後沿用現有索引流程，不重複新增專案。
- [x] 專案列表顯示 VCS 類型、branch/path、revision 與 dirty 狀態。

### 12.6 Skills UI

- [x] Skill 詳情顯示是否只有 Prompt 或可執行腳本。
- [x] 顯示需要的 Python/Node.js 版本與依賴。
- [x] 顯示 Runtime Ready／Missing／Incompatible。
- [x] 顯示 Script、Network、Credential 等能力標籤。
- [x] 安裝或同步前顯示風險掃描與執行權限。
- [x] 提供重新掃描與重新載入按鈕。

## 13. 安全與敏感資料

- [x] 雖採明文 DB，所有 VCS secrets 必須集中於 `vcs_credentials`，不得散落其他表。
- [x] 前端、REST/gRPC response、Telemetry、Exception、Command Preview 不得出現完整 Secret。
- [x] Git AskPass 暫存檔執行後立即清理。
- [x] 子行程環境只注入當次操作需要的 Credential。
- [x] SSL 忽略只作用於指定 Profile 與當次子行程。
- [x] 禁止寫入全域 `http.sslVerify=false` 或永久 SVN trust 設定。
- [x] 防止 path traversal、symlink/junction escape 與工作區外修改。
- [x] 禁止 Agent 讀取 `.git-credentials`、瀏覽器憑證與未授權 Secret 檔案。
- [x] Prompt、Skill、MCP 輸出不可直接提升自身權限。
- [x] 對 force push、recursive delete、系統設定修改建立明確拒絕或強制核准規則。

## 14. 測試與驗收

### 14.1 單元測試

- [x] 四種 Agent Mode Policy 測試。
- [x] Protected Ref matching 測試。
- [x] Runtime Resolver 版本匹配測試。
- [x] Skill Manifest parser 測試。
- [x] Skill path traversal 測試。
- [x] Process Runner timeout/cancel/output limit 測試。
- [x] Credential redaction 測試。
- [x] Git/SVN command argument escaping 測試。
- [x] Shadow Git ChangeSet mapping 測試。
- [x] Run recovery 與 idempotency 測試。

### 14.2 整合測試

- [x] 建立本機 Git remote 測試 clone/branch/worktree/commit/push。
- [x] 建立本機 SVN repository 測試 browse/checkout/switch/commit。
- [x] 驗證 SVN Shadow Git 建立、修改、套回與衝突流程。
- [x] 驗證 Bitbucket/SVN SSL ignore 僅限 Profile。
- [x] 驗證 Python Skill 正常執行、缺少 Runtime、版本不符與 timeout。
- [x] 驗證 Node.js Skill 正常執行、缺少套件與非零 exit code。
- [x] 驗證 MCP stdio/HTTP 連線、timeout 與有副作用核准。
- [x] 驗證 Plan -> Approval -> Auto -> Verify -> Commit 完整流程。
- [x] 驗證 App/Agent Service 重啟後 Run 恢復。

### 14.3 回歸測試

- [x] 多供應商對話與 OpenRouter。
- [x] Provider/Model picker icon 與模型選擇。
- [x] 專案解析問答、Impact Analysis、Repo Map。
- [x] Neo4j read-only Knowledge Graph。
- [x] Skills 市集、中央庫、同步、Preset 與 Agent icon。
- [x] MCP Registry 現有 CRUD 與同步。
- [x] 離線 Speech-to-Text、模型下載/匯入與簡繁轉換。
- [x] 共用 MessageComposer 在新對話與專案解析中的一致性。
- [x] SQLite 開發／正式路徑與既有資料 migration。
- [x] Desktop typecheck、Vite build、Rust build/test、Agent Service build/test。

## 15. 後續階段，不阻擋第一版核心閉環

- [x] Subagent 與平行 Agent Run。
- [x] Background Agent 與排程任務。
- [x] Hook 系統：Tool 前後、檔案修改後、Run 完成前。
- [x] Plugin 打包格式，整合 Skills、MCP、Hooks、Assets。
- [x] Agent Evals：工具選擇正確率、修改成功率、測試通過率、復原成功率。
- [x] 企業管理設定與不可由一般使用者突破的權限上限。
- [x] VCS secrets 從 plaintext 遷移至 Windows CurrentUser DPAPI（保留 plaintext 向後讀取與啟動遷移）。
- [x] Bitbucket Pull Request、SSH、Proxy、NTLM/Kerberos 依已確認需求排除，不納入目前實作。

## 16. 建議實作順序

- [x] Phase 1：統一 Agent Run、四種模式、Policy、Approval、Event persistence。
- [x] Phase 2：Tool Registry、Process Runner、File/Search/Patch/Shell 工具。
- [x] Phase 3：Changeset、Diff、Checkpoint、復原與 Agent Workbench Timeline。
- [x] Phase 4：Skill Manifest、Python/Node.js Runtime Resolver、Script Runner。
- [x] Phase 5：MCP Client Runtime 與 Tool Registry 整合。
- [x] Phase 6：VCS schema、Connection Profile、Portable Git/SVN。
- [x] Phase 7：Git clone/worktree/commit/push 與受保護分支。
- [x] Phase 8：SVN checkout/commit、Shadow Git 與 revision conflict。
- [x] Phase 9：設定頁、新增專案、Diff/Approval/VCS UI。
- [x] Phase 10：Provider fallback、Run recovery、審計 UI、完整回歸與發版驗收。

## 17. 實作前最終審查 Gate

- [x] 使用者確認本 TODO 範圍完整。
- [x] 使用者確認 Phase 順序。
- [x] 確認第一版打包的 Python、Node.js、Portable Git、SVN 版本與授權。
- [x] 確認資料庫 migration 與 plaintext credential 風險已接受。
- [x] 確認受保護分支／SVN 路徑預設規則。
- [x] 確認完全自動模式的非受保護分支 push 行為。
- [x] 取得使用者明確「開始實作」指示後，才可修改功能程式碼。
