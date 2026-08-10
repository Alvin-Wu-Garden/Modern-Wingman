import { AGENT_API_BASE_URL } from './client'

// ── 連線設定 DTO ──────────────────────────────────────────────────────────────

export type AtlassianAuthType = 'bearer' | 'basic'
export type AtlassianServiceType = 'jira' | 'wiki'

export interface AtlassianConnectionDto {
  serviceType: AtlassianServiceType
  baseUrl: string
  authType: AtlassianAuthType
  username: string | null
  hasSecret: boolean
  verified: boolean
  verifiedAt: string | null
  verifiedDisplayName: string | null
}

export interface AtlassianSettings {
  jira: AtlassianConnectionDto | null
  wiki: AtlassianConnectionDto | null
}

export interface ValidateConnectionInput {
  baseUrl: string
  authType: AtlassianAuthType
  username: string | null
  token: string | null   // 留空表示沿用既有 Token
  apiVersion: string | null
}

export interface ValidateConnectionResult {
  verified: boolean
  displayName: string
}

// ── JIRA 分析 DTO ─────────────────────────────────────────────────────────────

export interface JiraIssuePreview {
  key: string
  summary: string
  status: string
  issueType: string
  priority: string | null
  assignee: string | null
  updated: string | null
  projectKey: string
  projectName: string
}

export interface LocalJiraFileSummary {
  key: string
  summary: string
}

export interface AnalyzeJiraInput {
  projectId: string
  jiraKey: string
  providerProfileId: string | null
  modelId?: string | null
  localFileKey?: string
}

// ── API 函式 ──────────────────────────────────────────────────────────────────

async function json<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { error?: string } | null
    throw new Error(body?.error ?? `Atlassian 請求失敗 (${response.status})`)
  }
  return response.status === 204 ? undefined as T : (response.json() as Promise<T>)
}

export const getAtlassianSettings = (): Promise<AtlassianSettings> =>
  fetch(`${AGENT_API_BASE_URL}/api/atlassian/settings`).then(json<AtlassianSettings>)

export const validateAndSaveConnection = (
  serviceType: AtlassianServiceType,
  input: ValidateConnectionInput,
): Promise<ValidateConnectionResult> =>
  fetch(`${AGENT_API_BASE_URL}/api/atlassian/connections/${serviceType}/validate`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  }).then(json<ValidateConnectionResult>)

export const deleteAtlassianConnection = (serviceType: AtlassianServiceType): Promise<void> =>
  fetch(`${AGENT_API_BASE_URL}/api/atlassian/connections/${serviceType}`, {
    method: 'DELETE',
  }).then(json<void>)

export const listLocalJiraFiles = (): Promise<LocalJiraFileSummary[]> =>
  fetch(`${AGENT_API_BASE_URL}/api/atlassian/jira/local-files`).then(json<LocalJiraFileSummary[]>)

export const previewJiraIssue = (jiraKey: string): Promise<JiraIssuePreview> =>
  fetch(`${AGENT_API_BASE_URL}/api/atlassian/jira/preview`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ jiraKey }),
  }).then(json<JiraIssuePreview>)

/**
 * 發出分析請求（SSE 串流）。
 * onToken: 每個 token 回調；onDone: 完成時含 conversationId；onError: 錯誤 code。
 */
export async function analyzeJiraIssue(
  input: AnalyzeJiraInput,
  handlers: {
    onMeta: (conversationId: string, jiraKey: string, summary: string) => void
    onToken: (token: string) => void
    onDone: (conversationId: string) => void
    onError: (error: string) => void
  },
  signal?: AbortSignal,
): Promise<void> {
  const response = await fetch(
    `${AGENT_API_BASE_URL}/api/projects/${encodeURIComponent(input.projectId)}/analysis/jira`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        jiraKey: input.jiraKey,
        providerProfileId: input.providerProfileId,
        modelId: input.modelId ?? null,
        localFileKey: input.localFileKey ?? null,
      }),
      signal,
    },
  )

  if (!response.ok || !response.body) {
    const body = await response.json().catch(() => null) as { error?: string } | null
    handlers.onError(body?.error ?? `分析請求失敗 (${response.status})`)
    return
  }

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buf = ''
  let lastConversationId = ''

  while (true) {
    const { done, value } = await reader.read()
    if (done) break
    buf += decoder.decode(value, { stream: true })
    const lines = buf.split('\n\n')
    buf = lines.pop() ?? ''
    for (const chunk of lines) {
      const line = chunk.trim()
      if (!line.startsWith('data: ')) continue
      try {
        const obj = JSON.parse(line.slice(6)) as Record<string, unknown>
        if (obj.conversationId && obj.jiraKey && obj.summary) {
          lastConversationId = String(obj.conversationId)
          handlers.onMeta(lastConversationId, String(obj.jiraKey), String(obj.summary))
        } else if (obj.token) {
          handlers.onToken(String(obj.token))
        } else if (obj.done) {
          handlers.onDone(lastConversationId || String(obj.conversationId ?? ''))
        } else if (obj.error) {
          handlers.onError(String(obj.error))
        }
      } catch {
        // 忽略解析失敗的 SSE 片段
      }
    }
  }
}

/** 將 Atlassian 後端 error code 轉為繁體中文訊息。 */
export function atlassianErrorMessage(code: string): string {
  const map: Record<string, string> = {
    ATLASSIAN_NOT_CONFIGURED: 'Atlassian 尚未設定，請先至「設定 > 一般」完成連線設定。',
    ATLASSIAN_SECRET_NOT_FOUND: '找不到已儲存的 Token，請重新驗證連線。',
    ATLASSIAN_INVALID_URL: '服務網址格式不正確，請輸入 http 或 https 開頭的完整網址。',
    ATLASSIAN_AUTH_FAILED: 'JIRA 驗證失敗，請確認 Token 是否有效。',
    ATLASSIAN_FORBIDDEN: '已通過身分驗證，但沒有議題讀取權限。',
    ATLASSIAN_TLS_ERROR: '憑證驗證失敗，請確認企業根憑證是否已安裝。',
    ATLASSIAN_TIMEOUT: '連線逾時，請確認網路或服務可用性後重試。',
    JIRA_KEY_INVALID: 'JIRA 議題編號格式不正確。',
    JIRA_PROJECT_NOT_ALLOWED: '此 JIRA 專案不在允許清單中（僅支援 HD、NR）。',
    JIRA_ISSUE_NOT_FOUND: '找不到議題，或目前帳號無權檢視。',
    JIRA_RESPONSE_INVALID: 'JIRA 回應格式不符預期，請確認服務版本。',
    JIRA_RATE_LIMITED: 'JIRA 請求過於頻繁，請稍後再試。',
    JIRA_CONTENT_TOO_LARGE: '議題內容過大，無法處理。',
    AI_PROVIDER_NOT_CONFIGURED: '尚未設定 AI 供應商，請先至設定頁新增 API Key。',
    ANALYSIS_QUOTA_EXCEEDED: 'AI點數已達上線，請確認剩餘點數或更換 API Key。',
    AI_ANALYSIS_FAILED: 'AI 分析失敗，請確認 API Key 有效後重試。',
    ANALYSIS_CANCELLED: '分析已取消。',
  }
  return map[code] ?? `操作失敗：${code}`
}
