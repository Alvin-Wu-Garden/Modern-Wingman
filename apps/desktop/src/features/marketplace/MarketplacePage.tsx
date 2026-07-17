import { useCallback, useEffect, useMemo, useState } from 'react'
import { open } from '@tauri-apps/plugin-dialog'
import { AlertCircle, Box, Clock3, Database, Download, FileArchive, FileJson, FolderUp, PackageSearch, Puzzle, RefreshCw, Search, Server, Sparkles, Star } from 'lucide-react'
import { cn } from '@/lib/utils'
import { SkillsPage } from '@/features/skills/SkillsPage'
import {
  listMarketplace,
  checkMarketplaceUpdates,
  applyMarketplaceUpdate,
  getMarketplaceDiscovery,
  importMarketplaceFolder,
  importMarketplaceArchive,
  importCodexMarketplace,
  importGitHubRepository,
  deployMarketplaceSkills,
  deployMarketplaceMcp,
  previewMarketplaceSkills,
  previewMarketplaceMcp,
  installMarketplacePlugin,
  getMarketplacePluginPreview,
  listMarketplaceArtifacts,
  listMarketplacePlugins,
  listMarketplaceTargets,
  listMarketplaceArtifactDeployments,
  removeMarketplaceArtifactDeployments,
  removeMarketplaceMcpDeployments,
  setMarketplacePluginEnabled,
  getMarketplacePluginConfiguration,
  saveMarketplacePluginConfiguration,
  type MarketplaceArtifact,
  type MarketplaceArtifactUpdate,
  type MarketplaceDeploymentScope,
  type MarketplaceDeploymentPlan,
  type MarketplaceDeploymentState,
  type MarketplacePluginInstallation,
  type MarketplacePluginPreview,
  type MarketplacePluginConfiguration,
  type MarketplaceTargetDescriptor,
  refreshMarketplace,
  setMarketplaceFavorite,
  type MarketplaceArtifactKind,
  type MarketplaceDiscoveryRecord,
  type MarketplaceRefreshResult,
} from '@/services/agent-api/marketplace'

type MarketplaceTab = 'discover' | 'skills' | 'mcp' | 'plugins' | 'installed' | 'updates' | 'sources'

const tabs: Array<{ id: MarketplaceTab; label: string; icon: typeof PackageSearch }> = [
  { id: 'discover', label: 'Discover', icon: PackageSearch },
  { id: 'skills', label: 'Skills', icon: Sparkles },
  { id: 'mcp', label: 'MCP Servers', icon: Server },
  { id: 'plugins', label: 'Wingman Plugins', icon: Puzzle },
  { id: 'installed', label: 'Installed', icon: Download },
  { id: 'updates', label: 'Updates', icon: Clock3 },
  { id: 'sources', label: 'Sources', icon: Database },
]

const kindForTab: Partial<Record<MarketplaceTab, MarketplaceArtifactKind>> = {
  skills: 'Skill',
  mcp: 'McpServer',
  plugins: 'WingmanPlugin',
}

export function MarketplacePage() {
  const [tab, setTab] = useState<MarketplaceTab>('discover')
  const [query, setQuery] = useState('')
  const [items, setItems] = useState<MarketplaceDiscoveryRecord[]>([])
  const [loading, setLoading] = useState(false)
  const [refreshing, setRefreshing] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [importing, setImporting] = useState(false)
  const [detailId, setDetailId] = useState<string | null>(null)

  const load = useCallback(async () => {
    if (tab === 'installed' || tab === 'updates' || tab === 'sources') return
    setLoading(true)
    setError(null)
    try {
      const page = await listMarketplace({ kind: kindForTab[tab], search: query })
      setItems(page.items)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setLoading(false)
    }
  }, [query, tab])

  useEffect(() => { void load() }, [load])

  const onRefresh = async () => {
    setRefreshing(true)
    setError(null)
    setNotice(null)
    try {
      const result: MarketplaceRefreshResult = await refreshMarketplace()
      setNotice(
        result.isPartial
          ? `Marketplace 已部分重新整理：成功 ${result.successfulQueries}/${result.totalQueries} 個查詢。`
          : `Marketplace 已重新整理：新增 ${result.newCount}、更新 ${result.updatedCount}、未變更 ${result.unchangedCount}、標記過期 ${result.staleCount}。`,
      )
      await load()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setRefreshing(false)
    }
  }

  const onFavorite = async (item: MarketplaceDiscoveryRecord) => {
    try {
      await setMarketplaceFavorite(item.id, !item.isFavorite)
      setItems((current) => current.map((entry) => entry.id === item.id ? { ...entry, isFavorite: !entry.isFavorite } : entry))
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    }
  }

  const onImportFolder = async () => {
    const selected = await open({ directory: true, multiple: false, title: '選擇要匯入 Marketplace 的資料夾' })
    if (typeof selected !== 'string') return
    setImporting(true)
    setError(null)
    try {
      const result = await importMarketplaceFolder(selected)
      setNotice(`已匯入 ${result.artifacts.length} 個 artifact；找到 ${result.candidates.length} 個候選項目。`)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setImporting(false)
    }
  }

  const onImportArchive = async () => {
    const selected = await open({ multiple: false, title: '選擇 ZIP 或 .skill archive', filters: [{ name: 'Skill archive', extensions: ['zip', 'skill'] }] })
    if (typeof selected !== 'string') return
    setImporting(true)
    setError(null)
    try {
      const result = await importMarketplaceArchive(selected)
      setNotice(`已匯入 ${result.artifacts.length} 個 artifact；找到 ${result.candidates.length} 個候選項目。`)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setImporting(false)
    }
  }

  const onImportCodexMarketplace = async () => {
    const selected = await open({ multiple: false, title: '選擇 Codex marketplace.json', filters: [{ name: 'marketplace.json', extensions: ['json'] }] })
    if (typeof selected !== 'string') return
    setImporting(true)
    setError(null)
    try {
      const result = await importCodexMarketplace(selected)
      setNotice(`Codex marketplace 匯入完成：artifact ${result.importedArtifacts}、無效候選 ${result.invalidCandidates}、略過 ${result.skippedEntries.length}。`)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setImporting(false)
    }
  }

  const onImportGitHub = async (repositoryUrl: string, reference: string) => {
    setImporting(true)
    setError(null)
    try {
      const result = await importGitHubRepository(repositoryUrl, reference)
      setNotice(`GitHub 匯入完成：${result.canonicalUrl}@${result.commitSha.slice(0, 12)}，artifact ${result.import.artifacts.length}。`)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setImporting(false)
    }
  }

  const pageContent = useMemo(() => {
    if (tab === 'installed') return <InstalledPanel />
    if (tab === 'updates') return <UpdatesPanel />
    if (tab === 'sources') return <SourcesPanel importing={importing} onImportFolder={() => void onImportFolder()} onImportArchive={() => void onImportArchive()} onImportCodexMarketplace={() => void onImportCodexMarketplace()} onImportGitHub={(url, ref) => void onImportGitHub(url, ref)} />
    if (loading) return <LoadingCards />
    if (tab === 'plugins') return <PluginPanel />
    if (items.length === 0) return <EmptyState title="尚無可顯示項目" description="設定 GitHub PAT 後按下重新整理，或從 Sources 匯入本機資料夾與 ZIP / .skill archive。" />
    return <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-3">{items.map((item) => <MarketplaceCard key={item.id} item={item} onFavorite={onFavorite} onOpen={setDetailId} />)}</div>
  }, [items, loading, tab, importing])

  return (
    <div className="flex-1 flex flex-col overflow-hidden">
      <div className="px-6 pt-6 pb-4 border-b border-border space-y-4 shrink-0">
        <div className="flex items-start justify-between gap-4">
          <div>
            <h1 className="text-xl font-bold text-ink">Marketplace</h1>
            <p className="text-sm text-ink-secondary mt-0.5">集中探索、驗證與管理 Skill、MCP Server 與 Wingman Plugin</p>
          </div>
          <button type="button" onClick={() => void onRefresh()} disabled={refreshing} className="inline-flex items-center gap-2 rounded-xl border border-border bg-surface px-3 py-2 text-sm font-medium text-ink hover:bg-surface-alt disabled:opacity-50">
            <RefreshCw className={cn('w-4 h-4', refreshing && 'animate-spin')} />
            重新整理
          </button>
        </div>
        <div className="flex items-center gap-1 overflow-x-auto border-b border-border">
          {tabs.map(({ id, label, icon: Icon }) => (
            <button key={id} type="button" onClick={() => { setTab(id); setQuery('') }} className={cn('inline-flex items-center gap-1.5 shrink-0 px-3.5 py-2 text-sm font-medium border-b-2 -mb-px transition-colors', tab === id ? 'border-brand text-brand' : 'border-transparent text-ink-secondary hover:text-ink')}>
              <Icon className="w-3.5 h-3.5" />{label}
            </button>
          ))}
        </div>
        {tab !== 'installed' && tab !== 'updates' && tab !== 'sources' && (
          <label className="relative block">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-ink-subtle pointer-events-none" />
            <input type="search" value={query} onChange={(event) => setQuery(event.target.value)} placeholder="搜尋 Marketplace…" className="w-full pl-9 pr-4 py-2 rounded-xl border border-border bg-surface text-sm placeholder:text-ink-subtle focus:outline-none focus:ring-2 focus:ring-brand/40" />
          </label>
        )}
      </div>
      <div className="flex-1 overflow-y-auto p-6">
        {notice && <div className="mb-4 rounded-xl border border-green-200 bg-green-50 px-4 py-3 text-sm text-green-800">{notice}</div>}
        {error && <div className="mb-4 flex items-start gap-3 rounded-xl border border-red-200 bg-red-50 p-4 text-sm text-red-700"><AlertCircle className="w-4 h-4 mt-0.5 shrink-0" /><div><p>{error}</p><button type="button" onClick={() => void load()} className="mt-2 underline">重試</button></div></div>}
        {pageContent}
        {detailId && <MarketplaceDetailDialog id={detailId} onClose={() => setDetailId(null)} onImport={async item => { await onImportGitHub(item.canonicalUrl, ''); setDetailId(null) }} />}
      </div>
    </div>
  )
}

function MarketplaceCard({ item, onFavorite, onOpen }: { item: MarketplaceDiscoveryRecord; onFavorite: (item: MarketplaceDiscoveryRecord) => void; onOpen: (id: string) => void }) {
  const score = item.artifactQualityScore ?? item.discoveryScore
  const scoreLabel = item.artifactQualityScore === null ? 'Discovery Score' : 'Artifact Quality'
  return <article role="button" tabIndex={0} onClick={() => onOpen(item.id)} onKeyDown={event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); onOpen(item.id) } }} className="rounded-2xl border border-border bg-surface p-4 flex flex-col gap-3 shadow-sm cursor-pointer transition-colors hover:border-brand/40 hover:bg-surface-alt focus:outline-none focus:ring-2 focus:ring-brand/40">
    <div className="flex items-start justify-between gap-3"><div className="min-w-0"><p className="font-semibold text-ink truncate">{item.name}</p><p className="text-xs text-ink-subtle truncate">{item.owner}/{item.repository}</p></div><button type="button" title={item.isFavorite ? '取消收藏' : '收藏'} onClick={event => { event.stopPropagation(); onFavorite(item) }} className="p-1 text-ink-subtle hover:text-amber-500"><Star className={cn('w-4 h-4', item.isFavorite && 'fill-amber-400 text-amber-500')} /></button></div>
    <p className="text-sm text-ink-secondary line-clamp-2 min-h-10">{item.description || '尚無描述。'}</p>
    <div className="flex flex-wrap gap-1.5 text-xs"><Badge>{kindLabel(item.suggestedKind)}</Badge><Badge>{item.primaryCategory}</Badge>{item.license ? <Badge>{item.license}</Badge> : <span className="rounded-md bg-amber-50 px-2 py-1 text-amber-700">License unknown</span>}</div>
    <div className="flex items-center justify-between border-t border-border pt-3 text-xs"><span className="text-ink-secondary">{scoreLabel} <strong className="text-ink">{score.toFixed(1)}</strong></span><span className={cn('rounded-md px-2 py-1', item.status === 'Stale' ? 'bg-amber-50 text-amber-700' : 'bg-surface-alt text-ink-secondary')}>{item.status}</span></div>
  </article>
}

function MarketplaceDetailDialog({ id, onClose, onImport }: { id: string; onClose: () => void; onImport: (item: MarketplaceDiscoveryRecord) => Promise<void> }) {
  const [item, setItem] = useState<MarketplaceDiscoveryRecord | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [importing, setImporting] = useState(false)
  useEffect(() => { let active = true; void getMarketplaceDiscovery(id).then(value => { if (active) setItem(value) }).catch(cause => { if (active) setError(cause instanceof Error ? cause.message : String(cause)) }); return () => { active = false } }, [id])
  const importArtifact = async () => { if (!item) return; setImporting(true); setError(null); try { await onImport(item) } catch (cause) { setError(cause instanceof Error ? cause.message : String(cause)) } finally { setImporting(false) } }
  return <div className="fixed inset-0 z-50 grid place-items-center bg-black/30 p-4" onMouseDown={onClose}><section role="dialog" aria-modal="true" aria-label="Marketplace 詳情" onMouseDown={event => event.stopPropagation()} className="w-full max-w-2xl rounded-2xl border border-border bg-surface p-5 shadow-xl"><div className="flex items-start justify-between gap-4"><div><h2 className="font-semibold text-ink">{item?.name ?? '載入詳情…'}</h2>{item && <p className="mt-1 text-sm text-ink-secondary">{item.owner}/{item.repository}</p>}</div><button type="button" onClick={onClose} className="rounded-lg px-2 py-1 text-ink-secondary hover:bg-surface-alt">關閉</button></div>{error ? <p className="mt-4 text-sm text-red-600">{error}</p> : !item ? <div className="mt-5 h-40 animate-pulse rounded-xl bg-surface-alt" /> : <div className="mt-5 space-y-4 text-sm"><p className="whitespace-pre-wrap text-ink-secondary">{item.description || '尚無描述。'}</p><div className="grid grid-cols-2 gap-2 sm:grid-cols-4"><DetailField label="種類" value={kindLabel(item.suggestedKind)} /><DetailField label="分類" value={item.primaryCategory} /><DetailField label="Discovery" value={item.discoveryScore.toFixed(1)} /><DetailField label="狀態" value={item.status} /></div><div className="rounded-xl bg-surface-alt p-3"><p className="text-xs text-ink-subtle">來源</p><a href={item.canonicalUrl} target="_blank" rel="noreferrer" className="mt-1 block break-all text-brand hover:underline">{item.canonicalUrl}</a></div><div className="flex flex-wrap gap-1.5">{item.topics.map(topic => <Badge key={topic}>{topic}</Badge>)}{item.license && <Badge>{item.license}</Badge>}{item.isArchived && <Badge>Archived</Badge>}</div><p className="text-xs text-ink-subtle">分類可信度：{item.classificationConfidence} · 最近更新：{item.gitHubUpdatedAt ? new Date(item.gitHubUpdatedAt).toLocaleString() : '未知'}</p><div className="flex justify-end"><button type="button" onClick={() => void importArtifact()} disabled={importing || item.isArchived} className="rounded-xl bg-brand px-3 py-2 text-sm font-medium text-white disabled:opacity-50">{importing ? '解析並匯入中…' : '解析並匯入 artifact'}</button></div></div>}</section></div>
}
function DetailField({ label, value }: { label: string; value: string }) { return <div className="rounded-xl border border-border p-3"><p className="text-xs text-ink-subtle">{label}</p><p className="mt-1 truncate font-medium text-ink">{value}</p></div> }
function UpdatesPanel() {
  const [updates, setUpdates] = useState<MarketplaceArtifactUpdate[] | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const check = async () => { setLoading(true); setError(null); try { setUpdates(await checkMarketplaceUpdates()) } catch (cause) { setError(cause instanceof Error ? cause.message : String(cause)) } finally { setLoading(false) } }
  const apply = async (update: MarketplaceArtifactUpdate) => { if (!update.availableCommitSha) return; setLoading(true); setError(null); try { await applyMarketplaceUpdate(update.artifactId, update.availableCommitSha); await check() } catch (cause) { setError(cause instanceof Error ? cause.message : String(cause)) } finally { setLoading(false) } }
  return <div className="max-w-3xl space-y-4"><div className="rounded-2xl border border-border bg-surface p-4"><h2 className="font-semibold text-ink">手動 Check for Updates</h2><p className="mt-1 text-sm text-ink-secondary">只比較已匯入 GitHub snapshot 的 commit SHA；只有你確認後才會建立新版 snapshot，既有部署不會自動改動。</p><button type="button" onClick={() => void check()} disabled={loading} className="mt-3 inline-flex items-center gap-2 rounded-xl border border-border px-3 py-2 text-sm font-medium text-ink hover:bg-surface-alt disabled:opacity-50"><RefreshCw className={cn('h-4 w-4', loading && 'animate-spin')} />{loading ? '檢查中…' : 'Check for Updates'}</button></div>{error && <div className="rounded-xl border border-red-200 bg-red-50 p-3 text-sm text-red-700">{error}</div>}{updates?.length === 0 && <EmptyState title="沒有可檢查的 artifact" description="先從 GitHub 解析並匯入 artifact。" />}{updates?.map(update => <div key={update.artifactId} className="rounded-2xl border border-border bg-surface p-4"><div className="flex items-center justify-between gap-3"><div><p className="font-medium text-ink">{update.displayName}</p><p className="mt-1 break-all text-xs text-ink-subtle">{update.sourceLocation}</p></div><Badge>{update.status}</Badge></div>{update.status === 'UpdateAvailable' && <div className="mt-3 flex items-center justify-between gap-3"><p className="text-sm text-amber-700">可用新 SHA：{update.availableCommitSha?.slice(0, 12)}。建立新版 snapshot 不會重新部署。</p><button type="button" disabled={loading} onClick={() => void apply(update)} className="shrink-0 rounded-xl bg-brand px-3 py-2 text-sm font-medium text-white disabled:opacity-50">確認建立新版</button></div>}{update.message && <p className="mt-3 text-sm text-ink-secondary">{update.message}</p>}</div>)}</div>
}

function Badge({ children }: { children: React.ReactNode }) { return <span className="rounded-md bg-surface-alt px-2 py-1 text-ink-secondary">{children}</span> }
function kindLabel(kind: MarketplaceArtifactKind) { return ({ Skill: 'Skill', McpServer: 'MCP Server', WingmanPlugin: 'Wingman Plugin', Unknown: 'Unknown', UnsupportedProject: 'Unsupported' })[kind] }
function LoadingCards() { return <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-3">{Array.from({ length: 6 }, (_, index) => <div key={index} className="h-48 rounded-2xl border border-border bg-surface-alt animate-pulse" />)}</div> }
function EmptyState({ title, description }: { title: string; description: string }) { return <div className="py-20 text-center"><Box className="mx-auto mb-3 w-8 h-8 text-ink-subtle" /><p className="font-medium text-ink">{title}</p><p className="mt-1 text-sm text-ink-secondary">{description}</p></div> }
function SourcesPanel({ importing, onImportFolder, onImportArchive, onImportCodexMarketplace, onImportGitHub }: { importing: boolean; onImportFolder: () => void; onImportArchive: () => void; onImportCodexMarketplace: () => void; onImportGitHub: (url: string, ref: string) => void }) { const [url, setUrl] = useState(''); const [ref, setRef] = useState(''); return <div className="max-w-2xl space-y-3"><section className="rounded-2xl border border-border bg-surface p-4"><h2 className="font-semibold text-ink">GitHub Discovery</h2><p className="mt-1 text-sm text-ink-secondary">使用 Settings 中的 GitHub PAT，只有按下「重新整理」才查詢 GitHub。結果會保存於本機 SQLite，不建立或下載 JSON Index。</p></section><section className="rounded-2xl border border-border bg-surface p-4"><h2 className="font-semibold text-ink">直接 GitHub 匯入</h2><p className="mt-1 text-sm text-ink-secondary">輸入 repository root URL；可選 branch、tag 或 commit。Wingman 會先固定解析為 immutable commit SHA，再建立本機 snapshot。</p><div className="mt-3 grid gap-2 sm:grid-cols-[1fr_10rem_auto]"><input value={url} onChange={event => setUrl(event.target.value)} placeholder="https://github.com/owner/repository" className="rounded-xl border border-border bg-surface px-3 py-2 text-sm text-ink" /><input value={ref} onChange={event => setRef(event.target.value)} placeholder="ref (optional)" className="rounded-xl border border-border bg-surface px-3 py-2 text-sm text-ink" /><button type="button" disabled={importing || !url.trim()} onClick={() => onImportGitHub(url.trim(), ref.trim())} className="rounded-xl border border-border px-3 py-2 text-sm font-medium text-ink hover:bg-surface-alt disabled:opacity-50">匯入</button></div></section><section className="rounded-2xl border border-border bg-surface p-4"><div className="flex items-start justify-between gap-4"><div><h2 className="font-semibold text-ink">本機匯入</h2><p className="mt-1 text-sm text-ink-secondary">只分析你手動選取的資料夾或 archive；不會掃描或收編 Agent IDE 既有內容。匯入期間不執行任何 script。</p></div><div className="flex shrink-0 flex-wrap justify-end gap-2"><button type="button" onClick={onImportFolder} disabled={importing} className="inline-flex items-center gap-2 rounded-xl border border-border px-3 py-2 text-sm font-medium text-ink hover:bg-surface-alt disabled:opacity-50"><FolderUp className="w-4 h-4" />資料夾</button><button type="button" onClick={onImportArchive} disabled={importing} className="inline-flex items-center gap-2 rounded-xl border border-border px-3 py-2 text-sm font-medium text-ink hover:bg-surface-alt disabled:opacity-50"><FileArchive className="w-4 h-4" />ZIP / .skill</button><button type="button" onClick={onImportCodexMarketplace} disabled={importing} className="inline-flex items-center gap-2 rounded-xl border border-border px-3 py-2 text-sm font-medium text-ink hover:bg-surface-alt disabled:opacity-50"><FileJson className="w-4 h-4" />Codex JSON</button></div></div></section></div> }
function InstalledPanel() {
  const [artifacts, setArtifacts] = useState<MarketplaceArtifact[]>([])
  const [targets, setTargets] = useState<MarketplaceTargetDescriptor[]>([])
  const [selected, setSelected] = useState<MarketplaceArtifact | null>(null)
  const [deploymentStates, setDeploymentStates] = useState<{ artifact: MarketplaceArtifact; states: MarketplaceDeploymentState[] } | null>(null)
  const [showLegacy, setShowLegacy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [resultMessage, setResultMessage] = useState<string | null>(null)
  const reload = useCallback(async () => { try { const [stored, availableTargets] = await Promise.all([listMarketplaceArtifacts(), listMarketplaceTargets()]); setArtifacts(stored); setTargets(availableTargets) } catch (cause) { setError(cause instanceof Error ? cause.message : String(cause)) } }, [])
  useEffect(() => { void reload() }, [reload])
  if (showLegacy) return <SkillsPage />
  const remove = async (artifact: MarketplaceArtifact) => {
    try {
      setError(null)
      setResultMessage(null)
      const result = artifact.kind === 'McpServer' ? await removeMarketplaceMcpDeployments(artifact.id) : await removeMarketplaceArtifactDeployments(artifact.id)
      if (result.results.length === 0) {
        setResultMessage('沒有找到由 Wingman 管理的部署／設定，因此未移除任何內容。')
      } else {
        const removed = result.results.filter(item => item.status === 'Removed').length
        const detached = result.results.filter(item => item.status === 'DetachedDueToDrift').length
        const failed = result.results.filter(item => item.status === 'Failed')
        setResultMessage(`已移除 ${removed} 個受管理目標${detached ? `；保留 ${detached} 個已被使用者修改的目標` : ''}${failed.length ? `；${failed.length} 個失敗` : ''}。`)
        setError(failed[0]?.message ?? null)
      }
      await reload()
    } catch (cause) { setError(cause instanceof Error ? cause.message : String(cause)) }
  }
  const inspectDeployments = async (artifact: MarketplaceArtifact) => { try { setError(null); setDeploymentStates({ artifact, states: await listMarketplaceArtifactDeployments(artifact.id) }) } catch (cause) { setError(cause instanceof Error ? cause.message : String(cause)) } }
  const retry = async (state: MarketplaceDeploymentState) => {
    if (!deploymentStates) return
    try {
      setError(null)
      const request = [{ artifactId: deploymentStates.artifact.id, targetId: state.targetId, scope: state.scope, projectPath: state.projectPath }]
      const result = deploymentStates.artifact.kind === 'McpServer' ? await deployMarketplaceMcp(request) : await deployMarketplaceSkills(request)
      const failed = result.results.find(item => item.status === 'Failed' || item.status === 'BlockedByConflict')
      if (failed) setError(failed.message ?? failed.status)
      setDeploymentStates({ artifact: deploymentStates.artifact, states: await listMarketplaceArtifactDeployments(deploymentStates.artifact.id) })
    } catch (cause) { setError(cause instanceof Error ? cause.message : String(cause)) }
  }
  const selectedTargets = selected ? targets.filter(target => selected.kind === 'McpServer' ? target.supportsMcp : target.supportsSkill) : []
  return <div className="space-y-4">{error && <div className="rounded-xl border border-red-200 bg-red-50 p-3 text-sm text-red-700">{error}</div>}{resultMessage && <div className="rounded-xl border border-green-200 bg-green-50 p-3 text-sm text-green-800">{resultMessage}</div>}<div className="flex items-center justify-between"><div><h2 className="font-semibold text-ink">Installed artifacts</h2><p className="text-sm text-ink-secondary">部署只會對明確選擇的 Target 與 scope 執行。</p></div><button type="button" onClick={() => setShowLegacy(true)} className="rounded-xl border border-border px-3 py-2 text-sm text-ink hover:bg-surface-alt">既有 Skill / MCP 管理</button></div>{artifacts.length === 0 ? <EmptyState title="尚未匯入 artifact" description="從 Sources 手動匯入資料夾或 archive 後，在這裡選擇跨 IDE 部署。" /> : <div className="space-y-2">{artifacts.map((artifact) => <div key={artifact.id} className="rounded-2xl border border-border bg-surface p-4 flex items-center justify-between gap-4"><div className="min-w-0"><p className="font-medium text-ink">{artifact.displayName}</p><p className="text-xs text-ink-subtle mt-1">{kindLabel(artifact.kind)} · {artifact.status} · {artifact.contentHash.slice(0, 12)}</p></div>{artifact.kind === 'Skill' || (artifact.kind === 'McpServer' && artifact.status === 'Resolved') ? <div className="flex gap-2"><button type="button" onClick={() => setSelected(artifact)} className="rounded-xl border border-border px-3 py-2 text-sm text-ink hover:bg-surface-alt">{artifact.kind === 'McpServer' ? '配置' : '部署'}</button><button type="button" onClick={() => void inspectDeployments(artifact)} className="rounded-xl border border-border px-3 py-2 text-sm text-ink hover:bg-surface-alt">部署狀態</button><button type="button" onClick={() => void remove(artifact)} className="rounded-xl border border-border px-3 py-2 text-sm text-ink-secondary hover:text-red-600">全部移除</button></div> : <span className="text-xs text-ink-secondary">{artifact.kind === 'WingmanPlugin' ? '請至 Wingman Plugins 管理' : '此 MCP 需要手動設定'}</span>}</div>)}</div>}{selected && <DeployDialog artifact={selected} targets={selectedTargets} onClose={() => setSelected(null)} onDone={() => { setSelected(null); void reload() }} />}{deploymentStates && <div className="fixed inset-0 z-50 grid place-items-center bg-black/30 p-4" onMouseDown={() => setDeploymentStates(null)}><section className="w-full max-w-xl rounded-2xl border border-border bg-surface p-5 shadow-xl" onMouseDown={event => event.stopPropagation()}><div className="flex justify-between"><h2 className="font-semibold text-ink">{deploymentStates.artifact.displayName} 的部署狀態</h2><button type="button" onClick={() => setDeploymentStates(null)} className="text-sm text-ink-secondary">關閉</button></div>{deploymentStates.states.length === 0 ? <p className="mt-4 text-sm text-ink-secondary">尚無 Wingman 管理的部署紀錄。</p> : <div className="mt-4 space-y-2">{deploymentStates.states.map(state => <div key={`${state.targetId}-${state.scope}-${state.targetPath}`} className="rounded-xl border border-border p-3 text-sm"><p className="font-medium text-ink">{state.targetId} · {state.scope} · {state.status}</p><p className="mt-1 break-all text-xs text-ink-subtle">{state.targetPath}</p>{state.status === 'Failed' && <button type="button" onClick={() => void retry(state)} className="mt-2 rounded-lg border border-border px-2 py-1 text-xs text-ink hover:bg-surface-alt">重試此目標</button>}</div>)}</div>}</section></div>}</div>
}

function DeployDialog({ artifact, targets, onClose, onDone }: { artifact: MarketplaceArtifact; targets: MarketplaceTargetDescriptor[]; onClose: () => void; onDone: () => void }) {
  const [selections, setSelections] = useState<Record<string, { scope?: MarketplaceDeploymentScope; projectPath?: string | null }>>({})
  const [preview, setPreview] = useState<MarketplaceDeploymentPlan | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const isMcp = artifact.kind === 'McpServer'
  const toggleTarget = (targetId: string) => { setPreview(null); setSelections(current => {
    const next = { ...current }
    if (next[targetId]) delete next[targetId]; else next[targetId] = {}
    return next
  }) }
  const setScope = (targetId: string, scope: MarketplaceDeploymentScope) => { setPreview(null); setSelections(current => ({ ...current, [targetId]: { ...current[targetId], scope } })) }
  const chooseProject = async (targetId: string) => { const selected = await open({ directory: true, multiple: false, title: '選擇 project root' }); if (typeof selected === 'string') { setPreview(null); setSelections(current => ({ ...current, [targetId]: { ...current[targetId], projectPath: selected } })) } }
  const deploy = async () => {
    const requests = Object.entries(selections).map(([targetId, selection]) => ({ artifactId: artifact.id, targetId, scope: selection.scope, projectPath: selection.projectPath }))
    if (requests.length === 0) { setError('至少選擇一個 Target。'); return }
    if (requests.some(request => !request.scope)) { setError('每個已選 Target 都必須明確選擇 scope。'); return }
    if (requests.some(request => request.scope === 'Project' && !request.projectPath)) { setError('Project scope 必須為每個 Target 選擇 project root。'); return }
    setBusy(true)
    try {
      const typedRequests = requests as Array<{ artifactId: string; targetId: string; scope: MarketplaceDeploymentScope; projectPath?: string | null }>
      if (!preview) {
        const plan = isMcp ? await previewMarketplaceMcp(typedRequests) : await previewMarketplaceSkills(typedRequests)
        setPreview(plan)
        const blocking = plan.items.find(item => !['Compatible', 'CompatibleNeedsUserInput', 'AlreadyDeployed'].includes(item.status))
        if (blocking) setError(blocking.reason ?? blocking.status)
        return
      }
      if (preview.items.some(item => !['Compatible', 'CompatibleNeedsUserInput', 'AlreadyDeployed'].includes(item.status))) return
      const result = isMcp ? await deployMarketplaceMcp(typedRequests) : await deployMarketplaceSkills(typedRequests)
      const failed = result.results.find(item => item.status === 'Failed' || item.status === 'BlockedByConflict')
      if (failed) { setError(failed.message ?? failed.status); return }
      onDone()
    } catch (cause) { setError(cause instanceof Error ? cause.message : String(cause)) } finally { setBusy(false) }
  }
  return <div className="fixed inset-0 z-50 grid place-items-center bg-black/30 p-4"><section className="w-full max-w-xl rounded-2xl bg-surface border border-border p-5 shadow-xl"><h2 className="font-semibold text-ink">{isMcp ? '配置' : '部署'} {artifact.displayName}</h2><p className="mt-1 text-sm text-ink-secondary">可一次選擇多個 Target；每個 Target 都要明確選擇 scope。{isMcp && ' Wingman 只會寫入設定檔，不會啟動 MCP。'}</p><div className="mt-4 max-h-[48vh] space-y-2 overflow-y-auto">{targets.length === 0 ? <p className="text-sm text-ink-secondary">沒有相容 Target。</p> : targets.map(target => { const selection = selections[target.id]; return <div key={target.id} className="rounded-xl border border-border p-3"><label className="flex items-center gap-2 text-sm font-medium text-ink"><input type="checkbox" checked={Boolean(selection)} onChange={() => toggleTarget(target.id)} />{target.displayName}<span className="ml-auto text-xs text-ink-subtle">{target.isDetected ? '已偵測' : '未偵測'}</span></label>{selection && <div className="mt-3 grid gap-2 sm:grid-cols-[1fr_auto]"><select value={selection.scope ?? ''} onChange={event => setScope(target.id, event.target.value as MarketplaceDeploymentScope)} className="rounded-xl border border-border bg-surface px-3 py-2 text-sm text-ink"><option value="" disabled>選擇 scope…</option>{target.supportsGlobalScope && <option value="Global">Per-user global</option>}{target.supportsProjectScope && <option value="Project">Project</option>}</select>{selection.scope === 'Project' && <button type="button" onClick={() => void chooseProject(target.id)} className="rounded-xl border border-border px-3 py-2 text-sm text-ink hover:bg-surface-alt">選擇 project root</button>}{selection.scope === 'Project' && selection.projectPath && <p className="sm:col-span-2 break-all text-xs text-ink-subtle">{selection.projectPath}</p>}</div>}</div> })}</div>{preview && <div className="mt-3 rounded-xl border border-border bg-surface-alt p-3 text-sm"><p className="font-medium text-ink">執行預覽（尚未寫入檔案）</p>{preview.items.map(item => <p key={`${item.targetId}-${item.scope}`} className={cn('mt-1', ['Compatible', 'CompatibleNeedsUserInput', 'AlreadyDeployed'].includes(item.status) ? 'text-green-700' : 'text-red-600')}>{item.targetId} · {item.scope}：{item.status}{item.reason ? ` — ${item.reason}` : ''}</p>)}</div>}{error && <p className="mt-3 text-sm text-red-600">{error}</p>}<div className="mt-5 flex justify-end gap-2"><button type="button" onClick={onClose} className="rounded-xl px-3 py-2 text-sm text-ink-secondary">取消</button><button type="button" disabled={busy || targets.length === 0 || Boolean(preview?.items.some(item => !['Compatible', 'CompatibleNeedsUserInput', 'AlreadyDeployed'].includes(item.status)))} onClick={() => void deploy()} className="rounded-xl bg-brand px-3 py-2 text-sm font-medium text-white disabled:opacity-50">{busy ? '處理中…' : preview ? (isMcp ? '確認配置' : '確認部署') : '預覽變更'}</button></div></section></div>
}

function PluginPanel() {
  const [artifacts, setArtifacts] = useState<MarketplaceArtifact[]>([]); const [installations, setInstallations] = useState<MarketplacePluginInstallation[]>([]); const [preview, setPreview] = useState<MarketplacePluginPreview | null>(null); const [configuration, setConfiguration] = useState<MarketplacePluginConfiguration | null>(null); const [values, setValues] = useState<Record<string, string>>({}); const [error, setError] = useState<string | null>(null)
  const load = useCallback(async () => { try { const [items, installed] = await Promise.all([listMarketplaceArtifacts(), listMarketplacePlugins()]); setArtifacts(items.filter(item => item.kind === 'WingmanPlugin')); setInstallations(installed) } catch (cause) { setError(cause instanceof Error ? cause.message : String(cause)) } }, [])
  useEffect(() => { void load() }, [load])
  const install = async (artifactId: string) => { try { await installMarketplacePlugin(artifactId); await load() } catch (cause) { setError(cause instanceof Error ? cause.message : String(cause)) } }
  const toggle = async (installation: MarketplacePluginInstallation) => { try { await setMarketplacePluginEnabled(installation.id, !installation.enabled); await load() } catch (cause) { setError(cause instanceof Error ? cause.message : String(cause)) } }
  const inspect = async (installation: MarketplacePluginInstallation) => { try { setPreview(await getMarketplacePluginPreview(installation.id)) } catch (cause) { setError(cause instanceof Error ? cause.message : String(cause)) } }
  const configure = async (installation: MarketplacePluginInstallation) => { try { const next = await getMarketplacePluginConfiguration(installation.id); setConfiguration(next); setValues({}) } catch (cause) { setError(cause instanceof Error ? cause.message : String(cause)) } }
  const saveConfiguration = async () => { if (!configuration) return; try { await saveMarketplacePluginConfiguration(configuration.installationId, values); setConfiguration(null); await load() } catch (cause) { setError(cause instanceof Error ? cause.message : String(cause)) } }
  return <div className="space-y-3">{error && <div className="rounded-xl border border-red-200 bg-red-50 p-3 text-sm text-red-700">{error}</div>}{artifacts.length === 0 ? <EmptyState title="尚未匯入 Wingman Plugin" description="從 Sources 手動匯入符合 .codex-plugin/plugin.json 與 wingman.json 的 Plugin package。" /> : artifacts.map(artifact => { const installation = installations.find(item => item.artifactId === artifact.id); return <div key={artifact.id} className="rounded-2xl border border-border bg-surface p-4 flex items-center justify-between gap-4"><div><p className="font-medium text-ink">{artifact.displayName}</p><p className="mt-1 text-xs text-ink-subtle">{artifact.validationProfileId ?? '未驗證'}</p></div>{installation ? <div className="flex gap-2"><button type="button" onClick={() => void configure(installation)} className="rounded-xl border border-border px-3 py-2 text-sm text-ink hover:bg-surface-alt">設定</button><button type="button" onClick={() => void inspect(installation)} className="rounded-xl border border-border px-3 py-2 text-sm text-ink hover:bg-surface-alt">檢視能力</button><button type="button" onClick={() => void toggle(installation)} className={cn('rounded-xl px-3 py-2 text-sm font-medium', installation.enabled ? 'bg-brand text-white' : 'border border-border text-ink hover:bg-surface-alt')}>{installation.enabled ? 'Disable' : 'Enable'}</button></div> : <button type="button" onClick={() => void install(artifact.id)} className="rounded-xl border border-border px-3 py-2 text-sm text-ink hover:bg-surface-alt">Install</button>}</div> })}{preview && <div className="fixed inset-0 z-50 grid place-items-center bg-black/30 p-4" onMouseDown={() => setPreview(null)}><section onMouseDown={event => event.stopPropagation()} className="w-full max-w-lg rounded-2xl border border-border bg-surface p-5 shadow-xl"><div className="flex justify-between gap-3"><div><h2 className="font-semibold text-ink">{preview.pluginId}@{preview.version}</h2><p className="mt-1 text-sm text-ink-secondary">Enable 前能力預覽</p></div><button type="button" onClick={() => setPreview(null)} className="text-sm text-ink-secondary">關閉</button></div><p className="mt-4 text-sm text-ink-secondary">{preview.safetySummary}</p><div className="mt-4 grid gap-3 text-sm"><DetailField label="Skills" value={preview.skillPaths.join(', ') || '無'} /><DetailField label="MCP" value={preview.mcpPaths.join(', ') || '無'} /><DetailField label="Functions" value={preview.functionIds.join(', ') || '無'} /><DetailField label="Hooks" value={preview.hookIds.join(', ') || '無'} /></div></section></div>}{configuration && <div className="fixed inset-0 z-50 grid place-items-center bg-black/30 p-4" onMouseDown={() => setConfiguration(null)}><section onMouseDown={event => event.stopPropagation()} className="w-full max-w-lg rounded-2xl border border-border bg-surface p-5 shadow-xl"><div className="flex justify-between gap-3"><div><h2 className="font-semibold text-ink">{configuration.pluginId} 設定</h2><p className="mt-1 text-sm text-ink-secondary">設定值以 Windows 使用者加密保存；已保存的值不會回傳至畫面。</p></div><button type="button" onClick={() => setConfiguration(null)} className="text-sm text-ink-secondary">關閉</button></div>{configuration.fields.length === 0 ? <p className="mt-4 text-sm text-ink-secondary">這個 Plugin 沒有宣告需要設定的欄位。</p> : <div className="mt-4 space-y-3">{configuration.fields.map(field => <label key={field.name} className="block text-sm text-ink"><span className="flex gap-2">{field.name}{field.isSecret && <span className="text-xs text-amber-700">Secret</span>}{field.isConfigured && <span className="text-xs text-green-700">已設定</span>}</span><input type={field.isSecret ? 'password' : 'text'} value={values[field.name] ?? ''} onChange={event => setValues(current => ({ ...current, [field.name]: event.target.value }))} placeholder={field.isConfigured ? '留白代表保留既有設定' : '請填寫設定值'} className="mt-1 w-full rounded-xl border border-border bg-surface px-3 py-2 text-sm text-ink" /></label>)}</div>}<div className="mt-5 flex justify-end gap-2"><button type="button" onClick={() => setConfiguration(null)} className="rounded-xl px-3 py-2 text-sm text-ink-secondary">取消</button><button type="button" onClick={() => void saveConfiguration()} className="rounded-xl bg-brand px-3 py-2 text-sm font-medium text-white">儲存</button></div></section></div>}</div>
}
