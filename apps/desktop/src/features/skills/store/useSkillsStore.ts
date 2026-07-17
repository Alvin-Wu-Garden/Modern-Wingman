import { invoke } from '@tauri-apps/api/core'
import { create } from 'zustand'
import type {
  AgentInfo,
  InstalledSkillInfo,
  InstallSkillParams,
  SkillMeta,
  SkillSourceInfo,
} from '@modern-wingman/contracts'
import { parseSkillDescription } from '../utils/parseSkillMd'
import { refreshWingmanSkills } from '@/services/agent-api/skills-runtime'

const hasTauriRuntime = () =>
  typeof window !== 'undefined' && '__TAURI_INTERNALS__' in window

// ── State shape ───────────────────────────────────────────────────────────────

interface SkillsState {
  sources: SkillSourceInfo[]
  skillsBySource: Record<string, SkillMeta[]>
  loadingBySource: Record<string, boolean>
  errorBySource: Record<string, string | null>
  installedSkills: InstalledSkillInfo[]
  agents: AgentInfo[]
  actionError: string | null

  // README & description cache — keyed by "<sourceId>/<skillName>"
  readmeCache: Record<string, string>
  readmeLoading: Record<string, boolean>
  readmeErrors: Record<string, string>
  descriptionCache: Record<string, string>

  // ── Actions ────────────────────────────────────────────────────────────
  fetchSources: () => Promise<void>
  fetchSkills: (sourceId: string, githubPat?: string) => Promise<void>
  fetchInstalledSkills: () => Promise<void>
  fetchAgents: () => Promise<void>
  installSkill: (params: InstallSkillParams) => Promise<void>
  uninstallSkill: (installId: number) => Promise<void>
  updateAgentPath: (agentId: string, customPath?: string) => Promise<void>
  clearActionError: () => void
  /** Fetches SKILL.md and caches both full content + parsed description. */
  fetchReadme: (sourceId: string, skillName: string, githubPat?: string) => Promise<void>
  /** Clear the readme error for a skill so it can be retried. */
  clearReadmeError: (sourceId: string, skillName: string) => void
}

// ── Store ─────────────────────────────────────────────────────────────────────

export const useSkillsStore = create<SkillsState>((set, get) => ({
  sources: [],
  skillsBySource: {},
  loadingBySource: {},
  errorBySource: {},
  installedSkills: [],
  agents: [],
  actionError: null,
  readmeCache: {},
  readmeLoading: {},
  readmeErrors: {},
  descriptionCache: {},

  fetchSources: async () => {
    if (!hasTauriRuntime()) {
      set({ sources: [] })
      return
    }
    const sources = await invoke<SkillSourceInfo[]>('list_skill_sources')
    set({ sources })
  },

  fetchSkills: async (sourceId, githubPat) => {
    set((s) => ({
      loadingBySource: { ...s.loadingBySource, [sourceId]: true },
      errorBySource: { ...s.errorBySource, [sourceId]: null },
    }))
    try {
      const skills = await invoke<SkillMeta[]>('fetch_remote_skills', {
        sourceId,
        githubPat: githubPat || null,
      })
      set((s) => ({
        skillsBySource: { ...s.skillsBySource, [sourceId]: skills },
        loadingBySource: { ...s.loadingBySource, [sourceId]: false },
      }))
    } catch (err) {
      set((s) => ({
        loadingBySource: { ...s.loadingBySource, [sourceId]: false },
        errorBySource: {
          ...s.errorBySource,
          [sourceId]: err instanceof Error ? err.message : String(err),
        },
      }))
    }
  },

  fetchInstalledSkills: async () => {
    if (!hasTauriRuntime()) {
      set({ installedSkills: [] })
      return
    }
    const installedSkills = await invoke<InstalledSkillInfo[]>('list_installed_skills')
    set({ installedSkills })
  },

  fetchAgents: async () => {
    if (!hasTauriRuntime()) {
      set({ agents: [] })
      return
    }
    const agents = await invoke<AgentInfo[]>('list_agents')
    set({ agents })
  },

  installSkill: async (params) => {
    set({ actionError: null })
    try {
      const installed = await invoke<InstalledSkillInfo>('install_skill', {
        sourceId: params.sourceId,
        skillName: params.skillName,
        agentId: params.agentId,
        scope: params.scope,
        projectPath: params.projectPath ?? null,
        githubPat: params.githubPat || null,
      })
      set((s) => ({ installedSkills: [installed, ...s.installedSkills] }))
      if (params.agentId === 'wingman') await refreshWingmanSkills().catch(() => undefined)
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err)
      set({ actionError: msg })
      throw err
    }
  },

  uninstallSkill: async (installId) => {
    set({ actionError: null })
    try {
      await invoke('uninstall_skill', { installId })
      set((s) => ({
        installedSkills: s.installedSkills.filter((i) => i.id !== installId),
      }))
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err)
      set({ actionError: msg })
      throw err
    }
  },

  updateAgentPath: async (agentId, customPath) => {
    await invoke('update_agent_path', {
      agentId,
      customGlobalPath: customPath ?? null,
    })
    // Refresh agent list
    await get().fetchAgents()
  },

  clearActionError: () => set({ actionError: null }),

  fetchReadme: async (sourceId, skillName, githubPat) => {
    const skillId = `${sourceId}/${skillName}`
    const state = get()
    // Skip if already successfully cached or in-flight
    if (state.readmeCache[skillId] !== undefined) {
      const description = parseSkillDescription(state.readmeCache[skillId])
      if (state.descriptionCache[skillId] !== description) {
        set((s) => ({
          descriptionCache: { ...s.descriptionCache, [skillId]: description },
        }))
      }
      return
    }
    if (state.readmeLoading[skillId]) return

    set((s) => ({
      readmeLoading: { ...s.readmeLoading, [skillId]: true },
      // Clear previous error on retry
      readmeErrors: { ...s.readmeErrors, [skillId]: '' },
    }))
    try {
      const content = await invoke<string>('get_skill_readme', {
        sourceId,
        skillName,
        githubPat: githubPat || null,
      })
      const description = parseSkillDescription(content)
      set((s) => ({
        readmeCache: { ...s.readmeCache, [skillId]: content },
        descriptionCache: { ...s.descriptionCache, [skillId]: description },
        readmeLoading: { ...s.readmeLoading, [skillId]: false },
      }))
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err)
      console.error(`[Skills] fetchReadme failed for ${skillId}:`, msg)
      // Do NOT cache on error — leave readmeCache[skillId] = undefined so the
      // detail page can retry when the user opens it.
      set((s) => ({
        readmeLoading: { ...s.readmeLoading, [skillId]: false },
        readmeErrors: { ...s.readmeErrors, [skillId]: msg },
      }))
    }
  },

  clearReadmeError: (sourceId, skillName) => {
    const skillId = `${sourceId}/${skillName}`
    set((s) => ({ readmeErrors: { ...s.readmeErrors, [skillId]: '' } }))
  },
}))
