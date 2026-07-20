/**
 * Agent Service REST API Client
 * Base URL: http://localhost:5002  (REST endpoint)
 */

import type { AgentMode } from '@modern-wingman/contracts'

const BASE_URL = 'http://localhost:5002'

// ── Types ─────────────────────────────────────────────────────────────────────

export interface ConversationSummary {
  id: string
  title: string
  providerProfileId: string | null
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

export interface ProviderInfo {
  id: string
  displayName: string
  kind: string
  modelId: string | null
  providerType: string | null
  baseUrl: string | null
  sortOrder: number
}

export interface ProviderKeyStatus {
  profileId: string
  displayName: string
  hasEnvVar: boolean
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
export interface AttachmentReference { path:string; name:string; mediaType:string|null }

export interface PendingApproval {
  id: string
  runId: string
  operation: string
  target: string | null
  workingDirectory: string | null
  summary: string | null
  capabilities: string
  riskLevel: 'low' | 'medium' | 'high' | 'critical'
  status: 'pending'
  createdAt: string
}

export interface RunChangeSet {
  checkpointId: string
  runId: string
  workspacePath: string
  createdAt: string
  files: Array<{
    relativePath: string
    kind: 'Added' | 'Modified' | 'Deleted' | 'Renamed'
    baselineHash: string | null
    currentHash: string | null
    binary: boolean
    unifiedDiff: string | null
    originalPath: string | null
    hunks: Array<{index:number;oldStart:number;oldCount:number;newStart:number;newCount:number;lines:string[]}> | null
  }>
  validation:{status:string;attempt:number;errorSanitized:string|null;endedAt:string|null}|null
}

// ── Conversation API ──────────────────────────────────────────────────────────

export async function listConversations(): Promise<ConversationSummary[]> {
  const res = await fetch(`${BASE_URL}/api/conversations`)
  if (!res.ok) throw new Error(`listConversations: ${res.status}`)
  return res.json()
}

export async function createConversation(providerProfileId?: string): Promise<ConversationSummary> {
  const res = await fetch(`${BASE_URL}/api/conversations`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ providerProfileId: providerProfileId ?? null }),
  })
  if (!res.ok) throw new Error(`createConversation: ${res.status}`)
  return res.json()
}

export async function getConversation(id: string): Promise<ConversationDetail> {
  const res = await fetch(`${BASE_URL}/api/conversations/${id}`)
  if (!res.ok) throw new Error(`getConversation: ${res.status}`)
  return res.json()
}

export async function deleteConversation(id: string): Promise<void> {
  const res = await fetch(`${BASE_URL}/api/conversations/${id}`, { method: 'DELETE' })
  if (!res.ok) throw new Error(`deleteConversation: ${res.status}`)
}

export async function renameConversation(id: string, title: string): Promise<void> {
  const res = await fetch(`${BASE_URL}/api/conversations/${id}/title`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ title }),
  })
  if (!res.ok) throw new Error(`renameConversation: ${res.status}`)
}

/**
 * 傳送訊息並以 SSE 串流接收回應。
 * onToken: 每個 token 回呼
 * onDone:  串流完成回呼
 * onError: 發生錯誤回呼
 */
export async function sendMessage(
  conversationId: string,
  userMessage: string,
  providerProfileId: string | null,
  onToken: (token: string) => void,
  onDone: () => void,
  onError: (err: string) => void,
  signal?: AbortSignal,
  modelId?: string | null,
  agentMode: AgentMode = 'plan',
  onRunStarted?: (run: {runId:string}) => void,
  onTimeline?: (event: TimelineEvent) => void,
  attachments: AttachmentReference[] = [],
  projectId?: string | null,
  includeUncommittedChanges=true,
): Promise<void> {
  const res = await fetch(`${BASE_URL}/api/conversations/${conversationId}/messages`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ userMessage, providerProfileId, modelId: modelId ?? null, agentMode, attachments, projectId:projectId??null,includeUncommittedChanges }),
    signal,
  })

  if (!res.ok) {
    onError(`HTTP ${res.status}`)
    return
  }

  if (!res.body) {
    onError('No response body')
    return
  }

  const reader = res.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''

  try {
    while (true) {
      const { done, value } = await reader.read()
      if (done) break

      buffer += decoder.decode(value, { stream: true })
      const lines = buffer.split('\n')
      buffer = lines.pop() ?? ''

      for (const line of lines) {
        if (!line.startsWith('data: ')) continue
        const json = line.slice(6).trim()
        if (!json) continue

        try {
          const evt = JSON.parse(json) as Record<string, unknown>
          if (evt.runId && typeof evt.runId === 'string') {
            onRunStarted?.({runId:evt.runId})
          } else if (evt.timeline && typeof evt.timeline === 'object') {
            onTimeline?.(evt.timeline as TimelineEvent)
          } else if (evt.token) {
            // unescape the token
            const raw = (evt.token as string)
              .replace(/\\n/g, '\n')
              .replace(/\\r/g, '\r')
              .replace(/\\\\/g, '\\')
            onToken(raw)
          } else if (evt.done) {
            onDone()
          } else if (evt.error) {
            onError(evt.error as string)
          } else if (evt.cancelled) {
            onDone()
          }
        } catch {
          // ignore malformed lines
        }
      }
    }
  } catch (e) {
    if ((e as Error).name !== 'AbortError') onError(String(e))
  }
}

export interface TimelineEvent { type:'tool_call'|'tool_result'|'phase'|'plan'|'verify';callId:string|null;name:string|null;data:unknown;timestamp:string }
export interface PersistedRunEvent{sequence:number;event:{runId:string;eventType:string;timestamp:string;payloadJson:string}}
export async function listRunEvents(runId:string,after=0){const response=await fetch(`${BASE_URL}/api/runs/${runId}/events?after=${after}&limit=200`);if(!response.ok)throw new Error(`listRunEvents: ${response.status}`);return response.json() as Promise<PersistedRunEvent[]>}

export async function listPendingApprovals(runId: string): Promise<PendingApproval[]> {
  const res = await fetch(`${BASE_URL}/api/approvals/runs/${runId}`)
  if (!res.ok) throw new Error(`listPendingApprovals: ${res.status}`)
  return res.json()
}

export async function resolveApproval(
  approvalId: string,
  approved: boolean,
  scope: 'once' | 'run' | 'workspace' = 'once',
): Promise<void> {
  const res = await fetch(`${BASE_URL}/api/approvals/${approvalId}/decision`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ approved, scope }),
  })
  if (!res.ok) throw new Error(`resolveApproval: ${res.status}`)
}

export async function getRunChangeSet(runId: string): Promise<RunChangeSet | null> {
  const res = await fetch(`${BASE_URL}/api/runs/${runId}/changeset`)
  if (res.status === 404 || res.status === 409) return null
  if (!res.ok) throw new Error(`getRunChangeSet: ${res.status}`)
  return res.json()
}

export async function restoreRunChangeSet(runId: string): Promise<void> {
  const res = await fetch(`${BASE_URL}/api/runs/${runId}/changeset/restore`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ force: false }),
  })
  if (!res.ok) {
    const body = await res.json().catch(() => null)
    throw new Error(body?.conflicts?.length
      ? `檔案已被再次修改，無法安全復原：${body.conflicts.join(', ')}`
      : `restoreRunChangeSet: ${res.status}`)
  }
}
export async function updateRunChangeFiles(runId:string,paths:string[],action:'accept'|'restore'):Promise<void>{const res=await fetch(`${BASE_URL}/api/runs/${runId}/changeset/files/${action}`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({paths,force:false})});if(!res.ok){const body=await res.json().catch(()=>null);throw new Error(body?.error??body?.conflicts?.join(', ')??`HTTP ${res.status}`)}}
export async function updateRunChangeHunks(runId:string,path:string,hunkIndexes:number[],action:'accept'|'restore'):Promise<void>{const res=await fetch(`${BASE_URL}/api/runs/${runId}/changeset/hunks/${action}`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({path,hunkIndexes})});if(!res.ok){const body=await res.json().catch(()=>null);throw new Error(body?.error??body?.conflicts?.join(', ')??`HTTP ${res.status}`)}}
export interface WorkspaceActionResult{success:boolean;action:string;output:string|null;error:string|null;requiresProtectedConfirmation:boolean}
export interface WorkspaceActionPreview{vcsType:string|null;remote:string|null;target:string|null;revision:string|null;protected:boolean}
export async function runWorkspaceAction(runId:string,action:'retain'|'discard'|'apply'|'commit'|'push'|'svn_commit',message?:string,protectedConfirmed=false){const res=await fetch(`${BASE_URL}/api/runs/${runId}/workspace/actions`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action,message:message||null,protectedConfirmed})});const body=await res.json() as WorkspaceActionResult;if(!res.ok&&!body.requiresProtectedConfirmation)throw new Error(body.error??`HTTP ${res.status}`);return body}
export async function getWorkspaceActionPreview(runId:string){const res=await fetch(`${BASE_URL}/api/runs/${runId}/workspace/preview`);if(!res.ok)throw new Error(`HTTP ${res.status}`);return res.json() as Promise<WorkspaceActionPreview>}
export async function retryRunFromSafeStep(runId:string,providerProfileId?:string|null){const res=await fetch(`${BASE_URL}/api/runs/${runId}/retry`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({providerProfileId:providerProfileId??null})});const body=await res.json();if(!res.ok)throw new Error(body?.error??`HTTP ${res.status}`);return body}
export async function approveWorkflowPlan(runId:string){const res=await fetch(`${BASE_URL}/api/workflow/${runId}/approve`,{method:'POST'});const body=await res.json().catch(()=>null);if(!res.ok)throw new Error(body?.error??`HTTP ${res.status}`);return body}

// ── Provider / API Key API ────────────────────────────────────────────────────

export async function listProviders(): Promise<ProviderInfo[]> {
  const res = await fetch(`${BASE_URL}/api/providers`)
  if (!res.ok) throw new Error(`listProviders: ${res.status}`)
  return res.json()
}

export async function getProviderKeyStatus(profileId: string): Promise<ProviderKeyStatus> {
  const res = await fetch(`${BASE_URL}/api/providers/${profileId}/key-status`)
  if (!res.ok) throw new Error(`getProviderKeyStatus: ${res.status}`)
  return res.json()
}

export async function setProviderKey(profileId: string, apiKey: string): Promise<void> {
  const res = await fetch(`${BASE_URL}/api/providers/${profileId}/key`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ apiKey }),
  })
  if (!res.ok) {
    const body = await res.json().catch(() => null) as { error?: string } | null
    throw new Error(body?.error ?? `setProviderKey: ${res.status}`)
  }
}

export async function deleteProviderKey(profileId: string): Promise<void> {
  const res = await fetch(`${BASE_URL}/api/providers/${profileId}/key`, { method: 'DELETE' })
  if (!res.ok) throw new Error(`deleteProviderKey: ${res.status}`)
}

export async function setProviderBaseUrl(profileId: string, baseUrl: string | null): Promise<void> {
  const res = await fetch(`${BASE_URL}/api/providers/${profileId}/base-url`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ baseUrl }),
  })
  if (!res.ok) throw new Error(`setProviderBaseUrl: ${res.status}`)
}

export async function reorderProviders(profileIds: string[]): Promise<void> {
  const res = await fetch(`${BASE_URL}/api/providers/reorder`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ order: profileIds }),
  })
  if (!res.ok) throw new Error(`reorderProviders: ${res.status}`)
}

// ── Key Validation ────────────────────────────────────────────────────────────

export interface KeyValidationResult {
  valid: boolean
  /** For GitHub PAT: the x-oauth-scopes header value */
  scopes?: string
  error?: string
}

/**
 * 透過後端代理驗證 API Key，避免 Tauri WebView2 的 SSL/CRL 限制。
 * providerType: "openai" | "anthropic" | "azure" | "github"
 */
export async function validateKeyViaBackend(
  providerType: string,
  apiKey: string,
  baseUrl?: string | null,
): Promise<KeyValidationResult> {
  try {
    const res = await fetch(`${BASE_URL}/api/providers/validate-key`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ providerType, apiKey, baseUrl: baseUrl ?? null }),
    })
    if (!res.ok) return { valid: false, error: `HTTP ${res.status}` }
    const data = await res.json() as { valid: boolean; error?: string; scopes?: string }
    return { valid: data.valid, error: data.error, scopes: data.scopes }
  } catch (e) {
    return { valid: false, error: String(e) }
  }
}

/**
 * @deprecated 改用 validateKeyViaBackend — 直接從 WebView2 發請求在企業環境會遇到 SSL 問題。
 */
export async function validateOpenAiKey(
  apiKey: string,
  baseUrl = 'https://api.openai.com/v1',
): Promise<KeyValidationResult> {
  try {
    const res = await fetch(`${baseUrl.replace(/\/$/, '')}/models`, {
      headers: { Authorization: `Bearer ${apiKey}` },
    })
    return { valid: res.ok }
  } catch (e) {
    return { valid: false, error: String(e) }
  }
}

/**
 * Validate an Anthropic API key by calling /v1/models.
 */
export async function validateAnthropicKey(apiKey: string): Promise<KeyValidationResult> {
  try {
    const res = await fetch('https://api.anthropic.com/v1/models', {
      headers: {
        'x-api-key': apiKey,
        'anthropic-version': '2023-06-01',
      },
    })
    return { valid: res.ok }
  } catch (e) {
    return { valid: false, error: String(e) }
  }
}

/**
 * Validate an Azure OpenAI key.
 * The baseUrl should be the full resource URL, e.g. https://<resource>.openai.azure.com
 */
export async function validateAzureKey(
  apiKey: string,
  baseUrl: string,
  apiVersion = '2024-10-21',
): Promise<KeyValidationResult> {
  try {
    const url = `${baseUrl.replace(/\/$/, '')}/openai/models?api-version=${apiVersion}`
    const res = await fetch(url, { headers: { 'api-key': apiKey } })
    return { valid: res.ok }
  } catch (e) {
    return { valid: false, error: String(e) }
  }
}

/**
 * Validate a GitHub Personal Access Token and return the granted scopes.
 * Uses the GitHub REST API meta endpoint which returns x-oauth-scopes header.
 */
export async function validateGithubPat(pat: string): Promise<KeyValidationResult> {
  try {
    const res = await fetch('https://api.github.com/user', {
      headers: {
        Authorization: `token ${pat}`,
        Accept: 'application/vnd.github+json',
      },
    })
    if (!res.ok) return { valid: false, error: `HTTP ${res.status}` }
    const scopes = res.headers.get('x-oauth-scopes') ?? ''
    return { valid: true, scopes }
  } catch (e) {
    return { valid: false, error: String(e) }
  }
}

// ── Model listing (React-side only) ──────────────────────────────────────────

export interface ModelGroup {
  group: string
  models: string[]
}

/**
 * Fetch available models for a specific provider via backend API.
 * Works for all providers including CopilotDefault (which uses the Copilot SDK server-side).
 */
export async function listProviderModels(profileId: string): Promise<ModelGroup[]> {
  const res = await fetch(`${BASE_URL}/api/providers/${profileId}/models`)
  if (!res.ok) return []
  const data = await res.json() as { id: string; displayName: string; group: string }[]
  // Group by the 'group' field
  const map = new Map<string, string[]>()
  for (const m of data) {
    if (!map.has(m.group)) map.set(m.group, [])
    map.get(m.group)!.push(m.id)
  }
  return Array.from(map.entries()).map(([group, models]) => ({ group, models }))
}
