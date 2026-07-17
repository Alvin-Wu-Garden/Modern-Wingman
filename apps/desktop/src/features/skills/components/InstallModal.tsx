import { useEffect, useState } from 'react'
import { AlertTriangle, X, FolderOpen, ShieldCheck } from 'lucide-react'
import { invoke } from '@tauri-apps/api/core'
import { cn } from '@/lib/utils'
import type { AgentInfo, InstallSkillParams, RiskReport, SkillMeta } from '@modern-wingman/contracts'

interface InstallModalProps {
  skill: SkillMeta
  agents: AgentInfo[]
  githubPat?: string
  riskContent?: string
  onConfirm: (params: InstallSkillParams) => Promise<void>
  onClose: () => void
}

const AGENT_ICON_BY_ID: Record<string, string> = {
  amp: '/assets/icons/amp-color.svg',
  antigravity: '/assets/icons/antigravity-color.svg',
  'claude-code': '/assets/icons/claudecode-color.svg',
  cline: '/assets/icons/cline.svg',
  codex: '/assets/icons/codex.svg',
  cursor: '/assets/icons/cursor.svg',
  goose: '/assets/icons/goose.svg',
  grok: '/assets/icons/grok.svg',
  'kilo-code': '/assets/icons/kiro.svg',
  opencode: '/assets/icons/opencode.svg',
  'roo-code': '/assets/icons/roocode.svg',
  trae: '/assets/icons/trae-color.svg',
  windsurf: '/assets/icons/windsurf.svg',
  'gemini-cli': '/assets/icons/gemini-color.svg',
  'copilot': '/assets/icons/githubcopilot.svg'
}

function getAgentIconSrc(agent: AgentInfo) {
  if (!agent.icon) return AGENT_ICON_BY_ID[agent.id] ?? null
  if (agent.icon.startsWith('/') || agent.icon.startsWith('http')) return agent.icon
  return `/assets/icons/${agent.icon}`
}

function getAgentInitials(displayName: string) {
  return displayName
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase())
    .join('')
}

export function InstallModal({ skill, agents, githubPat, riskContent, onConfirm, onClose }: InstallModalProps) {
  const [selectedAgent, setSelectedAgent] = useState(agents[0]?.id ?? '')
  const [scope, setScope] = useState<'global' | 'project'>('global')
  const [projectPath, setProjectPath] = useState('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [risk,setRisk]=useState<RiskReport|null>(null)

  useEffect(()=>{let cancelled=false;if(!riskContent){setError('無法取得 SKILL.md，暫時不能安裝。');return}void invoke<RiskReport>('library_scan_risk',{content:riskContent}).then(report=>{if(!cancelled)setRisk(report)}).catch(err=>{if(!cancelled)setError(err instanceof Error?err.message:String(err))});return()=>{cancelled=true}},[riskContent])

  const chosenAgent = agents.find((a) => a.id === selectedAgent)

  const handleConfirm = async () => {
    if (scope === 'project' && !projectPath.trim()) {
      setError('請輸入專案路徑')
      return
    }
    setError(null)
    setLoading(true)
    try {
      await onConfirm({
        sourceId: skill.sourceId,
        skillName: skill.skillName,
        agentId: selectedAgent,
        scope,
        projectPath: scope === 'project' ? projectPath.trim() : undefined,
        githubPat,
      })
      onClose()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setLoading(false)
    }
  }

  return (
    /* Backdrop */
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 backdrop-blur-sm p-4"
      onClick={(e) => e.target === e.currentTarget && onClose()}
    >
      <div className="w-full max-w-md bg-surface rounded-2xl shadow-xl border border-border flex flex-col">
        {/* Header */}
        <div className="flex items-center justify-between px-5 py-4 border-b border-border">
          <div>
            <p className="text-sm font-semibold text-ink">安裝 Skill</p>
            <p className="text-xs text-ink-subtle mt-0.5 font-mono">{skill.skillName}</p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="text-ink-secondary hover:text-ink transition-colors"
          >
            <X className="w-4 h-4" />
          </button>
        </div>

        {/* Body */}
        <div className="px-5 py-4 space-y-4">
          {/* Agent selector */}
          <div>
            <label className="block text-xs font-medium text-ink-secondary mb-1.5">
              目標 AI Agent
            </label>
            <div className="grid grid-cols-2 gap-2">
              {agents.map((agent) => {
                const iconSrc = getAgentIconSrc(agent)
                const selected = selectedAgent === agent.id

                return (
                  <button
                    key={agent.id}
                    type="button"
                    onClick={() => setSelectedAgent(agent.id)}
                    className={cn(
                      'flex items-center gap-2.5 text-left px-3 py-2.5 rounded-xl border text-sm transition-all duration-150',
                      selected
                        ? 'border-brand bg-brand/5 text-ink font-medium shadow-sm'
                        : 'border-border text-ink-secondary hover:border-brand/30 hover:text-ink'
                    )}
                  >
                    <span
                      className={cn(
                        'flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border bg-white',
                        selected ? 'border-brand/30' : 'border-border'
                      )}
                    >
                      {iconSrc ? (
                        <img
                          src={iconSrc}
                          alt=""
                          className="h-5 w-5 object-contain"
                          draggable={false}
                        />
                      ) : (
                        <span className="text-[10px] font-semibold text-ink-subtle">
                          {getAgentInitials(agent.displayName)}
                        </span>
                      )}
                    </span>
                    <span className="min-w-0 truncate">{agent.displayName}</span>
                  </button>
                )
              })}
            </div>
          </div>

          {/* Scope toggle */}
          <div>
            <label className="block text-xs font-medium text-ink-secondary mb-1.5">
              安裝範圍
            </label>
            <div className="flex rounded-xl border border-border overflow-hidden">
              {(['global', 'project'] as const).map((s) => (
                <button
                  key={s}
                  type="button"
                  onClick={() => setScope(s)}
                  className={cn(
                    'flex-1 py-2 text-sm transition-colors duration-150',
                    scope === s
                      ? 'bg-brand text-white font-medium'
                      : 'text-ink-secondary hover:bg-surface-alt'
                  )}
                >
                  {s === 'global' ? '全域安裝' : '專案安裝'}
                </button>
              ))}
            </div>
          </div>

          {/* Install path preview / project path input */}
          {scope === 'global' && chosenAgent && (
            <div className="text-xs text-ink-secondary bg-surface-alt rounded-xl px-3 py-2.5 font-mono break-all">
              {chosenAgent.effectiveGlobalPath}/{skill.skillName}/SKILL.md
            </div>
          )}

          {scope === 'project' && (
            <div>
              <label className="block text-xs font-medium text-ink-secondary mb-1.5">
                專案根目錄路徑
              </label>
              <div className="flex items-center gap-2">
                <input
                  type="text"
                  value={projectPath}
                  onChange={(e) => setProjectPath(e.target.value)}
                  placeholder="C:/Users/you/my-project"
                  className={cn(
                    'flex-1 rounded-xl border border-border bg-surface px-3 py-2 text-sm',
                    'placeholder:text-ink-subtle focus:outline-none focus:ring-2 focus:ring-brand/40'
                  )}
                />
                <FolderOpen className="w-4 h-4 text-ink-secondary shrink-0" />
              </div>
              {projectPath && chosenAgent && (
                <p className="mt-1.5 text-xs text-ink-subtle font-mono break-all">
                  {projectPath}/{chosenAgent.projectSkillsSubpath}/{skill.skillName}/SKILL.md
                </p>
              )}
            </div>
          )}

          <div className="rounded-lg border border-border bg-surface-alt p-3 text-xs">
            <div className="flex items-center gap-2 font-medium text-ink">{risk?.level==='high'?<AlertTriangle className="h-4 w-4 text-red-500"/>:<ShieldCheck className="h-4 w-4 text-success"/>}安裝權限與風險：{risk?risk.level.toUpperCase():'掃描中…'}</div>
            {risk&&risk.findings.length>0&&<ul className="mt-2 space-y-1 text-ink-secondary">{risk.findings.map((finding,index)=><li key={`${finding.rule}-${index}`}>{finding.severity.toUpperCase()} · {finding.message}</li>)}</ul>}
            <p className="mt-2 text-ink-subtle">安裝會寫入所選 Agent 的 Skill 目錄；腳本執行、網路與憑證能力仍受 Agent Policy 與核准流程限制。</p>
          </div>

          {error && (
            <p className="text-xs text-red-500 bg-red-50 rounded-xl px-3 py-2">{error}</p>
          )}
        </div>

        {/* Footer */}
        <div className="flex justify-end gap-2 px-5 py-4 border-t border-border">
          <button
            type="button"
            onClick={onClose}
            className="px-4 py-2 text-sm text-ink-secondary hover:text-ink transition-colors"
          >
            取消
          </button>
          <button
            type="button"
            onClick={handleConfirm}
            disabled={loading||!risk}
            className={cn(
              'px-4 py-2 rounded-xl text-sm font-medium transition-colors duration-150',
              'bg-brand text-white hover:bg-brand/90 disabled:opacity-60 disabled:cursor-not-allowed'
            )}
          >
            {loading ? '安裝中…' : '確認安裝'}
          </button>
        </div>
      </div>
    </div>
  )
}
