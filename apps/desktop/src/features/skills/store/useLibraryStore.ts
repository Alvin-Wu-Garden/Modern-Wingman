import { invoke } from '@tauri-apps/api/core'
import { create } from 'zustand'
import type {
  AgentPresence,
  InstallToLibraryParams,
  InstallToLibraryResult,
  LibrarySkill,
  RiskReport,
  SkillAgentLink,
  SkillPreset,
} from '@modern-wingman/contracts'
import { refreshWingmanSkills } from '@/services/agent-api/skills-runtime'

/**
 * Central Skill Library store (WS1).
 * Responsibility: library CRUD + agent sync links + presets + adoption.
 * (Marketplace browsing stays in useSkillsStore; MCP in useMcpStore.)
 */
interface LibraryState {
  skills: LibrarySkill[]
  links: SkillAgentLink[]
  presets: SkillPreset[]
  presence: AgentPresence[]
  loading: boolean
  error: string | null

  fetchAll: () => Promise<void>
  installSkill: (params: InstallToLibraryParams) => Promise<InstallToLibraryResult>
  previewSkill: (params: InstallToLibraryParams) => Promise<RiskReport>
  removeSkill: (skillId: number) => Promise<void>
  setTags: (skillId: number, tags: string) => Promise<void>
  readSkillMd: (skillId: number) => Promise<string>
  syncSkill: (skillId: number, agentId: string, scope: 'global' | 'project', projectPath?: string) => Promise<void>
  unsyncSkill: (linkId: number) => Promise<void>
  detectAgents: () => Promise<void>
  adoptSkill: (agentId: string, skillName: string) => Promise<void>
  createPreset: (name: string) => Promise<void>
  deletePreset: (presetId: number) => Promise<void>
  setPresetMember: (presetId: number, skillId: number, member: boolean) => Promise<void>
  applyPreset: (presetId: number, agentId: string, scope: 'global' | 'project', projectPath?: string) => Promise<void>
  clearError: () => void
}

const toMessage = (err: unknown) => (err instanceof Error ? err.message : String(err))

export const useLibraryStore = create<LibraryState>((set, get) => ({
  skills: [],
  links: [],
  presets: [],
  presence: [],
  loading: false,
  error: null,

  fetchAll: async () => {
    set({ loading: true, error: null })
    try {
      const [skills, links, presets] = await Promise.all([
        invoke<LibrarySkill[]>('library_list_skills'),
        invoke<SkillAgentLink[]>('library_list_links'),
        invoke<SkillPreset[]>('library_list_presets'),
      ])
      set({ skills, links, presets, loading: false })
    } catch (err) {
      set({ loading: false, error: toMessage(err) })
    }
  },

  installSkill: async (params) => {
    set({ error: null })
    try {
      const result = await invoke<InstallToLibraryResult>('library_install_skill', { params })
      await get().fetchAll()
      return result
    } catch (err) {
      set({ error: toMessage(err) })
      throw err
    }
  },

  previewSkill: (params) => invoke<RiskReport>('library_preview_skill', { params }),

  removeSkill: async (skillId) => {
    set({ error: null })
    try {
      await invoke('library_remove_skill', { skillId })
      await get().fetchAll()
      await refreshWingmanSkills().catch(() => undefined)
    } catch (err) {
      set({ error: toMessage(err) })
      throw err
    }
  },

  setTags: async (skillId, tags) => {
    await invoke('library_set_tags', { skillId, tags })
    await get().fetchAll()
  },

  readSkillMd: (skillId) => invoke<string>('library_read_skill_md', { skillId }),

  syncSkill: async (skillId, agentId, scope, projectPath) => {
    set({ error: null })
    try {
      await invoke<SkillAgentLink>('library_sync_skill', {
        skillId,
        agentId,
        scope,
        projectPath: projectPath ?? null,
      })
      await get().fetchAll()
      if (agentId === 'wingman') await refreshWingmanSkills().catch(() => undefined)
    } catch (err) {
      set({ error: toMessage(err) })
      throw err
    }
  },

  unsyncSkill: async (linkId) => {
    set({ error: null })
    try {
      const wingmanLinked = get().links.some((link) => link.id === linkId && link.agentId === 'wingman')
      await invoke('library_unsync_skill', { linkId })
      await get().fetchAll()
      if (wingmanLinked) await refreshWingmanSkills().catch(() => undefined)
    } catch (err) {
      set({ error: toMessage(err) })
      throw err
    }
  },

  detectAgents: async () => {
    try {
      const presence = await invoke<AgentPresence[]>('library_detect_agents')
      set({ presence })
    } catch (err) {
      set({ error: toMessage(err) })
    }
  },

  adoptSkill: async (agentId, skillName) => {
    set({ error: null })
    try {
      await invoke<LibrarySkill>('library_adopt_skill', { agentId, skillName })
      await Promise.all([get().fetchAll(), get().detectAgents()])
    } catch (err) {
      set({ error: toMessage(err) })
      throw err
    }
  },

  createPreset: async (name) => {
    await invoke<number>('library_create_preset', { name })
    await get().fetchAll()
  },

  deletePreset: async (presetId) => {
    await invoke('library_delete_preset', { presetId })
    await get().fetchAll()
  },

  setPresetMember: async (presetId, skillId, member) => {
    await invoke('library_set_preset_member', { presetId, skillId, member })
    await get().fetchAll()
  },

  applyPreset: async (presetId, agentId, scope, projectPath) => {
    set({ error: null })
    try {
      await invoke<SkillAgentLink[]>('library_apply_preset', {
        presetId,
        agentId,
        scope,
        projectPath: projectPath ?? null,
      })
      await get().fetchAll()
      if (agentId === 'wingman') await refreshWingmanSkills().catch(() => undefined)
    } catch (err) {
      set({ error: toMessage(err) })
      throw err
    }
  },

  clearError: () => set({ error: null }),
}))
