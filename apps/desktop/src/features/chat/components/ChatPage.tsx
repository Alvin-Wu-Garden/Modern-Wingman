import React, { useState, useRef, useEffect, useCallback, useMemo } from 'react'
import { AppShell } from '@/app/layout/AppShell'
import { SidebarNavigation } from '@/components/layout/sidebar-navigation'
import { Button } from '@/components/ui/button'
import { DashboardCard } from '@/components/ui/dashboard-card'
import { SettingsPage } from '@/features/settings/components/SettingsPage'
import { ProjectsPage } from '@/features/projects/components/ProjectsPage'
import { useChatStore } from '@/features/chat/store/useChatStore'
import { useAppStore } from '@/app/store'
import { ChatInput } from './ChatInput'
import {
  MessageSquare,
  Plus,
  Settings,
  Bot,
  User,
  Zap,
  Clock,
  MessageCircle,
  TrendingUp,
  Blocks,
  FolderGit2,
  Pencil,
  Trash2,
  ShieldAlert,
} from 'lucide-react'
import { cn } from '@/lib/utils'
import { MarkdownRenderer } from '@/components/ui/markdown-renderer'
import type { AgentMode } from '@modern-wingman/contracts'
import { RunTimeline } from './RunTimeline'
import { AgentWorkbenchPanel } from './AgentWorkbenchPanel'
import { approveWorkflowPlan, type AttachmentReference } from '@/services/agent-api/client'
import { listProjects, type ProjectInfo } from '@/services/agent-api/projects'

const MarketplacePage = React.lazy(async () => ({ default: (await import('@/features/marketplace/MarketplacePage')).MarketplacePage }))

/* ── Types ── */
type ActiveView = 'home' | 'chat' | 'settings' | 'skills' | 'projects'

/* ── Context Menu State ── */
interface CtxMenu {
  x: number
  y: number
  convId: string
}

interface RenameDialog {
  convId: string
  currentTitle: string
}

/* ── Component ── */
export function ChatPage() {
  const [activeView, setActiveView] = useState<ActiveView>('home')
  const [selectedProviderId, setSelectedProviderId] = useState<string | null>(null)
  const [selectedModel, setSelectedModel] = useState<string | null>(null)
  const defaultAgentMode = useAppStore((s) => s.defaultAgentMode)
  const [agentMode, setAgentMode] = useState<AgentMode>(defaultAgentMode)
  const [ctxMenu, setCtxMenu] = useState<CtxMenu | null>(null)
  const [renameDialog, setRenameDialog] = useState<RenameDialog | null>(null)
  const [renameDraft, setRenameDraft] = useState('')
  const [workspaceProjects,setWorkspaceProjects]=useState<ProjectInfo[]>([])
  const [selectedProjectId,setSelectedProjectId]=useState<string|null>(null)
  const [includeUncommittedChanges,setIncludeUncommittedChanges]=useState(true)
  const messagesEndRef = useRef<HTMLDivElement>(null)
  const renameInputRef = useRef<HTMLInputElement>(null)

  /* ── Chat store ── */
  const {
    conversations,
    isLoadingList,
    activeConversationId,
    messages,
    isStreaming,
    activeRunId,
    loadConversations,
    openConversation,
    startNewConversation,
    deleteConv,
    renameConv,
    send,
    cancelStreaming,
    pendingApprovals,
    timeline,
    decideApproval,
    changeSet,
    restoreChanges,
    acceptChangeFiles,
    restoreChangeFiles,
    updateChangeHunks,
    lastError,
    retryLast,
    retryFromSafeStep,
    clearLastError,
  } = useChatStore()

  useEffect(()=>{void listProjects().then(setWorkspaceProjects).catch(()=>setWorkspaceProjects([]))},[])

  // useAppStore is kept for potential future use; globalApiKey removed — now using provider picker
  const { } = useAppStore()

  /* ── Load conversations on mount ── */
  useEffect(() => {
    loadConversations()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // 刪除最後一個對話後，避免保留沒有內容可顯示的對話頁。
  useEffect(() => {
    if (activeView === 'chat' && !activeConversationId && !isLoadingList) {
      setActiveView('home')
    }
  }, [activeConversationId, activeView, isLoadingList])

  /* ── Close context menu on click-outside or Escape ── */
  useEffect(() => {
    if (!ctxMenu) return
    const close = () => setCtxMenu(null)
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') close() }
    document.addEventListener('click', close)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('click', close)
      document.removeEventListener('keydown', onKey)
    }
  }, [ctxMenu])

  /* ── Focus rename input when dialog opens ── */
  useEffect(() => {
    if (renameDialog) {
      setRenameDraft(renameDialog.currentTitle)
      setTimeout(() => renameInputRef.current?.select(), 30)
    }
  }, [renameDialog])

  const handleRenameSubmit = useCallback(async () => {
    if (!renameDialog) return
    const trimmed = renameDraft.trim()
    if (trimmed && trimmed !== renameDialog.currentTitle) {
      await renameConv(renameDialog.convId, trimmed)
    }
    setRenameDialog(null)
  }, [renameDialog, renameDraft, renameConv])

  /* ── Auto-scroll ── */
  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages, isStreaming])

  /* ── Handlers ── */
  const handleSend = useCallback(async (text: string, attachments:AttachmentReference[]) => {
    if (isStreaming) return

    let convId = activeConversationId
    if (!convId) {
      convId = await startNewConversation()
    }

    setActiveView('chat')
    await send(text, selectedProviderId, selectedModel, agentMode, attachments,selectedProjectId,includeUncommittedChanges)
  }, [isStreaming, activeConversationId, startNewConversation, send, selectedProviderId, selectedModel, agentMode,selectedProjectId,includeUncommittedChanges])

  const handleNewChat = useCallback(async () => {
    // 每次建立新對話都重新依設定頁排序選擇供應商，不能沿用上一個對話的選擇。
    setSelectedProviderId(null)
    setSelectedModel(null)
    await startNewConversation()
    setActiveView('chat')
  }, [startNewConversation])

  const handleOpenChat = useCallback(async (id: string) => {
    await openConversation(id)
    setActiveView('chat')
  }, [openConversation])

  const handleDeleteConv = useCallback(async (e: React.MouseEvent, id: string) => {
    e.stopPropagation()
    await deleteConv(id)
  }, [deleteConv])

  /* ── Sidebar ── */
  const sidebarSections = useMemo(() => [
    {
      items: [
        {
          id: 'home',
          label: '總覽',
          icon: <Zap className="w-4 h-4" />,
          onClick: () => setActiveView('home'),
        },
        {
          id: 'new-chat',
          label: '新對話',
          icon: <Plus className="w-4 h-4" />,
          onClick: handleNewChat,
        },
      ],
    },
    {
      title: '近期對話',
      items: isLoadingList
        ? [{ id: 'loading', label: '載入中…', icon: <MessageSquare className="w-4 h-4" /> }]
        : conversations.map((conv) => ({
            id: conv.id,
            label: conv.title,
            icon: <MessageSquare className="w-4 h-4" />,
            onClick: () => handleOpenChat(conv.id),
            onContextMenu: (e: React.MouseEvent) => {
              e.preventDefault()
              e.stopPropagation()
              setCtxMenu({ x: e.clientX, y: e.clientY, convId: conv.id })
            },
          })),
    },
    {
      title: '擴充',
      items: [
        {
          id: 'projects',
          label: '專案解析',
          icon: <FolderGit2 className="w-4 h-4" />,
          onClick: () => setActiveView('projects'),
        },
        {
          id: 'skills',
          label: 'Marketplace',
          icon: <Blocks className="w-4 h-4" />,
          onClick: () => setActiveView('skills'),
        },
      ],
    },
  ], [isLoadingList, conversations, handleNewChat, handleOpenChat])

  const activeItemId =
    activeView === 'chat' && activeConversationId
      ? activeConversationId
      : activeView

  /* ── Sidebar header / footer ── */
  const sidebarHeader = (
    <div className="flex items-center gap-2.5">
      <div className="w-8 h-8 rounded-xl bg-brand flex items-center justify-center shrink-0">
        <Bot className="w-4 h-4 text-white" />
      </div>
      <div className="min-w-0 max-[640px]:hidden">
        <p className="text-sm font-semibold text-ink truncate">Modern Wingman</p>
        <p className="text-xs text-brand-green font-medium">● Online</p>
      </div>
    </div>
  )

  const sidebarFooter = (
    <button
      type="button"
      title="設定"
      onClick={() => setActiveView('settings')}
      className={cn(
        'w-full flex items-center gap-3 px-3 py-2.5 rounded-xl text-sm transition-colors duration-150',
        activeView === 'settings'
          ? 'bg-brand/10 text-brand font-medium'
          : 'text-ink-secondary hover:bg-surface-alt hover:text-ink',
      )}
    >
      <Settings className="w-4 h-4 shrink-0" />
      <span className="max-[640px]:hidden">設定</span>
    </button>
  )

  /* ── Home view ── */
  const homeView = (
    <div className="flex-1 overflow-y-auto p-8 max-[640px]:p-4">
      <div className="max-w-3xl mx-auto">
        <div className="mb-8">
          <h1 className="text-2xl font-bold text-ink">今日概覽</h1>
          <p className="mt-1 text-sm text-ink-secondary">
            歡迎回來！共有 {conversations.length} 個對話紀錄
          </p>
        </div>

        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3 mb-8">
          <DashboardCard
            title="歷史對話"
            value={conversations.length}
            description="儲存於本機 SQLite"
            icon={<MessageCircle className="w-4 h-4" />}
            trend={{ value: 0, label: '持久化' }}
          />
          <DashboardCard
            title="最後活躍"
            value={
              conversations[0]
                ? new Date(conversations[0].updatedAt).toLocaleDateString('zh-TW')
                : '—'
            }
            description={conversations[0]?.title ?? '尚無對話'}
            icon={<Clock className="w-4 h-4" />}
            trend={{ value: 0, label: '' }}
          />
          <DashboardCard
            title="AI 服務"
            value={selectedProviderId ? '已選擇' : '未選擇'}
            description={selectedProviderId ?? '新對話時選擇供應商'}
            icon={<TrendingUp className="w-4 h-4" />}
            trend={{ value: 0, label: '' }}
          />
        </div>

        <div>
          <h2 className="text-base font-semibold text-ink mb-4">快速操作</h2>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <button
              type="button"
              onClick={handleNewChat}
              className="group flex items-center gap-4 p-4 rounded-2xl bg-surface border border-border hover:border-brand/40 hover:shadow-md transition-all duration-200 text-left"
            >
              <div className="w-10 h-10 rounded-xl bg-brand/10 flex items-center justify-center text-brand group-hover:bg-brand group-hover:text-white transition-colors duration-200">
                <Plus className="w-5 h-5" />
              </div>
              <div>
                <p className="text-sm font-medium text-ink">新對話</p>
                <p className="text-xs text-ink-subtle mt-0.5">開始新的 AI 對話</p>
              </div>
            </button>

            <button
              type="button"
              onClick={() => setActiveView('settings')}
              className="group flex items-center gap-4 p-4 rounded-2xl bg-surface border border-border hover:border-brand/40 hover:shadow-md transition-all duration-200 text-left"
            >
              <div className="w-10 h-10 rounded-xl bg-brand/10 flex items-center justify-center text-brand group-hover:bg-brand group-hover:text-white transition-colors duration-200">
                <Settings className="w-5 h-5" />
              </div>
              <div>
                <p className="text-sm font-medium text-ink">API 金鑰設定</p>
                <p className="text-xs text-ink-subtle mt-0.5">配置 AI 供應商</p>
              </div>
            </button>
          </div>
        </div>
      </div>
    </div>
  )

  /* ── Chat view ── */
  const chatView = (
    <>
      <div className="flex items-center justify-between px-6 py-3.5 bg-surface border-b border-border shrink-0">
        <div className="flex items-center gap-3">
          <div className="w-8 h-8 rounded-xl bg-brand/10 flex items-center justify-center">
            <Bot className="w-4 h-4 text-brand" />
          </div>
          <div>
            <p className="text-sm font-semibold text-ink">
              {conversations.find((c) => c.id === activeConversationId)?.title ?? 'Wingman AI'}
            </p>
            <p className="text-xs text-brand-green font-medium">
              {isStreaming ? '● 回應中…' : '● 就緒'}
            </p>
          </div>
        </div>
        <div className="flex items-center gap-1">
          <Button variant="ghost" size="icon" onClick={handleNewChat}>
            <Plus className="w-4 h-4" />
          </Button>
          {activeConversationId && (
            <Button
              variant="ghost"
              size="icon"
              onClick={(e) => handleDeleteConv(e, activeConversationId)}
            >
              <Trash2 className="w-4 h-4 text-red-400" />
            </Button>
          )}
          <Button variant="ghost" size="icon" onClick={() => setActiveView('settings')}>
            <Settings className="w-4 h-4" />
          </Button>
        </div>
      </div>

      <div className="flex min-h-0 flex-1">
      <div className="flex min-w-0 flex-1 flex-col">
      <div className="flex-1 overflow-y-auto px-6 py-5 space-y-4">
        {messages.length === 0 && (
          <div className="flex flex-col items-center justify-center h-full text-center text-ink-subtle gap-3">
            <Bot className="w-10 h-10 opacity-30" />
            <p className="text-sm">在下方輸入訊息開始對話</p>
          </div>
        )}

        {messages.map((msg) => (
          <div
            key={msg.id}
            className={cn(
              'flex gap-3',
              msg.role === 'user' ? 'flex-row-reverse' : 'flex-row',
            )}
          >
            <div
              className={cn(
                'shrink-0 w-8 h-8 rounded-xl flex items-center justify-center',
                msg.role === 'assistant' ? 'bg-brand/10 text-brand' : 'bg-ink/10 text-ink',
              )}
            >
              {msg.role === 'assistant' ? (
                <Bot className="w-4 h-4" />
              ) : (
                <User className="w-4 h-4" />
              )}
            </div>

            <div
              className={cn(
                'max-w-[75%] rounded-2xl px-4 py-3 text-sm leading-relaxed',
                msg.role === 'assistant' ? 'bg-surface text-ink shadow-sm' : 'bg-brand text-white',
              )}
            >
              {msg.role === 'assistant' ? (
                <MarkdownRenderer content={msg.content} streaming={msg.streaming} />
              ) : (
                <p className="whitespace-pre-wrap">
                  {msg.content}
                </p>
              )}
              <p
                className={cn(
                  'mt-1.5 text-xs',
                  msg.role === 'assistant' ? 'text-ink-subtle' : 'text-white/60',
                )}
              >
                {new Date(msg.createdAt).toLocaleTimeString('zh-TW', {
                  hour: '2-digit',
                  minute: '2-digit',
                })}
              </p>
            </div>
          </div>
        ))}

        <div ref={messagesEndRef} />
      </div>

      <RunTimeline events={timeline} onApprovePlan={activeRunId?async()=>{await approveWorkflowPlan(activeRunId)}:undefined} />
      <div className="flex flex-wrap items-center gap-2 border-t border-border bg-surface px-6 pt-3 text-xs"><FolderGit2 className="h-4 w-4 text-ink-subtle"/><label className="text-ink-secondary">工作區</label><select value={selectedProjectId??''} onChange={event=>setSelectedProjectId(event.target.value||null)} disabled={isStreaming} className="min-w-0 flex-1 border border-border bg-surface-alt px-2 py-1.5 text-xs"><option value="">不使用專案工作區</option>{workspaceProjects.map(project=><option key={project.id} value={project.id}>{project.name} · {project.rootPath}</option>)}</select>{workspaceProjects.find(project=>project.id===selectedProjectId)?.dirty&&<label className="flex items-center gap-1.5 text-ink-secondary"><input type="checkbox" checked={includeUncommittedChanges} onChange={event=>setIncludeUncommittedChanges(event.target.checked)} disabled={isStreaming}/>帶入目前未提交變更</label>}</div>
      <ChatInput
        selectedProviderId={selectedProviderId}
        selectedModel={selectedModel}
        onProviderChange={setSelectedProviderId}
        onModelChange={setSelectedModel}
        isStreaming={isStreaming}
        onSend={handleSend}
        onCancel={cancelStreaming}
        agentMode={agentMode}
        onAgentModeChange={setAgentMode}
        workspacePath={workspaceProjects.find(project=>project.id===selectedProjectId)?.rootPath}
      />
      {lastError&&<div className="mx-6 mb-3 border border-red-400/40 bg-surface p-3"><div className="flex items-start gap-2"><ShieldAlert className="mt-0.5 h-4 w-4 shrink-0 text-red-500"/><div className="min-w-0 flex-1"><p className="text-sm font-medium text-ink">模型回應失敗</p><p className="mt-1 text-xs text-ink-secondary">{lastError}</p><div className="mt-2 flex flex-wrap gap-2"><Button size="sm" variant="outline" onClick={()=>void retryLast()}>重試</Button><Button size="sm" variant="outline" onClick={()=>void retryLast(selectedProviderId,selectedModel)}>換模型重試</Button><Button size="sm" variant="outline" disabled={!activeRunId} onClick={()=>void retryFromSafeStep(selectedProviderId)}>從安全步驟繼續</Button><Button size="sm" variant="ghost" onClick={()=>setActiveView('settings')}>查看稽核</Button><Button size="sm" variant="ghost" onClick={clearLastError}>關閉</Button></div></div></div></div>}
      </div>
      <AgentWorkbenchPanel
        runId={activeRunId}
        mode={agentMode}
        providerId={selectedProviderId}
        modelId={selectedModel}
        changeSet={changeSet}
        timeline={timeline}
        approvals={pendingApprovals}
        onRestore={restoreChanges}
        onAcceptFiles={acceptChangeFiles}
        onRestoreFiles={restoreChangeFiles}
        onUpdateHunks={updateChangeHunks}
      />
      </div>
      {pendingApprovals.length > 0 && (
        <div className="fixed bottom-32 left-1/2 z-40 w-[min(560px,calc(100vw-2rem))] -translate-x-1/2 space-y-2">
          {pendingApprovals.map((approval) => (
            <div key={approval.id} className="rounded-lg border border-amber-400/40 bg-surface p-4 shadow-xl">
              <div className="flex items-start gap-3">
                <ShieldAlert className="mt-0.5 h-5 w-5 shrink-0 text-amber-500" />
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-semibold text-ink">需要操作核准</p>
                  <p className="mt-1 text-sm text-ink-secondary">{approval.summary ?? approval.operation}</p>
                  {approval.target && (
                    <code className="mt-2 block max-h-20 overflow-auto rounded-md bg-surface-alt px-2.5 py-2 text-xs text-ink-secondary">
                      {approval.target}
                    </code>
                  )}
                  <div className="mt-3 flex justify-end gap-2">
                    <Button variant="ghost" size="sm" onClick={() => void decideApproval(approval.id, false)}>
                      拒絕
                    </Button>
                    <Button size="sm" onClick={() => void decideApproval(approval.id, true)}>
                      允許一次
                    </Button>
                  </div>
                </div>
              </div>
            </div>
          ))}
        </div>
      )}
    </>
  )

  /* ── Render ── */
  return (
    <>
    <AppShell
      sidebar={
        <SidebarNavigation
          sections={sidebarSections}
          activeItemId={activeItemId}
          header={sidebarHeader}
          footer={sidebarFooter}
        />
      }
    >
      <div className="flex flex-col h-full">
        {activeView === 'home' && homeView}
        {activeView === 'chat' && chatView}
        {activeView === 'settings' && <SettingsPage />}
        {activeView === 'skills' && <React.Suspense fallback={<div className="p-6 text-sm text-ink-secondary">載入 Marketplace…</div>}><MarketplacePage /></React.Suspense>}
        {activeView === 'projects' && <ProjectsPage />}
      </div>
    </AppShell>

    {/* ── Conversation right-click context menu ── */}
    {ctxMenu && (
      <div
        className="fixed z-50 min-w-[152px] rounded-xl border border-border bg-surface shadow-lg py-1 select-none"
        style={{ top: ctxMenu.y, left: ctxMenu.x }}
        onClick={(e) => e.stopPropagation()}
      >
        <button
          type="button"
          className="w-full flex items-center gap-2.5 px-3.5 py-2 text-sm text-ink hover:bg-surface-alt transition-colors text-left"
          onClick={() => {
            const conv = conversations.find((c) => c.id === ctxMenu.convId)
            setCtxMenu(null)
            if (conv) setRenameDialog({ convId: conv.id, currentTitle: conv.title })
          }}
        >
          <Pencil className="w-3.5 h-3.5 shrink-0 text-ink-subtle" />
          重新命名
        </button>
        <div className="mx-3 my-0.5 h-px bg-border" />
        <button
          type="button"
          className="w-full flex items-center gap-2.5 px-3.5 py-2 text-sm text-red-500 hover:bg-red-50 transition-colors text-left"
          onClick={async () => {
            setCtxMenu(null)
            await deleteConv(ctxMenu.convId)
          }}
        >
          <Trash2 className="w-3.5 h-3.5 shrink-0" />
          刪除對話
        </button>
      </div>
    )}

    {/* ── Rename dialog ── */}
    {renameDialog && (
      <div
        className="fixed inset-0 z-50 flex items-center justify-center bg-black/30 backdrop-blur-[2px]"
        onClick={() => setRenameDialog(null)}
      >
        <div
          className="w-80 rounded-2xl border border-border bg-surface shadow-2xl p-5 space-y-3"
          onClick={(e) => e.stopPropagation()}
        >
          <p className="text-sm font-semibold text-ink">重新命名對話</p>
          <input
            ref={renameInputRef}
            type="text"
            value={renameDraft}
            onChange={(e) => setRenameDraft(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') handleRenameSubmit()
              if (e.key === 'Escape') setRenameDialog(null)
            }}
            maxLength={80}
            className={cn(
              'w-full px-3 py-2 rounded-xl border border-border bg-surface-alt text-sm text-ink',
              'focus:outline-none focus:ring-2 focus:ring-brand/40',
            )}
          />
          <div className="flex justify-end gap-2 pt-1">
            <Button variant="ghost" size="sm" onClick={() => setRenameDialog(null)}>
              取消
            </Button>
            <Button
              variant="primary" size="sm"
              onClick={handleRenameSubmit}
              disabled={!renameDraft.trim()}
            >
              儲存
            </Button>
          </div>
        </div>
      </div>
    )}
    </>
  )
}
