import { create } from 'zustand'
import {
  buildSummaries,
  createProject,
  deleteProject,
  getIndexProgress,
  getSummaryProgress,
  importProject,
  listProjects,
  startIndex,
  type IndexProgress,
  type ProjectInfo,
} from '@/services/agent-api/projects'

interface ProjectsState {
  projects: ProjectInfo[]
  activeProjectId: string | null
  progress: IndexProgress | null
  loading: boolean
  error: string | null

  fetchProjects: () => Promise<void>
  addProject: (name: string, rootPath: string) => Promise<ProjectInfo>
  importRemoteProject: (
    request: Parameters<typeof importProject>[0],
    signal?: AbortSignal,
  ) => Promise<ProjectInfo>
  removeProject: (projectId: string) => Promise<void>
  setActiveProject: (projectId: string | null) => void
  indexProject: (projectId: string) => Promise<void>
  clearError: () => void
}

const messageOf = (error: unknown) =>
  error instanceof Error ? error.message : String(error)

export const useProjectsStore = create<ProjectsState>((set, get) => ({
  projects: [],
  activeProjectId: null,
  progress: null,
  loading: false,
  error: null,

  fetchProjects: async () => {
    set({ loading: true })
    try {
      const projects = await listProjects()
      set((state) => ({
        projects,
        loading: false,
        activeProjectId:
          state.activeProjectId ?? projects[0]?.id ?? null,
      }))
    } catch (error) {
      set({ loading: false, error: messageOf(error) })
    }
  },

  addProject: async (name, rootPath) => {
    try {
      const project = await createProject(name, rootPath)
      await get().fetchProjects()
      set({ activeProjectId: project.id, error: null })
      return project
    } catch (error) {
      set({ error: messageOf(error) })
      throw error
    }
  },

  importRemoteProject: async (request, signal) => {
    try {
      const project = await importProject(request, signal)
      await get().fetchProjects()
      set({ activeProjectId: project.id, error: null })
      return project
    } catch (error) {
      set({ error: messageOf(error) })
      throw error
    }
  },

  removeProject: async (projectId) => {
    try {
      await deleteProject(projectId)
      set((state) => ({
        activeProjectId: state.activeProjectId === projectId ? null : state.activeProjectId,
        error: null,
      }))
      await get().fetchProjects()
    } catch (error) {
      set({ error: messageOf(error) })
      throw error
    }
  },

  setActiveProject: (projectId) => set({ activeProjectId: projectId }),

  indexProject: async (projectId) => {
    set({
      error: null,
      progress: {
        projectId,
        phase: 'starting',
        message: '啟動索引…',
        percent: 0,
      },
    })
    try {
      await startIndex(projectId)
      const pollIndex = async (): Promise<void> => {
        try {
          const progress = await getIndexProgress(projectId)
          set({ progress })
          if (progress.phase === 'failed') {
            set({ error: progress.message || '索引失敗。', progress: null })
            return
          }
          // 後端以 complete 表示 canonical graph 已原子發布完成。
          // 此處必須以 phase 作為終止依據；舊的 done 並不存在，會讓 100% 永遠輪詢。
          if (progress.phase !== 'complete') {
            setTimeout(() => void pollIndex(), 1000)
            return
          }

          // 結構圖譜已可問答；業務摘要在背景繼續，不再把專案標成「部分可用」。
          await get().fetchProjects()
          await buildSummaries(projectId)
          const pollSummary = async (): Promise<void> => {
            try {
              const summary = await getSummaryProgress(projectId)
              const terminal = ['Ready', 'Degraded', 'Superseded', 'Canceled']
                .includes(summary.state)
              set({
                progress: terminal
                  ? null
                  : {
                      projectId,
                      phase: 'summaries',
                      message: `索引可用 · 業務摘要生成中 ${summary.completedCommunities}/${summary.totalCommunities}`,
                      percent: summary.totalCommunities > 0
                        ? Math.round(
                            (summary.completedCommunities /
                              summary.totalCommunities) *
                              100,
                          )
                        : 0,
                    },
                error: summary.state === 'Degraded'
                  ? summary.error ?? summary.message ?? '部分業務摘要生成失敗。'
                  : get().error,
              })
              if (!terminal) setTimeout(() => void pollSummary(), 1000)
            } catch (error) {
              set({ error: messageOf(error), progress: null })
            }
          }
          void pollSummary()
        } catch (error) {
          set({ error: messageOf(error), progress: null })
        }
      }
      setTimeout(() => void pollIndex(), 500)
    } catch (error) {
      set({ error: messageOf(error), progress: null })
    }
  },

  clearError: () => set({ error: null }),
}))
