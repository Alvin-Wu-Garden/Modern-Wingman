import { useEffect, useMemo, useState } from 'react'
import { AlertTriangle, Cpu, FolderOpen, Package, Plus, RefreshCw, Shield, ShieldAlert, Tag, Trash2, X } from 'lucide-react'
import { open } from '@tauri-apps/plugin-dialog'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/modal'
import { useAppStore } from '@/app/store'
import { useLibraryStore } from '../store/useLibraryStore'
import type { InstallToLibraryParams, LibrarySkill, RiskReport } from '@modern-wingman/contracts'
import { listSkillRuntimeStatus, refreshWingmanSkills, type SkillRuntimeStatus } from '@/services/agent-api/skills-runtime'

function RiskBadge({ level }: { level: string }) {
  if (level === 'low') {
    return (
      <span className="inline-flex items-center gap-1 text-xs px-2 py-0.5 rounded-full bg-emerald-50 text-emerald-600">
        <Shield className="w-3 h-3" /> 低風險
      </span>
    )
  }
  const isHigh = level === 'high'
  return (
    <span
      className={cn(
        'inline-flex items-center gap-1 text-xs px-2 py-0.5 rounded-full',
        isHigh ? 'bg-red-50 text-red-600' : 'bg-amber-50 text-amber-600',
      )}
    >
      <ShieldAlert className="w-3 h-3" /> {isHigh ? '高風險' : '中風險'}
    </span>
  )
}

/** Risk report shown after installation (P3 quality gate). */
function RiskReportModal({ report, onClose, onConfirm, busy }: { report: RiskReport; onClose: () => void; onConfirm:()=>Promise<void>;busy:boolean }) {
  return (
    <Modal open onOpenChange={(o) => !o && onClose()} title="安裝前風險掃描" size="md">
      <div className="space-y-3">
        <RiskBadge level={report.level} />
        {report.findings.length === 0 ? (
          <p className="text-sm text-ink-secondary">未發現可疑內容。</p>
        ) : (
          <ul className="space-y-2">
            {report.findings.map((f, i) => (
              <li key={i} className="rounded-xl border border-border bg-surface-alt p-3">
                <div className="flex items-center gap-2">
                  <AlertTriangle
                    className={cn('w-4 h-4', f.severity === 'high' ? 'text-red-500' : 'text-amber-500')}
                  />
                  <span className="text-sm font-medium text-ink">{f.message}</span>
                </div>
                <p className="text-xs text-ink-subtle mt-1.5 font-mono break-all">{f.excerpt}</p>
              </li>
            ))}
          </ul>
        )}
        <p className="text-xs text-ink-subtle">
          Skill 內容可能引導 Agent 執行非預期操作，請確認以上片段是否合理。
        </p>
        <div className="flex justify-end gap-2">
          <Button variant="ghost" size="sm" onClick={onClose} disabled={busy}>取消</Button>
          <Button variant="primary" size="sm" onClick={()=>void onConfirm()} isLoading={busy}>確認安裝</Button>
        </div>
      </div>
    </Modal>
  )
}

/** Central Skill Library management tab. */
export function LibraryTab() {
  const githubPat = useAppStore((s) => s.githubPat)
  const { skills, links, loading, error, fetchAll, installSkill, previewSkill, removeSkill, setTags, clearError } =
    useLibraryStore()

  const [query, setQuery] = useState('')
  const [riskReport, setRiskReport] = useState<RiskReport | null>(null)
  const [pendingInstall,setPendingInstall]=useState<InstallToLibraryParams|null>(null)
  const [importUrl, setImportUrl] = useState('')
  const [showImport, setShowImport] = useState(false)
  const [busy, setBusy] = useState(false)
  const [tagEditing, setTagEditing] = useState<LibrarySkill | null>(null)
  const [tagDraft, setTagDraft] = useState('')
  const [runtimeStatus, setRuntimeStatus] = useState<Record<string, SkillRuntimeStatus>>({})

  const loadRuntimeStatus = async () => {
    try {
      const statuses = await listSkillRuntimeStatus()
      setRuntimeStatus(Object.fromEntries(statuses.map((status) => [status.name, status])))
    } catch { setRuntimeStatus({}) }
  }

  useEffect(() => {
    fetchAll()
    void loadRuntimeStatus()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const filtered = useMemo(() => {
    if (!query.trim()) return skills
    const q = query.toLowerCase()
    return skills.filter(
      (s) =>
        s.name.toLowerCase().includes(q) ||
        (s.description ?? '').toLowerCase().includes(q) ||
        s.tags.toLowerCase().includes(q),
    )
  }, [skills, query])

  const linkCount = useMemo(() => {
    const counts: Record<number, number> = {}
    for (const l of links) counts[l.skillId] = (counts[l.skillId] ?? 0) + 1
    return counts
  }, [links])

  const handleImportLocal = async () => {
    const dir = await open({ directory: true, title: '選擇含 SKILL.md 的資料夾' })
    if (!dir) return
    setBusy(true)
    try {
      const params={sourceKind:'local',sourceRef:dir as string}
      setRiskReport(await previewSkill(params));setPendingInstall(params)
    } finally {
      setBusy(false)
    }
  }

  const handleImportZip = async () => {
    const file = await open({
      title: '選擇 .zip / .skill 壓縮檔',
      filters: [{ name: 'Skill archive', extensions: ['zip', 'skill'] }],
    })
    if (!file) return
    setBusy(true)
    try {
      const params={sourceKind:'zip',sourceRef:file as string}
      setRiskReport(await previewSkill(params));setPendingInstall(params)
    } finally {
      setBusy(false)
    }
  }

  const handleImportGithub = async () => {
    if (!importUrl.trim()) return
    setBusy(true)
    try {
      const params={
        sourceKind: 'github',
        sourceRef: importUrl.trim(),
        githubPat: githubPat || undefined,
      }
      setRiskReport(await previewSkill(params));setPendingInstall(params)
      setShowImport(false)
      setImportUrl('')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="space-y-4">
      {/* Toolbar */}
      <div className="flex items-center gap-2 flex-wrap">
        <input
          type="search"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="搜尋中央庫技能…"
          className="flex-1 min-w-48 px-3 py-2 rounded-xl border border-border bg-surface text-sm placeholder:text-ink-subtle focus:outline-none focus:ring-2 focus:ring-brand/40"
        />
        <Button variant="outline" size="sm" leftIcon={<FolderOpen className="w-4 h-4" />} onClick={handleImportLocal} disabled={busy}>
          匯入資料夾
        </Button>
        <Button variant="outline" size="sm" leftIcon={<Package className="w-4 h-4" />} onClick={handleImportZip} disabled={busy}>
          匯入 Zip
        </Button>
        <Button variant="primary" size="sm" leftIcon={<Plus className="w-4 h-4" />} onClick={() => setShowImport(true)} disabled={busy}>
          從市集來源
        </Button>
        <Button variant="ghost" size="icon" title="重新掃描 Wingman Skills" onClick={async()=>{await refreshWingmanSkills();await loadRuntimeStatus()}}><RefreshCw className="h-4 w-4"/></Button>
      </div>

      {error && (
        <div className="flex items-center justify-between rounded-xl border border-red-200 bg-red-50 px-4 py-2.5">
          <p className="text-sm text-red-700">{error}</p>
          <button type="button" onClick={clearError} className="text-red-400 hover:text-red-600">
            <X className="w-4 h-4" />
          </button>
        </div>
      )}

      {/* Skill list */}
      {loading ? (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          {Array.from({ length: 4 }).map((_, i) => (
            <div key={i} className="h-28 rounded-2xl bg-surface-alt animate-pulse border border-border" />
          ))}
        </div>
      ) : filtered.length === 0 ? (
        <div className="text-center py-16 text-ink-subtle text-sm">
          {query ? '沒有符合的技能' : '中央庫是空的 — 從市集安裝或匯入本地技能'}
        </div>
      ) : (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          {filtered.map((skill) => (
            <div key={skill.id} className="rounded-2xl border border-border bg-surface p-4 space-y-2">
              <div className="flex items-start justify-between gap-2">
                <div className="min-w-0">
                  <p className="text-sm font-semibold text-ink truncate">{skill.displayName}</p>
                  <p className="text-xs text-ink-subtle font-mono truncate">{skill.name}</p>
                </div>
                <RiskBadge level={skill.riskLevel} />
              </div>
              {skill.description && (
                <p className="text-xs text-ink-secondary line-clamp-2">{skill.description}</p>
              )}
              {runtimeStatus[skill.name] && (
                <div className="flex flex-wrap items-center gap-1.5 text-xs text-ink-subtle" title={runtimeStatus[skill.name].error ?? undefined}>
                  <Cpu className="h-3.5 w-3.5" />
                  {runtimeStatus[skill.name].status === 'instruction_only' ? 'Prompt only' : `${runtimeStatus[skill.name].runtime ?? 'Runtime'} ${runtimeStatus[skill.name].version ?? ''} · ${runtimeStatus[skill.name].status}`}
                  {runtimeStatus[skill.name].dependencyFile&&<span className="border border-border px-1.5 py-0.5">{runtimeStatus[skill.name].packageManager??runtimeStatus[skill.name].dependencyFile}</span>}
                  {runtimeStatus[skill.name].network&&<span className="border border-border px-1.5 py-0.5">Network</span>}
                  {runtimeStatus[skill.name].requiredEnvironment.length>0&&<span className="border border-border px-1.5 py-0.5">Credential</span>}
                  {runtimeStatus[skill.name].requiresApproval&&<span className="border border-border px-1.5 py-0.5">Approval</span>}
                </div>
              )}
              <div className="flex items-center justify-between pt-1">
                <div className="flex items-center gap-2 text-xs text-ink-subtle">
                  <span>{skill.sourceKind}</span>
                  <span>·</span>
                  <span>{linkCount[skill.id] ?? 0} 個 Agent 使用中</span>
                  {skill.tags && (
                    <>
                      <span>·</span>
                      <span className="inline-flex items-center gap-1">
                        <Tag className="w-3 h-3" />
                        {skill.tags}
                      </span>
                    </>
                  )}
                </div>
                <div className="flex items-center gap-1">
                  <button
                    type="button"
                    title="編輯標籤"
                    onClick={() => {
                      setTagEditing(skill)
                      setTagDraft(skill.tags)
                    }}
                    className="p-1.5 rounded-lg text-ink-subtle hover:bg-surface-alt hover:text-ink transition-colors"
                  >
                    <Tag className="w-3.5 h-3.5" />
                  </button>
                  <button
                    type="button"
                    title="從中央庫移除"
                    onClick={() => removeSkill(skill.id)}
                    className="p-1.5 rounded-lg text-ink-subtle hover:bg-red-50 hover:text-red-500 transition-colors"
                  >
                    <Trash2 className="w-3.5 h-3.5" />
                  </button>
                </div>
              </div>
            </div>
          ))}
        </div>
      )}

      {/* GitHub import modal */}
      {showImport && (
        <Modal open onOpenChange={(o) => !o && setShowImport(false)} title="從市集來源安裝" size="sm">
          <div className="space-y-3">
            <p className="text-xs text-ink-secondary">
              輸入 <span className="font-mono">&lt;來源ID&gt;/&lt;技能名&gt;</span>，例如{' '}
              <span className="font-mono">anthropics/pdf</span>
            </p>
            <input
              type="text"
              value={importUrl}
              onChange={(e) => setImportUrl(e.target.value)}
              placeholder="anthropics/pdf"
              className="w-full px-3 py-2 rounded-xl border border-border bg-surface text-sm font-mono focus:outline-none focus:ring-2 focus:ring-brand/40"
            />
            <div className="flex justify-end gap-2">
              <Button variant="ghost" size="sm" onClick={() => setShowImport(false)}>
                取消
              </Button>
              <Button variant="primary" size="sm" onClick={handleImportGithub} isLoading={busy}>
                安裝至中央庫
              </Button>
            </div>
          </div>
        </Modal>
      )}

      {/* Tag editor */}
      {tagEditing && (
        <Modal open onOpenChange={(o) => !o && setTagEditing(null)} title={`標籤 — ${tagEditing.name}`} size="sm">
          <div className="space-y-3">
            <input
              type="text"
              value={tagDraft}
              onChange={(e) => setTagDraft(e.target.value)}
              placeholder="web,frontend（逗號分隔）"
              className="w-full px-3 py-2 rounded-xl border border-border bg-surface text-sm focus:outline-none focus:ring-2 focus:ring-brand/40"
            />
            <div className="flex justify-end gap-2">
              <Button variant="ghost" size="sm" onClick={() => setTagEditing(null)}>
                取消
              </Button>
              <Button
                variant="primary"
                size="sm"
                onClick={async () => {
                  await setTags(tagEditing.id, tagDraft)
                  setTagEditing(null)
                }}
              >
                儲存
              </Button>
            </div>
          </div>
        </Modal>
      )}

      {riskReport && pendingInstall && (
        <RiskReportModal
          report={riskReport}
          busy={busy}
          onClose={() => {
            setRiskReport(null)
            setPendingInstall(null)
          }}
          onConfirm={async () => {
            setBusy(true)
            try {
              await installSkill(pendingInstall)
              setRiskReport(null)
              setPendingInstall(null)
            } finally {
              setBusy(false)
            }
          }}
        />
      )}
    </div>
  )
}
