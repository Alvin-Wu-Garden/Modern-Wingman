# AI 供應商與模型下拉選單篩選研究

## 目標

讓「對話」與「分析 JIRA 議題」共用的選擇器遵守兩個規則：

1. 供應商只顯示已完成驗證並成功儲存憑證的設定。
2. 模型只顯示目前帳號／憑證可使用、由供應商實際回報的模型。

本文件只記錄現況與最小調整方案，不修改產品程式碼。

## 結論

不需要新增另一套 picker、資料表或「enabled model」管理介面。

- 對話與 JIRA 分析已共用 `ProviderModelPicker`，因此只要修正這個元件的供應商過濾，即可同時套用到兩個功能。
- 後端已經具備「先向供應商驗證，再以 DPAPI 加密寫入 SQLite」的原子流程；資料庫中的 `hasStoredKey` 可作為「驗證並儲存成功」的既有證據。
- 模型端點目前只有 GitHub Copilot 與 OpenRouter 嘗試讀取即時清單，其他供應商仍回傳硬編碼清單；而 Copilot/OpenRouter 失敗時也會回傳 fallback。這些 fallback 無法保證屬於該帳號可用模型，應移除。
- 最小方案是：前端以既有 key status 篩掉未設定 provider；後端 `/models` 對所有 provider 使用已存憑證向既有模型清單 API 查詢，失敗時回傳空清單（或明確錯誤），不保存模型清單、不增加 schema。

## 現況資料流

### 1. 設定、驗證與持久化

設定頁的 `handleKeyBlur` 只做格式初篩；`handleSaveKey` 才會呼叫後端儲存 API。驗證成功後設定頁會重新載入 provider status。[SettingsPage.tsx:479](../apps/desktop/src/features/settings/components/SettingsPage.tsx#L479)

前端 `setProviderKey` 呼叫：

```text
PUT /api/providers/{profileId}/key
```

[client.ts:207](../apps/desktop/src/services/agent-api/client.ts#L207)

後端 `SetKey` 將 profile、候選 key 與 base URL 交給 `ProviderCredentialService.ValidateAndSaveAsync`。[ProviderEndpoints.cs:306](../apps/agent-service/src/Host/RestEndpoints/ProviderEndpoints.cs#L306)

`ValidateAndSaveAsync` 的行為是：

1. 選擇符合 profile 的 validator。
2. 先向實際 provider 驗證候選 key。
3. 驗證失敗不寫入。
4. 驗證成功才呼叫 `SetValidatedCredentialAsync`。
5. Copilot PAT 即使通過初步驗證，若 bundled runtime 啟用失敗，仍會還原舊設定。

[ProviderCredentialService.cs:18](../apps/agent-service/src/Infrastructure/Providers/ProviderCredentialService.cs#L18)

BYOK validator 已使用供應商模型端點驗證：

- OpenAI-compatible：`GET {baseUrl}/models`，Bearer token。
- Anthropic：`GET {baseUrl}/v1/models`，`x-api-key`。
- Azure：`GET {baseUrl}/openai/models?api-version=...`，`api-key`。
- Copilot：交由 bundled runtime 驗證。

[ProviderApiKeyValidators.cs:29](../apps/agent-service/src/Infrastructure/Providers/ProviderApiKeyValidators.cs#L29)

驗證成功的 key 最後由 `ProviderSettingStore.SetValidatedCredentialAsync` 使用 DPAPI 保護，寫入 `wingman.db` 的 `ProviderSettings.ProtectedApiKey`；API 不回傳明文。[ProviderSettingStore.cs:87](../apps/agent-service/src/Infrastructure/Persistence/ProviderSettingStore.cs#L87) [ProviderSettingEntity.cs:28](../apps/agent-service/src/Domain/Models/ProviderSettingEntity.cs#L28)

`GET /api/providers/{id}/key-status` 目前回傳 `hasStoredKey`；Provider 金鑰只接受設定頁驗證後的本機加密值。[ProviderEndpoints.cs:68](../apps/agent-service/src/Host/RestEndpoints/ProviderEndpoints.cs#L68)

### 2. 對話與 JIRA 共用選擇器

對話輸入框由 `MessageComposer` 掛載 `ProviderModelPicker`。[MessageComposer.tsx:162](../apps/desktop/src/features/chat/components/MessageComposer.tsx#L162)

JIRA 分析預覽也掛載同一個 `ProviderModelPicker`，並將選擇結果傳入 `analyzeJiraIssue` 的 `providerProfileId` 與 `modelId`。[JiraAnalysisModal.tsx:90](../apps/desktop/src/features/projects/components/JiraAnalysisModal.tsx#L90) [JiraAnalysisModal.tsx:229](../apps/desktop/src/features/projects/components/JiraAnalysisModal.tsx#L229)

因此不應分別修改兩個功能。

### 3. 供應商下拉的現有缺口

`ProviderModelPicker` 已為每個 profile 呼叫 `getProviderKeyStatus`，也已有 `isVerifiedProvider`：

```ts
return status.hasStoredKey
```

[ProviderModelPicker.tsx:24](../apps/desktop/src/features/chat/components/ProviderModelPicker.tsx#L24)

但驗證結果目前只用來挑第一個預設 provider；state 仍執行：

```ts
setProviders(loadedProviders)
```

所以 render 時仍列出全部 appsettings profiles。[ProviderModelPicker.tsx:61](../apps/desktop/src/features/chat/components/ProviderModelPicker.tsx#L61) [ProviderModelPicker.tsx:70](../apps/desktop/src/features/chat/components/ProviderModelPicker.tsx#L70)

### 4. 模型下拉的現有缺口

picker 在 provider 改變時呼叫：

```text
GET /api/providers/{id}/models
```

[ProviderModelPicker.tsx:94](../apps/desktop/src/features/chat/components/ProviderModelPicker.tsx#L94) [client.ts:267](../apps/desktop/src/services/agent-api/client.ts#L267)

後端目前的來源不一致：[ProviderEndpoints.cs:106](../apps/agent-service/src/Host/RestEndpoints/ProviderEndpoints.cs#L106)

| Provider | 現況 | 是否能保證為該帳號可用模型 |
|---|---|---|
| GitHub Copilot | SDK `ListModelsAsync`；失敗改用硬編碼 fallback | 成功時可以，fallback 不可以 |
| OpenRouter | 帶已存 key 呼叫 `/models`；失敗改用硬編碼 fallback | 成功時由 provider 回報，fallback 不可以 |
| OpenAI、Anthropic、Azure、Foundry、Custom | 直接回傳 `GetByokFixedModels` | 不可以 |

對應來源：[ProviderEndpoints.cs:132](../apps/agent-service/src/Host/RestEndpoints/ProviderEndpoints.cs#L132) [ProviderEndpoints.cs:181](../apps/agent-service/src/Host/RestEndpoints/ProviderEndpoints.cs#L181) [ProviderEndpoints.cs:235](../apps/agent-service/src/Host/RestEndpoints/ProviderEndpoints.cs#L235)

現有 domain 與 DB 沒有 `EnabledModels` 欄位；使用者需求可直接解讀為「該憑證向 provider 查詢後可見的模型」，不需另建本機 enable/disable 系統。

## 最小調整方案

### A. 前端：只保存並呈現已設定 provider

只修改共用 `ProviderModelPicker` 的載入邏輯：

1. 保留 `listProviders()` 與平行查詢 key status。
2. 建立 `configuredProviders`，只保留成功取得 status 且 `hasStoredKey === true` 的項目。
3. `setProviders(configuredProviders)`，不要再保存完整 `loadedProviders`。
4. 若目前選擇不在新清單中，改選第一個 configured provider；清單為空則把 provider/model 都清為 `null`。
5. status API 失敗的 profile 保守排除，不顯示為可用。

這一處會同時修正對話與 JIRA 分析。

不要把 `/api/providers` 本身改成只列 configured profiles：設定頁也使用這個 API，而且必須看得到尚未設定的 provider 才能輸入 key。篩選應只發生在共用 picker。

#### Provider 金鑰來源

Provider 金鑰只可在設定頁輸入，經後端向實際服務驗證後，以 DPAPI 加密保存於本機資料庫；Runtime、模型清單與對話流程都不讀取環境變數金鑰。

### B. 後端：模型清單一律取自目前憑證的 live endpoint

調整 `ProviderEndpoints.ListModels`：

1. 先確認該 profile 確實有可取得的 key；未設定時不回傳模型。
2. Copilot 保留 SDK `ListModelsAsync`，但 SDK 失敗時回傳空清單，不再回傳 fallback。
3. 所有 BYOK provider 使用 `ProviderSettingStore.GetApiKey(profile.Id)` 取得既有 key。
4. 重用 validator 已存在的 provider-specific URL/header 規則，向模型端點發出 authenticated GET。
5. 解析 provider 回傳的模型 ID，再沿用既有 `InferModelGroup` 分組。
6. HTTP 失敗、無法解析或空結果時回傳空清單（若要呈現原因，可用既有非 2xx 錯誤路徑；不要用假資料補滿）。
7. 移除或停止使用 `GetCopilotFallbackModels`、`GetOpenRouterFallbackModels`、`GetByokFixedModels`。

建議只抽出一個很小的共用 request builder，讓「驗證 key」和「列出 models」共用 URL/header 規則，避免兩處日後漂移；不要新增 repository、cache 或資料表。

### C. 選擇狀態

`selectDefaultModel` 已會在實際回傳清單中依偏好、profile 預設與第一筆模型選擇，因此可保留。[ProviderModelPicker.tsx:30](../apps/desktop/src/features/chat/components/ProviderModelPicker.tsx#L30)

但載入模型失敗或清單為空時，必須同步執行 `onModelChange(null)`，避免 UI 隱藏清單後仍送出上一個 provider 的 model ID。

### D. 本次不做

- 不新增「手動勾選 enabled models」設定頁。
- 不保存或快取模型清單。
- 不建立對話版與 JIRA 版兩套 picker。
- 不改 conversation/JIRA request contract。
- 不在本次加入完整的後端 provider/model allow-list 授權層；UI 顯示需求完成後，如要防止手動 API 呼叫任意 profile/model，再另開工作。

## Azure 注意事項

現有 validator 已把 Azure `/openai/models` 當成驗證端點，因此最小改動可先沿用現有 contract。不過 Azure 推論常以 deployment name 而非基礎 model ID 指定模型；實作時應用現有 Azure 測試帳號確認 `/openai/models` 回傳的 ID 是否可直接交給目前 Copilot SDK provider config。若不可以，Azure 應顯示 deployment 清單或在設定中使用 deployment name，不能用硬編碼 `gpt-*` 代替。

## 驗收測試

### 後端單元／端點測試

1. 無 stored key 的 profile 呼叫 `/models`，不回傳任何模型。
2. OpenAI-compatible 使用 Bearer token 呼叫 `{baseUrl}/models`，只回傳 response 中的 IDs。
3. Anthropic 使用 `x-api-key` 與 `/v1/models`，只回傳 response 中的 IDs。
4. Azure 使用 `api-key` 與既有 api-version URL，回傳可用的模型／deployment IDs。
5. Copilot SDK 回傳三個模型時，API 僅回傳這三個。
6. Copilot/OpenRouter/BYOK 的 HTTP、SDK 或 JSON 解析失敗時，不回傳硬編碼 fallback。
7. 無效 key 仍不寫入 DB；有效 key 仍以密文保存。保留並擴充既有 `ProviderCredentialServiceTests`、`ProviderApiKeyValidatorTests`、`ProviderEndpointPersistenceTests`。

### 前端元件測試

1. 七個 appsettings profiles 中只有兩個 `hasStoredKey=true` 時，provider dropdown 只顯示兩個。
2. status 讀取失敗的 provider 不顯示。
3. 已選 provider 被刪除 key 後，重新載入會選第一個可用 provider；若沒有可用 provider，provider/model 都變成 `null`。
4. 切換 provider 後，model dropdown 只顯示該 provider API 回傳的 IDs。
5. 模型請求失敗或回傳空陣列時，舊 model selection 被清除。

### 整合驗收

使用相同設定依序開啟：

1. 一般對話輸入框。
2. 專案的「分析 JIRA 議題」預覽。

兩處應顯示完全相同的 provider 順序與各自的 live model 清單；未驗證／未儲存 provider 不出現，移除 key 後也不再出現。
