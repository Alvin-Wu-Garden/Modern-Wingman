# GraphRAG V4 Phase 0 基線

記錄時間：2026-07-30（Asia/Taipei）

本文件只保存可重現的統計與雜湊，不保存資料庫密碼、連線字串或交易資料內容。

## V3 程式與 Snapshot

| 項目 | 基線 |
|---|---|
| Git commit | `e0daf6b` |
| Graph schema | `3.0` |
| Manifest | `9c017d23116241dba7167d187dfd1909` |
| Snapshot digest | `0c1b1eda208db944618e9719f852a051a28a6b3d87cf65609e440900a12ae7dc` |
| Node | 16,962 |
| Edge | 23,532 |
| V3 full wall-clock | 45,545 ms（manifest timestamps） |
| V3 processed files | 12,476 |
| V3 processed bytes | 308,425,594 |
| V3 evidence property size | 待啟動既有 Neo4j 後補測；V4 驗收以移除 `evidenceJson` 並相對縮減至少 80% 為硬門檻 |
| V3 peak working set | 舊 manifest 未保存；V4 已新增執行期量測，C3 因 scope 變更標示 N/A，C4/C5 仍為硬門檻 |

V4 的 `FullIndexAbsoluteBudgetMinutes` 固定為 **10 分鐘**。此上限包含
Project discovery、MSBuild/Synthetic、前端、ASPX、SQL live metadata、Evidence 與 Neo4j publish，
但不包含非阻塞的 Community AI Summary。

## 測試硬體

| 項目 | 值 |
|---|---|
| CPU | Intel Core i5-12450H，8 cores / 12 logical processors |
| RAM | 34,031,001,600 bytes |
| OS | Windows 11 Pro 10.0.26200 |
| 執行模式 | 本機、Debug baseline；正式報告分列 cold/warm |

## FBL Source Scope

以 V4 支援副檔名及排除目錄重新盤點：

| 項目 | 數量 |
|---|---:|
| Files | 13,178 |
| Bytes | 310,163,731 |
| C# | 9,891 |
| ASPX | 668 |
| TSX | 562 |
| SQL | 390 |

V4 新增 `.aspx` 並改為 project-aware compilation，因此與 V3 scope 不可直接比較。
正式 C3 倍率記為 N/A，改驗 10 分鐘絕對時間；C4 仍比較 files/min 與 MB/min。

## GraphRAG Table Allowlist

索引與驗收只允許改動以下 Modern Wingman SQLite 表：

- `project_index_manifests`
- `graph_evidence`
- `graph_publish_manifests`

`Projects` 只有索引狀態、manifest、node/edge count 與錯誤摘要欄位可因正式索引更新；
D4 的非 GraphRAG 內容雜湊會排除這些已知狀態欄位後另行核對。

## 非 GraphRAG SQLite 基線

格式：`table | row count | schema SHA-256 | content SHA-256`。

```text
Conversations | 8 | 0a6ac5a3ad08a4f1f18aaa898c4bfd0575325b60fe9e9cde9dab9546cc20fec4 | 653c6f4ad78d0c839065322a3f62157790eca742b51df2ddbd144fc01aa0f422
Messages | 64 | da56f1765564d67b94f709f0948281715e9fdb141cacce80205e21a99235a5af | 0afb25b2593e9a545537436149b03b78f21d2e58faa4f6b130a36e600ddfd2cc
ProviderSettings | 1 | 23196d5ece394f28be4f4d29449ce043aa1092e542ef46646026b9033a987c34 | 09e6147fc0f4fc8e6feb4f0b7e27b786bea4dfedf9fc577152939b8fe251dccb
artifact_candidates | 0 | 3946eb15820240689aad0bb5a3f1f6a8b55b0f7741c049c632aa40a4469f35f5 | e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
artifact_score_snapshots | 0 | 1a6019d7052ff9e5070546a6cefd48ff8c341b94a62d4e5fe28c5636e108a856 | e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
artifacts | 0 | 07d4d4a2f09fb3496503b08820f39abafbd839a4b99e72ff9b3535d970b8d488 | e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
deployments | 0 | 7ba24d2d55f669f93b9c3f0ae634754ea4a7a31b43bef13644ae67cb7d8547bd | e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
discovery_records | 493 | b14b161d2075430028aa0e1075d95f2866695565c042a6bf8778e7eaf5e46d0c | 49b37c681bea1270a6698e010d48af3218846bb7104d64034b9e55e1a1d4361e
discovery_score_snapshots | 610 | bc23be48c63f952e8d7c7a4496ed60f358283554551cf5ce9a10de3dcc621885 | efd65b6e04fb1d8e4d703d857e779cd3befc56150e020835ec85c8dcb43e3166
installability_results | 0 | cb6130719c06189261ccade7b79219d23385e178cec9c7a608992add921a9cf7 | e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
marketplace_sync_runs | 4 | f33ddcef03a608eec4aa2cfce2cc3fc411aece04f49b2b16c11ec3c68942d0c8 | 81420eb1dc52d6689eb1d2df614b87c4b64f3cebe80488d081b8b15d3bf918b8
project_database_configurations | 1 | bd46d04e71dd8bdac8f695c3412c0a996937f3aa5e548caf8c3595ada8f50675 | b13599408e2224815e1d98fd5df0635d3295b0ae3da874c9ab4fc7a67ee675ca
project_vcs_bindings | 0 | 683326e875529bd2302dbb0213f923ee80bfd1559bdfc0b3b6a1d57c26a37021 | e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
vcs_connection_profiles | 0 | b5c103b89328097d26b7155ba2678df285dda73b3d117051c4273770429ce7b9 | e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
vcs_credentials | 0 | 77d0b54fda84801ac4a177a65d5e20e86d3433c5946a235ca61534f412e8b0a7 | e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
```

正式驗收會重新計算相同清單；任何未在 allowlist 的差異都使 D4 失敗。
