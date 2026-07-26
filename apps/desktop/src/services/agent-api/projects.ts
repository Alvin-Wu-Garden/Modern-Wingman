/**
 * 企業程式碼解析 API Client（WS3）。
 * Base URL: http://localhost:5002
 */

const BASE_URL = 'http://localhost:5002'

// ── Types ─────────────────────────────────────────────────────────────────────

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

export interface ProjectIndexedFileManifest {
  relativePath: string
  language: string
  length: number
  contentHash: string
  status: string
  reason?: string | null
}

export interface ProjectIndexManifest {
  projectId: string
  version: string
  repositoryRoot: string
  headCommit?: string | null
  workingTreeFingerprint: string
  untrackedFiles: string[]
  files: ProjectIndexedFileManifest[]
  pendingFiles: string[]
  indexerVersion: string
  startedAt: string
  completedAt?: string | null
  status: string
  nodeCount: number
  edgeCount: number
  error?: string | null
  pendingFileCount: number
  failedFileCount: number
}

export interface ProjectIndexDiagnostics {
  current?: ProjectIndexManifest | null
  latestAttempt?: ProjectIndexManifest | null
  pendingFiles: string[]
  isStale: boolean
}

export interface DataArtifactScanRecord {
  path: string
  technology: string
  status: string
  reason?: string | null
  contentHash: string
}

export interface DataExtractionDiagnostic {
  filePath: string
  adapterId: string
  severity: string
  message: string
}

export interface StaticDataScanResult {
  nodeCount: number
  edgeCount: number
  diagnostics: DataExtractionDiagnostic[]
  capabilityGaps: string[]
  scannedFiles: DataArtifactScanRecord[]
  skippedFiles: DataArtifactScanRecord[]
}

export type GlossaryProposalStatus = 'Proposed' | 'Confirmed' | 'Rejected'
export type GlossarySensitivity = 'Unknown' | 'Public' | 'Internal' | 'Confidential' | 'PersonalData' | 'Secret'

export interface DomainGlossaryEntry {
  id: string
  projectId: string
  term: string
  definition: string
  aliases: string[]
  sensitivity: GlossarySensitivity
  status: GlossaryProposalStatus
  evidenceKeys: string[]
  proposedBy: string
  reviewedBy?: string | null
  reviewComment?: string | null
  createdAt: string
  updatedAt: string
}

export interface ProposeGlossaryEntryRequest {
  term: string
  definition: string
  aliases?: string[]
  sensitivity: GlossarySensitivity
  evidenceKeys?: string[]
  proposedBy?: string
}

export interface ReviewGlossaryEntryRequest {
  confirm: boolean
  reviewedBy: string
  definition?: string
  aliases?: string[]
  sensitivity?: GlossarySensitivity
  comment?: string
}

export interface DatabaseRuntimeProviderStatus {
  pluginId: string
  databaseIdentity: string
  capabilities: string[]
  available: boolean
  error?: string | null
}

export interface IndexProgress {
  projectId?: string
  phase: string
  message: string
  percent: number
}

export interface GraphRagNode {
  id: string
  kind: string
  role: string
  name: string
  searchableText: string
  language: string
  technology: string | null
  state: string
  aliases: string[]
  filePath: string | null
  startLine: number | null
  endLine: number | null
  attributes: Record<string, string>
}

export interface ScoredGraphNode {
  node: GraphRagNode
  score: number
  depth: number
  seed: boolean
}

export interface GraphRagRelationship {
  id: string
  sourceId: string
  kind: string
  targetId: string
}

export interface ImpactResult {
  target: ScoredGraphNode | null
  affectedNodes: ScoredGraphNode[]
  relationships: GraphRagRelationship[]
  affectedFiles: string[]
  suggestedTestFilters: string[]
  truncated: boolean
}

/**
 * Project Change Intelligence is deliberately optional in the query response.
 * Older Agent Service versions return only answer/runId, so the desktop can be
 * upgraded independently of the service without losing project Q&A.
 */
export interface ProjectChangeTarget {
  kind?: string
  value?: string
  source?: string | null
  startLine?: number | null
  endLine?: number | null
}

export interface ProjectChangeClassification {
  changeKind?: string
  analysisMode?: string
  confidence?: string
  signals?: string[]
  isProjectScoped?: boolean
}

export interface ProjectChangeBrief {
  projectId?: string
  originalRequest?: string
  classification?: ProjectChangeClassification
  targets?: ProjectChangeTarget[]
  symptom?: string | null
  expectedBehavior?: string | null
  constraints?: string[]
  knownBoundaries?: string[]
  candidateAreas?: string[]
  unknowns?: string[]
}

export interface ProjectClarificationQuestion {
  priority?: number
  question?: string
  decisionImpact?: string
  category?: string
  isBlocking?: boolean
}

export interface ProjectEvidenceItem {
  id?: string
  kind?: string
  summary?: string
  confidence?: string
  sourceKind?: string
  filePath?: string | null
  startLine?: number | null
  endLine?: number | null
  symbol?: string | null
  relation?: string | null
  excerpt?: string | null
  reason?: string | null
  relevance?: number
}

export interface ProjectEvidencePath {
  kind?: string
  nodeIds?: string[]
  confidence?: string
  truncated?: boolean
}

export interface ProjectEvidencePack {
  brief?: ProjectChangeBrief
  items?: ProjectEvidenceItem[]
  paths?: ProjectEvidencePath[]
  freshness?: string
  manifestVersion?: string | null
  capabilityGaps?: string[]
  truncated?: boolean
}

export interface ProjectClarificationAnswer {
  category: string
  answer: string
}

export interface ProjectChangePlanStep {
  order?: number
  target?: string
  action?: string
  rationale?: string
  confidence?: string
  evidenceIds?: string[]
}

export interface ProjectChangeImplementationPlan {
  status?: string
  modificationSteps?: ProjectChangePlanStep[]
  impactAreas?: Array<{ scope?: string; riskLevel?: string; description?: string; evidenceIds?: string[] }>
  risks?: string[]
  tests?: Array<{ kind?: string; description?: string; relatedTargets?: string[] }>
  acceptanceCriteria?: string[]
  unknowns?: string[]
  manifestVersion?: string | null
}

export interface ProjectQueryResult {
  answer: string
  runId: string
  changeBrief?: ProjectChangeBrief
  clarificationQuestions?: ProjectClarificationQuestion[]
  evidencePack?: ProjectEvidencePack
  analysisSessionId?: string
  requiresClarification?: boolean
  implementationPlan?: ProjectChangeImplementationPlan
  /** P0/P2 service versions may return freshness at response level or inside the evidence pack. */
  indexFreshness?: string
  indexManifestVersion?: string | null
  indexStatus?: string
  indexedAt?: string | null
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value)

const optionalString = (value: unknown): string | undefined =>
  typeof value === 'string' ? value : undefined

const optionalNullableString = (value: unknown): string | null | undefined =>
  value === null ? null : optionalString(value)

const optionalNumber = (value: unknown): number | undefined =>
  typeof value === 'number' && Number.isFinite(value) ? value : undefined

const optionalBoolean = (value: unknown): boolean | undefined =>
  typeof value === 'boolean' ? value : undefined

const stringArray = (value: unknown): string[] | undefined =>
  Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string') : undefined

/**
 * Keep newly-added analysis metadata defensive: partial or newer service payloads
 * must never prevent the answer itself from rendering.
 */
const normalizeProjectQueryResult = (value: unknown): ProjectQueryResult => {
  const body = isRecord(value) ? value : {}
  const evidencePackSource = isRecord(body.evidencePack) ? body.evidencePack : undefined
  const changeBriefSource = isRecord(body.changeBrief)
    ? body.changeBrief
    : evidencePackSource && isRecord(evidencePackSource.brief)
      ? evidencePackSource.brief
      : undefined
  const classificationSource = changeBriefSource && isRecord(changeBriefSource.classification)
    ? changeBriefSource.classification
    : undefined
  const changeBrief: ProjectChangeBrief | undefined = changeBriefSource
    ? {
        projectId: optionalString(changeBriefSource.projectId),
        originalRequest: optionalString(changeBriefSource.originalRequest),
        classification: classificationSource
          ? {
              changeKind: optionalString(classificationSource.changeKind),
              analysisMode: optionalString(classificationSource.analysisMode),
              confidence: optionalString(classificationSource.confidence),
              signals: stringArray(classificationSource.signals),
              isProjectScoped: optionalBoolean(classificationSource.isProjectScoped),
            }
          : undefined,
        targets: Array.isArray(changeBriefSource.targets)
          ? changeBriefSource.targets.filter(isRecord).map((target) => ({
              kind: optionalString(target.kind),
              value: optionalString(target.value),
              source: optionalNullableString(target.source),
              startLine: optionalNumber(target.startLine),
              endLine: optionalNumber(target.endLine),
            }))
          : undefined,
        symptom: optionalNullableString(changeBriefSource.symptom),
        expectedBehavior: optionalNullableString(changeBriefSource.expectedBehavior),
        constraints: stringArray(changeBriefSource.constraints),
        knownBoundaries: stringArray(changeBriefSource.knownBoundaries),
        candidateAreas: stringArray(changeBriefSource.candidateAreas),
        unknowns: stringArray(changeBriefSource.unknowns),
      }
    : undefined

  const evidencePack: ProjectEvidencePack | undefined = evidencePackSource
    ? {
        brief: changeBrief,
        items: Array.isArray(evidencePackSource.items)
          ? evidencePackSource.items.filter(isRecord).map((item) => ({
              id: optionalString(item.id),
              kind: optionalString(item.kind),
              summary: optionalString(item.summary),
              confidence: optionalString(item.confidence),
              sourceKind: optionalString(item.sourceKind),
              filePath: optionalNullableString(item.filePath),
              startLine: optionalNumber(item.startLine),
              endLine: optionalNumber(item.endLine),
              symbol: optionalNullableString(item.symbol),
              relation: optionalNullableString(item.relation),
              excerpt: optionalNullableString(item.excerpt),
              reason: optionalNullableString(item.reason),
              relevance: optionalNumber(item.relevance),
            }))
          : undefined,
        paths: Array.isArray(evidencePackSource.paths)
          ? evidencePackSource.paths.filter(isRecord).map((path) => ({
              kind: optionalString(path.kind),
              nodeIds: stringArray(path.nodeIds),
              confidence: optionalString(path.confidence),
              truncated: optionalBoolean(path.truncated),
            }))
          : undefined,
        freshness: optionalString(evidencePackSource.freshness),
        manifestVersion: optionalNullableString(evidencePackSource.manifestVersion),
        capabilityGaps: stringArray(evidencePackSource.capabilityGaps),
        truncated: optionalBoolean(evidencePackSource.truncated),
      }
    : undefined
  const planSource = isRecord(body.implementationPlan) ? body.implementationPlan : undefined
  const implementationPlan: ProjectChangeImplementationPlan | undefined = planSource
    ? {
        status: optionalString(planSource.status),
        modificationSteps: Array.isArray(planSource.modificationSteps)
          ? planSource.modificationSteps.filter(isRecord).map((step) => ({
              order: optionalNumber(step.order),
              target: optionalString(step.target),
              action: optionalString(step.action),
              rationale: optionalString(step.rationale),
              confidence: optionalString(step.confidence),
              evidenceIds: stringArray(step.evidenceIds),
            }))
          : undefined,
        impactAreas: Array.isArray(planSource.impactAreas)
          ? planSource.impactAreas.filter(isRecord).map((area) => ({
              scope: optionalString(area.scope),
              riskLevel: optionalString(area.riskLevel),
              description: optionalString(area.description),
              evidenceIds: stringArray(area.evidenceIds),
            }))
          : undefined,
        risks: stringArray(planSource.risks),
        tests: Array.isArray(planSource.tests)
          ? planSource.tests.filter(isRecord).map((test) => ({
              kind: optionalString(test.kind),
              description: optionalString(test.description),
              relatedTargets: stringArray(test.relatedTargets),
            }))
          : undefined,
        acceptanceCriteria: stringArray(planSource.acceptanceCriteria),
        unknowns: stringArray(planSource.unknowns),
        manifestVersion: optionalNullableString(planSource.manifestVersion),
      }
    : undefined

  return {
    answer: optionalString(body.answer) ?? '',
    runId: optionalString(body.runId) ?? '',
    changeBrief,
    clarificationQuestions: Array.isArray(body.clarificationQuestions)
      ? body.clarificationQuestions.filter(isRecord).map((question) => ({
          priority: optionalNumber(question.priority),
          question: optionalString(question.question),
          decisionImpact: optionalString(question.decisionImpact),
          category: optionalString(question.category),
          isBlocking: optionalBoolean(question.isBlocking),
        }))
      : undefined,
    evidencePack,
    analysisSessionId: optionalString(body.analysisSessionId),
    requiresClarification: optionalBoolean(body.requiresClarification),
    implementationPlan,
    indexFreshness: optionalString(body.indexFreshness) ?? optionalString(body.freshness),
    indexManifestVersion: optionalNullableString(body.indexManifestVersion) ?? optionalNullableString(body.manifestVersion),
    indexStatus: optionalString(body.indexStatus),
    indexedAt: optionalNullableString(body.indexedAt),
  }
}

export interface CodeGraphVisualNode {
  id: string
  kind: string
  role: string
  name: string
  filePath: string | null
  startLine: number | null
  endLine: number | null
  language: string | null
  degree: number
  properties: Record<string, unknown>
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
  hasMore: boolean
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
}

export interface CodeGraphQueryResult {
  columns: string[]
  rows: Record<string, unknown>[]
  graph: CodeGraphVisualData
}

// ── API ───────────────────────────────────────────────────────────────────────

export async function listProjects(): Promise<ProjectInfo[]> {
  const res = await fetch(`${BASE_URL}/api/projects`)
  if (!res.ok) throw new Error(`listProjects: ${res.status}`)
  return res.json()
}

export async function createProject(name: string, rootPath: string): Promise<ProjectInfo> {
  const res = await fetch(`${BASE_URL}/api/projects`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, rootPath }),
  })
  if (!res.ok) {
    const body = await res.json().catch(() => null)
    throw new Error(body?.error ?? `createProject: ${res.status}`)
  }
  return res.json()
}

export async function deleteProject(projectId: string): Promise<void> {
  const res = await fetch(`${BASE_URL}/api/projects/${projectId}`, { method: 'DELETE' })
  if (!res.ok && res.status !== 204) throw new Error(`deleteProject: ${res.status}`)
}

export async function startIndex(projectId: string): Promise<void> {
  const res = await fetch(`${BASE_URL}/api/projects/${projectId}/index`, { method: 'POST' })
  if (!res.ok && res.status !== 202) throw new Error(`startIndex: ${res.status}`)
}

export async function getIndexProgress(projectId: string): Promise<IndexProgress> {
  const res = await fetch(`${BASE_URL}/api/projects/${projectId}/index/progress`)
  if (!res.ok) throw new Error(`getIndexProgress: ${res.status}`)
  return res.json()
}

export async function incrementalIndex(projectId: string): Promise<{ changed: boolean }> {
  const res = await fetch(`${BASE_URL}/api/projects/${projectId}/index/incremental`, {
    method: 'POST',
  })
  if (!res.ok) throw new Error(`incrementalIndex: ${res.status}`)
  return res.json()
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

export async function buildSummaries(projectId: string): Promise<void> {
  const res = await fetch(`${BASE_URL}/api/projects/${projectId}/summaries`, { method: 'POST' })
  if (!res.ok) throw new Error(`buildSummaries: ${res.status}`)
}

export async function getSummaryProgress(projectId: string): Promise<AiEnrichmentProgress> {
  const res = await fetch(`${BASE_URL}/api/projects/${projectId}/summaries/progress`)
  if (!res.ok) throw new Error(`getSummaryProgress: ${res.status}`)
  return res.json()
}

export async function queryProject(
  projectId: string,
  question: string,
  mode?: 'auto' | 'global' | 'local',
  providerProfileId?: string | null,
  modelId?: string | null,
  agentMode: import('@modern-wingman/contracts').AgentMode='plan',
  analysis?: {
    targets?: ProjectChangeTarget[]
    analysisSessionId?: string | null
    clarificationAnswers?: ProjectClarificationAnswer[]
  },
): Promise<ProjectQueryResult> {
  const res = await fetch(`${BASE_URL}/api/projects/${projectId}/query`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      question,
      mode: mode ?? 'auto',
      providerProfileId: providerProfileId ?? null,
      modelId: modelId ?? null,
      agentMode,
      targets: analysis?.targets ?? null,
      analysisSessionId: analysis?.analysisSessionId ?? null,
      clarificationAnswers: analysis?.clarificationAnswers ?? null,
    }),
  })
  if (!res.ok) {
    const body = await res.json().catch(() => null)
    throw new Error(body?.error ?? body?.detail ?? `queryProject: ${res.status}`)
  }
  return normalizeProjectQueryResult(await res.json())
}

export async function getProjectIndexDiagnostics(projectId: string): Promise<ProjectIndexDiagnostics> {
  const res = await fetch(`${BASE_URL}/api/projects/${projectId}/index/manifest`)
  if (!res.ok) throw new Error(`getProjectIndexDiagnostics: ${res.status}`)
  return res.json()
}

async function dataIntelligenceRequest<T>(projectId: string, path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE_URL}/api/projects/${encodeURIComponent(projectId)}/data-intelligence${path}`, init)
  if (!res.ok) {
    const body = await res.json().catch(() => null)
    throw new Error(body?.error ?? body?.detail ?? `dataIntelligence: ${res.status}`)
  }
  return res.json() as Promise<T>
}

export function scanProjectData(projectId: string): Promise<StaticDataScanResult> {
  return dataIntelligenceRequest(projectId, '/scan', { method: 'POST' })
}

export function listDomainGlossary(
  projectId: string,
  status?: GlossaryProposalStatus,
): Promise<DomainGlossaryEntry[]> {
  const query = status ? `?status=${encodeURIComponent(status)}` : ''
  return dataIntelligenceRequest(projectId, `/glossary${query}`)
}

export function proposeDomainGlossaryEntry(
  projectId: string,
  request: ProposeGlossaryEntryRequest,
): Promise<DomainGlossaryEntry> {
  return dataIntelligenceRequest(projectId, '/glossary', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  })
}

export function reviewDomainGlossaryEntry(
  projectId: string,
  entryId: string,
  request: ReviewGlossaryEntryRequest,
): Promise<DomainGlossaryEntry> {
  return dataIntelligenceRequest(projectId, `/glossary/${encodeURIComponent(entryId)}/review`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  })
}

export function getDatabaseRuntimeStatus(projectId: string, refresh = false): Promise<DatabaseRuntimeProviderStatus[]> {
  return dataIntelligenceRequest(projectId, `/runtime/status${refresh ? '?refresh=true' : ''}`)
}

export async function importProject(request: {
  sourceType: 'git' | 'svn'
  name: string
  profileId: string
  repositoryUrl: string
  ref: string | null
  destinationPath: string
  operationId?: string
}, signal?: AbortSignal): Promise<ProjectInfo> {
  const res = await fetch(`${BASE_URL}/api/projects/import`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
    signal,
  })
  if (!res.ok) {
    const body = await res.json().catch(() => null)
    throw new Error(body?.error ?? body?.Error ?? `importProject: ${res.status}`)
  }
  return res.json()
}

export interface ProjectImportProgress {
  operationId: string
  sourceType: string
  status: 'running' | 'completed' | 'failed' | 'cancelled'
  message: string
  isError: boolean
  startedAt: string
  updatedAt: string
}

export async function getProjectImportProgress(operationId: string): Promise<ProjectImportProgress | null> {
  const res = await fetch(`${BASE_URL}/api/projects/import-progress/${encodeURIComponent(operationId)}`)
  if (res.status === 404) return null
  if (!res.ok) throw new Error(`getProjectImportProgress: ${res.status}`)
  return res.json()
}

export async function analyzeImpact(
  projectId: string,
  symbol: string,
  maxDepth = 3,
): Promise<ImpactResult> {
  const res = await fetch(`${BASE_URL}/api/projects/${projectId}/impact`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ symbol, maxDepth }),
  })
  if (!res.ok) throw new Error(`analyzeImpact: ${res.status}`)
  return res.json()
}

export async function getRepoMap(projectId: string): Promise<string> {
  const res = await fetch(`${BASE_URL}/api/projects/${projectId}/repomap`)
  if (!res.ok) throw new Error(`getRepoMap: ${res.status}`)
  const body = await res.json()
  return body.map
}

export async function generateAgentsMd(projectId: string): Promise<string> {
  const res = await fetch(`${BASE_URL}/api/projects/${projectId}/agents-md`, { method: 'POST' })
  if (!res.ok) throw new Error(`generateAgentsMd: ${res.status}`)
  const body = await res.json()
  return body.content
}

// Bundled Neo4j cold start is allowed up to 90 seconds. Keep a finite UI guard
// above that SLA so a legitimate first start is not canceled prematurely.
const GRAPH_REQUEST_TIMEOUT_MS = 120_000

async function fetchGraphApi(url: string, init?: RequestInit): Promise<Response> {
  const controller = new AbortController()
  const timer = window.setTimeout(() => controller.abort(), GRAPH_REQUEST_TIMEOUT_MS)
  try {
    return await fetch(url, { ...init, signal: controller.signal })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw new Error('圖譜服務在 120 秒內沒有回應，請確認 Wingman 管理的 Neo4j 狀態後重試。')
    }
    throw error
  } finally {
    window.clearTimeout(timer)
  }
}

export async function getProjectGraphSchema(projectId: string): Promise<CodeGraphSchema> {
  const res = await fetchGraphApi(`${BASE_URL}/api/projects/${projectId}/graph/schema`)
  if (!res.ok) {
    const body = await res.json().catch(() => null)
    throw new Error(body?.error ?? `getProjectGraphSchema: ${res.status}`)
  }
  return res.json()
}

export async function getProjectGraph(
  projectId: string,
  options?: {
    limit?: number
    kinds?: string[]
    relations?: string[]
  },
): Promise<CodeGraphVisualData> {
  const params = new URLSearchParams()
  if (options?.limit) params.set('limit', String(options.limit))
  if (options?.kinds?.length) params.set('kinds', options.kinds.join(','))
  if (options?.relations?.length) params.set('relations', options.relations.join(','))

  const query = params.toString()
  const res = await fetchGraphApi(`${BASE_URL}/api/projects/${projectId}/graph${query ? `?${query}` : ''}`)
  if (!res.ok) {
    const body = await res.json().catch(() => null)
    throw new Error(body?.error ?? `getProjectGraph: ${res.status}`)
  }
  return res.json()
}

export async function queryProjectGraph(
  projectId: string,
  cypher: string,
  limit = 1000,
): Promise<CodeGraphQueryResult> {
  const res = await fetchGraphApi(`${BASE_URL}/api/projects/${projectId}/graph/query`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ cypher, limit }),
  })
  if (!res.ok) {
    const body = await res.json().catch(() => null)
    throw new Error(body?.error ?? body?.detail ?? `queryProjectGraph: ${res.status}`)
  }
  return res.json()
}

export async function expandProjectGraphNeighbors(
  projectId: string,
  nodeKeys: string[],
  options?: {
    depth?: number
    limit?: number
    mode?: 'all' | 'callers' | 'callees' | 'same-file'
  },
): Promise<CodeGraphVisualData> {
  const res = await fetchGraphApi(`${BASE_URL}/api/projects/${projectId}/graph/neighbors`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      nodeKeys,
      depth: options?.depth ?? 1,
      limit: options?.limit ?? 1000,
      mode: options?.mode ?? 'all',
    }),
  })
  if (!res.ok) {
    const body = await res.json().catch(() => null)
    throw new Error(body?.error ?? body?.detail ?? `expandProjectGraphNeighbors: ${res.status}`)
  }
  return res.json()
}
