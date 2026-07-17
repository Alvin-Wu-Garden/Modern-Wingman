# Modern Wingman Marketplace、本機 Registry、Skill/MCP 分發與 Plugin Runtime SPEC

> 文件版本：1.2 GitHub Discovery & Modular Marketplace Baseline  
> 文件日期：2026-07-14  
> 文件狀態：產品需求已修訂，Marketplace 與 Discovery Pipeline 邊界已確認  
> 實作範圍：Modern Wingman Desktop、C# Agent Service、EF Core/SQLite 本機儲存與 Agent Target Adapters

## 1. 產品定位

Modern Wingman Desktop 將原「Skill 技能庫」升級並更名為單一頂層選單 **Marketplace**。Marketplace 內建 Wingman 自有的 Discovery、評分、Artifact 解析與安裝流程，讓單一 Windows 使用者在同一個 Desktop UI 中：

1. 從 Wingman 管理的 Discovery Provider 發現、搜尋、分類與評分 Skill、MCP 與 Wingman Plugin。
2. 將 repository-level discovery record 解析為真正可驗證的 artifact；monorepo 可拆成多個 Skill/MCP。
3. 對 artifact 執行 Wingman 自有格式驗證、風險提示與 Agent Target 相容性判定。
4. 一次選擇一個或多個 Agent Target，將 Skill/MCP 以 per-user global 或 project scope 一鍵安裝/配置。
5. 安裝與執行僅供 Wingman 使用的 Plugin。
6. 查看實際來源、版本、評分依據、可安裝性、部署目標、檔案差異與狀態。

本 SPEC 中的 Registry 指「Wingman Desktop 內嵌的本機 Catalog + Artifact Cache + Deployment Database」，**不是中央 Registry Server**。

Marketplace、Discovery Provider、評分與排序均為 Wingman 自行設計與實作的產品能力。可以借鑑公開目錄產品的概念與演算法思路，但不得把第三方目錄名稱、評分、索引或品牌當成 Wingman item 的來源，也不得在執行期依賴其服務。

## 2. 已確認核心決策

- Registry 實作在 Wingman Desktop 與本機 Agent Service。
- 不需要登入、使用者角色、Publisher、Reviewer、Admin 或租戶。
- 不建立 Registry Web UI、中央 API、中央 database 或中央 blob storage。
- 不建立 artifact 簽章、trust tier、人工審核、遠端 yank/revoke 或強制停用。
- 不需要企業 audit retention、RPO、RTO 或遠端備份政策。
- 不整合 `agentregistry-dev/agentregistry`；Wingman 實作自己的本機資料模型。
- Skill、MCP 與 Wingman Plugin 共用單一頂層 Marketplace，以分頁與 artifact kind 區分。
- Wingman Plugin 不允許 export，也不提供部署 component 到其他 Agent 的操作。
- Skill/MCP 若要供外部 Agent 使用，必須在 Marketplace 中解析成獨立 artifact；不可直接部署 Wingman Plugin component 或未解析的 Discovery Record。
- Marketplace catalog 只在使用者按下「重新整理」時使用設定頁 GitHub PAT 搜尋/讀取 GitHub；不作背景或啟動時自動刷新。
- 不維護或下載 Wingman JSON Marketplace Index；GitHub 是第一版唯一線上 Discovery Source，SQLite 是本機 materialized catalog。
- Artifact/Deployment 更新仍由使用者手動執行；Marketplace 重新整理不得自動更新或重新部署已安裝 artifact。
- Marketplace Discovery Catalog 第一版上限 5,000 筆；已安裝、已解析、已收藏及手動 URL 項目優先保留。
- Codex `marketplace.json` 僅作使用者明確選擇的本機 importer，不是線上 Discovery Source。
- Discovery、正規化、分類、評分、Artifact Resolver 與相容性驗證由 C# Agent Service 原生實作。
- Marketplace metadata 使用 Wingman 現有 EF Core/SQLite 本機資料層；不導入 Python、FastAPI、SQLAlchemy、PostgreSQL、Supabase 或外部目錄資料庫。
- Discovery Provider 是 Wingman 內部來源 Adapter 抽象，不是第三方品牌或卡片上的內容來源。UI 顯示的來源必須是實際 provenance，例如 GitHub repository/ref/path、local folder、ZIP 或使用者明確匯入的 Codex marketplace。
- Marketplace 採 modular monolith：新增獨立 `Wingman.Marketplace` C# class library，但仍在 Agent Service process 內執行並共用唯一 `AppDbContext/wingman.db`。
- `Wingman.Marketplace` 不直接依賴 Microsoft Agent Framework；Plugin 透過 Agent Service 的 `MafPluginRuntimeAdapter` 映射為 MAF Skills、MCP、Functions、Middleware、Agent Profile 與 Workflow capabilities。
- Tauri/Rust 不再直接讀寫 Skill/MCP/Plugin/Marketplace tables；Marketplace persistence、schema migration 與 lifecycle state 只由 C# Agent Service 管理。

## 3. 非目標

- 不建立公司中央 Marketplace 後端。
- 不依賴第三方 Marketplace/Directory 的 API、靜態索引、評分或 security grade 才能運作。
- 不建立或維護 Wingman Marketplace 商品 JSON Index；線上發現結果直接由 GitHub 正規化後保存至 SQLite。
- 不在 Wingman 產品或資料模型中複製第三方目錄品牌、item ID 或來源標籤。
- 不部署或內嵌 Python Discovery service，也不要求外部 PostgreSQL/Supabase。
- 不在 Wingman 之間點對點分發 artifact。
- 不提供企業級發布審核或遠端政策。
- 不自動掃描或收編 Agent Target 已有的 Skill/MCP。
- 不從 Codex 的使用者目錄或 Plugin cache 載入 Wingman Runtime。
- 不安裝 Node、Python、Docker、npm/pip package、binary 或其他 MCP runtime。
- 在將 MCP 配置到外部 Agent Target 的流程中，不啟動 MCP process、不連線 remote MCP、不執行 MCP protocol health check。
- 不建立 Codex 隔離 VM、測試帳號或 local marketplace smoke test。
- 第一版不支援 artifact runtime dependencies 或 dependency solver。
- 第一版不作 Skill 格式轉換；目標不原生相容就不部署。
- 第一版的品質分數與風險訊號是靜態證據評估，不宣稱等同 Agent 任務成功率、完整 malware audit 或執行期安全保證。

## 4. 產品模型

| 名稱 | 定義 |
|---|---|
| Marketplace | Desktop 的單一頂層能力入口，包含 Discover、Skills、MCP Servers、Wingman Plugins、Installed 與 Updates |
| Discovery Provider | Wingman 內部 C# Adapter，使用設定頁 GitHub PAT 從 GitHub Search/API 取得候選 metadata |
| Discovery Record | 尚未保證可安裝的 repository/source-level 候選紀錄，可被搜尋、分類、評分與排序 |
| Artifact Candidate | Resolver 從 Discovery Record 或匯入內容中找到的單一 Skill/MCP/Plugin 候選目錄 |
| Score Snapshot | Wingman 依版本化 scoring profile 產生的品質分數、各維度證據與計算時間 |
| Installability Result | 特定 artifact 對特定 Agent Target/scope 的驗證結果與不可安裝原因 |
| Local Registry | Desktop 內嵌的 Catalog、Artifact Cache 與狀態資料庫 |
| Source | Artifact 的實際 provenance，例如 GitHub repository/ref/path、本機資料夾、ZIP 或明確匯入的 Codex marketplace |
| Artifact | 已解析、驗證、計算 hash 並放入本機 cache 的 Skill/MCP/Plugin 版本 |
| Skill | `SKILL.md` 與可選的 scripts/references/assets |
| MCP Definition | 來源定義、command/url、args、env schema 與 target compatibility |
| Wingman Plugin | Codex-format-compatible 封裝，僅安裝到 Wingman Plugin Store |
| Agent Target | `product + surface`，例如 `codex-cli` 或 `cursor-windows` |
| Deployment | Skill/MCP artifact 到 Agent Target + scope + project 的本機紀錄 |
| Installation | Wingman Plugin 到 Wingman Plugin Store 的本機紀錄 |
| Scope | `global` 或 `project`，以 Adapter 真正支援能力為準 |

UI 可以一站式，但 Domain Model 不可把 Skill、MCP、Plugin 當成同一種執行物件。

Discovery Record 也不可直接當成 Artifact。只有 Resolver 找到確切 artifact root、固定來源 snapshot、通過格式驗證並產生 Installability Result 後，才可顯示一鍵安裝操作。

## 5. 概念架構

```text
GitHub Search/API / Direct GitHub URL / Codex explicit import / Local Import
                                  │
                                  ▼
                  C# Discovery Provider Pipeline
        ┌─ Normalize + deduplicate repository identity
        ├─ Classify suggested artifact kind/platforms
        ├─ Compute Wingman quality/ranking/risk signals
        └─ Save Discovery Records + Score Snapshots to SQLite
                                  │
                                  ▼
                       Artifact Resolver
        ┌─ Fetch/copy immutable source snapshot
        ├─ Locate SKILL.md/MCP/Plugin roots
        ├─ Split monorepo into Artifact Candidates
        └─ Validate + compute per-target Installability
                                  │
                                  ▼
                      Wingman Local Registry
        ┌─ Content-addressed artifact cache
        ├─ Source/update metadata
        └─ Deployment/installation records
                    │                         │
                    ▼                         ▼
          Agent Target Adapters         Wingman Plugin Runtime
          ├─ Skill multi-target copy
          └─ MCP multi-target config merge
```

Registry 不對外開放 server API；Desktop Frontend 透過現有 Tauri/.NET Agent Service contract 操作本機 Registry。

Marketplace domain logic 的 source of truth 位於 C# Agent Service。React/Tauri Desktop 負責 UI、呼叫 contract 與必要的 native shell bridge，不得在 Rust/TypeScript 與 C# 各自維護一套不同的分類、評分或安裝規則。

### 5.1 Modular Marketplace 專案邊界

第一版只新增一個 Marketplace class library，避免把每一層拆成過多 `.csproj`：

```text
apps/agent-service/
├─ AgentService.csproj
├─ modules/
│  └─ Marketplace/
│     ├─ Wingman.Marketplace.csproj
│     ├─ Domain/
│     │  ├─ Discovery/
│     │  ├─ Artifacts/
│     │  ├─ Deployments/
│     │  └─ Plugins/
│     ├─ Application/
│     │  ├─ Discovery/
│     │  ├─ Classification/
│     │  ├─ Scoring/
│     │  ├─ Resolution/
│     │  ├─ Installation/
│     │  └─ PluginLifecycle/
│     └─ Contracts/
│        ├─ IMarketplaceStore.cs
│        ├─ IDiscoveryProvider.cs
│        ├─ IArtifactResolver.cs
│        ├─ ITargetAdapter.cs
│        └─ IEnabledPluginCapabilitySource.cs
├─ src/
│  ├─ Infrastructure/
│  │  ├─ Marketplace/
│  │  │  ├─ GitHub/
│  │  │  ├─ Persistence/
│  │  │  ├─ ArtifactStorage/
│  │  │  └─ TargetAdapters/
│  │  └─ AgentFramework/
│  │     └─ Plugins/
│  │        └─ MafPluginRuntimeAdapter.cs
│  └─ Host/
│     ├─ DependencyInjection/MarketplaceRegistration.cs
│     └─ RestEndpoints/MarketplaceEndpoints.cs
└─ tests/
   └─ UnitTests/
      └─ Marketplace/
         ├─ Domain/
         ├─ Application/
         └─ Integration/
```

Dependency 規則：

- `Wingman.Marketplace` 可包含 Domain、Application、純分類/評分/協調邏輯與 ports/contracts。
- `Wingman.Marketplace` 不引用 Agent Service Host、REST、React/Tauri、EF Core `AppDbContext` 或具體 MAF Agent Factory。
- GitHub、SQLite、filesystem、Agent Target 與 MAF 都是 Agent Service Infrastructure adapters，依賴 Marketplace contracts，不可反向依賴。
- `AppDbContext` 是唯一 EF Core context/schema authority；Marketplace persistence adapter 透過 `IDbContextFactory<AppDbContext>` 實作 `IMarketplaceStore`。
- 不建立第二顆 SQLite、第二個 Marketplace process 或 Rust-side Marketplace repository。
- 因 `AgentService.csproj` 目前會遞迴編譯專案目錄內的 `.cs`，新增 module 時必須在 Host project 明確 `Compile Remove="modules\Marketplace\**\*.cs"`，再以 `ProjectReference` 引用 `Wingman.Marketplace.csproj`，避免相同 source 被編譯兩次。
- 第一版沿用既有 `AgentService.UnitTests` project，在 `tests/UnitTests/Marketplace` 依層分資料夾；不為每一層再建立額外 test `.csproj`。

### 5.2 Wingman Plugin 到 MAF 的 Adapter

Marketplace 管理 Plugin package、安裝、enable/disable、版本與 component descriptor；MAF 整合只由 `MafPluginRuntimeAdapter` 負責：

```text
Enabled Wingman Plugin
→ IEnabledPluginCapabilitySource
→ immutable EnabledPluginCapabilities snapshot
→ MafPluginRuntimeAdapter
   ├─ Skill → MAF FileAgentSkillsProvider/context provider
   ├─ MCP → Wingman McpToolCatalog / MAF MCP tools
   ├─ Function → MAF Function Tool
   ├─ Hook → Wingman Hook Dispatcher + MAF middleware/event
   ├─ Agent Profile → AgentCreationContext/options
   └─ Workflow → MAF Workflow adapter
```

規則：

- MAF 不直接解析 Plugin archive/manifest，也不直接讀 Marketplace database。
- Agent 建立時取得當下已 enable Plugin 的 immutable capability snapshot。
- Plugin enable/disable 只更新 `wingman.db` 並 invalidate snapshot；已開始的 Agent Run 不在中途增刪 capability，下一次建立 Agent 才套用新狀態。
- Agent 工具面板只顯示目前 snapshot 中可由 Agent 呼叫的 MCP Tool 與 Function Tool，並標示 `Plugin name / component ID / capability kind` provenance；Skill 顯示在 context/skill 區，Hook 不冒充可呼叫工具。
- Discovery Record、尚未 enable 的 Plugin 或只有安裝描述的 component 絕不可註冊進 MAF 工具集合。
- Agent Timeline 對 tool call、hook event 與 workflow step 保存 Plugin ID、Plugin version、component ID、run ID 與結果；不得只顯示模糊的「Plugin tool」。
- Marketplace module 可在未載入 MAF packages 的單元測試中完整測試 discovery、分類、評分、解析與 lifecycle。

## 6. 本機儲存

實際 root 使用 Wingman app-data resolver，不從 Codex 路徑推導：

```text
<wingman-app-data>/
├─ sqlite/
│  └─ wingman.db
├─ registry/
│  ├─ blobs/<sha256>/
│  ├─ staging/
│  └─ sources/
├─ plugins/
│  ├─ installed/<plugin-id>/<version>/
│  └─ data/<plugin-id>/
└─ backups/
   └─ config-writes/
```

Marketplace 使用 Wingman 現有 `AppDbContext`、EF Core SQLite provider 與 `wingman.db`，不建立另一套 Supabase/PostgreSQL schema。至少包含下列邏輯資料集合；實際 table naming 依現有 EF Core convention：

```text
marketplace_sources
marketplace_favorites
discovery_records
discovery_score_snapshots
artifact_score_snapshots
artifact_candidates
artifacts
artifact_versions
installability_results
deployments
plugin_installations
marketplace_sync_runs
```

規則：

- Artifact snapshot 以 SHA-256 尋址，相同內容可共用 blob。
- Registry 資料庫只儲存 metadata、source、hash、deployment 與 status。
- Discovery Record 以 normalized repository/source identity 去重；Artifact 以 `source + resolved ref + artifact path + content hash` 識別。
- GitHub repository identity 優先使用 `GitHubNodeId`，另保存 canonical URL；owner/repository 更名時不得建立重複 item。
- Discovery Record 至少保存 `FirstSeenAt`、`LastSeenAt`、`ConsecutiveMissCount`、`GitHubUpdatedAt`、`PushedAt`、`MetadataFingerprint` 與 `DiscoveryStatus`。
- `DiscoveryScore` 評估 repository-level 發現品質；`ArtifactQualityScore` 只在 Resolver 取得確切 artifact 後計算。兩者分欄與分表保存，不互相覆蓋。
- 每個分數必須儲存 scoring profile ID、各維度分數與證據摘要，不只儲存總分。
- 外部來源提供的 popularity、topics 或 metadata 可成為 Wingman scoring input，但最終分數由 Wingman 計算並標示為 Wingman Score。
- Marketplace Discovery Catalog 最多保存 5,000 筆自動發現紀錄。清理優先級必須保留已安裝、已解析、已收藏及使用者手動 URL 項目，再移除過期、低分且未使用的 discovery-only records。
- GitHub PAT 由 Settings 的既有 GitHub/Copilot provider profile 管理；Marketplace 只透過 Settings credential abstraction 取得短生命週期值，不在 Marketplace table 複製或記錄 PAT。若既有 Settings PAT 尚未使用 `ISecretProtector` 做 at-rest protection，必須先在共用 Settings persistence 完成一次性保護 migration，不能另造 Marketplace PAT store。
- 真實 API Key 不儲存在 Registry database。
- Staging 檔案在成功匯入或失敗清理後刪除。
- 刪除 Codex cache 不影響 Wingman 的 artifact 或 Plugin。
- Artifact 只要仍有 Skill/MCP deployment 或 Plugin installation 就不可從 Local Registry 刪除；使用者必須先移除所有關聯項目。

### 6.1 舊資料一次性 Migration

- App 升級時由現有 C# `AgentSchemaMigrator` 建立 schema，再由 dedicated startup data-migration coordinator 執行一次性 migration，把現有 Rust/Tauri Skill、MCP 與 Plugin metadata 正規化進上述 Marketplace tables。
- Migration 必須有 schema/version marker、可重入檢查、逐筆錯誤記錄與完成摘要；重啟不可重複建立同一 artifact/deployment。
- 能確定來源、target、scope 與 path 的舊資料保留 ownership；資訊不足的舊資料標記為 `ImportedLegacy / ManualReviewRequired`，不得猜測。
- Migration 成功後，Rust/Tauri 舊 repository 進入 read-disabled/write-disabled 狀態；後續所有 Marketplace 讀寫只經 Agent Service API 與唯一 `AppDbContext`。
- 第一版不維持 Rust 舊資料表與 C# 新資料表的雙寫或長期雙向同步。需要 rollback 時回復升級前的本機資料庫備份，不以雙寫當 rollback 策略。

## 7. 支援格式

### 7.1 Skill

最小格式：

```text
my-skill/
├─ SKILL.md
├─ scripts/       # optional
├─ references/    # optional
└─ assets/        # optional
```

規則：

- `SKILL.md` 必須可讀取，frontmatter 必須可解析。
- Skill 宣告支援的 Agent Target。
- Wingman 的 Skill validator baseline 固定為 `agent-skill-standard/v1`，profile 版本必須保存於 validation result。
- 第一版只處理原生相容；Adapter 原樣 copy，不改寫 `SKILL.md`。
- 含 scripts 的 Skill 仍可 copy，但匯入與安裝畫面必須標示含可執行檔。
- Wingman 不在匯入或安裝期執行 Skill script。

### 7.2 MCP Definition

本機 Registry 使用正規化 definition：

```json
{
  "id": "internal-search",
  "version": "1.0.0",
  "transport": "stdio",
  "command": "internal-search-mcp",
  "args": ["--stdio"],
  "env": {
    "SEARCH_API_KEY": {
      "required": true,
      "secret": true,
      "placeholder": "REPLACE_WITH_YOUR_API_KEY"
    }
  },
  "supportedTargets": ["codex-cli", "cursor-windows"]
}
```

規則：

- Definition 只用於產生目標 Agent config。
- Wingman 不執行 `command`，不連線 `url`，不執行 OAuth/login。
- Adapter 只能寫入目標 Agent 明確支援的 transport/config 格式。
- Secret 值一律寫入 `REPLACE_WITH_YOUR_API_KEY`，使用者再到目標 config 手動替換。

### 7.3 Wingman Plugin

```text
my-plugin/
├─ .codex-plugin/
│  └─ plugin.json
├─ wingman.json
├─ skills/          # optional
├─ .mcp.json        # optional
├─ hooks/           # optional
└─ assets/          # optional
```

規則：

- `.codex-plugin/plugin.json` 符合 Wingman 維護的版本化 Codex-format profile。
- `wingman.json` 只承載 Wingman-specific metadata、Agent Profile 與權限說明。
- Plugin 不提供 `exports`。
- Plugin 只安裝到 Wingman 自己的 Plugin Store。
- 線上 Plugin 來源只接受 Wingman 版本內建的官方 GitHub owner/organization allowlist，且必須通過 Wingman Validator。非 allowlist GitHub URL 不得解析成 Wingman Plugin；第一版其他 Plugin 來源只允許使用者明確選擇的本機 folder/ZIP 或 Codex marketplace 匯入，且仍須通過 Validator 與手動確認。
- 格式相容是產品格式承諾，不是 OpenAI certification。
- Wingman 不讀取或依賴 Codex Plugin cache。

### 7.4 Wingman 自有 Validator

Validator 固定 profile ID，例如 `codex-plugin-compat/2026-07`，至少檢查：

- `.codex-plugin/plugin.json` 存在且 JSON 有效。
- 必要 metadata 與版本格式。
- Component path 必須是相對路徑，不得離開 Plugin root。
- 指向的 Skill、MCP、Hook 與 Asset 存在。
- Archive 沒有 path traversal、absolute path 或 symlink escape。
- 未識別欄位保留，但 Wingman 不自動執行未識別 component。

Wingman 不使用官方 validator SDK/CLI，也不作 Codex 安裝 smoke test。

## 8. Source、Discovery、評分與匯入

### 8.1 Wingman 自有 Discovery Provider

第一版 C# Provider/Importer：

- `GitHubDiscoveryProvider`：使用 Settings GitHub PAT 與版本化 query profile 搜尋公開 Skill/MCP repositories。
- `OfficialWingmanPluginGitHubProvider`：只搜尋 Wingman 版本內建的官方 GitHub owner/organization allowlist，且 repository 必須通過 Plugin Validator 才能進入 Wingman Plugins 分頁。
- `GitHubRepositoryProvider`：讀取使用者直接輸入的 GitHub repository URL、metadata、branch/tag/commit、tree 與檔案內容。
- `LocalFolderImporter`：匯入使用者選擇的本機資料夾。
- `LocalArchiveImporter`：匯入使用者選擇的 ZIP/archive。
- `CodexMarketplaceImporter`：只在使用者明確選擇 `marketplace.json` 時轉換為 Wingman Discovery Records；不讀取 Codex 個人 Marketplace 設定。

這些名稱是 Wingman 內部 Adapter ID。Marketplace 卡片的「來源」欄位顯示實際 provenance，例如 `github.com/owner/repo`、本機資料夾、ZIP 檔名或明確匯入的 Codex marketplace，不顯示借鑑概念的第三方產品名稱。

線上 Discovery 不使用 Wingman JSON Index。GitHub 搜尋條件以 C# `DiscoveryQueryProfile` 版本化，包含 query ID、artifact kind hint、query text、page/result budget 與 classifier profile；它是演算法設定，不是商品清單。

Discovery refresh 只更新 metadata、score 與候選狀態，不自動下載/安裝 artifact，也不自動更新已匯入 snapshot。所有 Provider 必須正規化為相同 C# contract，至少包含：

```csharp
public interface IDiscoveryProvider
{
    string ProviderId { get; }
    Task<DiscoveryPage> DiscoverAsync(
        DiscoveryCursor? cursor,
        CancellationToken cancellationToken);
}
```

Provider 回傳的 `SuggestedKind`、`SuggestedTargets` 都只是 discovery hint；不得直接轉為可安裝狀態。

GitHub PAT 規則：

- Marketplace 不建立自己的 PAT 欄位，必須透過 Settings credential abstraction 讀取既有 GitHub PAT profile。
- 未設定 PAT 時「重新整理」不可執行，UI 顯示前往 Settings 的提示。
- PAT 不進入 Marketplace database、log、Timeline、HTTP error body 或 score evidence。
- 第一版只搜尋使用者可透過該 PAT 讀取的 repositories；本 SPEC 的公開 Marketplace 仍以 public repository 為產品目標。

### 8.2 Discovery 正規化與去重

```text
User clicks Refresh
→ Read Settings GitHub PAT
→ Start MarketplaceSyncRun
→ Execute versioned GitHub query profile
→ Normalize by GitHub node ID + canonical repository URI
→ Compute MetadataFingerprint
→ Batch compare with wingman.db
   ├─ New       → insert Discovery Record
   ├─ Changed   → update metadata + reclassify + rescore
   ├─ Unchanged → update LastSeenAt only
   └─ Missing   → increment miss only after a successful full refresh
→ Apply 5,000-record retention policy
→ Commit sync result
→ Show refresh completion notification
```

規則：

- GitHub URL 必須正規化大小寫、`.git` suffix、fragment 與 ref；同一 repository 不因不同顯示 URL 重複建檔。
- `GitHubNodeId` 是 repository rename/transfer 後的主要 identity；canonical URL 是可變 provenance metadata。
- 同一 repository 可產生多個 Artifact Candidate，但 Discovery Record 仍保留 repository-level identity。
- Description、topics、語言與 README 關鍵字只能用於候選分類與排序，不能證明格式相容。
- `MetadataFingerprint` 未變時不得重跑昂貴 Resolver；只更新 `LastSeenAt` 與可變 popularity metadata 所需的 score component。
- 某 repository 只有在完整 query profile 成功後仍未出現，才增加 `ConsecutiveMissCount`；部分失敗/取消的 refresh 不增加 miss。
- 連續三次成功 refresh 未出現時標記 `Stale`，不立即刪除。
- 已安裝、已解析、已收藏或使用者直接輸入 URL 的項目永不因搜尋結果消失而自動刪除。
- 需要為 5,000 筆上限清理時，只能移除 stale、未安裝、未解析、未收藏且非手動來源的低分項目。
- Provider refresh 失敗保留上次成功 catalog，不清空 Marketplace。
- GitHub Discovery 若沒有明確 artifact path，必須由 Artifact Resolver 掃描；不得從 repository name 猜 path/package。

UI refresh 行為：

- 只有使用者按下「重新整理」才執行；執行中按鈕 disabled，顯示進度並允許取消。
- 完整成功提示：`Marketplace 已重新整理：新增 {new}、更新 {updated}、未變更 {unchanged}、標記過期 {stale}`。
- 部分成功提示：`Marketplace 已部分重新整理：成功 {succeeded}/{total} 個查詢`，並列出新增/更新統計。
- refresh 完成不切換目前分頁、不改變已安裝版本、不執行任何 deployment。

### 8.3 Marketplace 分類模型

Marketplace 不使用單一 `category` 同時表示格式、用途、平台與狀態。第一版固定拆成以下分類軸，C# domain model 與 SQLite schema 必須分欄儲存。

#### A. Artifact Kind（安裝物類型）

| ID | UI 名稱 | 定義 | 是否可進入安裝流程 |
|---|---|---|---|
| `skill` | Skill | 以 `SKILL.md` 為根的 Agent Skill | 通過 Resolver/Validator 後可以 |
| `mcp-server` | MCP Server | 可正規化為 Wingman MCP Definition 的 MCP | 通過 Resolver/Validator 後可以 |
| `wingman-plugin` | Wingman Plugin | 含 Plugin manifest、僅供 Wingman 使用的完整封裝 | 通過 Plugin Validator 後可以 |
| `unknown` | 尚未識別 | Discovery metadata 不足，尚未確定類型 | 不可以 |
| `unsupported-project` | 不支援的專案 | Agent framework、prompt collection、coding assistant 或一般工具，但不是第一版可安裝 artifact | 不可以；一般瀏覽隱藏，直接 URL 才顯示說明 |

`SuggestedKind` 可由 Discovery 分類器推斷；`ResolvedKind` 只能由 Artifact Resolver 根據實際檔案確認。UI 分頁可使用 SuggestedKind 顯示候選項目，但安裝能力只讀取 ResolvedKind。

#### B. Functional Category（功能用途）

每個 Discovery Record/Artifact 必須有一個 `PrimaryCategory`，可有最多三個 `SecondaryCategories`。第一版 stable category ID：

| ID | UI 名稱 | 包含範圍 |
|---|---|---|
| `software-development` | 軟體開發 | 程式生成、框架使用、前後端、行動端與一般工程工作流 |
| `code-quality-review` | 程式品質與審查 | Code review、refactoring、debugging、效能與靜態分析 |
| `testing-qa` | 測試與 QA | 單元、整合、E2E、測試資料、品質閘門與測試自動化 |
| `devops-cloud` | DevOps 與雲端 | CI/CD、容器、Kubernetes、IaC、部署、監控與雲端平台 |
| `data-databases` | 資料與資料庫 | SQL/NoSQL、資料處理、ETL、BI、分析與資料庫管理 |
| `integration-api` | 整合與 API | 第三方服務、API client、webhook、SaaS connector 與系統整合 |
| `web-browser` | Web 與瀏覽器 | Browser automation、爬取、網站操作、網頁擷取與前端診斷 |
| `search-research` | 搜尋與研究 | Web search、研究流程、資料蒐集、文獻與來源整理 |
| `documentation-knowledge` | 文件與知識 | 文件生成、知識庫、摘要、翻譯、Notion/wiki 與內容整理 |
| `productivity-project` | 生產力與專案管理 | 任務、排程、專案管理、工作流與個人效率 |
| `design-media` | 設計與媒體 | UI/UX、圖像、影音、音訊、簡報與創意內容 |
| `security-compliance` | 安全與合規 | 安全檢查、弱點分析、權限、隱私、法遵與供應鏈風險 |
| `communication-collaboration` | 溝通與協作 | Email、聊天、會議、團隊協作與通知 |
| `business-marketing-sales` | 商業、行銷與銷售 | SEO、廣告、CRM、銷售、客戶支援與商業分析 |
| `finance-operations` | 財務與營運 | 會計、報表、採購、營運流程與成本分析 |
| `science-education` | 科學與教育 | 科學計算、研究工具、教學、學習與學術工作流 |
| `ai-agent-automation` | AI 與 Agent 自動化 | Agent orchestration、LLM workflow、prompt/模型操作與通用自動化 |
| `other` | 其他／未分類 | 沒有足夠證據放入其他功能分類 |

`other` 是必要 fallback，不得為了消除未分類而用低信心關鍵字強行歸類。

#### C. Agent Target Compatibility（相容平台）

這不是 Functional Category。第一版使用既有 Target ID：

```text
codex-cli
codex-vscode
claude-code-cli
cursor-windows
github-copilot-vscode
wingman-desktop
```

每個 target 分別保存 `Suggested` 與 `Verified` 狀態，以及 global/project scope 支援。`wingman-desktop` 只用於 Wingman Plugin 或 Wingman Runtime 可使用的 component，不代表可以 export。

#### D. Technical Facets（技術篩選）

Technical Facet 用於 filter/tag，不建立互斥的主分類：

- Runtime：`none`、`node`、`python`、`dotnet`、`java`、`go`、`rust`、`docker`、`native`、`remote`、`unknown`。
- MCP Transport：`stdio`、`streamable-http`、`sse`、`unknown`。
- Operating System：`windows`、`macos`、`linux`、`cross-platform`、`unknown`。
- Content Flags：`has-scripts`、`has-assets`、`has-references`、`requires-secrets`、`requires-network`、`requires-runtime`。
- License、programming language、framework 與 integration service 以 normalized tag 保存，不擴充 Artifact Kind enum。

#### E. Classification Confidence（分類可信度）

每一個 SuggestedKind、PrimaryCategory 與 SecondaryCategory 都要保存 confidence 與 evidence：

```text
Declared   # 受支援 manifest/frontmatter 或明確 importer metadata 宣告
Verified   # Resolver 從實際檔案與格式驗證確認
Inferred   # description/topics/README 關鍵字推斷
Unknown    # 沒有足夠證據
```

判定優先順序：

```text
Verified artifact evidence
> Valid manifest/frontmatter declaration
> Explicit importer metadata
> Repository file/path evidence
> README/description/topics inference
> other + Unknown
```

分類器 profile ID 第一版為 `wingman-marketplace-classifier/2026-07`。結果必須保存 profile ID、evidence source 與 confidence；分類規則更新後可重算 inferred 結果，但不得覆蓋已解析的 Verified Artifact Kind。

### 8.4 Wingman 評分模型

第一版使用兩個版本化 profile，由 C# `IScoringEngine` 計算，不匯入第三方 quality/security score 作為 Wingman Score：

- `wingman-marketplace-discovery-score/2026-07`：只使用 GitHub repository metadata 與可安全解析的文字訊號，讓尚未 Resolve 的候選也能排序。
- `wingman-marketplace-artifact-score/2026-07`：Resolver 取得確切 artifact root 與 snapshot 後，使用 artifact-level 證據評估品質。

`DiscoveryScore` 為 0–100 的 repository-level 加權總分：

| 維度 | 權重 | 主要證據 |
|---|---:|---|
| Metadata Completeness | 20 | description、topics、homepage、language 與 license metadata |
| Documentation Signal | 20 | README 可用性、使用方式與範例訊號；不把文字當成已驗證事實 |
| Maintenance | 20 | GitHub updated/pushed time、archived 狀態與近期活動 |
| Source Maturity | 15 | repository age、default branch 與基本專案結構 |
| Community Signal | 15 | stars、forks、watchers；只作弱訊號，不可單獨決定高分 |
| Freshness | 10 | 相對於 refresh 時間的新鮮度衰減 |

`ArtifactQualityScore` 為 0–100 的 artifact-level 加權總分：

| 維度 | 權重 | 主要證據 |
|---|---:|---|
| Format Completeness | 20 | manifest/frontmatter、必要檔案、license/version metadata |
| Documentation Clarity | 15 | description、使用方式、輸入輸出與限制 |
| Installability Evidence | 20 | 明確 artifact path、安裝設定、可解析 MCP definition |
| Maintenance | 15 | 固定 ref、最近更新、issue/commit 活動 |
| Examples | 10 | 可辨識的 usage/example 與 config 範例 |
| Compatibility Evidence | 10 | 明確宣告且可由 Validator 證實的 target/format |
| Source Maturity | 10 | repository age、license、community metadata；stars 不可單獨決定高分 |

搜尋排序分數不是永久品質分數，依查詢即時計算：

```text
SearchRank = TextRelevance 35%
           + BestAvailableScore 25%
           + Installability 20%
           + Freshness 10%
           + CommunitySignal 10%
```

`BestAvailableScore` 在 artifact 尚未解析時使用 `DiscoveryScore`，解析後優先使用 `ArtifactQualityScore`；UI 必須明確標示目前顯示哪一種分數，不得把兩者混成同一欄覆蓋。空查詢瀏覽時以 `BestAvailableScore + Installability + Freshness` 排序，不用虛構 TextRelevance。所有分數都必須保存 component breakdown；scoring profile 改版後可重算，舊 snapshot 不覆蓋歷史 profile ID。

風險訊號與兩種 Score 分開，不以高 stars 或高品質分數抵銷風險：

```text
Unknown
NoStaticSignal
ReviewRequired
RejectedByValidator
```

第一版靜態訊號可檢查可執行 scripts、外部 command、secret/env、敏感路徑、下載後執行、混淆內容與 archive escape；它只是安裝前提示。只有格式/path 安全違規使用 `RejectedByValidator` 硬拒絕，其他訊號顯示給使用者確認。

### 8.5 Artifact Resolver

Repository-level Discovery Record 不可直接安裝。使用者打開詳情或按「分析可安裝性」時：

```text
Select Discovery Record
→ Resolve selected branch/tag/commit ref to immutable commit SHA
→ Fetch source tree/content to staging
→ Locate every SKILL.md, MCP definition and Plugin manifest
→ Split monorepo into independent Artifact Candidates
→ Validate candidate root and referenced files
→ Compute content hash
→ Compute Installability Result per Agent Target/scope
→ Persist candidate/artifact/results
→ Enable install only for compatible results
```

規則：

- Skill root 是包含 `SKILL.md` 的目錄，不假設 repository root 就是 Skill。
- 一個 repository 內有多個 `SKILL.md` 時產生多個獨立 Artifact Candidate。
- MCP 必須能從受支援原生 manifest/config 確定地解析 canonical definition，或由使用者在詳細頁明確補齊必要欄位；不得把 repository 名猜成 npm/PyPI/Docker package 或 remote MCP URL。無法確定解析時狀態為 `ManualSetupRequired`，保留來源與說明，但不顯示一鍵配置。
- Plugin 必須找到 `.codex-plugin/plugin.json` 與 `wingman.json`，不得由 description 關鍵字猜測。
- Resolver 必須有檔案數、單檔大小、總下載大小與 traversal/symlink 邊界，超過上限標示 `ManualReviewRequired`，不無限制遍歷。
- Suggested target 只有通過該 Target Adapter/Validator 後才轉成 verified compatible target。
- Repository 詳細頁必須列出 Resolver 找到的所有獨立 Artifact Candidate；使用者選擇 artifact，不把整個 repository 當成單一安裝單位。
- `unsupported-project` 不出現在一般 Discover/分類瀏覽結果；只有使用者直接輸入 GitHub URL 時可分析並顯示「第一版不支援」。
- 缺少 license 不阻止解析或安裝，但卡片、詳細頁與確認畫面必須顯示 `License unknown` 警告。
- Skill 第一版相容基準 profile 為 `agent-skill-standard/v1`；是否可安裝仍由各 Target Adapter 驗證，不能假設所有 Agent IDE 都支援同一 Skill。

### 8.6 匯入與 snapshot

```text
Select source/candidate
→ Download/copy to staging
→ Resolve exact artifact root
→ Validate required format and safe paths
→ Compute SHA-256
→ Copy immutable snapshot to local blob cache
→ Save artifact/version/installability metadata
→ Show in Marketplace and Installed management
```

「自動檢查」至少包含：

- JSON/YAML/frontmatter 可解析。
- 必要檔案存在。
- Archive 無 path traversal。
- ID/version 格式有效。
- 相同 ID/version 沒有不同內容衝突。
- artifact path 位於 snapshot root 內。
- 目標 Agent 的格式與 scope 確實受 Adapter 支援。

不包含中央 malware service、秘密掃描審核、Publisher approval 或 trust gate。格式錯誤或 path traversal 必須拒絕匯入；含 scripts/commands 顯示風險訊號與內容摘要。

GitHub Source 可由使用者選擇 branch、tag 或 commit。每次 Resolve、匯入或更新都必須把 ref 解析成 immutable commit SHA，並同時保存原始 ref 與 SHA；本機 snapshot 不會因 branch head 改變而靜默變更。手動 Check for Updates 對 branch 比較目前 head SHA，對 tag/commit 比較可用的新 ref，只有使用者確認後才建立並部署新 snapshot。

## 9. Agent Target Adapter

第一版 Windows Targets：

```text
codex-cli
codex-vscode
claude-code-cli
cursor-windows
github-copilot-vscode
```

每個 Adapter 宣告：

- 偵測方式與預設路徑。
- Skill global/project 支援能力。
- MCP global/project 支援能力。
- MCP config 格式。
- 是否支援所選 transport。
- 可寫入目標與可選 path override。

不支援的 Target/scope 在 UI 停用並說明；不靜默降級，不用 global 假裝 project。

Wingman 允許讀寫 Agent Target 路徑以完成偵測、diff、merge、copy、update 與 remove。此能力不代表 Wingman 可以將 Agent Target 的現有內容當成 Wingman Runtime。

## 10. Scope

### 10.1 Global

- 第一版僅表示當前 Windows 使用者的 per-user global。
- 不寫入 machine-wide、ProgramData、HKLM 或其他 Windows 使用者目錄。
- 目標路徑由 Adapter 解析。

### 10.2 Project

- 使用者選擇 project root，UI 可預選 Git root。
- Project Skill 直接 copy 到目標 Agent 的 project path，可被 Git commit。
- 同一 Skill 部署到多個 Agent 時可在 repo 存在多份原樣複本。
- `.wingman/extensions.lock.json` 記錄 artifact ID、version、source hash、target、path 與 deployed hash。
- Clone 含 lock file 的 repo 後不自動寫入；Wingman 顯示可手動 Apply。

## 11. Skill 部署

```text
Select artifact
→ Detect verified compatible Agent Targets
→ Select one or multiple targets, or Install to all compatible detected targets
→ Select global/project for each target
→ Resolve every target path
→ Read existing state and build one batch plan
→ Show per-target copy plan, conflicts and prerequisites
→ User confirms once
→ Execute one transactional deployment per target
→ Record every path/hash/deployment and batch result
```

規則：

- 「一鍵跨 IDE 安裝」表示使用者在同一流程選擇多個相容 Target、只確認一次，由 Wingman 實際執行所有 copy/config 操作；不是產生 shell command 交給使用者執行。
- UI 提供 `Install to all compatible detected targets` 快速操作，但仍先顯示目標、scope、路徑、衝突與變更摘要。
- 每一個已選 Target 都必須由使用者明確選擇 `global` 或 `project`；Wingman 不根據上次選擇、Git root 或 Target 預設值自動決定 scope。若 Target 只支援一種 scope，UI 仍顯示該固定值與原因。
- Global 與 project 第一版都使用 copy，不使用 symlink/junction。
- Adapter 原樣 copy Skill，不轉換內容。
- 同名未受 Wingman 管理內容存在時，停止並顯示衝突；不覆寫。
- 更新前比對 deployed hash。
- 使用者已修改時，停止覆寫並顯示 diff。
- 移除時若內容已改，不刪除；解除 ownership 並標記 `DetachedDueToDrift`。
- 不自動 Adopt。使用者可手動選擇資料夾匯入為新的本機 artifact。
- 跨 Target batch 不宣稱跨檔案系統全域 atomic。每個 Target 必須個別 transactional；batch 可能為 `PartialSuccess`，UI 必須列出成功、失敗、可重試與可回復項目，不可隱藏部分完成。
- Installed 詳細頁提供 `Remove from all managed targets`；執行前列出所有 Wingman-managed deployment、drift 與預計保留項目，只移除能證明 ownership 且未 drift 的內容。部分失敗仍保留成功移除結果並回報 `PartialSuccess`。

## 12. MCP 配置

```text
Select MCP definition
→ Detect verified compatible Agent Targets
→ Select one or multiple targets, or Configure all compatible detected targets
→ Select supported scope for each target
→ Read every existing target config
→ Convert definition to each target config shape
→ Preserve unrelated user entries
→ Insert secret placeholders
→ Show one batch preview with per-target diff
→ User confirms once
→ Atomic write per target
→ Record every deployment and batch result
```

規則：

- Wingman 只負責 config 讀寫。
- 每一個已選 Target 都必須由使用者明確選擇支援的 scope；不沿用 Skill、其他 Target 或前一次操作的 scope。
- Wingman 不啟動 MCP、不呼叫 MCP tool、不作 network health check。
- Wingman 可檢查 `command` 是否可在 PATH/宣告路徑找到，只用於顯示 runtime 提示。
- Runtime 缺失仍可寫入 config，狀態為 `Configured / PrerequisiteMissing`。
- Wingman 絕不替使用者安裝 runtime。
- Secret 欄位寫入 `REPLACE_WITH_YOUR_API_KEY`，狀態為 `NeedsUserInput`。
- 使用者直接在 Agent config 填入真實 Key。Wingman 不儲存、顯示或 log Key。
- Wingman 再次讀取 config 時對 secret 欄位 redact，並將 secret value 排除於 drift hash。
- 更新 MCP definition 時，必須保留使用者已填入的 secret，除非該 secret 欄位已從新 definition 移除。
- 同一 MCP 若有多個 secret 欄位，所有欄位仍使用同一個 `REPLACE_WITH_YOUR_API_KEY`；UI 另顯示欄位名稱與說明。
- Project config 內的 Key 是否被 Git 追蹤由使用者負責；Wingman 只顯示警告，不阻止。
- 若同一 MCP ID 已存在且無法證明由 Wingman 建立，停止並顯示衝突。
- MCP 跨 IDE 配置遵循與 Skill 相同的 batch semantics：每個 config write 個別 atomic，整批可為 `PartialSuccess`，可只重試失敗 Target。
- `Remove from all managed targets` 同樣適用 MCP，只刪除 Wingman 建立的 entry 並保留同一 config 中其他使用者內容。

Config write 使用 temporary file + atomic replace；失敗時回復原檔，不需要長期 backup retention。

## 13. Wingman Plugin Runtime

已確認：

- Plugin 安裝只寫入 Wingman Plugin Store。
- 安裝後不自動 enable。
- Enable 前顯示 Skills、MCP、scripts、hooks、commands 與權限說明。
- 禁止 install/update/uninstall lifecycle scripts。
- Runtime hook 可宣告任意外部 command，但必須列出 event、executable、arguments、working directory、可見 env 名稱與觸發時機，使用者 Enable 後才可執行。
- Disable 後移除該 Plugin 的 Wingman capabilities/hooks/MCP availability。
- Uninstall 預設保留 Plugin data，只有獨立「刪除資料」操作才清除。
- Plugin 更新由使用者手動執行。
- Plugin component 不可 export 到外部 Agent Target。
- 禁止 Plugin 攜帶或載入 .NET DLL、native DLL 或其他 in-process binary extension。
- 「MCP 只配置不啟動」僅適用於部署到外部 Agent Target 的 MCP。
- Wingman Plugin 已 enable 且 Wingman Agent Runtime 實際使用該 component 時，Wingman Runtime 可啟動 stdio MCP 或連線 remote MCP。
- Plugin MCP 的 runtime 仍由使用者自行安裝；缺失時該 component 不可用，但不影響 Plugin 其他 component。

Plugin 安裝/更新階段不啟動 MCP；只有 enable 後的 Wingman Agent Runtime 可在使用期間啟動或連線。

### 13.1 Plugin Function / Hook 的宣告式 Runtime

Plugin Function 與 Hook 各自必須二選一：使用 legacy `executable`，或使用 `runtime` 加 `entrypoint`。兩者不可同時存在。

```json
{
  "id": "uppercase",
  "runtime": { "kind": "python", "version": ">=3.12" },
  "entrypoint": "scripts/uppercase.py",
  "arguments": ["{{input.text}}"],
  "workingDirectory": "."
}
```

- 支援的 `runtime.kind`：`python`、`node`、`powershell`；entrypoint 必須為 Plugin root 內的相對路徑，且副檔名必須與 runtime 相符。
- Runtime 透過 Wingman 共用 `IRuntimeResolver` 解析；會依既有優先序尋找 Plugin／專案／Wingman 管理／Wingman bundled／系統 runtime。因此使用純 Python 或 Node 標準函式庫的 Plugin，不需使用者另行安裝 runtime。
- Plugin Function 與 Hook 不會自動安裝 runtime 或相依套件。需要第三方 package 的 Plugin 必須隨封裝提供可用依賴，或採用使用者明確管理的 runtime；缺少 prerequisite 時僅使該 component 失敗，不影響其他 component。
- `executable` 是既有 Plugin 的向後相容模式，仍依使用者環境執行；新 Plugin 應優先採用 `runtime` + `entrypoint`，不得寫死 Wingman 安裝路徑。

Plugin MCP 也可在 `.mcp.json` 的單一 server definition 使用相同的 `runtime` 與 `entrypoint` 欄位。Wingman 將它轉為 transient MCP stdio definition，並以解析後的 bundled/managed runtime 啟動；不使用 `npx`，不在使用者端下載或安裝 npm package。Plugin artifact 必須自行攜帶 entrypoint 的 production dependency tree。

若 MCP `env` 使用 `REPLACE_WITH_YOUR_*` placeholder，Desktop 的 Plugin 設定對話框會列出該欄位。使用者輸入的值以目前 Windows 使用者的 DPAPI 加密保存於 Wingman 唯一 SQLite DB；讀取 API 只回傳是否已設定，絕不回傳值。儲存設定、Enable 與 Disable 都會刷新 MAF MCP catalog；Disable 必須移除該 Plugin 已註冊的 MCP tools。

Hook 執行規則：

- Wingman 只在已 enable Plugin 收到 manifest 允許的 Wingman/MAF event 時，透過共用 `ManagedProcessRunner` 啟動 hook；hook 不會因安裝、更新或 discovery 自動執行。
- `ManagedProcessRunner` 必須實作 timeout、cancellation、stdout/stderr 大小上限、exit-code capture、並行數上限與 process-tree 終止；不得提升權限或繞過 Windows 使用者權限。
- Wingman 不替 hook 安裝 executable/runtime。找不到 command 時該 component 標記 `PrerequisiteMissing`，不影響 Plugin 其他 component。
- 參數以結構化 argument list 傳入，不經 shell 字串拼接；working directory 必須位於 Plugin root 或 Plugin data root。
- 不把 Settings PAT、MCP API Key 或其他未在 manifest 明確宣告且由使用者配置的 secret 自動注入 hook。
- Hook command 不自動成為 Agent 可呼叫工具；只有 Plugin 另外宣告並通過驗證的 Function component 才能透過 MAF 暴露。
- Disable Plugin 後不再接受新事件；正在執行的 hook 依 disable policy 取消並記錄 exit/result，不留背景 orphan process。

## 14. 狀態模型

### Discovery / Resolution

```text
Discovered
Scored
Stale
Resolving
Resolved
ManualSetupRequired
ManualReviewRequired
Invalid
```

### Installability

```text
Unknown
Compatible
PartiallyCompatible
Incompatible
BlockedByConflict
RejectedByValidator
```

### Skill

```text
Available
Downloaded
Deployed
Drifted
DetachedDueToDrift
Removed
```

### MCP

```text
Available
Downloaded
Configured
NeedsUserInput
PrerequisiteMissing
Removed
```

MCP 不使用 `Ready` 或 `Unhealthy`，因為 Wingman 不啟動或測試 MCP。

### Plugin

```text
Available
InstalledDisabled
Enabled
Disabled
RemovedDataRetained
Removed
```

## 15. UI 資訊架構

Desktop 側邊欄原「Skill 技能庫」更名為單一頂層 `Marketplace`。不再建立 Extension Marketplace 與 Plugin Marketplace 兩個入口。

```text
Marketplace
├─ Discover
├─ Skills
├─ MCP Servers
├─ Wingman Plugins
├─ Installed
│  ├─ By Target
│  ├─ Global
│  ├─ By Project
│  └─ Wingman Plugins
├─ Updates
└─ Sources
```

分頁定義：

- `Discover`：GitHub refresh 與使用者明確匯入的 Discovery Records；可依 Artifact Kind、Functional Category、兩種分數、分類可信度、驗證狀態、相容 Target、runtime、MCP transport、風險訊號與更新時間篩選。`unsupported-project` 不出現在一般瀏覽結果。
- `Skills`：已分類為 Skill 的 discovered/resolved items；只有 `Compatible` 顯示安裝按鈕。
- `MCP Servers`：已分類為 MCP 的 items；只有可產生 canonical definition 且 target compatible 才顯示配置按鈕。
- `Wingman Plugins`：僅安裝到 Wingman 的 Plugin；不顯示外部 Agent Target。
- `Installed`：統一管理 Skill deployments、MCP configurations 與 Plugin installations。
- `Updates`：只顯示使用者手動 Check for Updates 後發現的 branch head SHA、tag 或 commit 變更；不自動套用。
- `Sources`：顯示 GitHub Discovery query profile/上次 refresh 結果、官方 Plugin GitHub owner allowlist、直接 GitHub URL 與本機/Codex 明確匯入紀錄；不管理商品 JSON Index。

Marketplace 頁首提供「重新整理」按鈕。按下後才以 Settings GitHub PAT 執行 GitHub Discovery；未設定 PAT 時顯示前往 Settings，不要求 Marketplace 重複輸入。成功、部分成功與失敗均以通知顯示結果；成功通知文字與 8.2 節一致。

Marketplace 卡片至少顯示：

```text
Name / Artifact kind
Primary functional category / secondary categories
Actual provenance (GitHub owner/repo/ref/path、local folder/ZIP 或 explicit importer)
Discovery Score / Artifact Quality Score + 各自 profile/version
Risk signal
Resolution/validation status
Verified compatible Agent Targets
Resolved branch/tag/commit + immutable SHA
License 或 License unknown 警告
Installed target count / update state
```

若尚未解析，只顯示 `Analyze installability`；不得用 suggested platform 產生假的安裝按鈕或 package command。Repository 詳細頁依 artifact path 列出所有 Skill/MCP/Plugin candidates。MCP 無法解析 canonical definition 時顯示 `Manual Setup Required` 與缺少欄位，不顯示一鍵配置。

主要操作：

- Refresh Marketplace from GitHub。
- Add GitHub Repository URL。
- Import Folder/ZIP。
- Import Codex marketplace.json。
- Analyze Installability。
- Download to Local Registry。
- Install to selected/all compatible Agent IDEs。
- Deploy Skill。
- Configure MCP。
- Install/Enable/Disable/Remove Plugin。
- Check for Updates。
- Remove from all managed targets。
- Show Diff。
- Resolve Drift/Conflict。

## 16. Timeline 與本機記錄

Timeline 只是本機操作可視化，不是企業 Audit Service。

事件類型：

```text
marketplace.refresh_started
marketplace.refresh_completed
marketplace.refresh_partial_success
marketplace.source_synced
marketplace.discovery_failed
discovery.record_created
discovery.score_computed
artifact.resolution_started
artifact.resolved
artifact.installability_computed
source.added
artifact.imported
artifact.updated
deployment.batch_started
deployment.batch_partial_success
skill.deployed
skill.drift_detected
mcp.configured
mcp.prerequisite_missing
plugin.installed
plugin.enabled
plugin.disabled
plugin.hook_started
plugin.hook_completed
plugin.hook_failed
plugin.capability_invoked
deployment.removed
deployment.conflict
```

事件明確儲存 artifact kind、target、scope、project、version 與結果；Plugin runtime 事件另存 plugin/component/run provenance。UI 不再由事件名稱字串猜測 Skill/MCP/Plugin capability。

## 17. 安全與錯誤邊界

本產品不建立企業權限系統，但以下為必要的本機檔案安全，不屬於可移除的「權限功能」：

- 拒絕 archive path traversal、absolute path、junction escape 與 symlink escape。
- 不在匯入/安裝階段執行任何來源 script。
- Config write 必須是 diff-based、保留無關 entry 並可在寫入失敗時回復。
- 不覆寫無法證明由 Wingman 建立的同名 Skill/MCP。
- Secret 不進入 Wingman database、log 或 Timeline。
- Windows 檔案寫入被拒絕時，顯示真實目標路徑與 `AccessDenied`，不自動要求管理員權限。
- Discovery description、README、topics 與 metadata 都視為不可信輸入；只作資料解析與靜態分析，不送入 Wingman Agent 作為可執行指令。
- 分數與 `NoStaticSignal` 不代表安全認證。Marketplace 必須提供評分維度與 risk evidence，不使用無證據的「Verified Safe」。
- Provider/Resolver 的 HTTP、GitHub 與 archive 讀取必須有 timeout、大小、檔案數、redirect 與重試上限。

不需要中央 malware scanner、企業 secret scanner、artifact 簽章、發布審核或使用者登入。使用者主動新增的 Source 視為使用者信任來源。

## 18. 實作階段

### Phase 1：Marketplace Foundation 與 Discovery

- 建立獨立 `Wingman.Marketplace` class library、contracts、domain/application services 與 Agent Service infrastructure adapters；不新增第二個 process/database。
- 使用唯一 `AppDbContext` 與既有 EF Core/SQLite `wingman.db` 建立 Discovery、雙 Score、Source、Sync、Artifact、Deployment 與 Plugin entities。
- 使用 Settings PAT 的 `GitHubDiscoveryProvider` 與 `OfficialWingmanPluginGitHubProvider`，以及直接 GitHub URL、本機 folder/ZIP、Codex `marketplace.json` 明確 importer；不建立商品 JSON Index。
- 驗證 Settings PAT 經共用 `ISecretProtector` 保護；若現況仍為未保護儲存，先做 Settings credential migration，Marketplace 不新增第二套 PAT persistence。
- 手動 Refresh、批次 compare/upsert、GitHub node ID 去重、metadata fingerprint、三次成功 miss 才 Stale 與 5,000-record retention。
- Discovery normalization、固定分類 taxonomy、分類 evidence/confidence、版本化 `DiscoveryScore` 與搜尋排序。
- Marketplace `Discover/Skills/MCP Servers/Wingman Plugins` 基礎 UI。
- 對現有 Rust/Tauri Skill/MCP/Plugin metadata 執行一次性 migration，完成後關閉 Rust Marketplace table 讀寫。

### Phase 2：Artifact Resolver 與 Local Registry

- GitHub branch/tag/commit 解析為 immutable SHA，以及 folder/ZIP snapshot 到 staging/cache。
- Monorepo traversal 與 Skill/MCP/Plugin candidate detection。
- `agent-skill-standard/v1`、MCP/Plugin Validator、`ArtifactQualityScore`、risk signal、content hash 與 per-target Installability Result。
- Local Registry、手動 update checking 與 artifact detail UI。

### Phase 3：Skill 一鍵跨 IDE 安裝

- Agent Target capability matrix。
- 單一與多 Target global/project copy。
- Install to all compatible detected targets。
- Project lock file。
- Batch plan、per-target transaction、PartialSuccess、conflict、drift、manual import 與 remove。
- 每個 Target 明確 scope 選擇，以及 `Remove from all managed targets`。

### Phase 4：MCP 一鍵跨 IDE 配置

- MCP canonical definition。
- Target config adapters。
- 多 Target diff、merge、per-target atomic write、batch result 與 placeholder。
- Runtime presence hint，不安裝、不啟動。
- 無法解析 canonical definition 的 `ManualSetupRequired` 顯示與無一鍵配置狀態。

### Phase 5：Wingman Plugin

- Codex-format validator profile。
- Plugin Store、install、manual enable/disable/update/remove。
- Skills、MCP、hooks 與 Agent Profile loader。
- `MafPluginRuntimeAdapter`、immutable enabled capability snapshot 與 Agent 建立時的 MAF capability mapping。
- `ManagedProcessRunner` 的 hook timeout/cancel/output/exit-code/process-tree 控制。
- Component 隔離與無 export。

### Phase 6：Installed、Updates 與 Timeline 完整化

- 單一 Marketplace 頂層入口與全部分頁。
- Installed、Updates、Deployments、Sources 與 Plugin management。
- 明確狀態、diff、conflict 與 typed timeline events。

## 19. 驗收條件

- 沒有中央 Registry Server 仍可完成所有核心功能。
- 不登入任何帳號仍可匯入、管理與部署。
- Desktop 側邊欄只有一個 `Marketplace` 頂層入口，內含 Discover、Skills、MCP Servers、Wingman Plugins、Installed、Updates 與 Sources。
- Discovery/評分/Resolver 由獨立 `Wingman.Marketplace` C# 模組實作，Agent Service 提供 infrastructure adapters，且只使用唯一 `AppDbContext/wingman.db`；產品不需要 Python、FastAPI、PostgreSQL、Supabase 或第三方目錄 API。
- Marketplace 不建立、下載或維護商品 JSON Index；只有使用者按下「重新整理」時，才使用 Settings GitHub PAT 執行版本化 GitHub query profile。
- GitHub refresh 會以 GitHub node ID 去重並與 SQLite 批次 compare/upsert；完整成功後顯示新增、更新、未變更與 stale 數量，失敗不清空既有 catalog。
- Catalog 自動發現紀錄不超過 5,000 筆；repository 連續三次完整成功 refresh 未出現才標記 `Stale`，已安裝、已解析、已收藏及手動 URL 不自動刪除。
- Marketplace item、分數與風險訊號均為 Wingman 自有資料與計算結果，不顯示借鑑產品名稱為來源。
- Artifact Kind、Functional Category、Agent Target Compatibility、Technical Facet 與狀態分欄保存，不使用一個 category 字串混合所有概念。
- 第一版 Functional Category 使用本 SPEC 列出的 18 個 stable ID；無足夠證據時必須回落 `other`，不可強制猜測。
- SuggestedKind 與 ResolvedKind 分開；只有 Resolver 可產生 Verified ResolvedKind。
- `DiscoveryScore` 與 `ArtifactQualityScore` 分開保存、顯示各自 profile/evidence，不能互相覆蓋。
- Discovery Record 在未解析 artifact path、snapshot 與 target compatibility 前沒有安裝按鈕。
- 一個包含多個 `SKILL.md` 的 repository 可解析成多個 Artifact Candidate。
- 一般 Marketplace 隱藏 `unsupported-project`；直接 GitHub URL 分析仍可明確顯示不支援原因。
- Skill validation 保存 `agent-skill-standard/v1` profile，且每個 Target 仍需獨立驗證相容性。
- 缺少 license 的 artifact 可繼續分析/安裝，但所有決策畫面顯示 `License unknown`。
- Wingman 不把 GitHub repo 名猜成 npm/PyPI/Docker package，也不把 repo URL 猜成 remote MCP endpoint。
- 可從 GitHub、folder 或 ZIP 匯入 Skill/MCP/Plugin。
- 線上 Wingman Plugin 只來自內建官方 GitHub owner/organization allowlist且通過 Validator；其他 Plugin 僅允許明確本機/Codex 匯入。
- Codex `marketplace.json` 只透過使用者明確匯入轉換，不讀取 Codex 個人路徑。
- 可將同一 Skill 以 copy 安裝到兩個原生相容 Agent Target。
- 可在同一安裝流程勾選多個相容 Agent Target、只確認一次並由 Wingman 實際完成部署，不只顯示命令。
- 跨 Target batch 發生部分失敗時，成功項目與失敗項目分開記錄，狀態為 `PartialSuccess` 並可只重試失敗 Target。
- 每個 selected Target 都要求使用者明確選擇 per-user global 或 Agent 真正支援的 project scope；不自動推斷或沿用。
- Project deployment 寫入 `.wingman/extensions.lock.json`。
- Skill 發生 drift 時不覆寫、不刪除。
- MCP 配置保留使用者無關 entry 與已填入 secret。
- MCP secret 預設值為 `REPLACE_WITH_YOUR_API_KEY`。
- MCP 無法確定解析 canonical definition 時顯示 `ManualSetupRequired`，且不提供一鍵配置。
- Wingman 不啟動外部 Agent Target 的 MCP process 也可完成配置。
- Runtime 缺失只顯示提示，不觸發安裝。
- Plugin 只安裝到 Wingman，沒有 exports 與外部 Agent 部署按鈕。
- Plugin 安裝後不自動 enable，runtime hooks 只在手動 enable 後生效。
- Plugin hook 可宣告任意外部 command，但只由受管理 runner 執行，具 timeout/cancel/output limit/exit code/process-tree control，不提升權限、不安裝 runtime、不自動注入 secret。
- Plugin 不載入 in-process DLL；Plugin MCP 僅可由 enable 後的 Wingman Runtime 在使用期間啟動或連線。
- Enabled Plugin 經 `MafPluginRuntimeAdapter` 在建立 Agent 時映射為 immutable MAF capability snapshot；Marketplace domain 不直接依賴 MAF。
- 升級時舊 Rust/Tauri Skill/MCP/Plugin metadata 只 migration 一次；完成後 Marketplace table 只由 C# Agent Service 寫入，不雙寫。
- Skill/MCP 可從 Installed 詳細頁執行 `Remove from all managed targets`；跨 Target 部分失敗保留成功結果並清楚顯示。
- 存在 deployment/installation 的 artifact 無法從 Local Registry 刪除。
- Wingman 不讀取 Codex Plugin cache 作為 Runtime，Codex cache 刪除不影響 Wingman。
- 匯入包含 path traversal 時拒絕，不寫出 staging root。
- Config write 失敗可回復原檔，不留下半寫入 JSON/TOML。

## 20. 最終產品決策紀錄

### D1：Marketplace 的可瀏覽清單從哪裡來？

- A. 維護 Wingman 商品 JSON Index。
- B. 使用 Settings PAT 直接執行版本化 GitHub Search/API query，結果保存到 SQLite。（已確認）
- C. A + B。

決策：B。只有手動 Refresh 才查 GitHub，不作背景 refresh；SQLite 是 materialized catalog，不是另一份可發布 JSON Index。

### D2：Codex marketplace.json 在 Wingman 中扮演什麼角色？

- A. Wingman 的線上 Discovery Source。
- B. 只在使用者明確選擇檔案時匯入，轉成 Wingman records。（已確認）
- C. 不支援。

決策：B。Wingman 不讀取 Codex 個人路徑，也不以 Codex marketplace 取代 GitHub Discovery。

### D3：「MCP 只配置不啟動」是否也適用於 Wingman Plugin 內的 MCP？

- A. 只限外部 Agent Deployment；Wingman Runtime 在使用 Plugin 能力時仍可啟動/連線 Plugin MCP。（已確認）
- B. 所有情況都不啟動；Plugin 可帶 MCP config，但 Wingman Runtime 不使用它。
- C. Plugin 完全不允許內建 MCP。

決策：A。

### D4：Plugin 是否允許 in-process binary extension？

- A. 第一版禁止 .NET DLL/native DLL，只允許 declarative content、Skill、MCP、hook 與受管理 script。（已確認）
- B. 允許簽章不受限 DLL（與「不簽章」決策衝突）。
- C. 允許任意 DLL，由本機使用者承擔風險。

決策：A。

### D5：本機 artifact 已有 deployment/installation 時能否從 Local Registry 刪除？

- A. 不能；先移除所有 deployment/installation。（已確認）
- B. 可以；已 copy 的 Agent 檔案保留，deployment 變成 detached。

決策：A。

### D6：GitHub Source 更新追蹤哪一種 ref？

- A. 只允許 release/tag。
- B. 只追蹤 branch。
- C. 每個 Source 可選 branch/tag/commit，所有 artifact snapshot 固定到 immutable SHA。（已確認）

決策：C。branch 只有手動 Check for Updates 時比較 head SHA，永不靜默改變已安裝 snapshot。

### D7：同一 MCP 有多個 secret 時，是否全部使用同一 placeholder？

- A. 是，全部固定 `REPLACE_WITH_YOUR_API_KEY`，UI 另顯示欄位名稱。（已確認）
- B. 否，依欄位產生 `REPLACE_WITH_YOUR_<FIELD_NAME>`。

決策：A。

### D8：Skill、MCP 與 Wingman Plugin 是否共用同一個 Marketplace 頂層入口？

- A. 共用單一 `Marketplace`，以 Discover、Skills、MCP Servers、Wingman Plugins、Installed、Updates 與 Sources 分頁區分。（已確認）
- B. Skill/MCP 與 Plugin 使用兩個頂層 Marketplace。
- C. Plugin 只放在 Settings，不進 Marketplace。

決策：A。

### D9：Discovery 與評分是否依賴外部目錄產品？

- A. 不依賴；Wingman 只借鑑公開概念與演算法思路，以 C#、EF Core/SQLite 自行實作 Provider、分類、評分與本機 searchable catalog。（已確認）
- B. 直接讀取外部目錄 API/靜態 index，並顯示其名稱與分數。
- C. 同時保存外部分數與 Wingman 分數。

決策：A。

### D10：Marketplace 的安裝單位是 repository 還是已解析 artifact？

- A. 已解析 artifact；repository 只是一筆 Discovery Record，必須找到確切 path、固定 snapshot 並通過 target validation 才能安裝。（已確認）
- B. repository；依語言/category 猜測安裝命令。
- C. 兩者都可直接安裝。

決策：A。

### D11：Marketplace 分類是否使用單一 category 欄位？

- A. 不使用；拆成 Artifact Kind、Functional Category、Agent Target Compatibility、Technical Facets、Classification Confidence 與 lifecycle/installability status。（已確認）
- B. 只使用 Skill/MCP/Plugin/Other 四個 category。
- C. 將類型、用途、平台和狀態全部放進 tags。

決策：A。

### D12：GitHub Catalog 如何刷新與控制容量？

- A. 啟動/背景自動刷新，無筆數上限。
- B. 使用者按「重新整理」，讀取 Settings PAT，批次 compare/upsert；上限 5,000 筆，連續三次完整成功 refresh 未出現才 Stale。（已確認）
- C. 每次進入頁面清空並重抓。

決策：B。refresh 成功後必須顯示新增、更新、未變更與 stale 摘要；失敗保留舊 catalog。

### D13：發現評分與 artifact 品質是否使用同一分數？

- A. 使用同一欄位，Resolver 後直接覆寫。
- B. 分成 repository-level `DiscoveryScore` 與 resolved artifact-level `ArtifactQualityScore`，各有 profile、breakdown 與 evidence。（已確認）
- C. 只評 resolved artifact。

決策：B。

### D14：安裝範圍與跨 Target 移除如何決定？

- A. 每個 selected Target 都由使用者明確選擇 scope，並提供 `Remove from all managed targets`；每個 Target 獨立 transaction，整批允許 `PartialSuccess`。（已確認）
- B. 自動沿用上次 scope，移除需逐一執行。
- C. 一律 global。

決策：A。

### D15：不完整 MCP、缺少 license 與不支援專案如何呈現？

- A. 全部隱藏或拒絕。
- B. 不完整 MCP 標記 `ManualSetupRequired` 且無一鍵配置；缺 license 允許但警告；`unsupported-project` 從一般瀏覽隱藏、直接 URL 分析可說明。（已確認）
- C. 依 repository 名稱猜出可執行設定。

決策：B。

### D16：Wingman Plugin 的線上來源與 Hook 邊界為何？

- A. 線上只接受內建官方 GitHub owner/organization allowlist；其他 Plugin 只允許明確本機/Codex 匯入。Hook 可執行任意使用者機器已有的 command，但必須經受管理 runner、手動 enable、不得提權/安裝 runtime/自動注入 secret。（已確認）
- B. 接受任意 GitHub Plugin 且安裝後自動執行 hook。
- C. 完全禁止 Hook。

決策：A。

### D17：Marketplace、資料庫與 Microsoft Agent Framework 如何切分？

- A. Marketplace 自成獨立 service 與第二顆 database。
- B. Marketplace 是獨立 `Wingman.Marketplace` class library，但仍為 Agent Service modular monolith，共用唯一 `AppDbContext/wingman.db`；MAF 由 infrastructure `MafPluginRuntimeAdapter` 整合。（已確認）
- C. Marketplace domain 直接依賴 MAF 與 Desktop UI。

決策：B。這讓 Marketplace 邏輯可獨立測試，又避免跨 process、跨資料庫與循環依賴。

### D18：現有 Rust/Tauri Skill/MCP 資料如何過渡？

- A. 永久雙寫 Rust 與 C# tables。
- B. 不 migration，直接清空。
- C. 一次性 migration 到 C# `AppDbContext`，完成後 Rust 停止讀寫 Marketplace tables。（已確認）

決策：C。

以上 18 項均已確認。本 SPEC 的產品邊界自 1.2 起依本文件管理，後續變更必須先修訂本文件，再修改 data schema、Discovery/Classification/Scoring contract、Adapter contract、MAF integration 或 Desktop API contract。
