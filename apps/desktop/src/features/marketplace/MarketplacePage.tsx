import { useCallback, useEffect, useMemo, useState } from 'react'
import { open } from '@tauri-apps/plugin-dialog'
import {
  Download,
  FolderUp,
  Loader2,
  PackageSearch,
  RefreshCw,
  Search,
  Server,
  Sparkles,
} from 'lucide-react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import {
  deployMarketplaceMcp,
  deployMarketplaceSkills,
  importGitHubRepository,
  importMarketplaceFolder,
  listMarketplace,
  listMarketplaceArtifacts,
  listMarketplaceTargets,
  previewMarketplaceMcp,
  previewMarketplaceSkills,
  refreshMarketplace,
  removeMarketplaceMcp,
  removeMarketplaceSkills,
  type MarketplaceArtifact,
  type MarketplaceArtifactKind,
  type MarketplaceDiscoveryRecord,
  type MarketplaceTargetDescriptor,
  type MarketplaceDeploymentPlan,
} from '@/services/agent-api/marketplace'

type MarketplaceTab = 'skills' | 'mcp' | 'installed'

const tabs = [
  { id: 'skills' as const, label: 'Agent Skills', icon: Sparkles },
  { id: 'mcp' as const, label: 'MCP Servers', icon: Server },
  { id: 'installed' as const, label: '已匯入', icon: Download },
]

/**
 * Marketplace 只保留 Agent Skill 與 MCP Server。
 * 匯入後以實體複製部署到外部 Agent；Modern Wingman 本身不執行 Skill script 或 MCP。
 */
export function MarketplacePage() {
  const [tab, setTab] = useState<MarketplaceTab>('skills')
  const [query, setQuery] = useState('')
  const [discoveries, setDiscoveries] = useState<MarketplaceDiscoveryRecord[]>([])
  const [artifacts, setArtifacts] = useState<MarketplaceArtifact[]>([])
  const [targets, setTargets] = useState<MarketplaceTargetDescriptor[]>([])
  const [selectedArtifact, setSelectedArtifact] = useState<string | null>(null)
  const [selectedTargets, setSelectedTargets] = useState<string[]>([])
  const [scope, setScope] = useState<'Global' | 'Project'>('Global')
  const [projectPath, setProjectPath] = useState<string | null>(null)
  const [preview, setPreview] = useState<MarketplaceDeploymentPlan | null>(null)
  const [loading, setLoading] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const loadDiscoveries = useCallback(async () => {
    if (tab === 'installed') return
    setLoading(true)
    try {
      const kind: MarketplaceArtifactKind = tab === 'skills' ? 'Skill' : 'McpServer'
      const result = await listMarketplace({ kind, search: query })
      setDiscoveries(result.items)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setLoading(false)
    }
  }, [query, tab])

  const loadInstalled = useCallback(async () => {
    const [installed, availableTargets] = await Promise.all([
      listMarketplaceArtifacts(),
      listMarketplaceTargets(),
    ])
    setArtifacts(installed.filter(
      (artifact) => artifact.kind === 'Skill' || artifact.kind === 'McpServer',
    ))
    setTargets(availableTargets)
  }, [])

  useEffect(() => {
    void loadDiscoveries()
    void loadInstalled()
  }, [loadDiscoveries, loadInstalled])

  const activeArtifact = artifacts.find((artifact) => artifact.id === selectedArtifact)
  const compatibleTargets = useMemo(() => {
    if (!activeArtifact) return []
    return targets.filter((target) => {
      if (activeArtifact.kind === 'Skill') {
        return target.supportsSkill
          && (scope === 'Global'
            ? target.supportsGlobalScope
            : target.supportsProjectScope)
      }
      return target.supportsMcp
        && (scope === 'Global'
          ? target.supportsGlobalMcp
          : target.supportsProjectMcp)
    })
  }, [activeArtifact, scope, targets])

  /** 組合預覽與實際部署共用的 request，避免兩條流程產生不同 scope。 */
  const buildDeploymentRequests = () => {
    if (!activeArtifact || selectedTargets.length === 0) return []
    return selectedTargets.map((targetId) => ({
      artifactId: activeArtifact.id,
      targetId,
      scope,
      projectPath: scope === 'Project' ? projectPath : null,
    }))
  }

  /** 專案 scope 使用實際資料夾選擇器，不讓使用者手動輸入易錯路徑。 */
  const chooseProjectFolder = async () => {
    const folder = await open({ directory: true, multiple: false })
    if (typeof folder === 'string') {
      setProjectPath(folder)
      setPreview(null)
    }
  }

  const importFolder = async () => {
    const folder = await open({ directory: true, multiple: false })
    if (typeof folder !== 'string') return
    setLoading(true)
    try {
      const result = await importMarketplaceFolder(folder)
      setNotice(`已匯入 ${result.artifacts.length} 個項目。`)
      await loadInstalled()
      setTab('installed')
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setLoading(false)
    }
  }

  const importDiscovery = async (item: MarketplaceDiscoveryRecord) => {
    setLoading(true)
    try {
      const result = await importGitHubRepository(item.canonicalUrl)
      setNotice(`已匯入 ${result.import.artifacts.length} 個項目。`)
      await loadInstalled()
      setTab('installed')
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setLoading(false)
    }
  }

  const previewDeployment = async () => {
    if (!activeArtifact || (scope === 'Project' && !projectPath)) return
    setLoading(true)
    setError(null)
    try {
      const requests = buildDeploymentRequests()
      const result = activeArtifact.kind === 'Skill'
        ? await previewMarketplaceSkills(requests)
        : await previewMarketplaceMcp(requests)
      setPreview(result)
      setNotice('預覽完成；確認目的地與衝突狀態後再執行部署。')
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setLoading(false)
    }
  }

  /** 只有完成同一批 request 的預覽後才提供寫入按鈕。 */
  const deploy = async () => {
    if (!activeArtifact || !preview) return
    setLoading(true)
    setError(null)
    try {
      const requests = buildDeploymentRequests()
      const result = activeArtifact.kind === 'Skill'
        ? await deployMarketplaceSkills(requests)
        : await deployMarketplaceMcp(requests)
      const succeeded = result.results.filter((item) =>
        ['Deployed', 'Configured', 'NeedsUserInput'].includes(item.status)).length
      setNotice(`部署完成：${succeeded}/${result.results.length} 個目標。`)
      setPreview(null)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setLoading(false)
    }
  }

  /** 移除時仍由後端的 hash／managed record 保護使用者自行修改的檔案。 */
  const removeDeployment = async () => {
    if (!activeArtifact) return
    setLoading(true)
    setError(null)
    try {
      const result = activeArtifact.kind === 'Skill'
        ? await removeMarketplaceSkills(activeArtifact.id)
        : await removeMarketplaceMcp(activeArtifact.id)
      const removed = result.results.filter((item) => item.status === 'Removed').length
      setNotice(`已移除 ${removed} 個由 Modern Wingman 管理的部署。`)
      setPreview(null)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setLoading(false)
    }
  }

  const refresh = async () => {
    setLoading(true)
    try {
      const result = await refreshMarketplace()
      setNotice(`Marketplace 已更新：新增 ${result.newCount}、更新 ${result.updatedCount}。`)
      await loadDiscoveries()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col overflow-hidden bg-surface-alt">
      <header className="border-b border-border bg-surface px-6 py-4">
        <div className="flex items-center justify-between">
          <div>
            <h1 className="text-lg font-semibold text-ink">Marketplace</h1>
            <p className="mt-0.5 text-xs text-ink-subtle">
              將 Agent Skill 與 MCP Server 實體複製到外部 Agent。
            </p>
          </div>
          <div className="flex gap-2">
            <Button variant="outline" onClick={() => void importFolder()}>
              <FolderUp className="mr-2 h-4 w-4" />
              匯入資料夾
            </Button>
            <Button variant="outline" onClick={() => void refresh()} disabled={loading}>
              <RefreshCw className={cn('mr-2 h-4 w-4', loading && 'animate-spin')} />
              更新清單
            </Button>
          </div>
        </div>
        <div className="mt-4 flex gap-1">
          {tabs.map(({ id, label, icon: Icon }) => (
            <button
              key={id}
              type="button"
              onClick={() => setTab(id)}
              className={cn(
                'flex items-center gap-2 rounded-lg px-3 py-2 text-sm',
                tab === id ? 'bg-brand text-white' : 'text-ink-secondary hover:bg-surface-alt',
              )}
            >
              <Icon className="h-4 w-4" />
              {label}
            </button>
          ))}
        </div>
      </header>

      {(notice || error) && (
        <div className={cn(
          'border-b px-6 py-2 text-xs',
          error ? 'border-red-200 bg-red-50 text-red-700' : 'border-border bg-brand/5 text-brand',
        )}>
          {error ?? notice}
        </div>
      )}

      {tab !== 'installed' ? (
        <div className="min-h-0 flex-1 overflow-y-auto p-6">
          <div className="relative mb-4 max-w-xl">
            <Search className="absolute left-3 top-2.5 h-4 w-4 text-ink-subtle" />
            <input
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              placeholder="搜尋名稱或描述…"
              className="w-full rounded-xl border border-border bg-surface py-2 pl-9 pr-3 text-sm"
            />
          </div>
          {loading && <Loader2 className="h-5 w-5 animate-spin text-brand" />}
          <div className="grid gap-3 lg:grid-cols-2">
            {discoveries.map((item) => (
              <article key={item.id} className="rounded-xl border border-border bg-surface p-4">
                <div className="flex items-start gap-3">
                  <PackageSearch className="mt-0.5 h-5 w-5 text-brand" />
                  <div className="min-w-0 flex-1">
                    <h2 className="truncate text-sm font-semibold text-ink">{item.name}</h2>
                    <p className="mt-1 line-clamp-2 text-xs leading-5 text-ink-secondary">
                      {item.description ?? '沒有說明'}
                    </p>
                    <p className="mt-2 text-[11px] text-ink-subtle">
                      ★ {item.stars.toLocaleString()} · {item.owner}/{item.repository}
                    </p>
                  </div>
                  <Button size="sm" onClick={() => void importDiscovery(item)} disabled={loading}>
                    匯入
                  </Button>
                </div>
              </article>
            ))}
          </div>
        </div>
      ) : (
        <div className="flex min-h-0 flex-1">
          <div className="w-80 shrink-0 overflow-y-auto border-r border-border bg-surface p-3">
            {artifacts.map((artifact) => (
              <button
                key={artifact.id}
                type="button"
                onClick={() => {
                  setSelectedArtifact(artifact.id)
                  setSelectedTargets([])
                  setPreview(null)
                }}
                className={cn(
                  'mb-1 w-full rounded-lg px-3 py-2 text-left',
                  selectedArtifact === artifact.id
                    ? 'bg-brand/10 text-brand'
                    : 'text-ink-secondary hover:bg-surface-alt',
                )}
              >
                <p className="truncate text-sm font-medium">{artifact.displayName}</p>
                <p className="text-xs opacity-70">{artifact.kind === 'Skill' ? 'Agent Skill' : 'MCP Server'}</p>
              </button>
            ))}
          </div>
          <div className="min-w-0 flex-1 overflow-y-auto p-6">
            {!activeArtifact ? (
              <p className="text-sm text-ink-subtle">請選擇已匯入項目。</p>
            ) : (
              <div className="max-w-2xl">
                <h2 className="text-base font-semibold text-ink">{activeArtifact.displayName}</h2>
                <p className="mt-1 break-all text-xs text-ink-subtle">{activeArtifact.snapshotPath}</p>
                <h3 className="mt-6 text-sm font-semibold text-ink">部署目標</h3>
                <div className="mt-3 flex flex-wrap items-center gap-3 text-sm text-ink-secondary">
                  <label className="flex items-center gap-1.5">
                    <input
                      type="radio"
                      checked={scope === 'Global'}
                      onChange={() => {
                        setScope('Global')
                        setSelectedTargets([])
                        setPreview(null)
                      }}
                    />
                    使用者層
                  </label>
                  <label className="flex items-center gap-1.5">
                    <input
                      type="radio"
                      checked={scope === 'Project'}
                      onChange={() => {
                        setScope('Project')
                        setSelectedTargets([])
                        setPreview(null)
                      }}
                    />
                    專案層
                  </label>
                  {scope === 'Project' && (
                    <Button size="sm" variant="outline" onClick={() => void chooseProjectFolder()}>
                      {projectPath ? '更換專案資料夾' : '選擇專案資料夾'}
                    </Button>
                  )}
                </div>
                {scope === 'Project' && projectPath && (
                  <p className="mt-2 break-all text-xs text-ink-subtle">{projectPath}</p>
                )}
                <div className="mt-3 grid gap-2 sm:grid-cols-2">
                  {compatibleTargets.map((target) => (
                    <label
                      key={target.id}
                      className="flex items-start gap-2 rounded-lg border border-border bg-surface p-3"
                    >
                      <input
                        type="checkbox"
                        checked={selectedTargets.includes(target.id)}
                        onChange={(event) => setSelectedTargets((current) =>
                          event.target.checked
                            ? [...current, target.id]
                            : current.filter((id) => id !== target.id))}
                        onClick={() => setPreview(null)}
                      />
                      <span>
                        <span className="block text-sm font-medium text-ink">{target.displayName}</span>
                        <span className="block text-xs text-ink-subtle">
                          {target.isDetected ? '已偵測' : target.detectionReason ?? '未偵測'}
                        </span>
                      </span>
                    </label>
                  ))}
                </div>
                {preview && (
                  <div className="mt-4 space-y-2 rounded-xl border border-border bg-surface p-3">
                    {preview.items.map((item) => (
                      <div key={`${item.targetId}-${item.scope}`} className="text-xs">
                        <span className="font-medium text-ink">{item.targetId}</span>
                        <span className="ml-2 text-ink-secondary">{item.status}</span>
                        {item.targetPath && (
                          <p className="mt-0.5 break-all text-ink-subtle">{item.targetPath}</p>
                        )}
                        {item.reason && <p className="mt-0.5 text-amber-700">{item.reason}</p>}
                      </div>
                    ))}
                  </div>
                )}
                <div className="mt-5 flex gap-2">
                  <Button
                    variant="outline"
                    disabled={
                      selectedTargets.length === 0
                      || loading
                      || (scope === 'Project' && !projectPath)
                    }
                    onClick={() => void previewDeployment()}
                  >
                    預覽部署
                  </Button>
                  <Button
                    disabled={!preview || loading}
                    onClick={() => void deploy()}
                  >
                    {loading && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                    確認部署
                  </Button>
                  <Button
                    variant="ghost"
                    disabled={loading}
                    onClick={() => void removeDeployment()}
                  >
                    移除受管理部署
                  </Button>
                </div>
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  )
}
