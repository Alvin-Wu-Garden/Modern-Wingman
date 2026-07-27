import { AGENT_API_BASE_URL } from './client'

export type MarketplaceArtifactKind = 'Skill' | 'McpServer'

export interface MarketplaceDiscoveryRecord {
  id: string
  canonicalUrl: string
  owner: string
  repository: string
  name: string
  description: string | null
  suggestedKind: MarketplaceArtifactKind | 'Unknown'
  stars: number
}

export interface MarketplacePageResult {
  items: MarketplaceDiscoveryRecord[]
  totalCount: number
}

export interface MarketplaceRefreshResult {
  newCount: number
  updatedCount: number
}

export interface MarketplaceArtifact {
  id: string
  kind: MarketplaceArtifactKind
  displayName: string
  snapshotPath: string
  contentHash: string
  importedAt: string
}

export interface MarketplaceImportResult {
  sourceLocation: string
  artifacts: MarketplaceArtifact[]
}

export interface MarketplaceTargetDescriptor {
  id: string
  displayName: string
  supportsSkill: boolean
  supportsMcp: boolean
  supportsGlobalScope: boolean
  supportsProjectScope: boolean
  supportsGlobalMcp: boolean
  supportsProjectMcp: boolean
  isDetected: boolean
  detectionReason: string | null
}

export interface MarketplaceDeploymentRequest {
  artifactId: string
  targetId: string
  scope: 'Global' | 'Project'
  projectPath?: string | null
}

export interface MarketplaceDeploymentBatchResult {
  results: Array<{
    targetId: string
    scope: 'Global' | 'Project'
    status: string
    targetPath: string | null
    message: string | null
  }>
}

/** 部署前的唯讀相容性檢查；不建立資料夾也不寫入目標設定檔。 */
export interface MarketplaceDeploymentPlan {
  artifactId: string
  operation: string
  items: Array<{
    artifactId: string
    targetId: string
    scope: 'Global' | 'Project'
    status: string
    targetPath: string | null
    reason: string | null
  }>
}

export interface GitHubRepositoryImportResult {
  import: MarketplaceImportResult
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${AGENT_API_BASE_URL}${path}`, init)
  if (!response.ok) {
    const body = await response.json().catch(() => null)
    throw new Error(body?.error ?? `Marketplace 請求失敗 (${response.status})`)
  }
  return response.json() as Promise<T>
}

export function listMarketplace(params: {
  kind: MarketplaceArtifactKind
  search?: string
}): Promise<MarketplacePageResult> {
  const query = new URLSearchParams({ kind: params.kind, take: '100' })
  if (params.search?.trim()) query.set('search', params.search.trim())
  return request(`/api/marketplace/discover?${query}`)
}

export function refreshMarketplace(): Promise<MarketplaceRefreshResult> {
  return request('/api/marketplace/refresh', { method: 'POST' })
}

export function importMarketplaceFolder(folderPath: string): Promise<MarketplaceImportResult> {
  return request('/api/marketplace/import/folder', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ folderPath }),
  })
}

export function importGitHubRepository(
  repositoryUrl: string,
  reference?: string,
): Promise<GitHubRepositoryImportResult> {
  return request('/api/marketplace/import/github', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ repositoryUrl, reference: reference || null }),
  })
}

export function listMarketplaceArtifacts(): Promise<MarketplaceArtifact[]> {
  return request('/api/marketplace/artifacts')
}

export function listMarketplaceTargets(): Promise<MarketplaceTargetDescriptor[]> {
  return request('/api/marketplace/targets')
}

export function deployMarketplaceSkills(
  requests: MarketplaceDeploymentRequest[],
): Promise<MarketplaceDeploymentBatchResult> {
  return request('/api/marketplace/deploy/skills', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ requests }),
  })
}

export function deployMarketplaceMcp(
  requests: MarketplaceDeploymentRequest[],
): Promise<MarketplaceDeploymentBatchResult> {
  return request('/api/marketplace/deploy/mcp', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ requests }),
  })
}

/** 預覽 Skill 實體複製結果，讓使用者在寫入前看見衝突與目的地。 */
export function previewMarketplaceSkills(
  requests: MarketplaceDeploymentRequest[],
): Promise<MarketplaceDeploymentPlan> {
  return request('/api/marketplace/deploy/skills/preview', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ requests }),
  })
}

/** 預覽 MCP JSON 合併結果，且不啟動或探測 MCP Server。 */
export function previewMarketplaceMcp(
  requests: MarketplaceDeploymentRequest[],
): Promise<MarketplaceDeploymentPlan> {
  return request('/api/marketplace/deploy/mcp/preview', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ requests }),
  })
}

/** 只移除 Modern Wingman 有部署紀錄且內容未漂移的 Skill。 */
export function removeMarketplaceSkills(
  artifactId: string,
): Promise<MarketplaceDeploymentBatchResult> {
  return request(`/api/marketplace/artifacts/${encodeURIComponent(artifactId)}/deployments`, {
    method: 'DELETE',
  })
}

/** 只移除 Modern Wingman 寫入的 MCP server entry，保留同檔其他設定。 */
export function removeMarketplaceMcp(
  artifactId: string,
): Promise<MarketplaceDeploymentBatchResult> {
  return request(`/api/marketplace/artifacts/${encodeURIComponent(artifactId)}/mcp-deployments`, {
    method: 'DELETE',
  })
}
