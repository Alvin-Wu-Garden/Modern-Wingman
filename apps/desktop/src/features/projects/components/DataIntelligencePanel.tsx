import { useEffect, useState } from 'react'
import {
  AlertTriangle,
  Ban,
  Check,
  ChevronDown,
  Database,
  FileScan,
  Loader2,
  Pencil,
  Plus,
  RefreshCw,
  ShieldCheck,
  X,
} from 'lucide-react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import {
  getDatabaseRuntimeStatus,
  listDomainGlossary,
  proposeDomainGlossaryEntry,
  reviewDomainGlossaryEntry,
  scanProjectData,
  type DatabaseRuntimeProviderStatus,
  type DomainGlossaryEntry,
  type GlossarySensitivity,
  type StaticDataScanResult,
} from '@/services/agent-api/projects'

const sensitivities: GlossarySensitivity[] = [
  'Unknown',
  'Public',
  'Internal',
  'Confidential',
  'PersonalData',
  'Secret',
]

const sensitivityLabel: Record<GlossarySensitivity, string> = {
  Unknown: '未分類',
  Public: '公開',
  Internal: '內部',
  Confidential: '機密',
  PersonalData: '個資',
  Secret: '秘密',
}

const statusPresentation = {
  Proposed: { label: '待 IT 審核', className: 'bg-amber-50 text-amber-700' },
  Confirmed: { label: '已確認', className: 'bg-green-50 text-green-700' },
  Rejected: { label: '已拒絕', className: 'bg-surface-alt text-ink-subtle' },
} as const

const messageOf = (error: unknown) => error instanceof Error ? error.message : String(error)
const splitList = (value: string) => value.split(/[,，\n]/).map((item) => item.trim()).filter(Boolean)

interface GlossaryDraft {
  definition: string
  aliases: string
  sensitivity: GlossarySensitivity
  comment: string
}

function RuntimeStatusCard({ providers, loading, onRefresh }: {
  providers: DatabaseRuntimeProviderStatus[]
  loading: boolean
  onRefresh: () => void
}) {
  return (
    <section className="rounded-2xl border border-border bg-surface p-4">
      <div className="flex items-start justify-between gap-3">
        <div>
          <div className="flex items-center gap-2">
            <Database className="h-4 w-4 text-brand" />
            <h3 className="text-sm font-semibold text-ink">Database Runtime Plugin</h3>
          </div>
          <p className="mt-1 text-xs text-ink-subtle">只顯示 Plugin 宣告的能力與衍生狀態，不顯示設定原值、資料列或連線資訊。</p>
        </div>
        <Button type="button" variant="outline" size="sm" onClick={onRefresh} isLoading={loading}>
          <RefreshCw className="h-3.5 w-3.5" />重新整理
        </Button>
      </div>

      {providers.length === 0 && !loading ? (
        <div className="mt-3 rounded-xl border border-amber-200 bg-amber-50/50 px-3 py-2.5">
          <p className="text-xs font-medium text-amber-800">未偵測到可用的唯讀 Database Runtime capability</p>
          <p className="mt-1 text-[11px] text-amber-700">請先在 Marketplace 安裝並啟用提供標準唯讀能力的 Wingman Plugin。</p>
        </div>
      ) : (
        <div className="mt-3 grid gap-2 lg:grid-cols-2">
          {providers.map((provider) => (
            <div key={`${provider.pluginId}:${provider.databaseIdentity}`} className="rounded-xl border border-border bg-surface-alt p-3">
              <div className="flex items-center gap-2">
                <span className={cn('h-2 w-2 rounded-full', provider.available ? 'bg-green-500' : 'bg-red-500')} />
                <p className="min-w-0 truncate text-xs font-semibold text-ink">{provider.pluginId}</p>
                <span className={cn('ml-auto rounded px-1.5 py-0.5 text-[10px]', provider.available ? 'bg-green-50 text-green-700' : 'bg-red-50 text-red-700')}>
                  {provider.available ? '可用' : '不可用'}
                </span>
              </div>
              <p className="mt-1 truncate text-[11px] text-ink-secondary">資料庫識別：{provider.databaseIdentity}</p>
              <div className="mt-2 flex flex-wrap gap-1">
                {provider.capabilities.map((capability) => (
                  <span key={capability} className="rounded-md bg-surface px-1.5 py-0.5 text-[10px] text-ink-secondary">{capability}</span>
                ))}
              </div>
              {provider.error && <p className="mt-2 text-[11px] text-red-700">衍生狀態：{provider.error}</p>}
            </div>
          ))}
        </div>
      )}
    </section>
  )
}

function ScanResult({ result }: { result: StaticDataScanResult }) {
  return (
    <div className="mt-3 space-y-2">
      <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
        {[
          ['資料節點', result.nodeCount],
          ['關聯', result.edgeCount],
          ['已掃描檔案', result.scannedFiles.length],
          ['略過檔案', result.skippedFiles.length],
        ].map(([label, count]) => (
          <div key={String(label)} className="rounded-xl bg-surface-alt px-3 py-2">
            <p className="text-[11px] text-ink-subtle">{label}</p>
            <p className="mt-0.5 text-lg font-semibold text-ink">{count}</p>
          </div>
        ))}
      </div>
      {result.capabilityGaps.length > 0 && (
        <div className="rounded-xl border border-amber-200 bg-amber-50/50 p-3">
          <p className="flex items-center gap-1.5 text-xs font-medium text-amber-800"><AlertTriangle className="h-3.5 w-3.5" />Capability gaps</p>
          {result.capabilityGaps.map((gap, index) => <p key={`${gap}-${index}`} className="mt-1 text-[11px] text-amber-700">• {gap}</p>)}
        </div>
      )}
      {(result.diagnostics.length > 0 || result.skippedFiles.length > 0) && (
        <details className="rounded-xl border border-border bg-surface-alt px-3 py-2">
          <summary className="flex cursor-pointer list-none items-center gap-2 text-xs font-medium text-ink">
            <ChevronDown className="h-3.5 w-3.5" />掃描診斷（{result.diagnostics.length + result.skippedFiles.length}）
          </summary>
          <div className="mt-2 max-h-56 space-y-1 overflow-y-auto text-[11px] text-ink-secondary">
            {result.diagnostics.map((item, index) => (
              <p key={`${item.filePath}:${item.adapterId}:${index}`}><code className="font-mono">{item.filePath}</code> · {item.severity} · {item.message}</p>
            ))}
            {result.skippedFiles.map((item, index) => (
              <p key={`${item.path}:${index}`}><code className="font-mono">{item.path}</code> · 略過{item.reason ? `：${item.reason}` : ''}</p>
            ))}
          </div>
        </details>
      )}
    </div>
  )
}

export function DataIntelligencePanel({ projectId }: { projectId: string }) {
  const [providers, setProviders] = useState<DatabaseRuntimeProviderStatus[]>([])
  const [glossary, setGlossary] = useState<DomainGlossaryEntry[]>([])
  const [scanResult, setScanResult] = useState<StaticDataScanResult | null>(null)
  const [loading, setLoading] = useState(true)
  const [refreshingRuntime, setRefreshingRuntime] = useState(false)
  const [scanning, setScanning] = useState(false)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [showProposal, setShowProposal] = useState(false)
  const [term, setTerm] = useState('')
  const [definition, setDefinition] = useState('')
  const [aliases, setAliases] = useState('')
  const [evidenceKeys, setEvidenceKeys] = useState('')
  const [sensitivity, setSensitivity] = useState<GlossarySensitivity>('Internal')
  const [editingId, setEditingId] = useState<string | null>(null)
  const [draft, setDraft] = useState<GlossaryDraft | null>(null)

  const reloadGlossary = async () => setGlossary(await listDomainGlossary(projectId))
  const reloadRuntime = async (forceRefresh = false) => setProviders(await getDatabaseRuntimeStatus(projectId, forceRefresh))

  useEffect(() => {
    let active = true
    setLoading(true)
    setError(null)
    setScanResult(null)
    Promise.all([listDomainGlossary(projectId), getDatabaseRuntimeStatus(projectId)])
      .then(([entries, statuses]) => {
        if (!active) return
        setGlossary(entries)
        setProviders(statuses)
      })
      .catch((reason) => { if (active) setError(messageOf(reason)) })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [projectId])

  const refreshRuntime = async () => {
    setRefreshingRuntime(true)
    setError(null)
    try {
      await reloadRuntime(true)
      setNotice('Database Runtime Plugin 狀態已重新整理。')
    } catch (reason) { setError(messageOf(reason)) }
    finally { setRefreshingRuntime(false) }
  }

  const scan = async () => {
    setScanning(true)
    setError(null)
    try {
      const result = await scanProjectData(projectId)
      setScanResult(result)
      setNotice(`靜態資料掃描完成：${result.nodeCount} 個節點、${result.edgeCount} 個關聯。`)
    } catch (reason) { setError(messageOf(reason)) }
    finally { setScanning(false) }
  }

  const propose = async () => {
    const evidence = splitList(evidenceKeys)
    if (!term.trim() || !definition.trim() || evidence.length === 0) return
    setSaving(true)
    setError(null)
    try {
      await proposeDomainGlossaryEntry(projectId, {
        term: term.trim(),
        definition: definition.trim(),
        aliases: splitList(aliases),
        sensitivity,
        evidenceKeys: evidence,
        proposedBy: 'IT',
      })
      await reloadGlossary()
      setTerm('')
      setDefinition('')
      setAliases('')
      setEvidenceKeys('')
      setSensitivity('Internal')
      setShowProposal(false)
      setNotice('領域詞彙提案已建立，等待 IT 審核。')
    } catch (reason) { setError(messageOf(reason)) }
    finally { setSaving(false) }
  }

  const beginReview = (entry: DomainGlossaryEntry) => {
    setEditingId(entry.id)
    setDraft({
      definition: entry.definition,
      aliases: entry.aliases.join('、'),
      sensitivity: entry.sensitivity,
      comment: '',
    })
  }

  const review = async (entry: DomainGlossaryEntry, confirm: boolean) => {
    if (!draft) return
    setSaving(true)
    setError(null)
    try {
      await reviewDomainGlossaryEntry(projectId, entry.id, {
        confirm,
        reviewedBy: 'IT',
        definition: draft.definition.trim(),
        aliases: splitList(draft.aliases),
        sensitivity: draft.sensitivity,
        comment: draft.comment.trim() || undefined,
      })
      await reloadGlossary()
      setEditingId(null)
      setDraft(null)
      setNotice(confirm ? '詞彙已確認並套用修正。' : '詞彙提案已拒絕。')
    } catch (reason) { setError(messageOf(reason)) }
    finally { setSaving(false) }
  }

  if (loading) return <div className="flex flex-1 items-center justify-center gap-2 text-sm text-ink-subtle"><Loader2 className="h-4 w-4 animate-spin" />載入資料情報…</div>

  return (
    <div className="flex-1 overflow-y-auto p-6">
      <div className="mx-auto max-w-5xl space-y-4">
        {(notice || error) && (
          <div className={cn('flex items-center justify-between rounded-xl border px-3 py-2.5 text-xs', error ? 'border-red-200 bg-red-50 text-red-700' : 'border-green-200 bg-green-50 text-green-700')}>
            <p>{error ?? notice}</p>
            <button type="button" aria-label="關閉提示" onClick={() => { setError(null); setNotice(null) }}><X className="h-3.5 w-3.5" /></button>
          </div>
        )}

        <section className="rounded-2xl border border-border bg-surface p-4">
          <div className="flex items-start justify-between gap-3">
            <div>
              <div className="flex items-center gap-2"><FileScan className="h-4 w-4 text-brand" /><h3 className="text-sm font-semibold text-ink">靜態資料結構掃描</h3></div>
              <p className="mt-1 text-xs text-ink-subtle">手動從 migration、DDL、SQL 與可辨識的 ORM artifact 建立資料結構證據。</p>
            </div>
            <Button type="button" size="sm" onClick={scan} isLoading={scanning}>開始掃描</Button>
          </div>
          {scanResult ? <ScanResult result={scanResult} /> : <p className="mt-3 rounded-xl bg-surface-alt px-3 py-2 text-xs text-ink-secondary">尚未在本次畫面執行靜態資料掃描。</p>}
        </section>

        <RuntimeStatusCard providers={providers} loading={refreshingRuntime} onRefresh={() => void refreshRuntime()} />

        <section className="rounded-2xl border border-border bg-surface p-4">
          <div className="flex items-start justify-between gap-3">
            <div>
              <div className="flex items-center gap-2"><ShieldCheck className="h-4 w-4 text-brand" /><h3 className="text-sm font-semibold text-ink">Domain Glossary</h3></div>
              <p className="mt-1 text-xs text-ink-subtle">Agent 可以提案，但只有 IT 確認後的領域詞彙才會成為分析證據。</p>
            </div>
            <Button type="button" variant="outline" size="sm" onClick={() => setShowProposal((value) => !value)}><Plus className="h-3.5 w-3.5" />新增提案</Button>
          </div>

          {showProposal && (
            <div className="mt-3 grid gap-2 rounded-xl border border-brand/20 bg-brand/5 p-3 sm:grid-cols-2">
              <label className="text-xs text-ink">詞彙<input value={term} onChange={(event) => setTerm(event.target.value)} className="mt-1 w-full rounded-lg border border-border bg-surface px-2.5 py-2 text-xs" /></label>
              <label className="text-xs text-ink">敏感度<select value={sensitivity} onChange={(event) => setSensitivity(event.target.value as GlossarySensitivity)} className="mt-1 w-full rounded-lg border border-border bg-surface px-2.5 py-2 text-xs">{sensitivities.map((value) => <option key={value} value={value}>{sensitivityLabel[value]}</option>)}</select></label>
              <label className="text-xs text-ink sm:col-span-2">定義<textarea value={definition} onChange={(event) => setDefinition(event.target.value)} rows={2} className="mt-1 w-full resize-y rounded-lg border border-border bg-surface px-2.5 py-2 text-xs" /></label>
              <label className="text-xs text-ink sm:col-span-2">別名（逗號或換行分隔）<input value={aliases} onChange={(event) => setAliases(event.target.value)} className="mt-1 w-full rounded-lg border border-border bg-surface px-2.5 py-2 text-xs" /></label>
              <label className="text-xs text-ink sm:col-span-2">證據鍵（必填，逗號或換行分隔）<textarea value={evidenceKeys} onChange={(event) => setEvidenceKeys(event.target.value)} rows={2} placeholder="例如 table:default.orders 或完整 Symbol key" className="mt-1 w-full resize-y rounded-lg border border-border bg-surface px-2.5 py-2 font-mono text-xs" /><span className="mt-1 block text-[10px] text-ink-subtle">確認後會在圖譜建立 SUPPORTED_BY 關聯；請使用知識圖譜中實際存在的 key。</span></label>
              <div className="flex justify-end gap-2 sm:col-span-2"><Button type="button" variant="ghost" size="sm" onClick={() => setShowProposal(false)}>取消</Button><Button type="button" size="sm" onClick={() => void propose()} isLoading={saving} disabled={!term.trim() || !definition.trim() || splitList(evidenceKeys).length === 0}>建立提案</Button></div>
            </div>
          )}

          <div className="mt-3 space-y-2">
            {glossary.length === 0 ? <p className="rounded-xl bg-surface-alt px-3 py-3 text-center text-xs text-ink-subtle">尚無領域詞彙提案。</p> : glossary.map((entry) => {
              const presentation = statusPresentation[entry.status]
              const isEditing = entry.id === editingId && draft
              return (
                <article key={entry.id} className="rounded-xl border border-border bg-surface-alt p-3">
                  <div className="flex flex-wrap items-center gap-2">
                    <p className="text-sm font-semibold text-ink">{entry.term}</p>
                    <span className={cn('rounded px-1.5 py-0.5 text-[10px]', presentation.className)}>{presentation.label}</span>
                    <span className="rounded bg-surface px-1.5 py-0.5 text-[10px] text-ink-secondary">{sensitivityLabel[entry.sensitivity]}</span>
                    <span className="ml-auto text-[10px] text-ink-subtle">更新於 {new Date(entry.updatedAt).toLocaleString()}</span>
                  </div>
                  {isEditing ? (
                    <div className="mt-2 grid gap-2 sm:grid-cols-2">
                      <label className="text-[11px] text-ink sm:col-span-2">確認後定義<textarea value={draft.definition} onChange={(event) => setDraft({ ...draft, definition: event.target.value })} rows={2} className="mt-1 w-full rounded-lg border border-border bg-surface px-2.5 py-2 text-xs" /></label>
                      <label className="text-[11px] text-ink">別名<input value={draft.aliases} onChange={(event) => setDraft({ ...draft, aliases: event.target.value })} className="mt-1 w-full rounded-lg border border-border bg-surface px-2.5 py-2 text-xs" /></label>
                      <label className="text-[11px] text-ink">敏感度<select value={draft.sensitivity} onChange={(event) => setDraft({ ...draft, sensitivity: event.target.value as GlossarySensitivity })} className="mt-1 w-full rounded-lg border border-border bg-surface px-2.5 py-2 text-xs">{sensitivities.map((value) => <option key={value} value={value}>{sensitivityLabel[value]}</option>)}</select></label>
                      <label className="text-[11px] text-ink sm:col-span-2">審核備註（選填）<input value={draft.comment} onChange={(event) => setDraft({ ...draft, comment: event.target.value })} className="mt-1 w-full rounded-lg border border-border bg-surface px-2.5 py-2 text-xs" /></label>
                      <div className="flex flex-wrap justify-end gap-2 sm:col-span-2">
                        <Button type="button" variant="ghost" size="sm" onClick={() => { setEditingId(null); setDraft(null) }}>取消</Button>
                        <Button type="button" variant="outline" size="sm" onClick={() => void review(entry, false)} isLoading={saving}><Ban className="h-3.5 w-3.5" />拒絕</Button>
                        <Button type="button" size="sm" onClick={() => void review(entry, true)} isLoading={saving} disabled={!draft.definition.trim()}><Check className="h-3.5 w-3.5" />確認並套用修正</Button>
                      </div>
                    </div>
                  ) : (
                    <>
                      <p className="mt-1.5 text-xs text-ink-secondary">{entry.definition}</p>
                      {entry.aliases.length > 0 && <p className="mt-1 text-[11px] text-ink-subtle">別名：{entry.aliases.join('、')}</p>}
                      {entry.evidenceKeys.length > 0 && <p className="mt-1 break-all font-mono text-[10px] text-ink-subtle">證據：{entry.evidenceKeys.join('、')}</p>}
                      {entry.reviewComment && <p className="mt-1 text-[11px] text-ink-subtle">審核備註：{entry.reviewComment}</p>}
                      {entry.status === 'Proposed' && <div className="mt-2 flex justify-end"><Button type="button" variant="outline" size="sm" onClick={() => beginReview(entry)}><Pencil className="h-3.5 w-3.5" />IT 審核／修正</Button></div>}
                    </>
                  )}
                </article>
              )
            })}
          </div>
        </section>
      </div>
    </div>
  )
}
