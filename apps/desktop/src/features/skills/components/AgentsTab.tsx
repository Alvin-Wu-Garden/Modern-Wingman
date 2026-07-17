import { useEffect, useMemo, useState } from 'react'
import { Check, Download, Link2, RefreshCw, Unlink } from 'lucide-react'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { useLibraryStore } from '../store/useLibraryStore'
import { useSkillsStore } from '../store/useSkillsStore'

/**
 * Agents workspace tab: per-agent view of synced skills, one-click
 * sync/unsync per skill, and adoption of unmanaged skills.
 */
export function AgentsTab() {
  const { agents, fetchAgents } = useSkillsStore()
  const { skills, links, presence, fetchAll, detectAgents, syncSkill, unsyncSkill, adoptSkill } =
    useLibraryStore()

  const [activeAgent, setActiveAgent] = useState('')
  const [busyKey, setBusyKey] = useState<string | null>(null)

  useEffect(() => {
    fetchAgents()
    fetchAll()
    detectAgents()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    if (!activeAgent && agents.length > 0) setActiveAgent(agents[0].id)
  }, [agents, activeAgent])

  const agentLinks = useMemo(
    () => links.filter((l) => l.agentId === activeAgent && l.scope === 'global'),
    [links, activeAgent],
  )
  const linkedSkillIds = useMemo(() => new Set(agentLinks.map((l) => l.skillId)), [agentLinks])
  const agentPresence = presence.find((p) => p.agentId === activeAgent)
  const activeAgentInfo = agents.find((a) => a.id === activeAgent)

  const handleToggle = async (skillId: number) => {
    const key = `${activeAgent}:${skillId}`
    setBusyKey(key)
    try {
      const link = agentLinks.find((l) => l.skillId === skillId)
      if (link) {
        await unsyncSkill(link.id)
      } else {
        await syncSkill(skillId, activeAgent, 'global')
      }
    } finally {
      setBusyKey(null)
    }
  }

  return (
    <div className="space-y-4">
      {/* Agent selector */}
      <div className="flex items-center gap-1 overflow-x-auto pb-px">
        {agents.map((agent) => {
          const p = presence.find((x) => x.agentId === agent.id)
          return (
            <button
              key={agent.id}
              type="button"
              onClick={() => setActiveAgent(agent.id)}
              className={cn(
                'shrink-0 inline-flex items-center gap-1.5 px-3.5 py-1.5 rounded-xl text-sm font-medium transition-colors whitespace-nowrap',
                activeAgent === agent.id
                  ? 'bg-brand text-white shadow-sm'
                  : 'text-ink-secondary hover:bg-surface-alt hover:text-ink',
              )}
            >
              {agent.displayName}
              {p?.detected && (
                <span
                  className={cn(
                    'w-1.5 h-1.5 rounded-full',
                    activeAgent === agent.id ? 'bg-white' : 'bg-emerald-500',
                  )}
                  title="已偵測到此工具"
                />
              )}
            </button>
          )
        })}
        <button
          type="button"
          title="重新偵測"
          onClick={() => detectAgents()}
          className="shrink-0 p-2 rounded-xl text-ink-secondary hover:bg-surface-alt hover:text-ink transition-colors"
        >
          <RefreshCw className="w-4 h-4" />
        </button>
      </div>

      {activeAgentInfo && (
        <p className="text-xs text-ink-subtle font-mono">{activeAgentInfo.effectiveGlobalPath}</p>
      )}

      {/* Unmanaged skills (adopt) */}
      {agentPresence && agentPresence.unmanagedSkills.length > 0 && (
        <div className="rounded-2xl border border-amber-200 bg-amber-50/60 p-4 space-y-2">
          <p className="text-sm font-medium text-amber-800">
            偵測到 {agentPresence.unmanagedSkills.length} 個非 Wingman 管理的技能
          </p>
          <div className="flex flex-wrap gap-2">
            {agentPresence.unmanagedSkills.map((name) => (
              <div
                key={name}
                className="inline-flex items-center gap-2 rounded-xl bg-surface border border-border px-3 py-1.5"
              >
                <span className="text-xs font-mono text-ink">{name}</span>
                <Button
                  variant="ghost"
                  size="sm"
                  leftIcon={<Download className="w-3 h-3" />}
                  onClick={() => adoptSkill(activeAgent, name)}
                >
                  認養
                </Button>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Library skills with per-agent toggle */}
      {skills.length === 0 ? (
        <div className="text-center py-16 text-ink-subtle text-sm">
          中央庫是空的 — 先到「中央庫」分頁安裝技能
        </div>
      ) : (
        <div className="space-y-2">
          {skills.map((skill) => {
            const linked = linkedSkillIds.has(skill.id)
            const link = agentLinks.find((l) => l.skillId === skill.id)
            const key = `${activeAgent}:${skill.id}`
            return (
              <div
                key={skill.id}
                className="flex items-center justify-between rounded-2xl border border-border bg-surface px-4 py-3"
              >
                <div className="min-w-0 flex items-center gap-3">
                  {linked ? (
                    <span className="shrink-0 w-6 h-6 rounded-full bg-emerald-100 flex items-center justify-center">
                      <Check className="w-3.5 h-3.5 text-emerald-600" />
                    </span>
                  ) : (
                    <span className="shrink-0 w-6 h-6 rounded-full bg-surface-alt border border-border" />
                  )}
                  <div className="min-w-0">
                    <p className="text-sm font-medium text-ink truncate">{skill.displayName}</p>
                    <p className="text-xs text-ink-subtle truncate">
                      {skill.description ?? skill.name}
                      {link && <span className="ml-2 font-mono">({link.syncMode})</span>}
                    </p>
                  </div>
                </div>
                <Button
                  variant={linked ? 'ghost' : 'outline'}
                  size="sm"
                  isLoading={busyKey === key}
                  leftIcon={linked ? <Unlink className="w-3.5 h-3.5" /> : <Link2 className="w-3.5 h-3.5" />}
                  onClick={() => handleToggle(skill.id)}
                >
                  {linked ? '移除' : '同步'}
                </Button>
              </div>
            )
          })}
        </div>
      )}
    </div>
  )
}
