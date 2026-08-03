# GraphRAG V4 驗收報告

- 執行時間：2026-07-30 17:06:09 +08:00
- 執行人：mingj
- commit hash：e0daf6b
- ProjectId：91eecf4369f147fa9f0cf26ecf729ec6
- Source：D:\FBL_Release_Trunk
- DB：NotMeasured（唯讀 API 未暴露 DB fingerprint／Promote Gate 明細）
- Neo4j：active Graph readiness gate 通過
- SQLite：Evidence readiness gate 通過；row 明細未直接量測
- CPU/RAM/冷熱機：logical CPU=12；RAM/冷熱機 NotMeasured
- CompilationMode：NotMeasured
- MSBuild 成功/失敗 Project 數：NotMeasured
- Synthetic/batch fallback 數：NotMeasured
- DB Promote Gate：NotMeasured
- Project index：status=Partial，manifest=61cb325213474c7487a4172f43e64238
- Graph schema：nodes=18401，edges=42097

## 1. A1～A18 Coverage/Precision/Recall

| # | 指標 | 狀態 | 實測 | 門檻 | 證據／限制 |
|---|---|---|---|---|---|
| A1 | Menu-backed Feature | PASS | Graph attributes.menuId=698；SQL executable=698 | 兩者相等（目前唯讀 SQL baseline=698） | 以 attributesJson 的非空 menuId 計算，不限 role=menu-feature；custom-report/approval 也可能源自 Menu。 |
| A2 | active/resolved menu 孤立率 | PASS | 0.00% (0/698) | <5% | active graph role/state/degree 聚合。 |
| A3 | 低價值 Code degree=0 | PASS | 0 | 0；高價值 unresolved 附清單 | 低價值定義為 Code type/module/frontend-module；高價值清單仍需人工覆核。 |
| A4 | procedure+function | PASS | 401 | >=250 | active graph role 聚合。 |
| A5 | FK DEPENDS_ON | PASS | evidence=463；canonicalEdges=420 | evidence>=463；canonical pair edges>=420 | constraint evidence 與 canonical edge 分開量測，避免 source/kind/target 去重造成假失敗。 |
| A6 | PluginReport dispatch | Manual | DISPATCHES_TO=676 | >=24；stub unresolved | dispatch 數已自動量測；stub unresolved 需人工確認。 |
| A7 | frontend-page | PASS | 1449 | >=600 | active graph role 聚合。 |
| A8 | Named View | NotMeasured | 649 | >=T0.3 baseline 90% | 以 DISPATCHES_TO 且 reasonCode=roslyn-view-result 量測；未提供 T0.3 baseline，禁止假 PASS。 |
| A9 | 另類基金 Feature→Page→JS | Manual | 未執行 fixture path | hop<=4 | 需要已核准 fixture 與人工語意確認。 |
| A10 | 利息收入 ReportKernel→Data | Manual | 未執行 fixture path | 路徑存在 | 需要已核准 fixture。 |
| A11 | TSX reachable | NotMeasured | 665/665 | >=T0.3 baseline 90% | 已量測 degree>0；未提供 T0.3 baseline。 |
| A12 | Edge V4 必要 properties | PASS | 42097/42097 | 100% | weight/confidence/reasonCode/evidenceCount/evidenceRef 聚合。 |
| A13 | 禁止 relationship properties | PASS | forbidden=0 / total=42097 | 100% 無禁止欄位 | 檢查 evidenceJson/sourceId/targetId keys。 |
| A14 | RMDAL Code | Manual | 963（degree>0: 960） | >=500 且抽樣有關係 | 數量與 connected 已量測；關係抽樣需人工。 |
| A15 | Edge Precision | Manual | 未載入 fixture/人工判定 | 依 spec §11.1 | 不得在缺少 Golden fixture 時假 PASS。 |
| A16 | Internal CALLS Golden Recall | Manual | 未載入 fixture/人工判定 | 依 spec §11.1 | 不得在缺少 Golden fixture 時假 PASS。 |
| A17 | SQL Module→Module | Manual | 未載入 fixture/人工判定 | 依 spec §11.1 | 不得在缺少 Golden fixture 時假 PASS。 |
| A18 | E3 ReasonCode 抽樣 | Manual | 未載入 fixture/人工判定 | 依 spec §11.1 | 不得在缺少 Golden fixture 時假 PASS。 |

## 2. B1～B9 Community

| # | 指標 | 狀態 | 實測 | 門檻 | 證據／限制 |
|---|---|---|---|---|---|
| B1 | C0 數量 | NotMeasured | 目前 API 未暴露 GraphCommunity 明細 | 依 spec §11.2 | 受限 Graph query 只允許 active GraphEntity；需新增唯讀 diagnostics API 或人工 Cypher。 |
| B2 | C1 Anchor | NotMeasured | 目前 API 未暴露 GraphCommunity 明細 | 依 spec §11.2 | 受限 Graph query 只允許 active GraphEntity；需新增唯讀 diagnostics API 或人工 Cypher。 |
| B3 | C1 Resolved member | NotMeasured | 目前 API 未暴露 GraphCommunity 明細 | 依 spec §11.2 | 受限 Graph query 只允許 active GraphEntity；需新增唯讀 diagnostics API 或人工 Cypher。 |
| B4 | C1 Unresolved | NotMeasured | 目前 API 未暴露 GraphCommunity 明細 | 依 spec §11.2 | 受限 Graph query 只允許 active GraphEntity；需新增唯讀 diagnostics API 或人工 Cypher。 |
| B5 | C2 reports/member | NotMeasured | 目前 API 未暴露 GraphCommunity 明細 | 依 spec §11.2 | 受限 Graph query 只允許 active GraphEntity；需新增唯讀 diagnostics API 或人工 Cypher。 |
| B6 | connected primary communityId | FAIL | 80.57% (11368/14110) | >=90% | 由 active GraphEntity degree/communityId 聚合。 |
| B7 | C1 parent 指向 C0 | NotMeasured | 目前 API 未暴露 GraphCommunity 明細 | 依 spec §11.2 | 受限 Graph query 只允許 active GraphEntity；需新增唯讀 diagnostics API 或人工 Cypher。 |
| B8 | shared 不作跨社群中介 | NotMeasured | 目前 API 未暴露 GraphCommunity 明細 | 依 spec §11.2 | 受限 Graph query 只允許 active GraphEntity；需新增唯讀 diagnostics API 或人工 Cypher。 |
| B9 | C2 reproducibility | NotMeasured | 目前 API 未暴露 GraphCommunity 明細 | 依 spec §11.2 | 受限 Graph query 只允許 active GraphEntity；需新增唯讀 diagnostics API 或人工 Cypher。 |

## 3. S1～S6 Search Seed

| # | 指標 | 狀態 | 實測 | 門檻 | 證據／限制 |
|---|---|---|---|---|---|
| S1 | 中文業務名稱 | Manual | 本腳本不呼叫問答/檢索 POST | Top-5>=90%；Exact Top-1>=95% | 需要固定 fixture 與保存 normalized query/seed。 |
| S2 | BondTradeService | Manual | 本腳本不呼叫問答/檢索 POST | Top-5>=90%；Exact Top-1>=95% | 需要固定 fixture 與保存 normalized query/seed。 |
| S3 | ProcessLogin 等 Method | Manual | 本腳本不呼叫問答/檢索 POST | Top-5>=90%；Exact Top-1>=95% | 需要固定 fixture 與保存 normalized query/seed。 |
| S4 | SettlementDate／交割日 | Manual | 本腳本不呼叫問答/檢索 POST | Top-5>=90%；Exact Top-1>=95% | 需要固定 fixture 與保存 normalized query/seed。 |
| S5 | /Controller/Action | Manual | 本腳本不呼叫問答/檢索 POST | Top-5>=90%；Exact Top-1>=95% | 需要固定 fixture 與保存 normalized query/seed。 |
| S6 | SP/Function 名稱 | Manual | 本腳本不呼叫問答/檢索 POST | Top-5>=90%；Exact Top-1>=95% | 需要固定 fixture 與保存 normalized query/seed。 |

## 4. C1～C12 Storage/Publish/Performance

| # | 指標 | 狀態 | 實測 | 門檻 | 證據／限制 |
|---|---|---|---|---|---|
| C1 | Neo4j relationship property size | NotMeasured | 目前唯讀 API 資料不足 | 依 spec §11.4 | 不直接讀 SQLite/Neo4j/外部 SQL，也不啟動寫入型 benchmark。 |
| C2 | SQLite/Neo4j Evidence count 對帳 | NotMeasured | 目前唯讀 API 資料不足 | 依 spec §11.4 | 不直接讀 SQLite/Neo4j/外部 SQL，也不啟動寫入型 benchmark。 |
| C3 | Full Index wall-clock | PASS | V4=2.42min；scope comparable=false；absolute budget=5.00min | <=1.5x；scope 不可比時 <=核准絕對預算 | GET index/run；可比性同時檢查 file/bytes 差異<=10%及明確環境確認。 |
| C4 | Normalized throughput | NotMeasured | V4=5,430.08 files/min, 122.08 MB/min；V3=0.00, 0.00 | files/min 與 MB/min 均不得較可比 V3 下降>50% | 以完整 run elapsed 與 stageMetrics 最大 processed source 數計算，避免跨 stage 重複加總。 |
| C5 | Peak Working Set | PASS | peak=7070134272 bytes；budget=8589934592 bytes | <=preflight budget；無 OOM/process crash | GET index/run peakWorkingSetBytes。 |
| C6 | no-op | PASS | status=succeeded, mode=no-op | 無變更不重建 | 讀取 mode=no-op 的獨立 run；不會覆蓋 full run 效能資料。 |
| C7 | Local Retrieval P95 | NotMeasured | 目前唯讀 API 資料不足 | 依 spec §11.4 | 不直接讀 SQLite/Neo4j/外部 SQL，也不啟動寫入型 benchmark。 |
| C8 | Hydration 單次 batch | NotMeasured | 目前唯讀 API 資料不足 | 依 spec §11.4 | 不直接讀 SQLite/Neo4j/外部 SQL，也不啟動寫入型 benchmark。 |
| C9 | Community template 即時可用 | NotMeasured | 目前唯讀 API 資料不足 | 依 spec §11.4 | 不直接讀 SQLite/Neo4j/外部 SQL，也不啟動寫入型 benchmark。 |
| C10 | AI failure 不影響可用性 | NotMeasured | 目前唯讀 API 資料不足 | 依 spec §11.4 | 不直接讀 SQLite/Neo4j/外部 SQL，也不啟動寫入型 benchmark。 |
| C11 | DB failure no-promote | NotMeasured | 目前唯讀 API 資料不足 | 依 spec §11.4 | 不直接讀 SQLite/Neo4j/外部 SQL，也不啟動寫入型 benchmark。 |
| C12 | Reconciliation failure matrix | NotMeasured | 目前唯讀 API 資料不足 | 依 spec §11.4 | 不直接讀 SQLite/Neo4j/外部 SQL，也不啟動寫入型 benchmark。 |

## 5. AI1～AI6 Background Summary

| # | 指標 | 狀態 | 實測 | 門檻 | 證據／限制 |
|---|---|---|---|---|---|
| AI1 | Publish 阻塞 | NotMeasured | 未觀察 publish 時段 | AI call=0 | 腳本不啟動索引。 |
| AI2 | Queue concurrency | Manual | current project running=0 | per-project<=1、global<=2 | 可觀察單一專案，無 global progress API。 |
| AI3 | cacheKey dedupe | NotMeasured | API 未暴露重複工作數 | 重複工作=0 | 需 queue diagnostics 或測試報告。 |
| AI4 | Progress API | PASS | total=14, queued=0, running=0, completed=0, failed=14, percent=100 | 欄位正確且狀態總和一致 | GET summaries/progress。 |
| AI5 | UI 分離顯示 | Manual | 未執行 UI 視覺驗收 | 結構可用與 AI 進度分開 | 需桌面 UI 人工確認。 |
| AI6 | Failure 保留 template | Manual | failed=14 | template 保留、無無限 retry | Progress 可見 failed，但 template/retry 明細未由 API 暴露。 |

## 6. Q1～Q8 Answer Quality

| # | 指標 | 狀態 | 實測 | 門檻 | 證據／限制 |
|---|---|---|---|---|---|
| Q1 | 另類基金畫面加欄位 | Manual | 未呼叫問答 API | 至少7/8達4分；Q6-Q8不得 missing seed | 需要人工 1~5 分、source snippet 與 known gaps fixture。 |
| Q2 | 利息收入增減分析 | Manual | 未呼叫問答 API | 至少7/8達4分；Q6-Q8不得 missing seed | 需要人工 1~5 分、source snippet 與 known gaps fixture。 |
| Q3 | tblPosition105 加欄位影響 | Manual | 未呼叫問答 API | 至少7/8達4分；Q6-Q8不得 missing seed | 需要人工 1~5 分、source snippet 與 known gaps fixture。 |
| Q4 | 公告管理驗證前端影響 | Manual | 未呼叫問答 API | 至少7/8達4分；Q6-Q8不得 missing seed | 需要人工 1~5 分、source snippet 與 known gaps fixture。 |
| Q5 | Bloomberg 匯率排程 | Manual | 未呼叫問答 API | 至少7/8達4分；Q6-Q8不得 missing seed | 需要人工 1~5 分、source snippet 與 known gaps fixture。 |
| Q6 | 登入流程 | Manual | 未呼叫問答 API | 至少7/8達4分；Q6-Q8不得 missing seed | 需要人工 1~5 分、source snippet 與 known gaps fixture。 |
| Q7 | 債券交易流程 | Manual | 未呼叫問答 API | 至少7/8達4分；Q6-Q8不得 missing seed | 需要人工 1~5 分、source snippet 與 known gaps fixture。 |
| Q8 | SettlementDate 影響 | Manual | 未呼叫問答 API | 至少7/8達4分；Q6-Q8不得 missing seed | 需要人工 1~5 分、source snippet 與 known gaps fixture。 |

## 7. D1～D6 Cleanup/Safety

| # | 指標 | 狀態 | 實測 | 門檻 | 證據／限制 |
|---|---|---|---|---|---|
| D1 | 穩定狀態只保留 active | NotMeasured | 唯讀 API 未暴露 inactive version/SQLite/檔案清單 | 依 spec §11.7 | 腳本不執行 cleanup，也不直接查資料庫，禁止假 PASS。 |
| D2 | Publish/Reconcile version 狀態 | NotMeasured | 唯讀 API 未暴露 inactive version/SQLite/檔案清單 | 依 spec §11.7 | 腳本不執行 cleanup，也不直接查資料庫，禁止假 PASS。 |
| D3 | SQLite 無 retired/orphan Evidence | NotMeasured | 唯讀 API 未暴露 inactive version/SQLite/檔案清單 | 依 spec §11.7 | 腳本不執行 cleanup，也不直接查資料庫，禁止假 PASS。 |
| D4 | 非 GraphRAG table 不變 | NotMeasured | 唯讀 API 未暴露 inactive version/SQLite/檔案清單 | 依 spec §11.7 | 腳本不執行 cleanup，也不直接查資料庫，禁止假 PASS。 |
| D5 | 暫存檔清理 | NotMeasured | 唯讀 API 未暴露 inactive version/SQLite/檔案清單 | 依 spec §11.7 | 腳本不執行 cleanup，也不直接查資料庫，禁止假 PASS。 |
| D6 | Publish failure 保留舊 active | NotMeasured | 唯讀 API 未暴露 inactive version/SQLite/檔案清單 | 依 spec §11.7 | 腳本不執行 cleanup，也不直接查資料庫，禁止假 PASS。 |

## 8. Stage Metrics/Normalized Throughput/Peak RAM

- Stage metrics：assemble=15638ms/files:0/bytes:0；extract=92853ms/files:13133/bytes:309602025；extract:aspx-source-v4=297ms/files:13133/bytes:309602025；extract:csharp-project-aware-v4=88197ms/files:13133/bytes:309602025；extract:frontend-source-v4=14197ms/files:13133/bytes:309602025；extract:java-source-v4=4ms/files:13133/bytes:309602025；extract:sqlserver-live-database-v4=4574ms/files:13133/bytes:309602025；extract:sqlserver-scriptdom-v4=19449ms/files:13133/bytes:309602025；publish=32858ms/files:0/bytes:0；scan=2961ms/files:13133/bytes:309602025
- no-op run：phase=complete；mode=no-op；elapsedMilliseconds=2803。
- normalized throughput：5,430.08 files/min；122.08 MB/min。
- Peak RAM：7070134272 bytes；preflight budget=8589934592 bytes。

## 9. Diagnostics 統計

- Node roles：controller-action=4664；type=3262；table=2257；frontend-page=1449；frontend-module=1291；repository=1004；custom-report=662；controller=516；report-data-source=458；report-plugin=410；csv-format=406；function=382；schedule=370；custom-enum=287；menu-feature=246；view=187；approval-feature=151；data-model=132；business-service=73；batch-report=69；scheduled-task=56；product-type=27；procedure=19；custom-product-type=18；report-data-source-group=5
- Edge reasonCode：roslyn-invocation=15127；scriptdom-read=9387；roslyn-route=4641；frontend-url=4549；es-import=3183；db-metadata=861；scriptdom-write=803；es-import=792；db-metadata=678；roslyn-view-result=649；fk-constraint=420；naming-convention=402；menu-link=370；db-metadata=123；scriptdom-exec=55；menu-link-base64=27；roslyn-task-name=22；roslyn-invocation=8
- Node kinds：Code=6688；Data=4046；EntryPoint=6169；Feature=1498
- Relationship types：CALLS=18712；DEPENDS_ON=1336；DISPATCHES_TO=676；HANDLES=5455；MAPS_TO=123；READS=9395；ROUTES_TO=4919；TRIGGERS=678；WRITES=803
- Summary progress message：14 個 AI 摘要失敗；結構模板仍可使用。

## 10. FAIL/Warning/Conditional 處置

- FAIL B6：connected primary communityId；actual=80.57% (11368/14110)。
- Manual/NotMeasured 共 52 項，正式驗收前必須補 fixture、人工判定或唯讀 diagnostics。

## 11. Known Gaps

- 未直接連線外部 SQL、Neo4j 或 SQLite；避免驗收腳本繞過應用程式的 read-only 邊界。
- 未觸發 full/no-op index、AI failure、publish failure 或 reconciliation；這些操作會改變本機狀態。
- 未提供 V3/T0.3 baseline、Golden fixture、人工 edge precision 與 Q1～Q8 分數。
- GraphCommunity 明細目前無受限唯讀 API，因此 B1～B5/B7～B9 無法自動驗證。

> 安全聲明：本腳本只寫入本報告檔；未執行新增、刪除、修改或 cleanup 資料操作。
