# 知識圖譜 Viewer Contract

本文件說明 Modern Wingman GraphRAG V4 與知識圖譜瀏覽器之間的穩定邊界。

## 設計原則

- `FblAuthority` 與 Neo4j V4 schema 是唯一的權威索引格式。
- Viewer 不直接依賴 Neo4j driver，也不自行拼接實體 schema。
- 後端以 `GraphVisualNode`、`GraphVisualEdge`、`GraphVisualSchema` 投影通用欄位。
- 舊的 `GET /api/projects/{id}/graph` 保留；新版 Viewer 可使用 `/graph/view` 與 `/graph/search`。
- 所有查詢都受 project、active graph version、結果上限與唯讀規則限制。

## Viewer Contract

節點會保留 V4 相容欄位，並附上通用投影：

```json
{
  "id": "opaque-node-id",
  "labels": ["Code"],
  "caption": "BondTradeController.Save",
  "category": "Code",
  "properties": {},
  "metrics": { "degree": 12 }
}
```

schema descriptor 會提供：

- `contractVersion`
- `graphRevision`
- `facets`（目前為 `node-category`、`edge-type`）
- `captionOptions`
- `capabilities`
- `queryTemplates`
- `queryHelp`

## API

| API | 用途 |
|---|---|
| `GET /graph/schema` | 取得 active V4 graph 統計與 Viewer descriptor |
| `GET /graph` | 舊版 bounded 初始圖，相容入口 |
| `POST /graph/view` | 以 facet filters 取得 bounded 初始圖 |
| `POST /graph/search` | 以 V4 full-text index 搜尋完整 active graph |
| `POST /graph/neighbors` | 展開指定節點鄰域 |
| `POST /graph/query` | 執行受限 read-only V4 Cypher |

`/graph/view` 範例：

```json
{
  "filters": [
    { "facetId": "node-category", "tokens": ["Code"] },
    { "facetId": "edge-type", "tokens": ["CALLS"] }
  ],
  "limit": 1000
}
```

`/graph/search` 不受初始畫布取樣範圍限制，但最多回傳 bounded 候選；找到節點後，前端再以既有 neighbors API 展開，避免一次下載整張圖。

## Schema 變更規則

若未來 FBL authority schema 擴充，應只調整 `Neo4jGraphStore` 的 Viewer mapping、facet 與查詢範本。不要把 V3 node/relationship model 帶回索引核心，也不要讓 React 元件硬編碼新的 Neo4j label。

每次變更至少驗證：

1. 節點與關係兩端都在同一回應中。
2. project 與 active graph version 隔離仍有效。
3. 搜尋涵蓋全部 active graph，而非只有目前畫布。
4. 查詢仍是 bounded、read-only。
5. `dotnet test`、desktop typecheck 與 production build 通過。
