import { AGENT_API_BASE_URL, fetchWithConnectionRetry } from './client'

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
 * starting 是前端銜接狀態，其餘值由 Agent Service 回傳。
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

export interface IndexProgress {
  projectId?: string
  phase: IndexProgressPhase
  message: string
  percent: number
}

export interface AiEnrichmentProgress {
  projectId: string
  total: number
  queued: number
  running: number
  completed: number
  failed: number
  percent: number
  structuralIndexAvailable: boolean
  message?: string | null
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
  /** 舊版 V4 欄位；新 Viewer 以 category、caption 與 metrics 為主要來源。 */
  kind?: string
  role?: string
  name?: string
  filePath?: string | null
  startLine?: number | null
  endLine?: number | null
  language?: string | null
  degree?: number
  properties: Record<string, unknown>
  /** Viewer Contract 投影欄位；API adapter 會為舊版 V4 response 補上預設值。 */
  labels: string[]
  caption: string
  category: string | null
  metrics: Record<string, number>
}

export interface CodeGraphVisualEdge {
  id: string
  source: string
  target: string
  type: string
  properties: Record<string, unknown>
}

export interface CodeGraphVisualData {
  nodes: CodeGraphVisualNode[]
  edges: CodeGraphVisualEdge[]
  totalNodes: number
  loadedNodes: number
  loadedEdges: number
  /** 舊版 Viewer 使用 hasMore；V1 Contract 使用 truncated。兩者由 adapter 同步。 */
  hasMore: boolean
  contractVersion?: string
  truncated?: boolean
}

export interface CodeGraphFacet {
  name: string
  count: number
}

export interface CodeGraphSchema {
  totalNodes: number
  totalEdges: number
  nodeKinds: CodeGraphFacet[]
  relationshipTypes: CodeGraphFacet[]
  propertyKeys: string[]
  /** Viewer Contract V1 欄位；保留舊欄位供既有圖譜頁相容使用。 */
  contractVersion?: string
  graphRevision?: string | null
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
    target?: string
  }>
  queryHelp?: string
}

/** Viewer Contract 的動態篩選面板描述。 */
export interface CodeGraphViewerFacet {
  id: string
  label: string
  /** 舊版 V4 schema 使用 kind；通用 Viewer Contract 使用 target。 */
  kind: 'node' | 'edge' | string
  /** 顯示給使用者的篩選說明；舊版後端可能沒有提供。 */
  description?: string
  /** 通用 Viewer Contract 的篩選目標。 */
  target?: 'node' | 'edge' | string
  /** 單選或多選；未提供時沿用舊版 multiSelect。 */
  selection: 'single' | 'multiple'
  /** 多個 token 的比對方式。 */
  match?: 'any' | 'all'
  values: Array<{ token: string; label: string; count: number }>
  multiSelect?: boolean
}

/** Viewer Contract 的篩選值；只傳 facet token，不傳任意 Cypher。 */
export interface CodeGraphSearchFilter {
  facetId: string
  tokens: string[]
}

/** Viewer 全域搜尋命中。 */
export interface CodeGraphSearchHit {
  node: CodeGraphVisualNode
  score: number
}

/**
 * Viewer Contract V1 的搜尋項目。
 *
 * V4 舊版 API 回傳 hits；UI-improvement 版本回傳 items。client 會同時
 * 保留兩個欄位，讓既有頁面可以繼續使用 hits，新版頁面則能直接使用 items。
 */
export interface CodeGraphSearchItem {
  node: CodeGraphVisualNode
  score: number
}

/** Viewer 全域搜尋結果。 */
export interface CodeGraphSearchResult {
  hits: CodeGraphSearchHit[]
  items: CodeGraphSearchItem[]
  total: number
  hasMore: boolean
  contractVersion?: string
  /** UI-improvement API 使用 take 表示本次要求的回傳上限。 */
  take?: number
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
  let response: Response
  try {
    response = await fetchWithConnectionRetry(`${AGENT_API_BASE_URL}/api/projects`)
  } catch {
    throw new Error(
      `無法連線到 Agent Service（${AGENT_API_BASE_URL}）。請確認後端服務已啟動。`,
    )
  }
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

/** 取得專案所有已設定資料來源；SQL Server 與 SQLite 可同時存在。 */
export async function getProjectDatabaseConfigurations(
  projectId: string,
): Promise<ProjectDatabaseConfiguration[]> {
  const response = await fetch(`${AGENT_API_BASE_URL}/api/projects/${projectId}/database/all`)
  if (!response.ok) throw new Error(`讀取資料庫設定失敗 (${response.status})`)
  const body: unknown = await response.json()
  if (!Array.isArray(body)) return []
  return body as ProjectDatabaseConfiguration[]
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

export async function deleteProjectDatabaseConfiguration(
  projectId: string,
  provider?: SaveProjectDatabaseConfiguration['provider'],
): Promise<void> {
  const endpoint = provider
    ? `${AGENT_API_BASE_URL}/api/projects/${projectId}/database/${provider}`
    : `${AGENT_API_BASE_URL}/api/projects/${projectId}/database`
  const response = await fetch(endpoint, {
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
const GRAPH_RETRY_DELAYS_MS = [1_000, 2_000]

/**
 * 圖譜 API 在 Neo4j 剛啟動時可能暫時回傳 503；短暫重試可吸收啟動競態，
 * 但不會把真正的 4xx/5xx 錯誤吞掉，最後仍會保留後端提供的詳細訊息。
 */
const wait = (milliseconds: number) =>
  new Promise<void>((resolve) => window.setTimeout(resolve, milliseconds))

/** 判斷 API JSON 是否為可讀取屬性的物件。 */
function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

/** 讀取 API JSON 中的字串欄位；空字串也視為未提供。 */
function readString(value: unknown): string | undefined {
  return typeof value === 'string' && value.length > 0 ? value : undefined
}

/** 讀取 API JSON 中的整數／浮點數欄位。 */
function readNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

/**
 * 將 V4 舊版節點與 UI-improvement 通用節點正規化成同一個前端模型。
 *
 * V4 後端可能回傳 kind/name/filePath 等舊欄位，通用 Viewer 則使用
 * labels/caption/category/metrics。這裡只做欄位投影，不改動 Neo4j schema，
 * 讓同一個桌面版本可以與兩種 response shape 相容。
 */
function normalizeVisualNode(value: unknown): CodeGraphVisualNode {
  const node = isRecord(value) ? value : {}
  const properties = isRecord(node.properties) ? node.properties : {}
  const labels = Array.isArray(node.labels)
    ? node.labels.filter((item): item is string => typeof item === 'string')
    : []
  const category =
    readString(node.category) ?? readString(node.kind) ?? labels[0] ?? 'unknown'
  const caption = readString(node.caption) ?? readString(node.name) ?? readString(node.id) ?? ''
  const metrics = isRecord(node.metrics)
    ? Object.fromEntries(
        Object.entries(node.metrics).filter(
          (entry): entry is [string, number] => typeof entry[1] === 'number',
        ),
      )
    : {}
  const degree = readNumber(node.degree) ?? readNumber(metrics.degree) ?? 0

  return {
    id: readString(node.id) ?? caption,
    kind: readString(node.kind) ?? category,
    role: readString(node.role) ?? category,
    name: readString(node.name) ?? caption,
    filePath: readString(node.filePath) ?? null,
    startLine: readNumber(node.startLine) ?? null,
    endLine: readNumber(node.endLine) ?? null,
    language: readString(node.language) ?? null,
    degree,
    properties,
    labels: labels.length > 0 ? labels : [category],
    caption,
    category,
    metrics: { ...metrics, degree },
  }
}

/** 將兩個 Viewer Contract 版本的 edge response 投影成穩定的前端 edge。 */
function normalizeVisualEdge(value: unknown): CodeGraphVisualEdge {
  const edge = isRecord(value) ? value : {}
  return {
    id: readString(edge.id) ?? '',
    source: readString(edge.source) ?? readString(edge.start) ?? '',
    target: readString(edge.target) ?? readString(edge.end) ?? '',
    type: readString(edge.type) ?? 'UNKNOWN',
    properties: isRecord(edge.properties) ? edge.properties : {},
  }
}

/** 將 bounded graph response 正規化，並同步 hasMore 與 truncated 欄位。 */
function normalizeVisualData(value: unknown): CodeGraphVisualData {
  const graph = isRecord(value) ? value : {}
  const nodes = Array.isArray(graph.nodes) ? graph.nodes.map(normalizeVisualNode) : []
  const edges = Array.isArray(graph.edges) ? graph.edges.map(normalizeVisualEdge) : []
  const totalNodes = readNumber(graph.totalNodes) ?? nodes.length
  const loadedNodes = readNumber(graph.loadedNodes) ?? nodes.length
  const loadedEdges = readNumber(graph.loadedEdges) ?? edges.length
  const hasMore =
    typeof graph.hasMore === 'boolean'
      ? graph.hasMore
      : typeof graph.truncated === 'boolean'
        ? graph.truncated
        : loadedNodes < totalNodes

  return {
    nodes,
    edges,
    totalNodes,
    loadedNodes,
    loadedEdges,
    hasMore,
    contractVersion: readString(graph.contractVersion) ?? '1.0',
    truncated: hasMore,
  }
}

/** 將 API facet 值轉成前端可顯示的穩定型別。 */
function normalizeFacet(value: unknown): CodeGraphViewerFacet | null {
  if (!isRecord(value)) return null
  const id = readString(value.id)
  const label = readString(value.label) ?? id
  if (!id || !label) return null
  const kind = readString(value.kind) ?? readString(value.target) ?? 'node'
  const target = readString(value.target) ?? kind
  const multiSelect =
    typeof value.multiSelect === 'boolean'
      ? value.multiSelect
      : value.selection !== 'single'
  const values = Array.isArray(value.values)
    ? value.values
        .filter(isRecord)
        .map((item) => ({
          token: readString(item.token) ?? '',
          label: readString(item.label) ?? readString(item.token) ?? '',
          count: readNumber(item.count) ?? 0,
        }))
        .filter((item) => item.token.length > 0)
    : []

  return {
    id,
    label,
    kind,
    description: readString(value.description),
    target,
    selection: value.selection === 'single' ? 'single' : 'multiple',
    match: value.match === 'all' ? 'all' : 'any',
    values,
    multiSelect,
  }
}

/**
 * 將 schema response 正規化成既有頁面與通用 Viewer 都能使用的結構。
 * 舊版 nodeKinds/relationshipTypes 若不存在，會由 facets 回推，避免 UI 空白。
 */
function normalizeGraphSchema(value: unknown): CodeGraphSchema {
  const schema = isRecord(value) ? value : {}
  const facets = Array.isArray(schema.facets)
    ? schema.facets.map(normalizeFacet).filter((item): item is CodeGraphViewerFacet => item !== null)
    : []
  const nodeKinds = Array.isArray(schema.nodeKinds)
    ? schema.nodeKinds
        .filter(isRecord)
        .map((item) => ({
          name: readString(item.name) ?? '',
          count: readNumber(item.count) ?? 0,
        }))
        .filter((item) => item.name.length > 0)
    : facets
        .filter((facet) => (facet.target ?? facet.kind) === 'node')
        .flatMap((facet) => facet.values.map((item) => ({ name: item.token, count: item.count })))
  const relationshipTypes = Array.isArray(schema.relationshipTypes)
    ? schema.relationshipTypes
        .filter(isRecord)
        .map((item) => ({
          name: readString(item.name) ?? '',
          count: readNumber(item.count) ?? 0,
        }))
        .filter((item) => item.name.length > 0)
    : facets
        .filter((facet) => (facet.target ?? facet.kind) === 'edge')
        .flatMap((facet) => facet.values.map((item) => ({ name: item.token, count: item.count })))
  const captionOptions = Array.isArray(schema.captionOptions)
    ? schema.captionOptions
        .filter(isRecord)
        .map((item) => ({
          id: readString(item.id) ?? '',
          label: readString(item.label) ?? readString(item.id) ?? '',
        }))
        .filter((item) => item.id.length > 0)
    : []
  const queryTemplates = Array.isArray(schema.queryTemplates)
    ? schema.queryTemplates
        .filter(isRecord)
        .map((item) => ({
          id: readString(item.id) ?? '',
          label: readString(item.label) ?? readString(item.id) ?? '',
          text: readString(item.text) ?? '',
          target: readString(item.target),
        }))
        .filter((item) => item.id.length > 0)
    : []
  const capabilities = isRecord(schema.capabilities)
    ? {
        search: schema.capabilities.search !== false,
        neighbors: schema.capabilities.neighbors !== false,
        table: schema.capabilities.table !== false,
        rawQuery: schema.capabilities.rawQuery !== false,
      }
    : { search: true, neighbors: true, table: true, rawQuery: true }

  return {
    totalNodes: readNumber(schema.totalNodes) ?? 0,
    totalEdges: readNumber(schema.totalEdges) ?? 0,
    nodeKinds,
    relationshipTypes,
    propertyKeys: Array.isArray(schema.propertyKeys)
      ? schema.propertyKeys.filter((item): item is string => typeof item === 'string')
      : [],
    contractVersion: readString(schema.contractVersion) ?? '1.0',
    graphRevision: readString(schema.graphRevision) ?? null,
    facets,
    captionOptions,
    capabilities,
    queryTemplates,
    queryHelp: readString(schema.queryHelp),
  }
}

/** 將 V4 hits 與 UI-improvement items 統一成雙欄位搜尋結果。 */
function normalizeSearchResult(value: unknown): CodeGraphSearchResult {
  const result = isRecord(value) ? value : {}
  const rawItems = Array.isArray(result.items)
    ? result.items
    : Array.isArray(result.hits)
      ? result.hits
      : []
  const items = rawItems
    .filter(isRecord)
    .map((item) => ({
      node: normalizeVisualNode(item.node ?? item),
      score: readNumber(item.score) ?? 0,
    }))
  const total = readNumber(result.total) ?? items.length
  const hasMore = typeof result.hasMore === 'boolean' ? result.hasMore : false

  return {
    hits: items,
    items,
    total,
    hasMore,
    contractVersion: readString(result.contractVersion) ?? '1.0',
    take: readNumber(result.take),
  }
}

/** 圖譜 API 支援外部取消訊號，避免舊搜尋結果覆蓋新請求。 */
async function fetchGraphApi(
  url: string,
  init?: RequestInit,
  externalSignal?: AbortSignal,
): Promise<Response> {
  for (let attempt = 0; ; attempt += 1) {
    const controller = new AbortController()
    const timer = window.setTimeout(() => controller.abort(), GRAPH_REQUEST_TIMEOUT_MS)
    const abortFromCaller = () => controller.abort()
    externalSignal?.addEventListener('abort', abortFromCaller, { once: true })
    if (externalSignal?.aborted) controller.abort()
    try {
      const response = await fetch(url, { ...init, signal: controller.signal })
      if (response.status !== 503 || attempt >= GRAPH_RETRY_DELAYS_MS.length)
        return response

      await wait(GRAPH_RETRY_DELAYS_MS[attempt])
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') {
        if (externalSignal?.aborted) throw error
        throw new Error('圖譜服務在 120 秒內沒有回應，請確認 Neo4j 狀態後重試。')
      }

      if (attempt >= GRAPH_RETRY_DELAYS_MS.length) {
        throw new Error(
          `無法連線到圖譜服務（${AGENT_API_BASE_URL}）。請確認 Agent Service 與 Neo4j 已啟動。`,
        )
      }
      await wait(GRAPH_RETRY_DELAYS_MS[attempt])
    } finally {
      window.clearTimeout(timer)
      externalSignal?.removeEventListener('abort', abortFromCaller)
    }
  }
}

export async function getProjectGraphSchema(projectId: string): Promise<CodeGraphSchema> {
  const response = await fetchGraphApi(
    `${AGENT_API_BASE_URL}/api/projects/${projectId}/graph/schema`,
  )
  if (!response.ok)
    throw new Error(await errorMessage(response, `讀取圖譜結構失敗 (${response.status})`))
  return normalizeGraphSchema(await response.json())
}

export async function getProjectGraph(
  projectId: string,
  options?: {
    limit?: number
    kinds?: string[]
    relations?: string[]
    /** UI-improvement Viewer 使用的 schema-driven facet 篩選。 */
    filters?: CodeGraphSearchFilter[]
  },
  signal?: AbortSignal,
): Promise<CodeGraphVisualData> {
  // 新版呼叫端若提供 filters，直接使用 V4 bounded view；不影響舊版 GET /graph。
  if (options?.filters) {
    return getProjectGraphView(
      projectId,
      { limit: options.limit, filters: options.filters },
      signal,
    )
  }
  const params = new URLSearchParams()
  if (options?.limit) params.set('limit', String(options.limit))
  if (options?.kinds?.length) params.set('kinds', options.kinds.join(','))
  if (options?.relations?.length) params.set('relations', options.relations.join(','))
  const query = params.toString()
  const response = await fetchGraphApi(
    `${AGENT_API_BASE_URL}/api/projects/${projectId}/graph${query ? `?${query}` : ''}`,
    undefined,
    signal,
  )
  if (!response.ok)
    throw new Error(await errorMessage(response, `讀取知識圖譜失敗 (${response.status})`))
  return normalizeVisualData(await response.json())
}

/**
 * 使用通用 Viewer Contract 取得 bounded 初始圖。
 * 舊版 GET /graph 保留給既有頁面與驗收腳本，本函式只供新版 Viewer 使用。
 */
export async function getProjectGraphView(
  projectId: string,
  options?: { limit?: number; filters?: CodeGraphSearchFilter[] },
  signal?: AbortSignal,
): Promise<CodeGraphVisualData> {
  const response = await fetchGraphApi(
    `${AGENT_API_BASE_URL}/api/projects/${projectId}/graph/view`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        filters: options?.filters ?? [],
        limit: options?.limit ?? 1000,
      }),
    },
    signal,
  )
  if (!response.ok)
    throw new Error(await errorMessage(response, `讀取知識圖譜失敗 (${response.status})`))
  return normalizeVisualData(await response.json())
}

/**
 * 使用 active V4 full-text index 執行 bounded 全域搜尋。
 * 查詢文字由後端 tokenization；前端不建立或拼接 Cypher。
 */
export function searchProjectGraph(
  projectId: string,
  query: string,
  options?: { take?: number; filters?: CodeGraphSearchFilter[] },
  signal?: AbortSignal,
): Promise<CodeGraphSearchResult>
/**
 * 相容 UI-improvement 的呼叫方式：直接傳入 filters、take 與 AbortSignal。
 * 兩種呼叫方式最後都送出相同的 V4 POST body，不會改變後端索引邏輯。
 */
export function searchProjectGraph(
  projectId: string,
  query: string,
  filters: CodeGraphSearchFilter[],
  take?: number,
  signal?: AbortSignal,
): Promise<CodeGraphSearchResult>
export async function searchProjectGraph(
  projectId: string,
  query: string,
  optionsOrFilters: { take?: number; filters?: CodeGraphSearchFilter[] } | CodeGraphSearchFilter[] = {},
  takeOrSignal?: number | AbortSignal,
  externalSignal?: AbortSignal,
): Promise<CodeGraphSearchResult> {
  const options = Array.isArray(optionsOrFilters)
    ? {
        filters: optionsOrFilters,
        take: typeof takeOrSignal === 'number' ? takeOrSignal : undefined,
      }
    : optionsOrFilters
  const signal =
    externalSignal ??
    (typeof takeOrSignal === 'object' && takeOrSignal !== null ? takeOrSignal : undefined)
  const response = await fetchGraphApi(
    `${AGENT_API_BASE_URL}/api/projects/${projectId}/graph/search`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        query,
        filters: options?.filters ?? [],
        take: options?.take ?? 20,
      }),
    },
    signal,
  )
  if (!response.ok)
    throw new Error(await errorMessage(response, `搜尋知識圖譜失敗 (${response.status})`))
  return normalizeSearchResult(await response.json())
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
  const payload = await response.json()
  const result = isRecord(payload) ? payload : {}
  return {
    columns: Array.isArray(result.columns)
      ? result.columns.filter((item): item is string => typeof item === 'string')
      : [],
    rows: Array.isArray(result.rows) ? result.rows.filter(isRecord) : [],
    graph: normalizeVisualData(result.graph),
  }
}

export async function expandProjectGraphNeighbors(
  projectId: string,
  nodeKeys: string[],
  options?: {
    depth?: number
    limit?: number
    mode?: 'all' | 'in' | 'out' | 'callers' | 'callees' | 'same-file'
  },
  signal?: AbortSignal,
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
    signal,
  )
  if (!response.ok)
    throw new Error(await errorMessage(response, `展開知識圖譜失敗 (${response.status})`))
  return normalizeVisualData(await response.json())
}
