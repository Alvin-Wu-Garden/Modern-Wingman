import { AGENT_API_BASE_URL } from './client'

/** 專案清單實際顯示的欄位。 */
export interface ProjectInfo {
  id: string
  name: string
  rootPath: string
  languages: string
  indexStatus: 'NotIndexed' | 'PendingChanges' | 'Indexing' | 'Indexed' | 'Partial' | 'Stale' | 'Failed'
  indexedAt: string | null
  indexError: string | null
  nodeCount: number
  edgeCount: number
  createdAt: string
  vcsType?: 'git' | 'svn' | null
  currentRef?: string | null
  revision?: string | null
  repositoryPath?: string | null
  dirty?: boolean | null
  indexManifestVersion?: string | null
  pendingFileCount?: number
}

/**
 * 索引階段是前後端共用契約；列成 union 可讓不存在的 done 等字串在編譯期被攔下。
 * starting／summaries 是前端銜接狀態，其餘值由 Agent Service 回傳。
 */
export type IndexProgressPhase =
  | 'idle'
  | 'starting'
  | 'scan'
  | 'extract'
  | 'assemble'
  | 'publish'
  | 'complete'
  | 'failed'
  | 'summaries'

export interface IndexProgress {
  projectId?: string
  phase: IndexProgressPhase
  message: string
  percent: number
}

export interface AiEnrichmentProgress {
  projectId: string
  targetManifestVersion?: string | null
  state: 'NotRequested' | 'Detecting' | 'Summarizing' | 'Ready' | 'Degraded' | 'Superseded' | 'Canceled'
  completedCommunities: number
  totalCommunities: number
  message?: string | null
  error?: string | null
}

export interface ProjectDatabaseConfiguration {
  projectId: string
  provider: 'SqlServer' | 'Sqlite'
  server: string | null
  port: number | null
  databaseName: string | null
  authentication: 'SqlPassword' | 'IntegratedSecurity' | null
  username: string | null
  hasPassword: boolean
  trustServerCertificate: boolean
  sqlitePath: string | null
  updatedAt: string
}

export interface SaveProjectDatabaseConfiguration {
  provider: 'SqlServer' | 'Sqlite'
  server?: string | null
  port?: number | null
  databaseName?: string | null
  authentication?: 'SqlPassword' | 'IntegratedSecurity' | null
  username?: string | null
  password?: string | null
  trustServerCertificate?: boolean
  sqlitePath?: string | null
}

export interface CodeGraphVisualNode {
  id: string
  labels: string[]
  caption: string
  category?: string | null
  properties: Record<string, unknown>
  metrics?: Record<string, number>
}

export interface CodeGraphVisualEdge {
  id: string
  source: string
  target: string
  type: string
  properties: Record<string, unknown>
}

export interface CodeGraphVisualData {
  contractVersion: string
  nodes: CodeGraphVisualNode[]
  edges: CodeGraphVisualEdge[]
  totalNodes: number
  loadedNodes: number
  loadedEdges: number
  truncated: boolean
}

export interface CodeGraphViewerFacet {
  id: string
  label: string
  description: string
  target: 'node' | 'edge'
  selection: 'single' | 'multiple'
  match: 'any' | 'all'
  values: Array<{ token: string; label: string; count: number }>
}

export interface CodeGraphSchema {
  contractVersion: string
  graphRevision?: string | null
  totalNodes: number
  totalEdges: number
  facets: CodeGraphViewerFacet[]
  captionOptions: Array<{ id: string; label: string }>
  capabilities: {
    search: boolean
    neighbors: boolean
    table: boolean
    rawQuery: boolean
  }
  queryTemplates: Array<{
    id: string
    label: string
    text: string
    target?: 'manual' | 'node' | 'edge'
  }>
  queryHelp: string
}

export interface CodeGraphSearchResult {
  items: Array<{ node: CodeGraphVisualNode; score: number }>
  take: number
  hasMore: boolean
}

export interface CodeGraphQueryResult {
  columns: string[]
  rows: Record<string, unknown>[]
  graph: CodeGraphVisualData
}

export interface ImportProjectRequest {
  sourceType: 'git' | 'svn'
  name: string
  profileId: string
  repositoryUrl: string
  ref: string | null
  destinationPath: string
  operationId?: string
}

async function errorMessage(response: Response, fallback: string): Promise<string> {
  const body = await response.json().catch(() => null)
  return body?.error ?? body?.detail ?? fallback
}

export async function listProjects(): Promise<ProjectInfo[]> {
  const response = await fetch(`${AGENT_API_BASE_URL}/api/projects`)
  if (!response.ok) throw new Error(`讀取專案失敗 (${response.status})`)
  return response.json()
}

export async function createProject(name: string, rootPath: string): Promise<ProjectInfo> {
  const response = await fetch(`${AGENT_API_BASE_URL}/api/projects`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, rootPath }),
  })
  if (!response.ok)
    throw new Error(await errorMessage(response, `新增專案失敗 (${response.status})`))
  return response.json()
}

export async function importProject(
  request: ImportProjectRequest,
  signal?: AbortSignal,
): Promise<ProjectInfo> {
  const response = await fetch(`${AGENT_API_BASE_URL}/api/projects/import`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
    signal,
  })
  if (!response.ok)
    throw new Error(await errorMessage(response, `匯入專案失敗 (${response.status})`))
  return response.json()
}

export async function deleteProject(projectId: string): Promise<void> {
  const response = await fetch(`${AGENT_API_BASE_URL}/api/projects/${projectId}`, {
    method: 'DELETE',
  })
  if (!response.ok && response.status !== 204)
    throw new Error(`刪除專案失敗 (${response.status})`)
}

export async function startIndex(projectId: string): Promise<void> {
  const response = await fetch(`${AGENT_API_BASE_URL}/api/projects/${projectId}/index`, {
    method: 'POST',
  })
  if (!response.ok && response.status !== 202)
    throw new Error(await errorMessage(response, `啟動索引失敗 (${response.status})`))
}

export async function getIndexProgress(projectId: string): Promise<IndexProgress> {
  const response = await fetch(`${AGENT_API_BASE_URL}/api/projects/${projectId}/index/progress`)
  if (!response.ok) throw new Error(`讀取索引進度失敗 (${response.status})`)
  return response.json()
}

export async function buildSummaries(projectId: string): Promise<void> {
  const response = await fetch(`${AGENT_API_BASE_URL}/api/projects/${projectId}/summaries`, {
    method: 'POST',
  })
  if (!response.ok) throw new Error(`啟動業務摘要失敗 (${response.status})`)
}

export async function getSummaryProgress(projectId: string): Promise<AiEnrichmentProgress> {
  const response = await fetch(`${AGENT_API_BASE_URL}/api/projects/${projectId}/summaries/progress`)
  if (!response.ok) throw new Error(`讀取業務摘要進度失敗 (${response.status})`)
  return response.json()
}

export async function getProjectDatabaseConfiguration(
  projectId: string,
): Promise<ProjectDatabaseConfiguration | null> {
  const response = await fetch(`${AGENT_API_BASE_URL}/api/projects/${projectId}/database`)
  if (response.status === 204) return null
  if (!response.ok) throw new Error(`讀取資料庫設定失敗 (${response.status})`)
  return response.json()
}

export async function saveProjectDatabaseConfiguration(
  projectId: string,
  configuration: SaveProjectDatabaseConfiguration,
): Promise<ProjectDatabaseConfiguration> {
  const response = await fetch(`${AGENT_API_BASE_URL}/api/projects/${projectId}/database`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(configuration),
  })
  if (!response.ok)
    throw new Error(await errorMessage(response, `儲存資料庫設定失敗 (${response.status})`))
  return response.json()
}

export async function deleteProjectDatabaseConfiguration(projectId: string): Promise<void> {
  const response = await fetch(`${AGENT_API_BASE_URL}/api/projects/${projectId}/database`, {
    method: 'DELETE',
  })
  if (!response.ok && response.status !== 204)
    throw new Error(`刪除資料庫設定失敗 (${response.status})`)
}

export async function testProjectDatabaseConnection(
  projectId: string,
  configuration: SaveProjectDatabaseConfiguration,
): Promise<{ success: boolean; message?: string; error?: string }> {
  const response = await fetch(`${AGENT_API_BASE_URL}/api/projects/${projectId}/database/test`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(configuration),
  })
  if (!response.ok)
    throw new Error(await errorMessage(response, `測試資料庫連線失敗 (${response.status})`))
  return response.json()
}

/**
 * 使用尚未儲存的 SQL Server 候選設定讀取可用資料庫名稱。
 * 後端只會暫時連線，不會把這次輸入的密碼寫入設定資料庫。
 */
export async function listProjectSqlServerDatabases(
  projectId: string,
  configuration: SaveProjectDatabaseConfiguration,
): Promise<string[]> {
  const response = await fetch(
    `${AGENT_API_BASE_URL}/api/projects/${projectId}/database/databases`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(configuration),
    },
  )
  if (!response.ok)
    throw new Error(await errorMessage(response, `讀取資料庫清單失敗 (${response.status})`))
  const body = await response.json()
  return Array.isArray(body.databases)
    ? body.databases.filter((name: unknown): name is string => typeof name === 'string')
    : []
}

// 內建 Neo4j 第一次啟動可能需要下載與初始化，因此保留有限的 120 秒 UI 逾時。
const GRAPH_REQUEST_TIMEOUT_MS = 120_000

async function fetchGraphApi(
  url: string,
  init?: RequestInit,
  externalSignal?: AbortSignal,
): Promise<Response> {
  const controller = new AbortController()
  const timer = window.setTimeout(() => controller.abort(), GRAPH_REQUEST_TIMEOUT_MS)
  const abortFromCaller = () => controller.abort()
  externalSignal?.addEventListener('abort', abortFromCaller, { once: true })
  if (externalSignal?.aborted) controller.abort()
  try {
    return await fetch(url, { ...init, signal: controller.signal })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError')
      throw new Error('圖譜服務在 120 秒內沒有回應，請確認 Neo4j 狀態後重試。')
    throw error
  } finally {
    window.clearTimeout(timer)
    externalSignal?.removeEventListener('abort', abortFromCaller)
  }
}

export async function getProjectGraphSchema(projectId: string): Promise<CodeGraphSchema> {
  const response = await fetchGraphApi(
    `${AGENT_API_BASE_URL}/api/projects/${projectId}/graph/schema`,
  )
  if (!response.ok)
    throw new Error(await errorMessage(response, `讀取圖譜結構失敗 (${response.status})`))
  return response.json()
}

export async function getProjectGraph(
  projectId: string,
  options?: {
    limit?: number
    filters?: Array<{ facetId: string; tokens: string[] }>
  },
  signal?: AbortSignal,
): Promise<CodeGraphVisualData> {
  const response = await fetchGraphApi(
    `${AGENT_API_BASE_URL}/api/projects/${projectId}/graph/view`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        limit: options?.limit,
        filters: options?.filters ?? [],
      }),
    },
    signal,
  )
  if (!response.ok)
    throw new Error(await errorMessage(response, `讀取知識圖譜失敗 (${response.status})`))
  return response.json()
}

export async function searchProjectGraph(
  projectId: string,
  query: string,
  filters: Array<{ facetId: string; tokens: string[] }>,
  take = 50,
  signal?: AbortSignal,
): Promise<CodeGraphSearchResult> {
  const response = await fetchGraphApi(
    `${AGENT_API_BASE_URL}/api/projects/${projectId}/graph/search`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ query, filters, take }),
    },
    signal,
  )
  if (!response.ok)
    throw new Error(await errorMessage(response, `搜尋知識圖譜失敗 (${response.status})`))
  return response.json()
}

export async function queryProjectGraph(
  projectId: string,
  cypher: string,
  limit = 1000,
): Promise<CodeGraphQueryResult> {
  const response = await fetchGraphApi(
    `${AGENT_API_BASE_URL}/api/projects/${projectId}/graph/query`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ cypher, limit }),
    },
  )
  if (!response.ok)
    throw new Error(await errorMessage(response, `查詢知識圖譜失敗 (${response.status})`))
  return response.json()
}

export async function expandProjectGraphNeighbors(
  projectId: string,
  nodeKeys: string[],
  options?: {
    depth?: number
    limit?: number
    mode?: 'all' | 'in' | 'out'
  },
): Promise<CodeGraphVisualData> {
  const response = await fetchGraphApi(
    `${AGENT_API_BASE_URL}/api/projects/${projectId}/graph/neighbors`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        nodeKeys,
        depth: options?.depth ?? 1,
        limit: options?.limit ?? 1000,
        mode: options?.mode ?? 'all',
      }),
    },
  )
  if (!response.ok)
    throw new Error(await errorMessage(response, `展開知識圖譜失敗 (${response.status})`))
  return response.json()
}
