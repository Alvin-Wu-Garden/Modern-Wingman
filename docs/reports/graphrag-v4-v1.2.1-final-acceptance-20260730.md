# GraphRAG V4 V1.2.1 最終驗收摘要

日期：2026-07-30  
分支：`codex/graphrag-v4-refactor`  
實機專案：`D:\FBL_Release_Trunk`  
Graph 版本：`graphrag-v4.0.8`

## 最終結論

V1.2.1 的功能重構、正確性防護、儲存發布流程、背景 Community AI Summary、
前端進度提示與測試均已完成。B6 的 90% community 覆蓋率未達標，
依使用者 2026-07-30 的明確指示列為「核准例外」，不再為追求門檻擴大設計。

仍須誠實保留一項規格差異：V4 完整索引雖在 10 分鐘絕對預算內完成，
但相較同一批資料的 V3 相對效能門檻仍未達標。此差異不影響索引正確性、
問答使用或發布一致性，但若未來要正式關閉所有效能門檻，應另立效能工作項，
不可把本次功能重構繼續擴大。

## 實機索引結果

| 項目 | 結果 |
|---|---:|
| Full Index Run | `54a3270f217a4912ab877be4b9c61580` |
| No-op Run | `a087b7fe0aea4d849824f891c5e81eb7` |
| Manifest | `e2d3a3e084bc47bdb43c3ffd6ebebe02` |
| Canonical digest | `ba75011d2bd5dd4429b62bce01a69e400147c54ac103e79825625d90c8e57b37` |
| 完整索引時間 | 145,800 ms（2.43 分鐘） |
| No-op 時間 | 2,615 ms |
| 原始檔案 | 13,133 |
| 原始碼位元組 | 309,602,025 |
| Graph nodes / edges | 18,401 / 42,097 |
| Evidence nodes / edges | 18,401 / 42,097 |
| Peak working set | 7,261,986,816 bytes（低於 8 GiB） |
| Storage stable | PASS |
| 索引錯誤 | 0 |

## Community 結果

| 項目 | 結果 | 判定 |
|---|---:|---|
| C0 communities | 14 | PASS |
| C1 resolved anchors | 384 / 1,130 | PASS；其餘明確標成 unresolved |
| C2 reports | 100 | PASS |
| C2 最小成員數 | 9 | PASS |
| C2 invalid reports | 0 | PASS |
| Connected primary community | 10,402 / 14,110（73.72%） | 使用者核准例外 |

Community AI Summary 採背景佇列執行；本次外部 AI provider 的 50 個 summary
全部失敗，但 deterministic structural summary、索引與問答仍可使用，符合
「AI enrichment 失敗不得破壞 structural graph」的降級設計。右下角進度提示
可顯示 queued、running、completed、failed 與百分比。

## 問答品質與延遲

品質報告 `quality-v4.0.5-final.json` 使用同一個 FBL 專案資料集：

- 29 題，每題 5 次 warm-up、10 次量測，共 290 次量測。
- Local retrieval P95：184 ms，門檻 2,000 ms，PASS。
- blocked cases：0；missing seeds：0。
- Certain edge precision：100%。
- Probable edge precision：98%。
- Internal CALLS golden recall：100 / 100，PASS。
- SQL Module dependency golden：PASS。

V4.0.8 最終重跑因移除會使 Working Copy 結果過期的 Source/Answer 快取後，
完整壓測耗時過長而中止；沒有為了報表速度恢復不正確快取。最終變更已由
針對性測試與完整 240 項單元測試覆蓋。

## 最終回歸

| 驗收 | 結果 |
|---|---|
| AgentService unit tests | 240 / 240 PASS |
| Desktop TypeScript typecheck | PASS |
| Contracts TypeScript typecheck | PASS |
| First-party `.cs/.ts/.tsx/.ps1` 2,000 行限制 | 245 files checked，0 violations |
| 最大 first-party 檔案 | `CSharpGraphExtractor.cs`，1,998 行 |
| SQLite 非 Graph 資料前後 hash/count | 15 tables，0 differences |
| AgentService port 5002 cleanup | PASS |

第一次完整測試曾在既有 provider endpoint 測試的 Host shutdown 遇到一次
`TaskCanceledException`；立即以相同程式碼重跑後 240/240 全數通過，
判定為測試清理階段的偶發逾時，不是 GraphRAG 功能失敗。

## 效能差異

| 指標 | V3 | V4 | 規格判定 |
|---|---:|---:|---|
| Full Index | 0.76 分鐘 | 2.43 分鐘 | 相對門檻 FAIL；10 分鐘絕對預算 PASS |
| Files/min | 16,436.00 | 5,404.53 | 相對門檻 FAIL |
| MB/min | 387.60 | 121.51 | 相對門檻 FAIL |

本次不再為相對效能數字加入更多快取、背景服務或索引分支，以免重新引入
Working Copy 陳舊內容與維護成本。建議日後只針對 profiler 找出的熱點另開
小型效能工作，不改動 V4 canonical/evidence/publish contract。

## 驗收證據

- `docs/reports/graphrag-v4-acceptance-v4.0.8-final.md`
- `artifacts/graphrag-v4-quality/quality-v4.0.5-final.json`
- `artifacts/graphrag-v4-quality/graphrag-v4-quality-tests.trx`

