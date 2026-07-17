import { create } from 'zustand'
import {
  analyzeImpact,
  buildSummaries,
  getSummaryProgress,
  createProject,
  deleteProject,
  generateAgentsMd,
  getIndexProgress,
  importProject,
  listProjects,
  queryProject,
  startIndex,
  type ImpactResult,
  type IndexProgress,
  type ProjectInfo,
  type ProjectQueryResult,
  type ProjectChangeTarget,
  type ProjectClarificationAnswer,
} from '@/services/agent-api/projects'
import type { AgentMode } from '@modern-wingman/contracts'

export interface QAEntry extends ProjectQueryResult {
  question: string
}

interface ProjectsState {
  projects: ProjectInfo[]
  activeProjectId: string | null
  progress: IndexProgress | null
  qaHistory: Record<string, QAEntry[]>
  impactResult: ImpactResult | null
  loading: boolean
  querying: boolean
  activeRunId:string|null
  error: string | null

  fetchProjects: () => Promise<void>
  addProject: (name: string, rootPath: string) => Promise<ProjectInfo>
  importRemoteProject: (request: Parameters<typeof importProject>[0], signal?: AbortSignal) => Promise<ProjectInfo>
  removeProject: (projectId: string) => Promise<void>
  setActiveProject: (projectId: string | null) => void
  indexProject: (projectId: string) => Promise<void>
  pollProgress: (projectId: string) => Promise<IndexProgress>
  ask: (
    projectId: string,
    question: string,
    providerProfileId?: string | null,
    modelId?: string | null,
    agentMode?:AgentMode,
    analysis?: {
      targets?: ProjectChangeTarget[]
      analysisSessionId?: string | null
      clarificationAnswers?: ProjectClarificationAnswer[]
      displayQuestion?: string
    },
  ) => Promise<void>
  runImpact: (projectId: string, symbol: string) => Promise<void>
  makeAgentsMd: (projectId: string) => Promise<string>
  clearError: () => void
}

const toMessage = (err: unknown) => (err instanceof Error ? err.message : String(err))

export const useProjectsStore = create<ProjectsState>((set, get) => ({
  projects: [],
  activeProjectId: null,
  progress: null,
  qaHistory: {},
  impactResult: null,
  loading: false,
  querying: false,
  activeRunId:null,
  error: null,

  fetchProjects: async () => {
    set({ loading: true })
    try {
      const projects = await listProjects()
      set({ projects, loading: false })
    } catch (err) {
      set({ loading: false, error: toMessage(err) })
    }
  },

  addProject: async (name, rootPath) => {
    const project = await createProject(name, rootPath)
    await get().fetchProjects()
    return project
  },

  importRemoteProject: async (request, signal) => {
    const project = await importProject(request, signal)
    await get().fetchProjects()
    return project
  },

  removeProject: async (projectId) => {
    await deleteProject(projectId)
    if (get().activeProjectId === projectId) {
      set({ activeProjectId: null })
    }
    await get().fetchProjects()
  },

  setActiveProject: (projectId) => set({ activeProjectId: projectId, impactResult: null }),

  indexProject: async (projectId) => {
    set({ error: null, progress: { projectId, phase: 'starting', message: '啟動索引...', percent: 0 } })
    try {
      await startIndex(projectId)
      // 輪詢直到完成（FTUE 進度視覺化）
      const poll = async () => {
        try {
          const p = await getIndexProgress(projectId)
          set({ progress: p })
          if (p.percent < 100 && p.phase !== 'failed') {
            setTimeout(poll, 1200)
            return
          }

          await get().fetchProjects()
          if (p.phase === 'failed') {
            set({ error: p.message || '索引失敗。', progress: null })
            return
          }

          // 索引完成後自動建立社群摘要（全自動 GraphRAG，使用者決策）
          if (p.phase === 'done') {
            set({ progress: { projectId, phase: 'summaries', message: '生成模組摘要（GraphRAG）...', percent: 100 } })
            try {
              await buildSummaries(projectId)
              const pollEnrichment = async (): Promise<void> => {
                const enrichment = await getSummaryProgress(projectId)
                const terminal = ['Ready', 'Degraded', 'Superseded', 'Canceled'].includes(enrichment.state)
                const percent = enrichment.totalCommunities > 0
                  ? Math.round((enrichment.completedCommunities / enrichment.totalCommunities) * 100)
                  : terminal ? 100 : 0
                set({
                  progress: {
                    projectId,
                    phase: `enrichment-${enrichment.state.toLowerCase()}`,
                    message: enrichment.message ?? `AI Enrichment：${enrichment.state}`,
                    percent,
                  },
                })
                if (!terminal) {
                  setTimeout(() => { void pollEnrichment() }, 1200)
                  return
                }
                if (enrichment.state === 'Degraded')
                  set({ error: enrichment.error ?? enrichment.message ?? 'AI Enrichment 部分失敗。' })
                set({ progress: null })
              }
              void pollEnrichment()
              return
            } catch {
              // 摘要失敗不阻擋主流程
            }
          }
          set({ progress: null })
        } catch (err) {
          set({ error: toMessage(err), progress: null })
        }
      }
      setTimeout(poll, 800)
    } catch (err) {
      set({ error: toMessage(err), progress: null })
    }
  },

  pollProgress: (projectId) => getIndexProgress(projectId),

  ask: async (projectId, question, providerProfileId, modelId,agentMode='plan',analysis) => {
    set({ querying: true, error: null })
    try {
      const result = await queryProject(projectId, question, 'auto', providerProfileId, modelId,agentMode,analysis)
      set((s) => ({
        querying: false,
        activeRunId:result.runId,
        qaHistory: {
          ...s.qaHistory,
          [projectId]: [...(s.qaHistory[projectId] ?? []), { question: analysis?.displayQuestion ?? question, ...result }],
        },
      }))
    } catch (err) {
      set({ querying: false, error: toMessage(err) })
    }
  },

  runImpact: async (projectId, symbol) => {
    set({ querying: true, error: null, impactResult: null })
    try {
      const result = await analyzeImpact(projectId, symbol)
      set({ querying: false, impactResult: result })
    } catch (err) {
      set({ querying: false, error: toMessage(err) })
    }
  },

  makeAgentsMd: async (projectId) => {
    const content = await generateAgentsMd(projectId)
    return content
  },

  clearError: () => set({ error: null }),
}))
