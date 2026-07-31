# JIRA 測試資料 (本機檔案模式)

此目錄存放供「**無法連線 JIRA 的測試環境**」使用的本機 JIRA 議題 JSON 檔案。

## 啟用方式

在 `appsettings.Development.json` 中設定：

```json
{
  "LocalJiraFiles": {
    "Enabled": true,
    "Directory": "../../temp/jira-samples"
  }
}
```

## 檔案格式

每個檔案為一份 `NormalizedJiraIssue` 序列化 JSON，檔名為 `{JIRA_KEY}.json`。  
例如：`INNES1HD-1504.json`

### 產生方式

若有可連線的 JIRA 環境，可呼叫以下端點將結果存入此目錄：

```http
POST /api/atlassian/jira/analyze
{
  "projectId": "YOUR_PROJECT",
  "jiraKey": "INNES1HD-1504",
  "providerProfileId": null
}
```

或直接用任何方式把 `NormalizedJiraIssue` 物件序列化（`System.Text.Json` + `JsonStringEnumConverter`）後存入此目錄。

## 使用方式

1. `GET /api/atlassian/jira/local-files` → 取得此目錄下所有可選議題清單
2. 分析時在 request 中帶入 `localFileKey`：

```json
{
  "projectId": "YOUR_PROJECT",
  "jiraKey": "INNES1HD-1504",
  "localFileKey": "INNES1HD-1504"
}
```

啟用本機檔案模式時，`providerProfileId` 和 JIRA 連線設定均可省略（不會被呼叫）。

> **注意**：此目錄為開發測試用途，切勿存放真實敏感資料。  
> 生產環境請確保 `LocalJiraFiles.Enabled = false`（預設值）。
