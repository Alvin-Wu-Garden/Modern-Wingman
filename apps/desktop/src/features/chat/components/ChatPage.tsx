import React, { useEffect, useMemo, useState } from 'react'
import {
  Blocks,
  Bot,
  ChevronLeft,
  ChevronRight,
  FolderGit2,
  MessageSquare,
  Plus,
  Settings,
  Sparkles,
} from 'lucide-react'
import { AppShell } from '@/app/layout/AppShell'
import { SidebarNavigation, SidebarTooltip } from '@/components/layout/sidebar-navigation'
import { useResizablePanel } from '@/components/layout/use-resizable-panel'
import { Button } from '@/components/ui/button'
import { SettingsPage } from '@/features/settings/components/SettingsPage'
import { ProjectsPage } from '@/features/projects/components/ProjectsPage'
import { CommunitySummaryProgressToast } from '@/features/projects/components/CommunitySummaryProgressToast'
import { useProjectsStore } from '@/features/projects/store/useProjectsStore'
import { cn } from '@/lib/utils'
import { useChatStore } from '../store/useChatStore'
import { ConversationPane } from './ConversationPane'

const MarketplacePage = React.lazy(async () => ({
  default: (await import('@/features/marketplace/MarketplacePage')).MarketplacePage,
}))

type ActiveView = 'home' | 'chat' | 'projects' | 'marketplace' | 'settings'

/**
 * 桌面應用程式外殼。
 * 一般對話只顯示 general conversations；專案對話由 ProjectsPage 依 projectId 分組，
 * 但兩者都使用 ConversationPane，避免產生兩套訊息與輸入框實作。
 */
export function ChatPage() {
  const [activeView, setActiveView] = useState<ActiveView>('home')
  const appSidebar = useResizablePanel({
    storageKey: 'modern-wingman:layout:app-sidebar',
    defaultWidth: 256,
    minWidth: 208,
    maxWidth: 400,
  })
  const {
    conversations,
    activeConversationId,
    isLoadingList,
    isStreaming,
    loadConversations,
    openConversation,
    startNewConversation,
    deleteConv,
  } = useChatStore()
  const {
    activeProjectId,
    fetchProjects,
    startSummaryPolling,
    stopSummaryPolling,
  } = useProjectsStore()

  useEffect(() => {
    void loadConversations()
    void fetchProjects()
  }, [fetchProjects, loadConversations])

  useEffect(() => {
    if (!activeProjectId) {
      stopSummaryPolling()
      return stopSummaryPolling
    }

    // 進入專案或一輪問答結束時重新檢查一次；摘要進入終態後由 store 自行停止輪詢。
    if (!isStreaming) startSummaryPolling(activeProjectId)
    return stopSummaryPolling
  }, [activeProjectId, isStreaming, startSummaryPolling, stopSummaryPolling])

  const generalConversations = useMemo(
    () => conversations.filter((conversation) => conversation.projectId === null),
    [conversations],
  )
  const activeConversation = conversations.find(
    (conversation) => conversation.id === activeConversationId,
  )

  const newGeneralConversation = async () => {
    await startNewConversation()
    setActiveView('chat')
  }

  const openGeneralConversation = async (id: string) => {
    await openConversation(id, null)
    setActiveView('chat')
  }

  const sidebarSections = [
    {
      items: [
        {
          id: 'home',
          label: '首頁',
          icon: <Sparkles className="h-4 w-4" />,
          onClick: () => setActiveView('home'),
        },
        {
          id: 'new-chat',
          label: '新對話',
          icon: <Plus className="h-4 w-4" />,
          onClick: () => void newGeneralConversation(),
        },
      ],
    },
    {
      title: '近期對話',
      items: isLoadingList
        ? [{ id: 'loading', label: '載入中…', icon: <MessageSquare className="h-4 w-4" /> }]
        : generalConversations.map((conversation) => ({
            id: conversation.id,
            label: conversation.title,
            icon: <MessageSquare className="h-4 w-4" />,
            onClick: () => void openGeneralConversation(conversation.id),
          })),
    },
    {
      title: '擴充',
      items: [
        {
          id: 'projects',
          label: '專案解析',
          icon: <FolderGit2 className="h-4 w-4" />,
          onClick: () => setActiveView('projects'),
        },
        {
          id: 'marketplace',
          label: 'Marketplace',
          icon: <Blocks className="h-4 w-4" />,
          onClick: () => setActiveView('marketplace'),
        },
      ],
    },
  ]

  const sidebarHeader = (
    <div className={cn('flex items-center gap-2.5', appSidebar.collapsed && 'justify-center')}>
      <div className="flex h-8 w-8 shrink-0 items-center justify-center rounded-xl bg-brand">
        <Bot className="h-4 w-4 text-white" />
      </div>
      <div className={cn('min-w-0 max-[640px]:hidden', appSidebar.collapsed && 'hidden')}>
        <p className="truncate text-sm font-semibold text-ink">Modern Wingman</p>
        <p className="text-xs font-medium text-brand-green">● Online</p>
      </div>
    </div>
  )

  const sidebarFooter = (
    <SidebarTooltip label="設定" show={appSidebar.collapsed}>
      <button
        type="button"
        onClick={() => setActiveView('settings')}
        className={cn(
          'flex w-full items-center gap-3 rounded-xl px-3 py-2.5 text-sm transition-colors',
          activeView === 'settings'
            ? 'bg-brand/10 font-medium text-brand'
            : 'text-ink-secondary hover:bg-surface-alt hover:text-ink',
        )}
      >
        <Settings className="h-4 w-4" />
        {!appSidebar.collapsed && <span className="max-[640px]:hidden">設定</span>}
      </button>
    </SidebarTooltip>
  )

  return (
    <>
      <AppShell
      sidebar={(
        <>
        <SidebarNavigation
          sections={sidebarSections}
          activeItemId={
            activeView === 'chat' && activeConversationId
              ? activeConversationId
              : activeView
          }
          header={sidebarHeader}
          onHeaderDoubleClick={appSidebar.toggleCollapsed}
          footer={sidebarFooter}
          collapsed={appSidebar.collapsed}
          style={appSidebar.panelStyle}
          className="group/app-sidebar"
        />
        <button
          type="button"
          aria-label={appSidebar.collapsed ? '展開主側邊欄' : '收合主側邊欄'}
          aria-expanded={!appSidebar.collapsed}
          title={appSidebar.collapsed ? '展開側邊欄' : '收合側邊欄'}
          onClick={appSidebar.toggleCollapsed}
          className="fixed left-[var(--app-sidebar-toggle-left)] top-12 z-30 flex h-7 w-7 -translate-x-1/2 items-center justify-center rounded-full border border-border bg-surface text-ink-secondary shadow-sm transition-colors hover:bg-surface-alt hover:text-ink max-[640px]:hidden"
          style={{
            '--app-sidebar-toggle-left': `${appSidebar.collapsed ? 64 : appSidebar.width}px`,
          } as React.CSSProperties}
        >
          {appSidebar.collapsed
            ? <ChevronRight className="h-3.5 w-3.5" />
            : <ChevronLeft className="h-3.5 w-3.5" />}
        </button>
        {!appSidebar.collapsed && (
          <div
            {...appSidebar.resizeHandleProps}
            className="fixed bottom-0 top-0 z-20 w-1 -translate-x-1/2 cursor-col-resize touch-none outline-none hover:bg-brand/40 focus:bg-brand/50 max-[640px]:hidden"
            style={{ left: appSidebar.width }}
          />
        )}
        </>
      )}
    >
      {activeView === 'home' && (
        <div className="flex flex-1 items-center justify-center p-8">
          <div className="max-w-xl text-center">
            <div className="mx-auto flex h-14 w-14 items-center justify-center rounded-2xl bg-brand/10">
              <Bot className="h-7 w-7 text-brand" />
            </div>
            <h1 className="mt-5 text-2xl font-bold text-ink">Modern Wingman</h1>
            <p className="mt-2 text-sm leading-6 text-ink-secondary">
              一般對話可以自由討論；專案解析則以已索引的知識圖譜回答程式碼問題。
            </p>
            <div className="mt-6 flex justify-center gap-3">
              <Button onClick={() => void newGeneralConversation()}>
                <Plus className="mr-2 h-4 w-4" />
                新對話
              </Button>
              <Button variant="outline" onClick={() => setActiveView('projects')}>
                <FolderGit2 className="mr-2 h-4 w-4" />
                專案解析
              </Button>
            </div>
          </div>
        </div>
      )}

      {activeView === 'chat' && activeConversation?.projectId === null && (
        <ConversationPane
          title={activeConversation.title}
          emptyText="輸入任何想聊的內容。"
          onNewConversation={newGeneralConversation}
          onDeleteConversation={async () => {
            await deleteConv(activeConversation.id, null)
            setActiveView('home')
          }}
        />
      )}

      {activeView === 'projects' && <ProjectsPage />}
      {activeView === 'settings' && <SettingsPage />}
      {activeView === 'marketplace' && (
        <React.Suspense fallback={<div className="p-6 text-sm text-ink-secondary">載入中…</div>}>
          <MarketplacePage />
        </React.Suspense>
      )}
      </AppShell>

      <CommunitySummaryProgressToast />
    </>
  )
}
