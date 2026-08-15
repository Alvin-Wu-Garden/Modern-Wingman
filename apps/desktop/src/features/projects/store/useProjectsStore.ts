import { create } from 'zustand'
import {
  createProject,
  deleteProject,
  getIndexProgress,
  getSummaryProgress,
  importProject,
  listProjects,
  startIndex,
  type IndexProgress,
  type AiEnrichmentProgress,
  type ProjectInfo,
} from '@/services/agent-api/projects'

interface ProjectsState {
  projects: ProjectInfo[]
  activeProjectId: string | null
  progress: IndexProgress | null
  summaryProgress: AiEnrichmentProgress | null
  loading: boolean
  error: string | null

  fetchProjects: () => Promise<void>
  addProject: (
    name: string,
    rootPath: string,
    selectedSolutionPath?: string | null,
  ) => Promise<ProjectInfo>
  importRemoteProject: (
    request: Parameters<typeof importProject>[0],
    signal?: AbortSignal,
  ) => Promise<ProjectInfo>
  removeProject: (projectId: string) => Promise<void>
  setActiveProject: (projectId: string | null) => void
  indexProject: (projectId: string) => Promise<void>
  startSummaryPolling: (projectId: string) => void
  stopSummaryPolling: () => void
  clearError: () => void
}

const messageOf = (error: unknown) =>
  error instanceof Error ? error.message : String(error)

let summaryPollTimer: ReturnType<typeof setTimeout> | null = null
let summaryPollEpoch = 0

export const useProjectsStore = create<ProjectsState>((set, get) => ({
  projects: [],
  activeProjectId: null,
  progress: null,
  summaryProgress: null,
  loading: false,
  error: null,

  fetchProjects: async () => {
    // 新一輪載入開始時清除上一輪的錯誤，避免成功後仍在專案頁顯示
    // 已過期的「Failed to fetch」或舊的後端錯誤。
    set({ loading: true, error: null })
    try {
      const projects = await listProjects()
      set((state) => ({
        projects,
        loading: false,
        error: null,
        activeProjectId:
          state.activeProjectId ?? projects[0]?.id ?? null,
      }))
    } catch (error) {
      set({ loading: false, error: messageOf(error) })
    }
  },

  addProject: async (name, rootPath, selectedSolutionPath) => {
    try {
      const project = await createProject(name, rootPath, selectedSolutionPath)
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
      summaryProgress: null,
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
          if (progress.phase === 'failed' || progress.phase === 'canceled') {
            set({ error: progress.message || '索引失敗。', progress: null })
            return
          }
          // 後端以 complete 表示 canonical graph 已原子發布完成。
          // 此處必須以 phase 作為終止依據；舊的 done 並不存在，會讓 100% 永遠輪詢。
          if (progress.phase !== 'complete') {
            setTimeout(() => void pollIndex(), 1000)
            return
          }

          // 結構圖譜已可立即問答；V4 後端會自動預熱 C0 摘要，
          // C1/C2 則在第一次命中時排程，因此前端只需讀取獨立進度。
          await get().fetchProjects()
          set({ progress: null })
          get().startSummaryPolling(projectId)
        } catch (error) {
          set({ error: messageOf(error), progress: null })
        }
      }
      setTimeout(() => void pollIndex(), 500)
    } catch (error) {
      set({ error: messageOf(error), progress: null })
    }
  },

  /**
   * 持續觀察目前專案的 AI 摘要進度。
   *
   * queued/running 清空且 completed/failed 已涵蓋 total 時立即停止 timer，避免應用程式閒置時
   * 仍持續呼叫 AgentService。切換專案、索引完成或一輪問答完成時會由呼叫端重新啟動檢查，
   * 因此 C1/C2 晚到工作仍能被發現。
   */
  startSummaryPolling: (projectId) => {
    summaryPollEpoch += 1
    const epoch = summaryPollEpoch
    if (summaryPollTimer) clearTimeout(summaryPollTimer)

    const poll = async (): Promise<void> => {
      if (
        epoch !== summaryPollEpoch ||
        get().activeProjectId !== projectId
      ) return
      let delay = 5000
      try {
        const summary = await getSummaryProgress(projectId)
        const working = summary.queued > 0 || summary.running > 0
        const terminal =
          !working &&
          summary.completed + summary.failed >= summary.total
        set({
          summaryProgress: summary,
          error: summary.failed > 0
            ? summary.message ?? `${summary.failed} 個 AI 摘要失敗，結構索引仍可使用。`
            : get().error,
        })
        if (terminal) {
          summaryPollTimer = null
          return
        }
        delay = working ? 1000 : 5000
      } catch {
        // AI 摘要為非阻塞能力。短暫 API 失敗不清除最後已知進度，也不撤銷結構索引。
      }
      if (epoch === summaryPollEpoch)
        summaryPollTimer = setTimeout(() => void poll(), delay)
    }

    void poll()
  },

  /** 停止全域摘要輪詢，供應用程式卸載或目前沒有專案時清理 timer。 */
  stopSummaryPolling: () => {
    summaryPollEpoch += 1
    if (summaryPollTimer) clearTimeout(summaryPollTimer)
    summaryPollTimer = null
  },

  clearError: () => set({ error: null }),
}))
