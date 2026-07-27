import React, { useEffect, useMemo, useState } from 'react'
import {
  Blocks,
  Bot,
  FolderGit2,
  MessageSquare,
  Plus,
  Settings,
  Sparkles,
} from 'lucide-react'
import { AppShell } from '@/app/layout/AppShell'
import { SidebarNavigation } from '@/components/layout/sidebar-navigation'
import { Button } from '@/components/ui/button'
import { SettingsPage } from '@/features/settings/components/SettingsPage'
import { ProjectsPage } from '@/features/projects/components/ProjectsPage'
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
  const {
    conversations,
    activeConversationId,
    isLoadingList,
    loadConversations,
    openConversation,
    startNewConversation,
    deleteConv,
  } = useChatStore()

  useEffect(() => {
    void loadConversations()
  }, [loadConversations])

  const generalConversations = useMemo(
    () => conversations.filter((conversation) => conversation.scope === 'general'),
    [conversations],
  )
  const activeConversation = conversations.find(
    (conversation) => conversation.id === activeConversationId,
  )

  const newGeneralConversation = async () => {
    await startNewConversation('general')
    setActiveView('chat')
  }

  const openGeneralConversation = async (id: string) => {
    await openConversation(id)
    setActiveView('chat')
  }

  const sidebarSections = [
    {
      items: [
        {
          id: 'home',
          label: '總覽',
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
    <div className="flex items-center gap-2.5">
      <div className="flex h-8 w-8 shrink-0 items-center justify-center rounded-xl bg-brand">
        <Bot className="h-4 w-4 text-white" />
      </div>
      <div className="min-w-0 max-[640px]:hidden">
        <p className="truncate text-sm font-semibold text-ink">Modern Wingman</p>
        <p className="text-xs font-medium text-brand-green">● Online</p>
      </div>
    </div>
  )

  const sidebarFooter = (
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
      <span className="max-[640px]:hidden">設定</span>
    </button>
  )

  return (
    <AppShell
      sidebar={(
        <SidebarNavigation
          sections={sidebarSections}
          activeItemId={
            activeView === 'chat' && activeConversationId
              ? activeConversationId
              : activeView
          }
          header={sidebarHeader}
          footer={sidebarFooter}
        />
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

      {activeView === 'chat' && activeConversation?.scope === 'general' && (
        <ConversationPane
          title={activeConversation.title}
          emptyText="輸入任何想聊的內容。"
          onNewConversation={newGeneralConversation}
          onDeleteConversation={async () => {
            await deleteConv(activeConversation.id)
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
  )
}
