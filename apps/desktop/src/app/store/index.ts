import { create } from 'zustand'
import type { AgentMode } from '@modern-wingman/contracts'

export type AppTheme = 'default' | 'light' | 'dark' | 'glass'

interface AppState {
  isAgentServiceReady: boolean
  setAgentServiceReady: (ready: boolean) => void
  // Appearance
  theme: AppTheme
  setTheme: (theme: AppTheme) => void
  // General settings
  apiKey: string
  setApiKey: (key: string) => void
  systemPrompt: string
  setSystemPrompt: (prompt: string) => void
  defaultAgentMode: AgentMode
  setDefaultAgentMode: (mode: AgentMode) => void
  // Skills: optional GitHub PAT for higher API rate limits
  githubPat: string
  setGithubPat: (pat: string) => void
}

export const useAppStore = create<AppState>((set) => ({
  isAgentServiceReady: false,
  setAgentServiceReady: (ready) => set({ isAgentServiceReady: ready }),
  theme: 'default',
  setTheme: (theme) => set({ theme }),
  apiKey: '',
  setApiKey: (apiKey) => set({ apiKey }),
  systemPrompt: '',
  setSystemPrompt: (systemPrompt) => set({ systemPrompt }),
  defaultAgentMode: (localStorage.getItem('wingman:default-agent-mode') as AgentMode | null) ?? 'plan',
  setDefaultAgentMode: (defaultAgentMode) => {
    localStorage.setItem('wingman:default-agent-mode', defaultAgentMode)
    set({ defaultAgentMode })
  },
  githubPat: '',
  setGithubPat: (githubPat) => set({ githubPat }),
}))
