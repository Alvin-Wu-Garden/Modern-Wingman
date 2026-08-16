import { create } from 'zustand'
import type { AgentMode } from '@modern-wingman/contracts'

export type AppTheme = 'default' | 'light' | 'dark' | 'glass'

export const ZOOM_MIN = 0.5
export const ZOOM_MAX = 2.0
export const ZOOM_STEP = 0.1
const ZOOM_STORAGE_KEY = 'wingman:zoom-level'

function clampZoom(level: number): number {
  return Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, Math.round(level * 100) / 100))
}

function readStoredZoom(): number {
  const raw = localStorage.getItem(ZOOM_STORAGE_KEY)
  const parsed = raw ? Number.parseFloat(raw) : NaN
  return Number.isFinite(parsed) ? clampZoom(parsed) : 1.0
}

interface AppState {
  isAgentServiceReady: boolean
  setAgentServiceReady: (ready: boolean) => void
  // Appearance
  theme: AppTheme
  setTheme: (theme: AppTheme) => void
  // Whole-window zoom level (browser-like Ctrl +/- and Ctrl+wheel)
  zoomLevel: number
  setZoomLevel: (level: number) => void
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
  zoomLevel: readStoredZoom(),
  setZoomLevel: (level) => {
    const zoomLevel = clampZoom(level)
    localStorage.setItem(ZOOM_STORAGE_KEY, String(zoomLevel))
    set({ zoomLevel })
  },
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
