# Modern Wingman 產品路線圖與實作計畫

> 日期：2026-07-07
> 狀態：進行中
> 前置文件：`A_系統架構設計書_AI_Agent_桌面工具.md`、`B_Monorepo_初始骨架設計_AI_Agent_桌面工具.md`

---

## 0. 背景與目標

Modern Wingman 是類 Codex 的桌面 AI Agent（React 19 + Tauri 2 + .NET 10 + MAF 1.9.0 + GitHub Copilot SDK）。
本計畫涵蓋四大工作流（Work Stream），目標是打磨成消費級產品：

| WS | 名稱 | 核心價值 |
|----|------|---------|
| WS1 | Skills/MCP 聚合層 | 像 skill0.io / skills-manager 的統一技能管理，支援 15+ 工具 + Wingman 自身 MAF Agent |
| WS2 | SOLID 重構 | 可維護、易擴展的程式碼基礎 |
| WS3 | 企業程式碼解析 | .NET/Java 靜態分析 → Neo4j GraphRAG → know-how 問答 + 不改 A 壞 B |
| WS4 | Agent 方法論 | Explore→Plan→Code→Verify 已驗證方法論整合 |

**實作順序**：WS1 → WS2 → WS3 → WS4（使用者確認）

### 關鍵決策（使用者確認於 2026-07-07）

1. **Skills 同步目標**：全部主流 15+ 工具；第一波實作 Claude Code / Codex CLI / GitHub Copilot / Cursor 四個 adapter 驗證引擎，其餘以宣告式設定資料擴充。
2. **Neo4j**：App 內建自動管理（首次啟用時下載 Neo4j Community + JRE），設定頁保留外部連線選項。
3. **GraphRAG**：全自動含 LLM 社群摘要（走 Copilot SDK chat completion）。
4. **企業限制**：目前僅 GitHub PAT 可用，LLM 一律走 Copilot SDK；BYOK 介面完整保留，企業未來開通供應商 endpoint 即可直接使用。
5. **Embeddings 對策**：Copilot SDK 無 embeddings API → 設計 `IEmbeddingProvider` 抽象；現階段檢索用 Neo4j full-text index（BM25）+ 圖遍歷；未來可切換雲端 embeddings 或本地 ONNX 模型。

### 額外產品決策（使用者全數採納）

- **P1 FTUE**：加專案 → 自動索引（進度視覺化）→ 自動生成 AGENTS.md → 立即可問答。
- **P2 Impact Analysis 視覺化**：改動前將受影響呼叫鏈畫成圖。
- **P3 Skill 品質保障**：安裝前內容預覽 + 風險提示（prompt injection 防護）。
- **P4 CLI 化**：核心邏輯放共用層，提供 CLI 供 CI / 其他 agent 使用。

---

## 1. WS1 — Skills/MCP 聚合層

### 1.1 中央 Skill Library（資料驅動來源）

- 中央庫位置：`~/.wingman/library/skills/<skill-name>/`（SKILL.md + 附件）
- SQLite（Rust 端沿用 rusqlite）新增資料表：
  - `skill_sources`（id, display_name, repo, skills_root, kind, enabled）— 取代硬編碼 trait，內建 seed 資料（vercel-labs / anthropics / remotion / microsoft-azure / superpowers）
  - `library_skills`（id, name, description, source_kind, source_ref, installed_at, updated_at, content_hash, tags）
  - `skill_agent_links`（skill_id, agent_id, scope[global|project], project_path, sync_mode, synced_at）
- Rust 模組：`library/`（repository + service），指令層薄化。

### 1.2 安裝來源

- GitHub repo（用 PAT，沿用現有 github.rs 抓取邏輯）
- 本地資料夾匯入
- zip / .skill 壓縮檔匯入
- 安裝前風險掃描（P3）：偵測 SKILL.md 中的可疑模式（外部 URL 指令、shell 執行要求、憑證存取字樣），顯示預覽 + 風險等級。

### 1.3 多工具同步引擎（Adapter 宣告式）

- `AgentAdapter` 為資料而非程式碼：`{ id, display_name, global_skills_dir, project_skills_dir, detect_paths, sync_mode_default }`
- 內建 15+ 工具定義：claude-code, codex, github-copilot, cursor, windsurf, cline, roo-code, kilo-code, goose, gemini-cli, amp, opencode, trae, antigravity, grok, wingman（自身）
- 同步策略：Windows 優先 junction/symlink，失敗 fallback copy；記錄實際模式。
- per-skill per-agent 開關；偵測「已安裝但非 Wingman 管理」的 skills（adopt 功能）。

### 1.4 Wingman 自身 MAF Agent 消費 skills

- Agent Service 新增 `ISkillProvider` / `SkillCatalog`：
  - 啟動時載入 `~/.wingman/library` 中 enabled for wingman 的 skills
  - **Progressive disclosure**：system prompt 只注入 name + description 清單
  - 提供 `read_skill(name)` AIFunction 工具，Agent 按需展開全文
- BYOK（ChatClientAgent）與 CopilotDefault 兩路徑都掛載。

### 1.5 MCP Registry

- SQLite 表 `mcp_servers`（id, name, transport[stdio|sse|http], command/url, args, env, enabled, agent_links）
- 同步器：寫入各 IDE 的 mcp 設定檔（`.mcp.json`、`~/.cursor/mcp.json`、VS Code `mcp.json` 等，宣告式路徑模板）
- Wingman 自身：Agent Service 以 MAF `McpClientTool` 掛載 enabled MCP servers。

### 1.6 前端 UI

- SkillsPage 擴充為四分頁：Library（中央庫）/ Agents（各工具 workspace，含 adopt）/ Presets（技能組合一鍵套用）/ MCP（server 管理）
- InstallModal 增加風險預覽步驟（P3）。

### WS1 驗收標準

- [x] `skill_sources` 資料驅動：新增來源不改 Rust 程式碼（DB insert 即可，`add_skill_source` 指令）
- [x] 可從 GitHub / 本地資料夾 / zip 安裝 skill 至中央庫（`library/installer.rs`，E2E 驗證通過）
- [x] 安裝前顯示 SKILL.md 預覽 + 風險掃描結果（`library/risk.rs` 5 條規則 + RiskReportModal）
- [x] 同步引擎可將 skill 同步至各工具 global 目錄（junction→symlink→copy 三段 fallback，E2E 驗證 junction 成功）
- [x] 16 個工具 adapter 定義（宣告式 `BUILTIN_AGENTS` 資料表）、`library_detect_agents` 偵測
- [x] Wingman Agent system prompt 包含 enabled skills 清單（`SkillPromptBuilder`），`read_skill` AIFunction 可展開全文
- [x] MCP server CRUD + `mcp_set_agent_link` 同步寫入 IDE 設定檔（`config_writer` 保留使用者自有項目，3 個單元測試）
- [x] 前端五分頁 UI（市集/中央庫/Agents/Presets/MCP），`tsc` + `cargo build` + `dotnet build` 全綠

---

## 2. WS2 — SOLID 重構

### 2.1 Agent Service

- `WingmanChatAgent` 拆分：
  - `IAgentFactory` → `CopilotAgentFactory` / `ByokAgentFactory`（Strategy，依 `ProviderKind` 選擇）
  - Agent 建構、訊息組裝、usage 回報各自獨立
- `RunOrchestrator`：
  - Run 狀態持久化：`IRunRepository`（SQLite 實作），取代 in-memory ConcurrentDictionary
  - CopilotSession 事件橋接抽為 `CopilotEventBridge`
- 目錄整理：Application（use case / contracts）、Domain（entity）、Infrastructure（實作）、Host（endpoint）職責清晰。

### 2.2 Rust / 前端

- Rust：skills 硬編碼來源移除（1.1 已完成資料驅動）；command handler 只做參數轉換，業務邏輯在 service 層
- 前端：SkillsPage 批次 README 抓取抽為 `useBatchReadmeFetch` hook；useSkillsStore 按 Library / Sync / Mcp 拆分

### WS2 驗收標準

- [x] 新增一個 model provider 只需新增一個 IAgentFactory 實作（`CopilotAgentFactory` / `ByokAgentFactory`，OCP）
- [x] Run 狀態持久化至 SQLite（`RunRepository`，upsert + 終態保存，4 個單元測試）
- [x] 既有功能保留（WingmanChatAgent 對外簽章不變、CopilotEventBridge 抽出後 RunOrchestrator 行為一致）
- [x] `dotnet build` 0 錯誤、`cargo build` 通過、`tsc --noEmit` 通過
- [x] 單元測試 32 個全過（SkillProvider 8、PromptBuilder 3、RunRepository 4、分析器 7、其他 10）

---

## 3. WS3 — 企業程式碼解析（核心差異化）

### 3.1 專案管理

- 左側選單「專案」區（像 Codex）：新增（資料夾選擇器）、列表、移除
- SQLite 表 `projects`（id, name, path, language_hints, index_status, indexed_at）

### 3.2 索引管線（靜態分析 → Neo4j）

- **Neo4j 生命週期管理**（`Neo4jLifecycleService`）：
  - 首次啟用下載 Neo4j Community + JRE 至 `~/.wingman/neo4j/`（支援離線包路徑設定）
  - Agent Service 管理啟停、port 探測、初始密碼
  - 設定頁可切換外部 Neo4j 連線
- **.NET 分析器**：Roslyn（`Microsoft.CodeAnalysis.CSharp.Workspaces` + MSBuildWorkspace）抽取：
  - 節點：Solution / Project / Namespace / Type / Method / Property / Field
  - 關係：CONTAINS / CALLS / IMPLEMENTS / INHERITS / REFERENCES / DEPENDS_ON
- **Java 分析器**：tree-sitter-java（Rust 端）或 JavaParser 子行程，抽取同構節點/關係
- 增量索引：git diff 驅動，僅重建變更檔案的子圖
- 索引進度事件流 → 前端進度視覺化（P1 圖譜生長動畫）

### 3.3 GraphRAG（Neo4j）

- Leiden 社群偵測（Neo4j GDS 或 C# 端演算法）→ 社群層級結構
- LLM 社群摘要：Copilot SDK 由下而上生成模組摘要（全自動）
- 查詢模式：
  - **Global Search**：know-how 全局問題 → map-reduce over 社群摘要
  - **Local Search**：實體鄰域展開（full-text 定位 + 圖遍歷擴散）
- `IEmbeddingProvider` 抽象：`NoOpEmbeddingProvider`（現階段，檢索走 full-text）→ 未來 `OpenAIEmbeddingProvider` / `OnnxLocalEmbeddingProvider`

### 3.4 Repo Map + AGENTS.md

- Repo Map：符號 + 檔案依賴圖 → PageRank → token 預算內骨架（Aider 模式），每次 Run 注入
- AGENTS.md 自動生成：掃描建置系統/測試框架/慣例 → LLM 彙整（`/init` 等效），存專案根目錄

### 3.5 Impact Analysis（不改 A 壞 B）

- 修改前：Neo4j 反向呼叫鏈查詢（`CALLS*1..N` 反向）→ 受影響方法/類別/測試清單
- 前端視覺化（P2）：受影響呼叫鏈圖（React Flow 或輕量 canvas）
- 修改後：自動執行受影響測試作為驗證閘（`dotnet test --filter` / `mvn -Dtest`）

### WS3 驗收標準

- [x] .NET 專案索引（`RoslynCodeAnalyzer`：Type/Method/Property/Field + CALLS/INHERITS/IMPLEMENTS/REFERENCES，4 個單元測試）
- [x] Java 專案索引（`JavaCodeAnalyzer` 語彙解析：package/type/extends/implements/呼叫解析，3 個單元測試）
- [x] Neo4j 由 App 自動下載/啟動/停止（`Neo4jLifecycleService`：managed/external 模式、離線包支援、junction 密碼設定）
- [x] GraphRAG 問答（`GraphRagService`：Global map-reduce over 社群摘要 / Local full-text+鄰域，auto 路由）
- [x] Repo Map（`RepoMapService` token 預算內骨架）；AGENTS.md 一鍵生成（`AgentsMdGenerator` + 事實偵測 2 個測試）
- [x] Impact Analysis（`ImpactAnalysisService` 反向呼叫鏈 + 測試過濾建議 + `ImpactGraph` SVG 視覺化）
- [x] 增量索引（`IncrementalIndexAsync`：git status 驅動、只重建變更檔案子圖）
- [x] FTUE（ProjectsPage：新增專案→自動索引→進度條→自動社群摘要→立即問答）

> 注：Neo4j 執行期驗證（實際下載啟動、Cypher 查詢往返）需在有網路/離線包的環境手動執行一次。程式碼層驗收全數通過。

---

## 4. WS4 — Agent 方法論整合

- **Explore → Plan → Code → Verify** 四階段 MAF Workflow（Run = Workflow 實例）
- Plan mode：唯讀探索 + 計畫產出 + 使用者確認後才執行
- Sub-agent 調查隔離（MAF 子代理，摘要回報主 context）
- 驗證迴圈：建置/測試通過為停止條件，失敗自動迭代（上限 N 次）
- 整合 WS3：Explore 階段自動使用 Repo Map + GraphRAG 查詢；Code 階段前跑 Impact Analysis

### WS4 驗收標準

- [x] Run 以四階段工作流執行（`ExplorePlanCodeVerifyWorkflow`），`run:phase` 事件流可觀測各階段轉換
- [x] Plan mode：`POST /api/workflow/plan` 產出計畫（PlanOnly），`run:plan` 事件供前端核准介面
- [x] 調查隔離：Explore 階段以獨立 LLM completion（`ILlmCompletionService`）+ GraphRAG 查詢，摘要注入主 context（等效 sub-agent 隔離，不污染 code session）
- [x] 驗證迴圈：`VerificationService` 偵測建置/測試指令，失敗輸出回饋 Agent 迭代（上限 N 次，`run:verify` 事件），5 個單元測試

---

## 5. 桌面版產品邊界

- 2026-07-20 決定 Modern Wingman 僅提供 Tauri 桌面 UI，不再提供獨立
  `wingman-cli`。
- Skills、Agents 與 MCP 管理統一由桌面 UI 操作，Rust library 只供 Tauri
  desktop binary 使用。

---

## 6. 里程碑

| 里程碑 | 內容 | 驗收 |
|--------|------|------|
| M1 | WS1 完成 | ✅ 2026-07-07 全數通過 |
| M2 | WS2 完成 | ✅ 2026-07-07 全數通過 |
| M3 | WS3 完成 | ✅ 2026-07-07 程式碼層全數通過（Neo4j 執行期需環境驗證） |
| M4 | WS4 + 桌面整合完成 | ✅ 2026-07-20 桌面版產品邊界確認 |

## 7. 最終驗收紀錄（2026-07-07）

| 檢查項 | 結果 |
|--------|------|
| `dotnet build`（agent-service） | ✅ 0 錯誤 |
| `dotnet test`（32 tests） | ✅ 全過 |
| `cargo build`（desktop + CLI） | ✅ 0 錯誤 |
| `cargo test --lib`（13 tests） | ✅ 全過 |
| `tsc --noEmit`（desktop） | ✅ 0 錯誤 |
| `tsc --noEmit`（contracts） | ✅ 0 錯誤 |
| `vite build` | ✅ 成功 |
| CLI E2E（install→sync→unsync） | ✅ junction 模式驗證通過 |

每完成一項即更新 `工作日誌.md`。
