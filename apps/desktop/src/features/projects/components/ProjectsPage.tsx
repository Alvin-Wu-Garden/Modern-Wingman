import { useEffect, useMemo, useState } from 'react'
import { open } from '@tauri-apps/plugin-dialog'
import {
  ChevronLeft,
  ChevronRight,
  FolderGit2,
  FolderOpen,
  Loader2,
  MessageSquare,
  Network,
  Play,
  Plus,
  Trash2,
  BarChart2,
  Database,
} from 'lucide-react'
import { Button } from '@/components/ui/button'
import { useResizablePanel } from '@/components/layout/use-resizable-panel'
import { Modal } from '@/components/ui/modal'
import { cn } from '@/lib/utils'
import { ConversationPane } from '@/features/chat/components/ConversationPane'
import { useChatStore } from '@/features/chat/store/useChatStore'
import { KnowledgeGraphPage } from './KnowledgeGraphPage'
import { ProjectDatabaseModal } from './ProjectDatabaseModal'
import { ProjectHamburgerMenu } from './ProjectHamburgerMenu'
import { JiraAnalysisModal } from './JiraAnalysisModal'
import { SidebarTooltip } from '@/components/layout/sidebar-navigation'
import { useProjectsStore } from '../store/useProjectsStore'
import { listVcsProfiles, type VcsProfile } from '@/services/agent-api/vcs'

interface ProjectMenu {
  projectId: string
  x: number
  y: number
}

const hasUsableIndex = (manifestVersion: string | null | undefined) => Boolean(manifestVersion)

/**
 * 精簡後的專案解析頁：專案清單、索引狀態、專案對話與知識圖譜。
 * 不再提供知識問答／影響分析／資料情報分頁，所有問題都走共用 ConversationPane。
 */
export function ProjectsPage() {
  const projectSidebar = useResizablePanel({
    storageKey: 'modern-wingman:layout:project-sidebar',
    defaultWidth: 288,
    minWidth: 240,
    maxWidth: 480,
  })
  const {
    projects,
    activeProjectId,
    progress,
    loading,
    error,
    addProject,
    importRemoteProject,
    removeProject,
    setActiveProject,
    indexProject,
    clearError,
  } = useProjectsStore()
  const {
    conversations,
    activeConversationId,
    loadConversations,
    openConversation,
    startNewConversation,
    deleteConv,
  } = useChatStore()

  const [showAddProject, setShowAddProject] = useState(false)
  const [projectName, setProjectName] = useState('')
  const [projectPath, setProjectPath] = useState('')
  const [addMode, setAddMode] = useState<'local' | 'git' | 'svn'>('local')
  const [vcsProfiles, setVcsProfiles] = useState<VcsProfile[]>([])
  const [profileId, setProfileId] = useState('')
  const [repositoryUrl, setRepositoryUrl] = useState('')
  const [repositoryRef, setRepositoryRef] = useState('')
  const [saving, setSaving] = useState(false)
  const [graphProjectId, setGraphProjectId] = useState<string | null>(null)
  const [databaseProjectId, setDatabaseProjectId] = useState<string | null>(null)
  const [jiraProjectId, setJiraProjectId] = useState<string | null>(null)
  const [menu, setMenu] = useState<ProjectMenu | null>(null)
  const [deletingProjectId, setDeletingProjectId] = useState<string | null>(null)

  useEffect(() => {
    if (!showAddProject || addMode === 'local') return
    void listVcsProfiles().then((profiles) => {
      const compatible = profiles.filter((profile) => profile.vcsType === addMode && profile.enabled)
      setVcsProfiles(compatible)
      setProfileId((current) => current || compatible[0]?.id || '')
    })
  }, [addMode, showAddProject])

  useEffect(() => {
    if (!menu) return
    const close = () => setMenu(null)
    window.addEventListener('click', close)
    return () => window.removeEventListener('click', close)
  }, [menu])

  useEffect(() => {
    if (!activeProjectId) return
    // 專案對話使用專案路由載入，避免與一般對話共用同一個 API 查詢。
    void loadConversations(activeProjectId)
  }, [activeProjectId, loadConversations])

  const activeProject = projects.find((project) => project.id === activeProjectId)
  const projectConversations = useMemo(
    () => conversations.filter(
      (conversation) =>
        conversation.projectId === activeProjectId,
    ),
    [activeProjectId, conversations],
  )
  const activeConversation = conversations.find(
    (conversation) =>
      conversation.id === activeConversationId &&
      conversation.projectId === activeProjectId,
  )

  const chooseProjectFolder = async () => {
    const selected = await open({ directory: true, multiple: false })
    if (typeof selected !== 'string') return
    setProjectPath(selected)
    if (!projectName)
      setProjectName(selected.split(/[\\/]/).filter(Boolean).pop() ?? '新專案')
  }

  const saveProject = async () => {
    if (!projectName.trim() || !projectPath.trim() || saving) return
    setSaving(true)
    try {
      if (addMode === 'local') {
        await addProject(projectName.trim(), projectPath.trim())
      } else {
        await importRemoteProject({
          sourceType: addMode,
          name: projectName.trim(),
          profileId,
          repositoryUrl: repositoryUrl.trim(),
          ref: repositoryRef.trim() || null,
          destinationPath: projectPath.trim(),
        })
      }
      setShowAddProject(false)
      setProjectName('')
      setProjectPath('')
      setRepositoryUrl('')
      setRepositoryRef('')
    } catch {
      // Store 已統一保存可顯示的錯誤；Modal 保持開啟，讓使用者修正輸入。
    } finally {
      setSaving(false)
    }
  }

  const newProjectConversation = async () => {
    if (!activeProject) return
    await startNewConversation(activeProject.id)
  }

  const handleJiraConversationCreated = async (
    conversationId: string,
  ) => {
    if (!activeProjectId) return
    await loadConversations(activeProjectId)
    await openConversation(conversationId, activeProjectId)
  } 
  /**
   * Neo4j 圖譜清除可能需要數秒；保留專案列並顯示刪除狀態，
   * 讓使用者知道請求仍在執行，同時避免重複送出刪除。
   */
  const removeSelectedProject = async (projectId: string) => {
    if (deletingProjectId) return
    setDeletingProjectId(projectId)
    setMenu(null)
    try {
      await removeProject(projectId)
    } catch {
      // Store 會在頁面上顯示刪除錯誤；finally 會恢復專案列的操作能力。
    } finally {
      setDeletingProjectId(null)
    }
  }

  return (
    <div className="flex min-h-0 flex-1 bg-surface-alt">
      <aside
        style={projectSidebar.panelStyle}
        className="relative flex shrink-0 flex-col border-r border-border bg-surface transition-[width] duration-200 max-[640px]:!w-16"
      >
        <div className={cn(
          'relative flex h-16 shrink-0 items-center border-b border-border',
          projectSidebar.collapsed ? 'justify-center px-2' : 'justify-between px-4',
        )}
        >
          {projectSidebar.collapsed ? (
            <FolderGit2 className="h-4 w-4 text-brand" aria-label="專案解析" />
          ) : (
          <h1 className="text-sm font-semibold text-ink">專案解析</h1>
          )}
          {!projectSidebar.collapsed && (
            <Button size="icon" variant="ghost" title="新增專案" onClick={() => setShowAddProject(true)}>
              <Plus className="h-4 w-4" />
            </Button>
          )}
          <div
            aria-hidden="true"
            title="雙擊收合或展開專案側邊欄"
            onDoubleClick={projectSidebar.toggleCollapsed}
            className="absolute inset-x-0 bottom-0 h-2 cursor-pointer"
          />
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto overflow-x-hidden p-2">
          {loading && projects.length === 0 && (
            <p className="p-3 text-xs text-ink-subtle">載入專案中…</p>
          )}
          {projectSidebar.collapsed ? (
            <div className="flex flex-col items-center gap-1">
              {projects.map((project) => (
                <SidebarTooltip key={project.id} label={project.name}>
                  <button
                    type="button"
                    disabled={deletingProjectId === project.id}
                    onClick={() => setActiveProject(project.id)}
                    onContextMenu={(event) => {
                      event.preventDefault()
                      setMenu({ projectId: project.id, x: event.clientX, y: event.clientY })
                    }}
                    className={cn(
                      'flex h-9 w-9 items-center justify-center rounded-xl transition-colors',
                      project.id === activeProjectId
                        ? 'bg-brand/10 text-brand'
                        : 'text-ink-secondary hover:bg-surface-alt hover:text-ink',
                      deletingProjectId === project.id && 'cursor-wait opacity-70',
                    )}
                  >
                    {deletingProjectId === project.id
                      ? <Loader2 className="h-4 w-4 animate-spin" />
                      : <FolderGit2 className="h-4 w-4" />}
                  </button>
                </SidebarTooltip>
              ))}

              {activeProject && projectConversations.length > 0 && (
                <>
                  <div className="my-2 h-px w-6 bg-border" />
                  {projectConversations.map((conversation) => (
                    <SidebarTooltip key={conversation.id} label={conversation.title}>
                      <button
                        type="button"
                        onClick={() => void openConversation(conversation.id, activeProjectId)}
                        className={cn(
                          'flex h-9 w-9 items-center justify-center rounded-xl transition-colors',
                          conversation.id === activeConversationId
                            ? 'bg-surface-alt text-ink'
                            : 'text-ink-secondary hover:bg-surface-alt',
                        )}
                      >
                        <MessageSquare className="h-3.5 w-3.5" />
                      </button>
                    </SidebarTooltip>
                  ))}
                </>
              )}
            </div>
          ) : (
          <>
          {projects.map((project) => (
            <div
              key={project.id}
              className="relative mb-1"
            >
              <button
                type="button"
                disabled={deletingProjectId === project.id}
                onClick={() => setActiveProject(project.id)}
                onContextMenu={(event) => {
                  event.preventDefault()
                  setMenu({ projectId: project.id, x: event.clientX, y: event.clientY })
                }}
                className={cn(
                  'w-full rounded-xl px-3 py-2.5 pr-8 text-left transition-colors',
                  project.id === activeProjectId
                    ? 'bg-brand/10 text-brand'
                    : 'text-ink-secondary hover:bg-surface-alt hover:text-ink',
                  deletingProjectId === project.id && 'cursor-wait opacity-70',
                )}
              >
                <p className="truncate text-sm font-medium">{project.name}</p>
                <p className="mt-0.5 truncate text-xs opacity-70">
                  {deletingProjectId === project.id ? (
                    <span className="flex items-center gap-1.5">
                      <Loader2 className="h-3 w-3 animate-spin" />
                      刪除中…
                    </span>
                  ) : project.indexManifestVersion
                    ? `${project.nodeCount} 節點 · 索引可用`
                    : project.indexStatus === 'Indexing'
                      ? '索引中…'
                      : '尚未索引'}
                </p>
              </button>
              {/* 漢堡選單按鈕（絕對定位於右側） */}
              <div className="absolute right-1 top-1/2 -translate-y-1/2">
                <ProjectHamburgerMenu
                  disabled={deletingProjectId === project.id}
                  actions={{
                    onDatabaseSettings: () => setDatabaseProjectId(project.id),
                    onAnalyzeJira: () => setJiraProjectId(project.id),
                    onDeleteProject: () => void removeSelectedProject(project.id),
                  }}
                />
              </div>
            </div>
          ))}

          {activeProject && (
            <>
              <div className="my-3 border-t border-border" />
              <div className="mb-2 flex items-center justify-between px-2">
                <p className="text-xs font-semibold text-ink-subtle">專案對話</p>
                <Button
                  size="icon"
                  variant="ghost"
                  title="新增專案對話"
                  disabled={!hasUsableIndex(activeProject.indexManifestVersion)}
                  onClick={() => void newProjectConversation()}
                >
                  <Plus className="h-3.5 w-3.5" />
                </Button>
              </div>
              {projectConversations.map((conversation) => (
                <button
                  key={conversation.id}
                  type="button"
                  onClick={() => void openConversation(conversation.id, activeProjectId)}
                  className={cn(
                    'mb-1 flex w-full items-center gap-2 rounded-lg px-3 py-2 text-left text-xs',
                    conversation.id === activeConversationId
                      ? 'bg-surface-alt font-medium text-ink'
                      : 'text-ink-secondary hover:bg-surface-alt',
                  )}
                >
                  <MessageSquare className="h-3.5 w-3.5 shrink-0" />
                  <span className="truncate">{conversation.title}</span>
                </button>
              ))}
            </>
          )}
          </>
          )}
        </div>


        <button
          type="button"
          aria-label={projectSidebar.collapsed ? '展開專案側邊欄' : '收合專案側邊欄'}
          aria-expanded={!projectSidebar.collapsed}
          title={projectSidebar.collapsed ? '展開專案側邊欄' : '收合專案側邊欄'}
          onClick={projectSidebar.toggleCollapsed}
          className="absolute right-0 top-12 z-30 flex h-7 w-7 translate-x-1/2 items-center justify-center rounded-full border border-border bg-surface text-ink-secondary shadow-sm transition-colors hover:bg-surface-alt hover:text-ink max-[640px]:hidden"
        >
          {projectSidebar.collapsed
            ? <ChevronRight className="h-3.5 w-3.5" />
            : <ChevronLeft className="h-3.5 w-3.5" />}
        </button>
        {!projectSidebar.collapsed && (
          <div
            {...projectSidebar.resizeHandleProps}
            className="absolute bottom-0 right-0 top-0 z-20 w-1 translate-x-1/2 cursor-col-resize touch-none outline-none hover:bg-brand/40 focus:bg-brand/50 max-[640px]:hidden"
          />
        )}
      </aside>

      <section className="flex min-w-0 flex-1 flex-col">
        {!activeProject ? (
          <div className="flex flex-1 items-center justify-center text-sm text-ink-subtle">
            請先新增或選擇專案。
          </div>
        ) : (
          <>
            <header className="flex items-center justify-between border-b border-border bg-surface px-6 py-3">
              <div className="min-w-0">
                <h2 className="truncate text-base font-semibold text-ink">{activeProject.name}</h2>
                <p className="truncate text-xs text-ink-subtle">{activeProject.rootPath}</p>
              </div>
              <div className="flex items-center gap-2">
                <Button
                  variant="outline"
                  onClick={() => setGraphProjectId(activeProject.id)}
                  disabled={!hasUsableIndex(activeProject.indexManifestVersion)}
                >
                  <Network className="mr-2 h-4 w-4" />
                  查看知識圖譜
                </Button>
                <Button
                  onClick={() => void indexProject(activeProject.id)}
                  disabled={progress?.projectId === activeProject.id}
                >
                  {progress?.projectId === activeProject.id
                    ? <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                    : <Play className="mr-2 h-4 w-4" />}
                  開始索引
                </Button>
              </div>
            </header>

            {progress?.projectId === activeProject.id && (
              <div className="border-b border-border bg-brand/5 px-6 py-2.5">
                <div className="flex items-center justify-between text-xs">
                  <span className="font-medium text-brand">{progress.message}</span>
                  <span className="text-ink-subtle">{progress.percent}%</span>
                </div>
                <div className="mt-2 h-1.5 overflow-hidden rounded-full bg-border">
                  <div
                    className="h-full rounded-full bg-brand transition-all"
                    style={{ width: `${progress.percent}%` }}
                  />
                </div>
              </div>
            )}

            {error && (
              <div className="flex items-center justify-between border-b border-red-300 bg-red-50 px-6 py-2 text-xs text-red-700">
                <span>{error}</span>
                <button type="button" onClick={clearError}>關閉</button>
              </div>
            )}

            {activeConversation ? (
              <ConversationPane
                title={activeConversation.title}
                emptyText="詢問此專案的功能、資料流、bug 或新需求。"
                onNewConversation={newProjectConversation}
                onDeleteConversation={() => deleteConv(activeConversation.id, activeProjectId)}
              />
            ) : (
              <div className="flex flex-1 items-center justify-center">
                <div className="text-center">
                  <MessageSquare className="mx-auto h-10 w-10 text-ink-subtle/40" />
                  <p className="mt-3 text-sm text-ink-secondary">
                    {hasUsableIndex(activeProject.indexManifestVersion)
                      ? '建立一個對話來詢問此專案。'
                      : '完成第一次索引後即可開始詢問。'}
                  </p>
                  <Button
                    className="mt-4"
                    disabled={!hasUsableIndex(activeProject.indexManifestVersion)}
                    onClick={() => void newProjectConversation()}
                  >
                    <Plus className="mr-2 h-4 w-4" />
                    新增專案對話
                  </Button>
                </div>
              </div>
            )}
          </>
        )}
      </section>

      {menu && (
        <div
          className="fixed z-50 min-w-44 rounded-xl border border-border bg-surface p-1.5 shadow-xl"
          style={{ left: menu.x, top: menu.y }}
        >
          <button
            type="button"
            className="flex w-full items-center gap-2 rounded-lg px-3 py-2 text-sm text-ink hover:bg-surface-alt"
            onClick={() => {
              setDatabaseProjectId(menu.projectId)
              setMenu(null)
            }}
          >
            <Database className="h-4 w-4" />
            資料庫連線設定
          </button>
          <div className="my-1 border-t border-border" />
          <button
            type="button"
            className="flex w-full items-center gap-2 rounded-lg px-3 py-2 text-sm text-ink hover:bg-surface-alt"
            onClick={() => {
              setJiraProjectId(menu.projectId)
              setMenu(null)
            }}
          >
            <BarChart2 className="h-4 w-4" />
            分析 JIRA 議題
          </button>
          <button
            type="button"
            disabled={deletingProjectId !== null}
            className="flex w-full items-center gap-2 rounded-lg px-3 py-2 text-sm text-red-600 hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-50"
            onClick={() => void removeSelectedProject(menu.projectId)}
          >
            <Trash2 className="h-4 w-4" />
            刪除專案
          </button>
        </div>
      )}

      {showAddProject && (
        <Modal
          open
          onOpenChange={(openValue) => !openValue && setShowAddProject(false)}
          title="新增專案"
        >
          <div className="space-y-4">
            <div className="grid grid-cols-3 gap-1 rounded-xl bg-surface-alt p-1">
              {([
                ['local', '本機資料夾'],
                ['git', 'Git'],
                ['svn', 'SVN'],
              ] as const).map(([value, label]) => (
                <button
                  key={value}
                  type="button"
                  onClick={() => {
                    setAddMode(value)
                    setProfileId('')
                  }}
                  className={cn(
                    'rounded-lg px-2 py-2 text-xs',
                    addMode === value
                      ? 'bg-surface font-medium text-brand shadow-sm'
                      : 'text-ink-secondary',
                  )}
                >
                  {label}
                </button>
              ))}
            </div>
            <label className="block">
              <span className="text-xs font-medium text-ink-secondary">專案名稱</span>
              <input
                value={projectName}
                onChange={(event) => setProjectName(event.target.value)}
                className="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 text-sm"
              />
            </label>
            {addMode !== 'local' && (
              <>
                <label className="block">
                  <span className="text-xs font-medium text-ink-secondary">連線設定</span>
                  <select
                    value={profileId}
                    onChange={(event) => setProfileId(event.target.value)}
                    className="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 text-sm"
                  >
                    <option value="">請先到設定建立 {addMode.toUpperCase()} 連線</option>
                    {vcsProfiles.map((profile) => (
                      <option key={profile.id} value={profile.id}>{profile.name}</option>
                    ))}
                  </select>
                </label>
                <label className="block">
                  <span className="text-xs font-medium text-ink-secondary">Repository URL</span>
                  <input
                    value={repositoryUrl}
                    onChange={(event) => setRepositoryUrl(event.target.value)}
                    className="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 text-sm"
                  />
                </label>
                {addMode === 'git' && (
                  <label className="block">
                    <span className="text-xs font-medium text-ink-secondary">Branch（留白使用 main）</span>
                    <input
                      value={repositoryRef}
                      onChange={(event) => setRepositoryRef(event.target.value)}
                      className="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 text-sm"
                    />
                  </label>
                )}
              </>
            )}
            <label className="block">
              <span className="text-xs font-medium text-ink-secondary">
                {addMode === 'local' ? '專案路徑' : '下載目的路徑'}
              </span>
              <div className="mt-1 flex gap-2">
                <input
                  value={projectPath}
                  readOnly
                  className="min-w-0 flex-1 rounded-lg border border-border bg-surface-alt px-3 py-2 text-sm"
                />
                <Button variant="outline" onClick={() => void chooseProjectFolder()}>
                  <FolderOpen className="mr-2 h-4 w-4" />
                  選擇
                </Button>
              </div>
            </label>
            <div className="flex justify-end gap-2">
              <Button variant="ghost" onClick={() => setShowAddProject(false)}>取消</Button>
              <Button
                disabled={
                  !projectName.trim() ||
                  !projectPath.trim() ||
                  saving ||
                  (addMode !== 'local' && (!profileId || !repositoryUrl.trim()))
                }
                onClick={() => void saveProject()}
              >
                {saving && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                新增
              </Button>
            </div>
          </div>
        </Modal>
      )}

      {graphProjectId && (
        <KnowledgeGraphPage
          project={projects.find((project) => project.id === graphProjectId)!}
          onClose={() => setGraphProjectId(null)}
        />
      )}

      {databaseProjectId && (
        <ProjectDatabaseModal
          projectId={databaseProjectId}
          projectName={projects.find((project) => project.id === databaseProjectId)?.name ?? '專案'}
          onClose={() => setDatabaseProjectId(null)}
        />
      )}

      {jiraProjectId && (
        <JiraAnalysisModal
          projectId={jiraProjectId}
          projectName={projects.find((project) => project.id === jiraProjectId)?.name ?? '' }
          onClose={() => setJiraProjectId(null)}
          onConversationCreated={(conversationId) => {
            void handleJiraConversationCreated(conversationId)
          }}
        />
      )}
    </div>
  )
}
