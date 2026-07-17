export interface SkillSourceInfo {
  id: string
  displayName: string
  /** GitHub "owner/repo" slug */
  repo: string
  /** Path inside the repo where skill sub-directories live */
  skillsRoot: string
  isBuiltin: boolean
  enabled: boolean
}

export interface SkillMeta {
  /** "<source_id>/<skill_name>" */
  id: string
  sourceId: string
  skillName: string
  displayName: string
  description: string | null
  cachedAt: number
}

export interface InstalledSkillInfo {
  id: number
  sourceId: string
  skillName: string
  agentId: string
  /** "global" | "project" */
  scope: string
  projectPath: string | null
  installedPath: string
  installedAt: number
}

export interface AgentInfo {
  id: string
  displayName: string
  globalSkillsPath: string
  projectSkillsSubpath: string
  isBuiltin: boolean
  icon: string | null
  customGlobalPath: string | null
  /** Resolved: customGlobalPath ?? globalSkillsPath */
  effectiveGlobalPath: string
}

export interface InstallSkillParams {
  sourceId: string
  skillName: string
  agentId: string
  scope: 'global' | 'project'
  projectPath?: string
  githubPat?: string
}

// ── WS1: Central Skill Library ───────────────────────────────────────────────

export interface LibrarySkill {
  id: number
  name: string
  displayName: string
  description: string | null
  /** "github" | "local" | "zip" */
  sourceKind: string
  sourceRef: string
  libraryPath: string
  contentHash: string
  /** "low" | "medium" | "high" */
  riskLevel: string
  riskNotes: string | null
  /** Comma-separated */
  tags: string
  installedAt: number
  updatedAt: number
}

export interface SkillAgentLink {
  id: number
  skillId: number
  agentId: string
  /** "global" | "project" */
  scope: string
  projectPath: string | null
  targetPath: string
  /** "junction" | "symlink" | "copy" */
  syncMode: string
  syncedAt: number
}

export interface RiskFinding {
  severity: 'low' | 'medium' | 'high'
  rule: string
  message: string
  excerpt: string
}

export interface RiskReport {
  level: 'low' | 'medium' | 'high'
  findings: RiskFinding[]
}

export interface InstallToLibraryParams {
  /** "github" | "local" | "zip" */
  sourceKind: string
  /** github: "<source_id>/<skill_name>" ; local: dir path ; zip: zip path */
  sourceRef: string
  githubPat?: string
}

export interface InstallToLibraryResult {
  skill: LibrarySkill
  risk: RiskReport
}

export interface SkillPreset {
  id: number
  name: string
  skillIds: number[]
}

export interface AgentPresence {
  agentId: string
  detected: boolean
  unmanagedSkills: string[]
}

// ── WS1: MCP Registry ────────────────────────────────────────────────────────

export interface McpServer {
  id: number
  name: string
  /** "stdio" | "sse" | "http" */
  transport: string
  command: string | null
  args: string[]
  url: string | null
  env: Record<string, string>
  enabled: boolean
  createdAt: number
  updatedAt: number
  linkedAgents: string[]
}

export interface UpsertMcpServerParams {
  id?: number
  name: string
  transport: 'stdio' | 'sse' | 'http'
  command?: string
  args?: string[]
  url?: string
  env?: Record<string, string>
  enabled?: boolean
}
