# 知識圖譜功能操作手冊

> 適用對象：不熟悉 GraphRAG、Neo4j 或 Cypher 的一般使用者  
> 文件日期：2026-08-02

## 1. 先認識三個名詞

- **節點（Node）**：一個可被辨識的知識實體，例如程式類別、功能入口、資料表或畫面。
- **關係（Relationship／Edge）**：兩個節點之間有方向的連線，例如 A 呼叫 B、A 讀取資料表 B。
- **GraphRAG**：先從知識圖譜找出相關內容，再提供給 AI 回答的技術；查看圖譜本身不會呼叫模型，也不會消耗 AI 點數。

## 2. 畫面區域

| 區域 | 用途 |
|---|---|
| 上方搜尋列 | 全域搜尋節點、選擇畫布顯示筆數、切換檢視 |
| 左側「圖譜概覽」 | 查看總數，依節點類型、功能角色、關係類型篩選 |
| 中央結果區 | 顯示關聯圖、資料表或原始資料 |
| 搜尋結果列 | 顯示最佳命中；點擊項目會載入該節點與一階鄰居 |
| 右側「選取內容」 | 查看所選節點／關係的屬性、鄰居與進階 Cypher |

左右兩側都可用圓形箭頭收合，也可拖曳分隔線調整寬度。左側在收合狀態時，滑鼠移到左邊會以浮動方式暫時展開，不會推動中央畫面。

## 3. 左側分類是什麼

分類由目前圖譜的 descriptor（描述資訊）動態產生，未來可能隨 GraphRAG schema 改變。

### 節點類型

表示節點屬於哪一大類知識。例如目前常見：

- `Code`：程式碼實體。
- `Data`：資料相關實體。
- `EntryPoint`：使用者或系統進入功能的入口。

選「不限」會取消此類限制；選特定項目只載入符合的節點。

### 功能角色

表示節點在系統中的用途或責任。同一個節點類型可以有不同角色，例如 controller、business-service、repository、table、procedure。這些是目前資料實際提供的值，不是畫面寫死的清單。

### 關係類型

表示兩個節點之間的方向性語意，例如：

- `CALLS`：來源呼叫目標。
- `HANDLES`：來源負責處理目標。
- `READS`：來源讀取目標。
- `WRITES`：來源寫入目標。
- `ROUTES_TO`：來源路由到目標。

選取關係類型後，只顯示參與該關係的節點與連線。

## 4. 常用操作

### 4.1 瀏覽整體圖譜

1. 開啟「查看知識圖譜」。
2. 等待上方顯示「搜尋涵蓋全部 N 筆」。
3. 從筆數下拉選單選擇畫布大小。
4. 建議先用 1,000 筆觀察結構，需要更多資料再提高。
5. 使用滑鼠滾輪縮放，拖曳空白處移動畫布。

筆數只控制畫布顯示成本，不限制全域搜尋。10,000 節點可能使 layout（版面運算）與操作明顯變慢，只有整體觀察需要時再使用。

### 4.2 全域搜尋節點

1. 在「全域搜尋節點」輸入關鍵字。
2. 按 Enter 或放大鏡。
3. 上方結果列會顯示最佳命中及分數。
4. 點選一筆結果。
5. 系統載入該節點及一階鄰居，並切換到關聯圖。

搜尋會查目前專案的完整 active graph，不是只搜尋畫面已載入的 1,000／2,000 筆。

### 4.3 使用左側篩選

1. 展開節點類型、功能角色或關係類型。
2. 點選一個或多個分類。
3. 目前條件會以 chip（條件標籤）顯示在中央上方。
4. 點 chip 的 `×` 可移除單一條件；「清除全部」可重設。

提醒：目前展開鄰居是探索操作，新增的一階鄰居不會重新套用左側分類。若要回到嚴格篩選結果，重新點選條件或按「重新載入」。

### 4.4 選取節點或關係

- 點節點後，右側顯示節點名稱、分類、鄰居摘要與 properties（屬性）。
- 點關係後，右側顯示關係類型、來源、目標與關係屬性。
- 若右側已收合，點選節點／關係時會自動打開。
- 節點與關係有放大的透明 hit area（點擊區），不必精準點到細線中心。

### 4.5 將目前結果置中

點「將目前結果置中」只調整縮放與位置，不會查詢或新增資料。

### 4.6 展開鄰居

1. 先選取節點。
2. 選「全部」、「傳入」或「傳出」。
3. 系統顯示實際新增的節點與關係數。
4. 沒有新資料時顯示「此節點的一階鄰居已全部載入」，且不再次縮放。
5. 已展開的節點按鈕變成「已展開」；選取另一個未展開節點後恢復操作。

## 5. 三種檢視

### 關聯圖

適合查看方向、群聚與上下游關係。節點顏色依分類決定，關係顏色依類型決定。

### 資料表

適合逐筆查看完整資料：

- 「節點／關係」頁籤可切換資料種類。
- 拖曳欄名右側分隔線可調欄寬；每次新查詢會重新設定。
- 水平區域可直接用滑鼠滾輪左右捲動。
- 超長內容以按鈕開啟獨立檢視視窗，避免整張表被撐開。
- 執行 Cypher 後，表格改為顯示該查詢的 rows（資料列）。

### 原始資料

以 JSON 顯示目前結果，適合除錯、確認欄位或交給開發人員分析。一般使用者通常使用關聯圖與資料表即可。

## 6. 儲存圖譜

### 儲存圖片

1. 切換到關聯圖。
2. 點「圖片」。
3. 選擇儲存位置。
4. PNG 會使用目前主題的背景與畫面色系，並以 2 倍比例輸出；最大邊長為 4,096 像素。

### 儲存圖譜資料

1. 點「圖譜資料」。
2. 選擇儲存位置。
3. 系統輸出 JSON，內容包含專案、descriptor 與目前載入的通用圖譜資料。

若取消系統的儲存視窗，不會建立檔案，也不會顯示成功訊息。

## 7. Cypher 查詢（進階）

Cypher 是 Neo4j 的圖形查詢語言。一般搜尋與篩選不需要 Cypher；只有需要精確資料條件或自訂回傳欄位時才使用。

### 7.1 操作方式

1. 展開右側「Cypher 查詢（進階）」。
2. 點「填入『目前圖譜概覽』範例」，或輸入自己的唯讀查詢。
3. Enter 執行；Shift+Enter 換行。
4. 結果顯示在中央，並進入「Cypher 查詢結果」模式。
5. 可在關聯圖、資料表、原始資料之間切換。
6. 點「返回一般瀏覽」恢復搜尋與左側篩選的結果來源。

點選節點或關係後，也可按「以 Cypher 查詢此節點／關係」。這段語法由後端依目前 schema 產生，不是前端固定寫死。

### 7.2 必須遵守的規則

- 只能執行單一 statement（陳述式）。
- 必須是唯讀 `MATCH` 查詢。
- 禁止 CREATE、MERGE、INSERT、SET、DELETE、REMOVE、DROP、CALL、LOAD CSV 等寫入或管理操作。
- 每個節點 pattern（模式）必須保留 `:GraphEntity`、`$projectId`、`$graphVersion`，確保只讀目前專案版本。
- 不必自己寫 `LIMIT`；服務端會套用目前結果上限。
- 查詢中的 label、property、relationship type 是目前 V3 實體 schema。未來 schema 改版後，請改用當時 descriptor 提供的新範本。

### 7.3 可測試的語法

#### 範例一：目前圖譜概覽

```cypher
MATCH (n:GraphEntity {
  projectId: $projectId,
  graphVersion: $graphVersion
})
OPTIONAL MATCH (n:GraphEntity {
  projectId: $projectId,
  graphVersion: $graphVersion
})-[r]->(m:GraphEntity {
  projectId: $projectId,
  graphVersion: $graphVersion
})
RETURN n, r, m
```

#### 範例二：搜尋名稱或可搜尋文字含 moneymarket 的節點

```cypher
MATCH (n:GraphEntity {
  projectId: $projectId,
  graphVersion: $graphVersion
})
WHERE toLower(coalesce(n.name, '')) CONTAINS 'moneymarket'
   OR toLower(coalesce(n.searchableText, '')) CONTAINS 'moneymarket'
RETURN n
```

#### 範例三：找 Code 類型中的 controller

```cypher
MATCH (n:GraphEntity {
  projectId: $projectId,
  graphVersion: $graphVersion
})
WHERE n.kind = 'Code'
  AND n.role = 'controller'
RETURN n
```

#### 範例四：依檔案路徑搜尋

```cypher
MATCH (n:GraphEntity {
  projectId: $projectId,
  graphVersion: $graphVersion
})
WHERE toLower(coalesce(n.filePath, '')) CONTAINS 'authmanagement'
RETURN n
```

#### 範例五：查 CALLS 關係

```cypher
MATCH
  (source:GraphEntity {
    projectId: $projectId,
    graphVersion: $graphVersion
  })-[r]->(target:GraphEntity {
    projectId: $projectId,
    graphVersion: $graphVersion
  })
WHERE type(r) = 'CALLS'
RETURN source, r, target
```

#### 範例六：找指定節點及所有直接關係

把 `AuthManagementController` 換成想查的名稱：

```cypher
MATCH (n:GraphEntity {
  projectId: $projectId,
  graphVersion: $graphVersion
})
WHERE toLower(coalesce(n.name, '')) CONTAINS 'authmanagementcontroller'
OPTIONAL MATCH (n:GraphEntity {
  projectId: $projectId,
  graphVersion: $graphVersion
})-[r]-(neighbor:GraphEntity {
  projectId: $projectId,
  graphVersion: $graphVersion
})
RETURN n, r, neighbor
```

## 8. 常見狀況

### 搜尋有結果，但畫面沒有所有節點

搜尋結果列只顯示最佳候選。點選其中一筆後，系統才載入該節點與一階鄰居。

### 「找不到符合的節點」

先清除不必要的左側條件，再用較短的關鍵字搜尋。搜尋會忽略英文大小寫。

### 畫面很慢

降低顯示筆數到 1,000 或 2,000，縮小節點標籤或關閉光暈。10,000 適合偶爾觀察全貌，不適合日常互動。

### Cypher 顯示 scope 或唯讀錯誤

先重新點後端提供的範本，再只修改 `WHERE` 與 `RETURN`。不要刪除每個節點 pattern 中的 `GraphEntity`、`projectId`、`graphVersion`。

### Cypher 結果似乎被截斷

進階查詢仍受服務端結果上限保護。縮小條件後再查；目前畫面尚未分別顯示 row 與 node 的截斷統計。

