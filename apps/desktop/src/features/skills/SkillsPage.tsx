import { useEffect, useState, useMemo } from 'react'
import { Search, RefreshCw, AlertCircle } from 'lucide-react'
import { useSkillsStore } from './store/useSkillsStore'
import { useBatchReadmeFetch } from './hooks/useBatchReadmeFetch'
import { SkillCard } from './components/SkillCard'
import { SourceTabs } from './components/SourceTabs'
import { InstallModal } from './components/InstallModal'
import { SkillDetailPage } from './components/SkillDetailPage'
import { LibraryTab } from './components/LibraryTab'
import { AgentsTab } from './components/AgentsTab'
import { PresetsTab } from './components/PresetsTab'
import { McpTab } from './components/McpTab'
import { useAppStore } from '@/app/store'
import { cn } from '@/lib/utils'
import type { SkillMeta } from '@modern-wingman/contracts'

const INSTALLED_TAB_ID = '__installed__'

type TopTab = 'marketplace' | 'library' | 'agents' | 'presets' | 'mcp'

const TOP_TABS: { id: TopTab; label: string }[] = [
  { id: 'marketplace', label: '市集' },
  { id: 'library', label: '中央庫' },
  { id: 'agents', label: 'Agents' },
  { id: 'presets', label: 'Presets' },
  { id: 'mcp', label: 'MCP' },
]

/** Skills hub with five top-level views (WS1.6). */
export function SkillsPage() {
  const [topTab, setTopTab] = useState<TopTab>('marketplace')

  if (topTab !== 'marketplace') {
    return (
      <div className="flex-1 flex flex-col overflow-hidden">
        <div className="px-6 pt-6 pb-4 border-b border-border space-y-3 shrink-0">
          <div>
            <h1 className="text-xl font-bold text-ink">Skills</h1>
            <p className="text-sm text-ink-secondary mt-0.5">統一管理 AI Agent 技能與 MCP</p>
          </div>
          <TopTabBar active={topTab} onChange={setTopTab} />
        </div>
        <div className="flex-1 overflow-y-auto p-6">
          {topTab === 'library' && <LibraryTab />}
          {topTab === 'agents' && <AgentsTab />}
          {topTab === 'presets' && <PresetsTab />}
          {topTab === 'mcp' && <McpTab />}
        </div>
      </div>
    )
  }

  return <MarketplaceView topTabBar={<TopTabBar active={topTab} onChange={setTopTab} />} />
}

function TopTabBar({ active, onChange }: { active: TopTab; onChange: (t: TopTab) => void }) {
  return (
    <div className="flex items-center gap-1 overflow-x-auto border-b border-border">
      {TOP_TABS.map((tab) => (
        <button
          key={tab.id}
          type="button"
          onClick={() => onChange(tab.id)}
          className={cn(
            'shrink-0 px-3.5 py-2 text-sm font-medium border-b-2 -mb-px transition-colors',
            active === tab.id
              ? 'border-brand text-brand'
              : 'border-transparent text-ink-secondary hover:text-ink',
          )}
        >
          {tab.label}
        </button>
      ))}
    </div>
  )
}

function MarketplaceView({ topTabBar }: { topTabBar: React.ReactNode }) {
  const githubPat = useAppStore((s) => s.githubPat)

  const {
    sources,
    skillsBySource,
    loadingBySource,
    errorBySource,
    installedSkills,
    agents,
    descriptionCache,
    readmeCache,
    readmeLoading,
    fetchSources,
    fetchSkills,
    fetchInstalledSkills,
    fetchAgents,
    fetchReadme,
    installSkill,
    uninstallSkill,
  } = useSkillsStore()

  const [activeTab, setActiveTab] = useState<string>('')
  const [query, setQuery] = useState('')
  const [pendingSkill, setPendingSkill] = useState<SkillMeta | null>(null)
  const [selectedSkill, setSelectedSkill] = useState<SkillMeta | null>(null)

  useEffect(() => {
    fetchSources().then(() => {
      const store = useSkillsStore.getState()
      if (store.sources.length > 0 && !activeTab) {
        setActiveTab(store.sources[0].id)
      }
    })
    fetchInstalledSkills()
    fetchAgents()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    if (!activeTab || activeTab === INSTALLED_TAB_ID) return
    if (!skillsBySource[activeTab]) {
      fetchSkills(activeTab, githubPat || undefined)
    }
  }, [activeTab, skillsBySource, fetchSkills, githubPat])

  // Throttled background SKILL.md fetching (WS2: extracted hook)
  const marketplaceSkills =
    activeTab && activeTab !== INSTALLED_TAB_ID ? skillsBySource[activeTab] : undefined
  const { resetTab } = useBatchReadmeFetch(activeTab, marketplaceSkills, githubPat || undefined)

  const currentSkills = useMemo(() => {
    if (activeTab === INSTALLED_TAB_ID) return []
    const list = skillsBySource[activeTab] ?? []
    if (!query.trim()) return list
    const q = query.toLowerCase()
    return list.filter(
      (s) =>
        s.skillName.toLowerCase().includes(q) ||
        s.displayName.toLowerCase().includes(q) ||
        (descriptionCache[s.id] ?? '').toLowerCase().includes(q)
    )
  }, [activeTab, skillsBySource, query, descriptionCache])

  const currentInstalledFiltered = useMemo(() => {
    if (activeTab !== INSTALLED_TAB_ID) return []
    if (!query.trim()) return installedSkills
    const q = query.toLowerCase()
    return installedSkills.filter(
      (s) =>
        s.skillName.toLowerCase().includes(q) ||
        s.sourceId.toLowerCase().includes(q)
    )
  }, [activeTab, installedSkills, query])

  const installedMap: Record<string, number> = useMemo(() => {
    const map: Record<string, number> = {}
    for (const ins of installedSkills) {
      map[`${ins.sourceId}/${ins.skillName}`] = ins.id
    }
    return map
  }, [installedSkills])

  const isLoading = activeTab !== INSTALLED_TAB_ID && !!loadingBySource[activeTab]
  const tabError = activeTab !== INSTALLED_TAB_ID ? (errorBySource[activeTab] ?? null) : null

  const handleRefresh = () => {
    if (activeTab && activeTab !== INSTALLED_TAB_ID) {
      resetTab(activeTab)
      fetchSkills(activeTab, githubPat || undefined)
    }
  }

  const prepareInstall=async(skill:SkillMeta)=>{await fetchReadme(skill.sourceId,skill.skillName,githubPat||undefined);setPendingSkill(skill)}

  if (selectedSkill) {
    const installedId = installedMap[selectedSkill.id]
    const installedRecord =
      installedId !== undefined
        ? installedSkills.find((i) => i.id === installedId)
        : undefined

    return (
      <SkillDetailPage
        skill={selectedSkill}
        installedRecord={installedRecord}
        githubPat={githubPat || undefined}
        onBack={() => setSelectedSkill(null)}
        onInstall={(skill)=>void prepareInstall(skill)}
        onUninstall={(id) => uninstallSkill(id)}
      />
    )
  }

  return (
    <div className="flex-1 flex flex-col overflow-hidden">
      <div className="px-6 pt-6 pb-4 border-b border-border space-y-3 shrink-0">
        <div className="flex items-center justify-between">
          <div>
            <h1 className="text-xl font-bold text-ink">Skills</h1>
            <p className="text-sm text-ink-secondary mt-0.5">統一管理 AI Agent 技能與 MCP</p>
          </div>
          <button
            type="button"
            title="Reload"
            onClick={handleRefresh}
            disabled={isLoading}
            className="p-2 rounded-xl text-ink-secondary hover:bg-surface-alt hover:text-ink transition-colors disabled:opacity-40"
          >
            <RefreshCw className={cn('w-4 h-4', isLoading && 'animate-spin')} />
          </button>
        </div>

        {topTabBar}

        <SourceTabs
          sources={sources}
          activeId={activeTab}
          onChange={(id) => {
            setActiveTab(id)
            setQuery('')
          }}
        />

        <div className="relative">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-ink-subtle pointer-events-none" />
          <input
            type="search"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Search skills..."
            className={cn(
              'w-full pl-9 pr-4 py-2 rounded-xl border border-border bg-surface text-sm',
              'placeholder:text-ink-subtle focus:outline-none focus:ring-2 focus:ring-brand/40'
            )}
          />
        </div>
      </div>

      <div className="flex-1 overflow-y-auto p-6">
        {tabError && (
          <div className="flex items-start gap-3 rounded-2xl border border-red-200 bg-red-50 p-4 mb-4">
            <AlertCircle className="w-4 h-4 text-red-500 mt-0.5 shrink-0" />
            <div>
              <p className="text-sm font-medium text-red-700">Failed to load</p>
              <p className="text-xs text-red-600 mt-0.5">{tabError}</p>
              <button type="button" onClick={handleRefresh} className="mt-2 text-xs text-red-600 underline hover:no-underline">
                Retry
              </button>
            </div>
          </div>
        )}

        {isLoading && (
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
            {Array.from({ length: 6 }).map((_, i) => (
              <div key={i} className="h-36 rounded-2xl bg-surface-alt animate-pulse border border-border" />
            ))}
          </div>
        )}

        {!isLoading && activeTab !== INSTALLED_TAB_ID && (
          currentSkills.length === 0 && !tabError ? (
            <div className="text-center py-16 text-ink-subtle text-sm">
              {query ? 'No matching skills found' : 'No skills available from this source'}
            </div>
          ) : (
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
              {currentSkills.map((skill) => {
                const installedId = installedMap[skill.id]
                const installed =
                  installedId !== undefined
                    ? installedSkills.find((i) => i.id === installedId)
                    : undefined
                return (
                  <SkillCard
                    key={skill.id}
                    skill={skill}
                    description={descriptionCache[skill.id]}
                    isLoadingDescription={readmeLoading[skill.id] ?? false}
                    installed={installed}
                    onSelect={setSelectedSkill}
                    onInstall={(skill)=>void prepareInstall(skill)}
                    onUninstall={(id) => uninstallSkill(id)}
                  />
                )
              })}
            </div>
          )
        )}

        {activeTab === INSTALLED_TAB_ID && (
          currentInstalledFiltered.length === 0 ? (
            <div className="text-center py-16 text-ink-subtle text-sm">
              {query ? 'No matching installed skills' : 'No skills installed yet'}
            </div>
          ) : (
            <div className="space-y-2">
              {currentInstalledFiltered.map((ins) => (
                <div
                  key={ins.id}
                  className="flex items-center justify-between rounded-2xl border border-border bg-surface px-4 py-3"
                >
                  <div className="min-w-0">
                    <p className="text-sm font-medium text-ink truncate">{ins.skillName}</p>
                    <p className="text-xs text-ink-subtle mt-0.5 truncate">
                      {ins.sourceId} &middot; {ins.scope === 'global' ? 'Global' : 'Project'} &middot;{' '}
                      <span className="font-mono">{ins.installedPath}</span>
                    </p>
                  </div>
                  <button
                    type="button"
                    onClick={() => uninstallSkill(ins.id)}
                    className="shrink-0 ml-4 text-xs text-ink-secondary hover:text-red-500 transition-colors"
                  >
                    Remove
                  </button>
                </div>
              ))}
            </div>
          )
        )}
      </div>

      {pendingSkill && (
        <InstallModal
          skill={pendingSkill}
          agents={agents}
          githubPat={githubPat || undefined}
          riskContent={readmeCache[pendingSkill.id]}
          onConfirm={installSkill}
          onClose={() => setPendingSkill(null)}
        />
      )}
    </div>
  )
}
