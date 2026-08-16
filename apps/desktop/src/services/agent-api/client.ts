/**
 * Agent Service 共用 REST client。
 * 所有前端功能應由此處取得同一個 Base URL，避免各模組各自寫死連線位址。
 */
export const AGENT_API_BASE_URL = 'http://localhost:5002'

// 桌面 App 啟動時，Agent Service sidecar 可能還沒完全就緒（port 尚未 listen），
// 這時候首次讀取專案/對話清單若直接失敗且不重試，畫面會永遠停留在空白。
// 這裡只針對「連線層失敗」（fetch 本身 throw，例如 ECONNREFUSED）做有限次重試，
// 已經連線上但回傳 4xx/5xx 的應用層錯誤不會被這裡吞掉。
const CONNECTION_RETRY_DELAYS_MS = [300, 800, 1500]

const wait = (milliseconds: number) =>
  new Promise<void>((resolve) => window.setTimeout(resolve, milliseconds))

/** 對初次載入類請求的連線失敗做有限次數重試；4xx/5xx 由呼叫端照常處理。 */
export async function fetchWithConnectionRetry(
  input: string,
  init?: RequestInit,
): Promise<Response> {
  for (let attempt = 0; ; attempt += 1) {
    try {
      return await fetch(input, init)
    } catch (error) {
      if (attempt >= CONNECTION_RETRY_DELAYS_MS.length) throw error
      await wait(CONNECTION_RETRY_DELAYS_MS[attempt])
    }
  }
}

export interface ConversationSummary {
  id: string
  title: string
  providerProfileId: string | null
  projectId: string | null
  createdAt: string
  updatedAt: string
}

export interface MessageItem {
  id: string
  role: 'user' | 'assistant'
  content: string
  createdAt: string
}

export interface ConversationDetail extends ConversationSummary {
  messages: MessageItem[]
}

export interface AttachmentReference {
  name: string
  contentBase64: string
  mediaType: string | null
}

/**
 * Agent 單次執行期間的安全進度事件。
 * 事件只描述階段與工具摘要，不包含模型私有推理或完整工具輸出。
 */
export interface AgentActivityEvent {
  type: string
  runId: string
  activityId: string
  status: 'started' | 'completed' | 'failed' | 'status' | string
  label: string
  tool: string | null
  detail: string | null
  elapsedMs: number | null
  sequence: number
  timestamp: string
}

export interface ProviderInfo {
  id: string
  displayName: string
  kind: string
  modelId: string | null
  providerType: string | null
  baseUrl: string | null
  sortOrder: number
  hasStoredKey?: boolean
  storedBaseUrl?: string | null
  runtimeStatus?: CopilotRuntimeStatus | null
}

export interface ProviderKeyStatus {
  profileId: string
  displayName: string
  hasStoredKey: boolean
  storedBaseUrl: string | null
  sortOrder: number
  runtimeStatus: CopilotRuntimeStatus | null
}

export interface CopilotRuntimeStatus {
  state: 'not_configured' | 'validating' | 'configured' | 'invalid'
  isAuthenticated: boolean
  login: string | null
  authType: string | null
  copilotPlan: string | null
  modelCount: number | null
  error: string | null
}

export async function listConversations(): Promise<ConversationSummary[]> {
  const response = await fetchWithConnectionRetry(`${AGENT_API_BASE_URL}/api/conversations`)
  if (!response.ok) throw new Error(`無法載入對話：HTTP ${response.status}`)
  return response.json()
}

/** 載入指定專案的對話；專案上下文由路由決定，不再透過 scope request 欄位傳遞。 */
export async function listProjectConversations(
  projectId: string,
): Promise<ConversationSummary[]> {
  const response = await fetchWithConnectionRetry(
    `${AGENT_API_BASE_URL}/api/projects/${encodeURIComponent(projectId)}/conversations`,
  )
  if (!response.ok) throw new Error(`無法載入專案對話：HTTP ${response.status}`)
  return response.json()
}

export async function createConversation(
  providerProfileId?: string | null,
): Promise<ConversationSummary> {
  const response = await fetch(`${AGENT_API_BASE_URL}/api/conversations`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      providerProfileId: providerProfileId ?? null,
    }),
  })
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { error?: string } | null
    throw new Error(body?.error ?? `無法建立對話：HTTP ${response.status}`)
  }
  return response.json()
}

/** 建立指定專案的對話；projectId 僅存在 URL，不放入 request body。 */
export async function createProjectConversation(
  projectId: string,
  providerProfileId?: string | null,
): Promise<ConversationSummary> {
  const response = await fetch(
    `${AGENT_API_BASE_URL}/api/projects/${encodeURIComponent(projectId)}/conversations`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ providerProfileId: providerProfileId ?? null }),
    },
  )
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { error?: string } | null
    throw new Error(body?.error ?? `無法建立專案對話：HTTP ${response.status}`)
  }
  return response.json()
}

const conversationPath = (id: string | null, projectId?: string | null) => {
  const prefix = projectId
    ? `/api/projects/${encodeURIComponent(projectId)}/conversations`
    : '/api/conversations'
  return id ? `${prefix}/${encodeURIComponent(id)}` : prefix
}

export async function getConversation(
  id: string,
  projectId?: string | null,
): Promise<ConversationDetail> {
  const response = await fetch(`${AGENT_API_BASE_URL}${conversationPath(id, projectId)}`)
  if (!response.ok) throw new Error(`無法載入對話：HTTP ${response.status}`)
  return response.json()
}

export async function deleteConversation(id: string, projectId?: string | null): Promise<void> {
  const response = await fetch(`${AGENT_API_BASE_URL}${conversationPath(id, projectId)}`, {
    method: 'DELETE',
  })
  if (!response.ok) throw new Error(`無法刪除對話：HTTP ${response.status}`)
}

export async function renameConversation(
  id: string,
  title: string,
  projectId?: string | null,
): Promise<void> {
  const response = await fetch(
    `${AGENT_API_BASE_URL}${conversationPath(id, projectId)}/title`,
    {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ title }),
    },
  )
  if (!response.ok) throw new Error(`無法重新命名對話：HTTP ${response.status}`)
}

/**
 * 傳送訊息並解析後端 SSE。附件只存在本次 request，不會寫入 GraphRAG。
 */
export async function sendMessage(
  conversationId: string,
  userMessage: string,
  providerProfileId: string | null,
  modelId: string | null,
  attachments: AttachmentReference[],
  handlers: {
    onToken: (token: string) => void
    onDone: () => void
    onError: (error: string) => void
    onActivity?: (activity: AgentActivityEvent) => void
  },
  signal?: AbortSignal,
  projectId?: string | null,
): Promise<void> {
  try {
    const response = await fetch(
      `${AGENT_API_BASE_URL}${conversationPath(conversationId, projectId)}/messages`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          userMessage,
          providerProfileId,
          modelId,
          attachments,
        }),
        signal,
      },
    )
    if (!response.ok) {
      const body = await response.json().catch(() => null) as { error?: string } | null
      handlers.onError(body?.error ?? `HTTP ${response.status}`)
      return
    }
    if (!response.body) {
      handlers.onError('服務未回傳串流內容。')
      return
    }

    const reader = response.body.getReader()
    const decoder = new TextDecoder()
    let buffer = ''
    while (true) {
      const { done, value } = await reader.read()
      if (done) break
      buffer += decoder.decode(value, { stream: true })
      const lines = buffer.split('\n')
      buffer = lines.pop() ?? ''
      for (const line of lines) {
        if (!line.startsWith('data: ')) continue
        const payload = line.slice(6).trim()
        if (!payload) continue
        try {
          const event = JSON.parse(payload) as {
            token?: string
            done?: boolean
            error?: string
            activity?: AgentActivityEvent
          }
          if (event.activity) handlers.onActivity?.(event.activity)
          if (typeof event.token === 'string') handlers.onToken(event.token)
          if (event.done) handlers.onDone()
          if (event.error) handlers.onError(event.error)
        } catch {
          // 忽略單一格式錯誤事件，後續 SSE 仍可繼續處理。
        }
      }
    }
  } catch (error) {
    if ((error as Error).name !== 'AbortError')
      handlers.onError(String(error))
  }
}

export async function listProviders(signal?: AbortSignal): Promise<ProviderInfo[]> {
  const response = await fetch(`${AGENT_API_BASE_URL}/api/providers`, { signal })
  if (!response.ok) throw new Error(`無法載入供應商：HTTP ${response.status}`)
  return response.json()
}

export async function getProviderKeyStatus(profileId: string, signal?: AbortSignal): Promise<ProviderKeyStatus> {
  const response = await fetch(`${AGENT_API_BASE_URL}/api/providers/${profileId}/key-status`, { signal })
  if (!response.ok) throw new Error(`無法取得供應商狀態：HTTP ${response.status}`)
  return response.json()
}

export interface KeyValidationResult {
  valid: boolean
  scopes?: string
  error?: string
}

export async function setProviderKey(
  profileId: string,
  apiKey: string,
  baseUrl?: string | null,
): Promise<KeyValidationResult> {
  const response = await fetch(`${AGENT_API_BASE_URL}/api/providers/${profileId}/key`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ apiKey, baseUrl: baseUrl ?? null }),
  })
  const body = await response.json().catch(() => null) as {
    status?: 'valid' | 'invalid'
    message?: string
    error?: string
  } | null
  if (!response.ok)
    throw new Error(body?.error ?? `無法儲存 API Key：HTTP ${response.status}`)
  return {
    valid: body?.status === 'valid',
    error: body?.status === 'invalid' ? body.message : undefined,
  }
}

export async function deleteProviderKey(profileId: string): Promise<void> {
  const response = await fetch(`${AGENT_API_BASE_URL}/api/providers/${profileId}/key`, {
    method: 'DELETE',
  })
  if (!response.ok) throw new Error(`無法刪除 API Key：HTTP ${response.status}`)
}

export async function reorderProviders(profileIds: string[]): Promise<void> {
  const response = await fetch(`${AGENT_API_BASE_URL}/api/providers/reorder`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ order: profileIds }),
  })
  if (!response.ok) throw new Error(`無法調整供應商順序：HTTP ${response.status}`)
}

export async function validateGithubPatViaBackend(
  apiKey: string,
): Promise<KeyValidationResult> {
  try {
    const response = await fetch(`${AGENT_API_BASE_URL}/api/providers/validate-key`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ apiKey }),
    })
    if (!response.ok) return { valid: false, error: `HTTP ${response.status}` }
    return response.json()
  } catch (error) {
    return { valid: false, error: String(error) }
  }
}

export interface ModelGroup {
  group: string
  models: string[]
}

export async function listProviderModels(profileId: string, signal?: AbortSignal): Promise<ModelGroup[]> {
  const response = await fetch(`${AGENT_API_BASE_URL}/api/providers/${profileId}/models`, { signal })
  if (!response.ok) throw new Error(`無法載入模型：HTTP ${response.status}`)
  const models = await response.json() as Array<{
    id: string
    group: string
  }>
  const groups = new Map<string, string[]>()
  for (const model of models) {
    if (!groups.has(model.group)) groups.set(model.group, [])
    groups.get(model.group)!.push(model.id)
  }
  return [...groups].map(([group, values]) => ({ group, models: values }))
}
