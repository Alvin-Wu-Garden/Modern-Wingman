const BASE_URL = 'http://localhost:5002'

export type MarketplaceArtifactKind = 'Unknown' | 'Skill' | 'McpServer' | 'WingmanPlugin' | 'UnsupportedProject'
export type MarketplaceDiscoveryStatus = 'Discovered' | 'Scored' | 'Stale' | 'Resolving' | 'Resolved' | 'ManualSetupRequired' | 'ManualReviewRequired' | 'Invalid'

export interface MarketplaceDiscoveryRecord {
  id: string
  sourceId: string
  gitHubNodeId: string | null
  canonicalUrl: string
  owner: string
  repository: string
  name: string
  description: string | null
  suggestedKind: MarketplaceArtifactKind
  classificationConfidence: string
  primaryCategory: string
  secondaryCategories: string[]
  topics: string[]
  license: string | null
  isArchived: boolean
  stars: number
  forks: number
  gitHubUpdatedAt: string | null
  pushedAt: string | null
  firstSeenAt: string
  lastSeenAt: string
  consecutiveMissCount: number
  status: MarketplaceDiscoveryStatus
  metadataFingerprint: string
  discoveryScore: number
  discoveryScoreProfileId: string
  artifactQualityScoreProfileId: string | null
  artifactQualityScore: number | null
  isFavorite: boolean
  isManualSource: boolean
}

export interface MarketplacePageResult {
  items: MarketplaceDiscoveryRecord[]
  totalCount: number
}

export interface MarketplaceRefreshResult {
  syncRunId: string
  newCount: number
  updatedCount: number
  unchangedCount: number
  staleCount: number
  prunedCount: number
  successfulQueries: number
  totalQueries: number
  isPartial: boolean
  completedAt: string
}

export interface MarketplaceArtifactCandidate {
  id: string
  sourceLocation: string
  artifactPath: string
  kind: MarketplaceArtifactKind
  displayName: string
  status: MarketplaceDiscoveryStatus
  validationProfileId: string | null
  validationMessage: string | null
  createdAt: string
}

export interface MarketplaceArtifact {
  id: string
  candidateId: string
  kind: MarketplaceArtifactKind
  displayName: string
  snapshotPath: string
  contentHash: string
  status: MarketplaceDiscoveryStatus
  validationProfileId: string | null
  importedAt: string
}

export interface MarketplaceImportResult {
  sourceLocation: string
  candidates: MarketplaceArtifactCandidate[]
  artifacts: MarketplaceArtifact[]
}

export type MarketplaceDeploymentScope = 'Global' | 'Project'
export interface MarketplaceTargetDescriptor {
  id: string
  displayName: string
  supportsSkill: boolean
  supportsMcp: boolean
  supportsGlobalScope: boolean
  supportsProjectScope: boolean
  isDetected: boolean
  detectionReason: string | null
}
export interface MarketplaceDeploymentRequest {
  artifactId: string
  targetId: string
  scope: MarketplaceDeploymentScope
  projectPath?: string | null
}
export interface MarketplaceDeploymentResult {
  targetId: string
  scope: MarketplaceDeploymentScope
  status: string
  targetPath: string | null
  message: string | null
}
export interface MarketplaceDeploymentBatchResult { results: MarketplaceDeploymentResult[]; isPartialSuccess: boolean }
export interface MarketplaceInstallabilityResult { artifactId: string; targetId: string; scope: MarketplaceDeploymentScope; status: string; targetPath: string | null; reason: string | null; computedAt: string }
export interface MarketplaceDeploymentPlan { artifactId: string; operation: string; items: MarketplaceInstallabilityResult[] }
export interface MarketplaceDeploymentState { targetId: string; scope: MarketplaceDeploymentScope; projectPath: string | null; targetPath: string; deployedHash: string; status: string; updatedAt: string }
export interface MarketplacePluginInstallation {
  id: string
  artifactId: string
  pluginId: string
  version: string
  enabled: boolean
  installedPath: string
  installedAt: string
}
export interface MarketplacePluginPreview { installationId: string; pluginId: string; version: string; skillPaths: string[]; mcpPaths: string[]; functionIds: string[]; hookIds: string[]; safetySummary: string }
export interface MarketplacePluginConfigurationField { name: string; isSecret: boolean; isConfigured: boolean }
export interface MarketplacePluginConfiguration { installationId: string; pluginId: string; fields: MarketplacePluginConfigurationField[] }
export interface MarketplaceArtifactUpdate { artifactId: string; displayName: string; sourceLocation: string; installedCommitSha: string | null; status: string; availableCommitSha: string | null; message: string | null }
export interface MarketplaceUpdateCheck { id: string; artifactId: string; sourceLocation: string; installedCommitSha: string | null; status: string; availableCommitSha: string | null; message: string | null; checkedAt: string }
export interface MarketplaceArtifactUpdateApplicationResult { artifactId: string; previousCommitSha: string; newCommitSha: string; import: MarketplaceImportResult }

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${BASE_URL}${path}`, init)
  if (!response.ok) {
    const body = await response.text()
    try {
      const parsed = JSON.parse(body) as { error?: string }
      throw new Error(parsed.error ?? `Request failed (${response.status})`)
    } catch (error) {
      if (error instanceof Error && error.message !== body) throw error
      throw new Error(body || `Request failed (${response.status})`)
    }
  }
  return response.json() as Promise<T>
}

export function listMarketplace(params: {
  kind?: MarketplaceArtifactKind
  search?: string
  includeStale?: boolean
  take?: number
} = {}): Promise<MarketplacePageResult> {
  const search = new URLSearchParams()
  if (params.kind) search.set('kind', params.kind)
  if (params.search?.trim()) search.set('search', params.search.trim())
  if (params.includeStale) search.set('includeStale', 'true')
  search.set('take', String(params.take ?? 100))
  return request<MarketplacePageResult>(`/api/marketplace/discover?${search.toString()}`)
}

export function refreshMarketplace(): Promise<MarketplaceRefreshResult> {
  return request<MarketplaceRefreshResult>('/api/marketplace/refresh', { method: 'POST' })
}

export function getMarketplaceDiscovery(id: string): Promise<MarketplaceDiscoveryRecord> {
  return request<MarketplaceDiscoveryRecord>(`/api/marketplace/discover/${encodeURIComponent(id)}`)
}

export async function setMarketplaceFavorite(id: string, isFavorite: boolean): Promise<void> {
  const response = await fetch(`${BASE_URL}/api/marketplace/discover/${encodeURIComponent(id)}/favorite`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ isFavorite }),
  })
  if (!response.ok) throw new Error(await response.text())
}

export function importMarketplaceFolder(folderPath: string): Promise<MarketplaceImportResult> {
  return request<MarketplaceImportResult>('/api/marketplace/import/folder', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ folderPath }),
  })
}

export function importMarketplaceArchive(archivePath: string): Promise<MarketplaceImportResult> {
  return request<MarketplaceImportResult>('/api/marketplace/import/archive', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ archivePath }),
  })
}

export interface CodexMarketplaceImportResult { importedArtifacts: number; invalidCandidates: number; skippedEntries: string[] }
export interface GitHubRepositoryImportResult { canonicalUrl: string; requestedRef: string; commitSha: string; import: MarketplaceImportResult }
export function importCodexMarketplace(marketplaceJsonPath: string): Promise<CodexMarketplaceImportResult> {
  return request<CodexMarketplaceImportResult>('/api/marketplace/import/codex-marketplace', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ marketplaceJsonPath }),
  })
}
export function importGitHubRepository(repositoryUrl: string, reference?: string): Promise<GitHubRepositoryImportResult> {
  return request<GitHubRepositoryImportResult>('/api/marketplace/import/github', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ repositoryUrl, reference: reference || null }) })
}

export function listMarketplaceArtifacts(): Promise<MarketplaceArtifact[]> { return request<MarketplaceArtifact[]>('/api/marketplace/artifacts') }
export function checkMarketplaceUpdates(): Promise<MarketplaceArtifactUpdate[]> { return request<MarketplaceArtifactUpdate[]>('/api/marketplace/updates/check', { method: 'POST' }) }
export function listMarketplaceUpdateHistory(artifactId?: string): Promise<MarketplaceUpdateCheck[]> { return request<MarketplaceUpdateCheck[]>(`/api/marketplace/updates/history${artifactId ? `?artifactId=${encodeURIComponent(artifactId)}` : ''}`) }
export function applyMarketplaceUpdate(artifactId: string, expectedCommitSha: string): Promise<MarketplaceArtifactUpdateApplicationResult> { return request<MarketplaceArtifactUpdateApplicationResult>(`/api/marketplace/artifacts/${encodeURIComponent(artifactId)}/updates/apply`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ expectedCommitSha }) }) }
export function listMarketplaceTargets(): Promise<MarketplaceTargetDescriptor[]> { return request<MarketplaceTargetDescriptor[]>('/api/marketplace/targets') }
export function deployMarketplaceSkills(requests: MarketplaceDeploymentRequest[]): Promise<MarketplaceDeploymentBatchResult> {
  return request<MarketplaceDeploymentBatchResult>('/api/marketplace/deploy/skills', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ requests }) })
}
export function previewMarketplaceSkills(requests: MarketplaceDeploymentRequest[]): Promise<MarketplaceDeploymentPlan> {
  return request<MarketplaceDeploymentPlan>('/api/marketplace/deploy/skills/preview', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ requests }) })
}
export function deployMarketplaceMcp(requests: MarketplaceDeploymentRequest[]): Promise<MarketplaceDeploymentBatchResult> {
  return request<MarketplaceDeploymentBatchResult>('/api/marketplace/deploy/mcp', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ requests }) })
}
export function previewMarketplaceMcp(requests: MarketplaceDeploymentRequest[]): Promise<MarketplaceDeploymentPlan> {
  return request<MarketplaceDeploymentPlan>('/api/marketplace/deploy/mcp/preview', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ requests }) })
}
export function listMarketplaceArtifactDeployments(artifactId: string): Promise<MarketplaceDeploymentState[]> { return request<MarketplaceDeploymentState[]>(`/api/marketplace/artifacts/${encodeURIComponent(artifactId)}/deployments`) }
export function removeMarketplaceArtifactDeployments(artifactId: string): Promise<MarketplaceDeploymentBatchResult> {
  return request<MarketplaceDeploymentBatchResult>(`/api/marketplace/artifacts/${encodeURIComponent(artifactId)}/deployments`, { method: 'DELETE' })
}
export function removeMarketplaceMcpDeployments(artifactId: string): Promise<MarketplaceDeploymentBatchResult> {
  return request<MarketplaceDeploymentBatchResult>(`/api/marketplace/artifacts/${encodeURIComponent(artifactId)}/mcp-deployments`, { method: 'DELETE' })
}
export function listMarketplacePlugins(): Promise<MarketplacePluginInstallation[]> { return request<MarketplacePluginInstallation[]>('/api/marketplace/plugins') }
export function getMarketplacePluginPreview(installationId: string): Promise<MarketplacePluginPreview> { return request<MarketplacePluginPreview>(`/api/marketplace/plugins/${encodeURIComponent(installationId)}/preview`) }
export function installMarketplacePlugin(artifactId: string): Promise<MarketplacePluginInstallation> { return request<MarketplacePluginInstallation>(`/api/marketplace/plugins/${encodeURIComponent(artifactId)}/install`, { method: 'POST' }) }
export async function setMarketplacePluginEnabled(installationId: string, enabled: boolean): Promise<void> {
  const response = await fetch(`${BASE_URL}/api/marketplace/plugins/${encodeURIComponent(installationId)}/enabled/${enabled}`, { method: 'PUT' })
  if (!response.ok) throw new Error(await response.text())
}
export function getMarketplacePluginConfiguration(installationId: string): Promise<MarketplacePluginConfiguration> { return request<MarketplacePluginConfiguration>(`/api/marketplace/plugins/${encodeURIComponent(installationId)}/configuration`) }
export async function saveMarketplacePluginConfiguration(installationId: string, values: Record<string, string>): Promise<void> {
  const response = await fetch(`${BASE_URL}/api/marketplace/plugins/${encodeURIComponent(installationId)}/configuration`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ values }) })
  if (!response.ok) throw new Error(await response.text())
}
