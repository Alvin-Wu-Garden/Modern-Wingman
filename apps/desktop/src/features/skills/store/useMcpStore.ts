import { invoke } from '@tauri-apps/api/core'
import { create } from 'zustand'
import type { McpServer, UpsertMcpServerParams } from '@modern-wingman/contracts'

/**
 * MCP Registry store (WS1.5).
 * Responsibility: MCP server CRUD + agent config sync links.
 */
interface McpState {
  servers: McpServer[]
  loading: boolean
  error: string | null

  fetchServers: () => Promise<void>
  upsertServer: (params: UpsertMcpServerParams) => Promise<void>
  deleteServer: (serverId: number) => Promise<void>
  setAgentLink: (serverId: number, agentId: string, linked: boolean) => Promise<void>
  clearError: () => void
}

const toMessage = (err: unknown) => (err instanceof Error ? err.message : String(err))

export const useMcpStore = create<McpState>((set, get) => ({
  servers: [],
  loading: false,
  error: null,

  fetchServers: async () => {
    set({ loading: true, error: null })
    try {
      const servers = await invoke<McpServer[]>('mcp_list_servers')
      set({ servers, loading: false })
    } catch (err) {
      set({ loading: false, error: toMessage(err) })
    }
  },

  upsertServer: async (params) => {
    set({ error: null })
    try {
      await invoke<McpServer>('mcp_upsert_server', { params })
      await get().fetchServers()
    } catch (err) {
      set({ error: toMessage(err) })
      throw err
    }
  },

  deleteServer: async (serverId) => {
    set({ error: null })
    try {
      await invoke('mcp_delete_server', { serverId })
      await get().fetchServers()
    } catch (err) {
      set({ error: toMessage(err) })
      throw err
    }
  },

  setAgentLink: async (serverId, agentId, linked) => {
    set({ error: null })
    try {
      await invoke('mcp_set_agent_link', { serverId, agentId, linked })
      await get().fetchServers()
    } catch (err) {
      set({ error: toMessage(err) })
      throw err
    }
  },

  clearError: () => set({ error: null }),
}))
