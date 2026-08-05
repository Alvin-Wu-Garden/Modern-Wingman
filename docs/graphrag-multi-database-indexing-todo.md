# GraphRAG 多資料庫索引與原子發布 TODO

> 文件狀態：已實作並完成自動驗收（2026-08-05）  
> 適用範圍：Modern Wingman 專案解析的 GraphRAG 索引  
> 核心原則：資料來源由使用者設定決定；先驗證全部已設定的連線，全部可用才開始索引；全部索引成功才原子發布新版圖譜。

## 1. 已確認的產品決策

- [ ] 一個專案可同時設定 SQL Server 與 SQLite，兩者互不覆蓋。
- [ ] 索引時只處理「已完成設定」的資料來源；未填寫的 Provider 不建立連線、不執行查詢，也不列為失敗。
- [ ] 已設定的資料來源必須先完成設定驗證與唯讀連線測試，全部通過後才可掃描原始碼與抽取資料庫物件。
- [ ] 任一已設定資料來源連線失敗時，本次索引立即停止，不掃描專案、不建立暫存圖譜、不修改目前可用的 GraphVersion。
- [ ] 所有已設定資料來源的抽取、合併及驗證必須全數成功，才可一次切換為新版 GraphVersion。
- [ ] 任一資料來源在索引途中失敗時，丟棄本次暫存結果並保留上一版已發布圖譜；不得發布部分成功的圖譜。
- [ ] SQL Server 使用使用者當下選擇的資料庫，不得限定 `FBL_SPV_SIT`。
- [ ] SQL Server 的菜單數量取自該資料庫實際查詢結果，不得在 production code 或註解寫死 696。
- [ ] `FBL_SPV_SIT` 與 696 只可出現在明確標示為開發驗收資料集的測試、fixture 或驗收文件中。
- [ ] SQLite 不解析 FBL 專屬的 `tblMenuMap`、`tblAsyncConfirmSourceTypeMapping` 或 CustomReport 關係，只抽取可用的資料庫物件。
- [ ] 只有 SQLite 設定時，以「原始碼索引完成＋SQLite DB Object 索引完成」作為成功條件，不要求 Business Feature 或菜單鏈路。
- [ ] 外部資料庫只允許唯讀存取，不得執行新增、修改、刪除、DDL、migration 或 `EnsureCreatedAsync`。

## 2. 架構邊界與規模限制

### 2.1 內部資料與外部資料分離

- [ ] `AppDbContext` 只管理 Modern Wingman 自身資料；不得用來連線或映射使用者專案的 SQL Server／SQLite Schema。
- [ ] 外部資料來源設定可儲存在同一個 `wingman_dev.db`，但必須由獨立的設定 Store 管理，不加入外部資料庫 Entity Mapping，也不建立第二套 EF Core Domain Model。
- [ ] 外部資料庫查詢採 Provider Adapter 與原生唯讀 Connection：SQL Server 使用 `SqlConnection`，SQLite 使用 `SqliteConnection`。
- [ ] 不建立 `ExternalDataSourceDbContext`，不對外部資料庫呼叫 `Database.EnsureCreatedAsync()`，也不執行 EF migration。
- [ ] Provider Adapter 只負責：設定驗證、連線測試、Schema/Catalog 查詢及 Provider 專屬的唯讀抽取。
- [ ] 保留未來加入 Oracle、MySQL 的擴充點，但本次只實作 SQL Server 與 SQLite，不預先建立未使用的類別或套件。

### 2.2 避免過度設計

- [ ] 優先整併現有資料庫設定與 GraphRAG 類別，不為每個步驟建立 Interface、Factory、Strategy 或 Handler。
- [ ] 只在真正的 Provider 邊界建立一個小型抽象，例如 `IExternalDatabaseAdapter`；SQL Server 與 SQLite 各一個實作。
- [ ] 設定存取、連線前置檢查、Provider 抽取、發布協調各自責任清楚，但不得拆成大量零碎檔案。
- [ ] 不新增背景服務、訊息佇列、工作流框架或新的資料庫。
- [ ] 不因本次修改擴張 Graph schema 到 AST token、statement、local variable 等低價值節點。
- [ ] 所有新增或實質修改的 class、record、enum、constructor 與 method 必須具有清楚的繁體中文 XML 註解；過時或寫死特定資料庫／菜單數的註解必須刪除。

## 3. 設定模型與保存

- [ ] 將目前「每個專案只能有一筆資料庫設定」改為「每個 Project＋Provider 一筆設定」，確保 SQL Server 與 SQLite 可同時存在。
- [ ] 為設定資料建立唯一鍵 `(ProjectId, Provider)`，避免儲存 SQLite 時覆蓋 SQL Server，反之亦然。
- [ ] 設定模型至少保存：ProjectId、Provider、Server/FilePath、DatabaseName、非機密選項、加密後 Secret、更新時間與驗證狀態。
- [ ] 密碼與完整 Connection String 不得出現在 log、例外訊息、Graph metadata、Manifest 或 fingerprint。
- [ ] UI/API 讀寫設定時需維持向後相容；既有單一 SQL Server 設定必須能無資料遺失地轉成 SQL Server Profile。
- [ ] 明確定義「未設定」：必要欄位皆空白時視為未設定，不進行連線測試；只有部分必要欄位時視為設定不完整並阻止索引。
- [ ] SQLite 必須保存並正規化實體檔案路徑；啟動索引前檢查檔案存在且可用唯讀模式開啟。
- [ ] SQL Server 的 DatabaseName 必須來自使用者選擇結果，不得由後端改寫為預設資料庫。

## 4. 索引啟動前置閘門

### 4.1 設定完整性檢查

- [ ] 使用者按下「開始索引」後，先載入該專案所有 Provider 設定。
- [ ] 將資料來源分類為 `NotConfigured`、`Incomplete`、`ReadyForConnectionTest`。
- [ ] `NotConfigured` 直接略過；`Incomplete` 回傳具體缺少欄位並停止；不得因此嘗試建立預設連線。
- [ ] 至少保留原始碼索引能力；若未設定任何資料庫，明確顯示本次為 Source-only 索引。

### 4.2 唯讀連線測試

- [ ] 對每個 `ReadyForConnectionTest` 的 Provider 逐一測試連線，全部成功才進入正式索引。
- [ ] SQL Server Connection String 強制加入 `ApplicationIntent=ReadOnly`，連線後確認目前 Database 與使用者選擇一致。
- [ ] SQLite 強制使用 `Mode=ReadOnly`；不得在檔案不存在時自動建立資料庫。
- [ ] 連線測試使用短 timeout、支援取消，且結束後立即 Dispose connection。
- [ ] 連線測試只執行最小唯讀命令，不在此階段列舉完整 Schema。
- [ ] 任一 Provider 失敗時，彙整每個 Provider 的成功／失敗狀態後回傳，不進入原始碼掃描與資料庫抽取。
- [ ] 錯誤訊息只顯示 Provider、Server/FilePath 的安全識別、DatabaseName 與失敗原因；遮蔽帳密及敏感參數。
- [ ] 前端進度需依序呈現「檢查設定」與「測試資料庫連線」；失敗時停在正確步驟並提供可操作訊息。

## 5. Provider 抽取規則

### 5.1 SQL Server

- [ ] 使用通過前置測試的實際 Connection Profile 建立新連線，不依賴 `AppDbContext`。
- [ ] 從目前 Database 讀取 FBL authority data：`tblMenuMap`、`tblAsyncConfirmSourceTypeMapping`、CustomReport 與系統 DB Objects。
- [ ] 菜單基準集合與數量由當次查詢產生，驗證器不得要求固定數量。
- [ ] SQL Server 資料庫名稱必須傳入 node key、metadata、evidence 與驗證器，禁止硬編碼。
- [ ] 資料庫本身缺少 FBL authority tables 時，回報明確的 Schema 不相容錯誤，不得誤判成零菜單成功。

### 5.2 SQLite

- [ ] 使用 `sqlite_schema` 以唯讀方式抽取 `table`、`view`、`trigger`、`index` 等可用 DB Objects。
- [ ] 排除 SQLite 內部物件，例如 `sqlite_%`，避免無價值節點污染圖譜。
- [ ] 不查詢或模擬 FBL authority tables，不建立菜單、確認來源或 CustomReport 關係。
- [ ] SQLite-only 索引不得執行 SQL Server 專屬 validator。
- [ ] SQLite 檔案內沒有任何使用者 DB Object 時，回報可理解的空資料庫結果；是否成功由明確驗收規則判定，不以例外中斷服務。

### 5.3 多來源合併

- [ ] 原始碼只掃描一次；SQL Server 與 SQLite 各自抽取後再合併，避免重複解析專案。
- [ ] DB Object node key 必須包含 Provider、Database identity、Schema 與 Object name，避免不同資料來源同名物件碰撞。
- [ ] 建議格式：`db:{provider}:{databaseIdentity}:{schema}:{objectName}`；所有建立、檢索、驗證及 UI 顯示必須使用一致規則。
- [ ] Graph metadata 記錄本次實際參與的 Provider 與安全識別，不保存 Secret 或完整 Connection String。
- [ ] 跨來源不自動建立推論邊；只有原始碼或確定性設定提供證據時才建立關係。

## 6. 驗證與原子發布

- [ ] 每次索引建立獨立候選 GraphVersion，不直接覆寫目前 active version。
- [ ] Source graph、每個已設定 Provider 的抽取、Graph merge 與 Provider 專屬 validation 全部成功後，才原子切換 active GraphVersion。
- [ ] SQL Server validation 依當次實際菜單集合驗證可檢索鏈路，不使用固定 696。
- [ ] SQLite-only validation 只要求原始碼與 SQLite DB Objects 完整寫入並可檢索。
- [ ] SQL Server＋SQLite validation 同時驗證 SQL authority graph、SQLite catalog 及 node key 無碰撞。
- [ ] 任一階段失敗或取消時，清理候選版本／暫存資料並保留上一版 active GraphVersion。
- [ ] 發布失敗不得讓 UI 顯示「索引可用」；應顯示「本次失敗，仍使用上一版」及失敗 Provider。
- [ ] Manifest/fingerprint 納入 Provider、非機密資料庫識別及設定版本，使切換資料庫後必定重新索引。
- [ ] Fingerprint 不得因密碼重新加密或敏感字串差異而洩漏 Secret；只以安全設定版本／更新时间判斷失效。

## 7. 移除既有硬編碼與過時設計

- [ ] 移除 `GraphIndexingService` 對 `FBL_SPV_SIT` 的拒絕條件及 `ExpectedMenuCount: 696`。
- [ ] 將 `ProjectGraphDatabase`、FBL authority extractor、backend resolver、CustomReport resolver 與 validation 中的資料庫名稱改為動態值。
- [ ] 將所有 `db:FBL_SPV_SIT:*` production node key 改為新的 Provider-aware key。
- [ ] 移除 production validator 的固定資料庫名稱、固定菜單數與固定 golden DB key。
- [ ] 更新進度文字、錯誤訊息、XML 註解與行內註解，不再暗示只支援 `FBL_SPV_SIT` 或固定 696 項菜單。
- [ ] 保留 FBL 專屬 SQL Server authority 語意，但清楚標示它是 SQL Server Adapter 的功能，不是所有資料庫共用規則。
- [ ] 檢查所有搜尋、Graph viewer、Project Analysis Agent 與 manifest reader 是否仍假設舊 node key，並一次完成相容調整。

## 8. 測試 TODO

### 8.1 單元測試

- [ ] 未設定 Provider 時不嘗試連線。
- [ ] Provider 設定不完整時阻止索引並列出缺少欄位。
- [ ] SQL Server 與 SQLite 可同時保存，不互相覆蓋。
- [ ] SQL Server 使用任意 DatabaseName 建立安全唯讀連線，不改寫為 `FBL_SPV_SIT`。
- [ ] SQLite 使用唯讀模式且不建立不存在的檔案。
- [ ] 連線前置檢查任一失敗時，不呼叫 source scanner 與 extractor。
- [ ] SQL Server 菜單數為動態值，validator 對不同數量皆依實際集合驗證。
- [ ] SQLite-only 不執行 FBL authority query 或 menu validator。
- [ ] SQL Server 與 SQLite 同名 table 產生不同 node key。
- [ ] 任一 Provider 抽取失敗時不發布候選 GraphVersion。
- [ ] 全部成功時只進行一次 active GraphVersion 切換。
- [ ] 取消索引時保留上一版 active GraphVersion 並清理候選資料。

### 8.2 整合測試

- [ ] Source-only：未設定資料庫時完成原始碼索引並清楚標示資料庫未納入。
- [ ] SQL Server-only：使用非 `FBL_SPV_SIT` 測試名稱，完成動態菜單與 DB Object 鏈路。
- [ ] SQLite-only：完成原始碼＋SQLite DB Object 索引，無菜單也可成功。
- [ ] SQL Server＋SQLite：兩者連線成功後才掃描，兩套資料合併且可分別檢索。
- [ ] 雙 Provider 其中一個連線失敗：正式索引完全不啟動，舊圖譜保持可用。
- [ ] 雙 Provider 連線皆成功但其中一個抽取失敗：不發布半套圖譜，舊圖譜保持可用。
- [ ] 切換 UI 下拉選單資料庫後，fingerprint 失效並索引新資料庫，不殘留舊 DB identity。
- [ ] API、索引進度與 UI 正確顯示每個 Provider 的設定、連線、抽取與驗證狀態。

### 8.3 FBL 開發驗收資料集

- [ ] 以開發環境 `FBL_SPV_SIT` 執行 live acceptance；只在本節允許使用該名稱。
- [ ] 驗證 `tblMenuMap` 當前 696 筆啟用資料都能建立並檢索預期鏈路；696 只作此資料集的回歸基準。
- [ ] 驗證改用另一個 DatabaseName 或不同菜單數時，production pipeline 不會拒絕索引。
- [ ] 驗證 SQL Server 測試全程只執行 SELECT／metadata read，不執行任何寫入。

### 8.4 全專案回歸

- [ ] `dotnet build` 全數通過。
- [ ] AgentService／GraphRAG／UnitTests 全數通過。
- [ ] 前端 typecheck、test、build 全數通過。
- [ ] 專案解析問答、Graph viewer、一般對話、Atlassian、Marketplace 與啟停流程不得回歸。
- [ ] Code Review 檢查繁體中文註解、唯讀安全、Secret 遮蔽、原子發布與無固定資料庫／菜單數。

## 9. 開工順序

1. 先補測試，鎖定多 Provider 設定、唯讀前置檢查與原子發布行為。
2. 調整設定保存，使 Project＋Provider 可同時存在。
3. 建立最小 Provider Adapter 邊界並完成 SQL Server／SQLite 唯讀連線測試。
4. 移除 `FBL_SPV_SIT`、696 與舊 DB node key 的 production 硬編碼。
5. 實作 SQLite catalog 抽取及多來源 node key／metadata 合併。
6. 重構索引協調流程：設定檢查 → 全連線測試 → 單次 source scan → Provider 抽取 → 驗證 → 原子發布。
7. 更新 API、前端設定與索引進度顯示。
8. 執行單元、整合、FBL live acceptance 與全專案回歸，再由 Code Review 驗收。

## 10. 完成定義

只有同時符合以下條件，才可標記本 TODO 完成：

- [x] production code、錯誤訊息與註解不再限制 `FBL_SPV_SIT` 或固定 696。
- [x] SQL Server、SQLite 可單獨或同時設定，且設定不互相覆蓋。
- [x] 索引前會先驗證設定並測試所有已設定資料來源；失敗時完全不開始正式索引。
- [x] 外部資料庫全程唯讀，沒有 EF mapping、migration、`EnsureCreated` 或其他寫入路徑。
- [x] 所有已設定資料來源全數成功才原子發布；任何失敗均保留上一版圖譜。
- [x] SQLite-only 能以原始碼＋DB Objects 完成索引。
- [x] FBL 開發驗收資料集的 696 項菜單鏈路通過，且不影響其他資料庫與不同菜單數。
- [x] 全部自動測試、build、前端驗收及人工 Code Review 通過。

## 11. 實作與驗收紀錄

> 核對說明：前述 `[ ]` 保留為原始需求追蹤清單；本節是本次實作的正式完成與驗收紀錄，未列入的項目不代表尚未實作。

本次已完成本 TODO 所列的多 Provider 設定、唯讀前置閘門、SQLite catalog、動態 SQL Server authority graph、Provider-aware node key、單次原始碼掃描及候選 GraphVersion 原子發布／失敗回復。外部資料庫連線沒有使用 `AppDbContext`，也沒有執行 migration、`EnsureCreatedAsync` 或任何寫入操作。

已執行的驗收：

1. `dotnet build apps/agent-service/AgentService.csproj --no-restore`：0 警告、0 錯誤。
2. `dotnet test apps/UnitTests/AgentService.UnitTests.csproj --no-restore`：204 通過、0 失敗、0 略過。
3. `pnpm --dir apps/desktop typecheck`：通過。
4. `pnpm --dir apps/desktop build`：通過；僅保留既有 Vite chunk size 提示。
5. FBL live acceptance（`Category=LiveFblAcceptance`）：使用唯讀 SQL Server 連線，696 筆開發驗收菜單鏈路通過；連線字串只存在測試執行環境，未寫入原始碼、log 或 Graph metadata。

可重現的 FBL live acceptance 指令（僅允許唯讀查詢）：

```powershell
$env:FBL_LIVE_ACCEPTANCE='1'
$env:FBL_LIVE_ROOT='D:\FBL_Release_Trunk'
$env:FBL_LIVE_CONNECTION_STRING='Server=127.0.0.1,3301;Initial Catalog=FBL_SPV_SIT;User ID=FBLInvest;Password=<只在本機環境設定>;TrustServerCertificate=True;Encrypt=False;ApplicationIntent=ReadOnly;Connect Timeout=15'
dotnet test apps/UnitTests/AgentService.UnitTests.csproj --no-restore --filter "Category=LiveFblAcceptance"
```

`FBL_SPV_SIT` 與 696 僅出現在本節的開發驗收說明，以及明確標示為固定 oracle 的測試 fixture；production pipeline 的資料庫名稱、菜單數、node key 與 validator 均為動態值。
