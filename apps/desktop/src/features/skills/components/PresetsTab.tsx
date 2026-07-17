import { useEffect, useState } from 'react'
import { Layers, Play, Plus, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/modal'
import { useLibraryStore } from '../store/useLibraryStore'
import { useSkillsStore } from '../store/useSkillsStore'
import type { SkillPreset } from '@modern-wingman/contracts'

/** Presets tab: named skill groups applied to an agent in one click. */
export function PresetsTab() {
  const { skills, presets, fetchAll, createPreset, deletePreset, setPresetMember, applyPreset } =
    useLibraryStore()
  const { agents, fetchAgents } = useSkillsStore()

  const [newName, setNewName] = useState('')
  const [applying, setApplying] = useState<SkillPreset | null>(null)
  const [applyAgent, setApplyAgent] = useState('')
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    fetchAll()
    fetchAgents()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const handleCreate = async () => {
    if (!newName.trim()) return
    await createPreset(newName.trim())
    setNewName('')
  }

  const handleApply = async () => {
    if (!applying || !applyAgent) return
    setBusy(true)
    try {
      await applyPreset(applying.id, applyAgent, 'global')
      setApplying(null)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="space-y-4">
      {/* Create */}
      <div className="flex items-center gap-2">
        <input
          type="text"
          value={newName}
          onChange={(e) => setNewName(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && handleCreate()}
          placeholder="新 Preset 名稱（例如 Web 前端組合）"
          className="flex-1 px-3 py-2 rounded-xl border border-border bg-surface text-sm placeholder:text-ink-subtle focus:outline-none focus:ring-2 focus:ring-brand/40"
        />
        <Button variant="primary" size="sm" leftIcon={<Plus className="w-4 h-4" />} onClick={handleCreate}>
          建立
        </Button>
      </div>

      {presets.length === 0 ? (
        <div className="text-center py-16 text-ink-subtle text-sm">
          尚無 Preset — 建立一個技能組合，之後可一鍵套用到任何 Agent
        </div>
      ) : (
        <div className="space-y-3">
          {presets.map((preset) => (
            <div key={preset.id} className="rounded-2xl border border-border bg-surface p-4 space-y-3">
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <Layers className="w-4 h-4 text-brand" />
                  <p className="text-sm font-semibold text-ink">{preset.name}</p>
                  <span className="text-xs text-ink-subtle">{preset.skillIds.length} 個技能</span>
                </div>
                <div className="flex items-center gap-1">
                  <Button
                    variant="outline"
                    size="sm"
                    leftIcon={<Play className="w-3.5 h-3.5" />}
                    onClick={() => {
                      setApplying(preset)
                      setApplyAgent(agents[0]?.id ?? '')
                    }}
                    disabled={preset.skillIds.length === 0}
                  >
                    套用
                  </Button>
                  <button
                    type="button"
                    title="刪除 Preset"
                    onClick={() => deletePreset(preset.id)}
                    className="p-1.5 rounded-lg text-ink-subtle hover:bg-red-50 hover:text-red-500 transition-colors"
                  >
                    <Trash2 className="w-3.5 h-3.5" />
                  </button>
                </div>
              </div>

              {/* Membership chips */}
              {skills.length > 0 && (
                <div className="flex flex-wrap gap-1.5">
                  {skills.map((skill) => {
                    const member = preset.skillIds.includes(skill.id)
                    return (
                      <button
                        key={skill.id}
                        type="button"
                        onClick={() => setPresetMember(preset.id, skill.id, !member)}
                        className={
                          member
                            ? 'px-2.5 py-1 rounded-lg text-xs font-medium bg-brand/10 text-brand border border-brand/30'
                            : 'px-2.5 py-1 rounded-lg text-xs text-ink-subtle border border-border hover:bg-surface-alt transition-colors'
                        }
                      >
                        {skill.name}
                      </button>
                    )
                  })}
                </div>
              )}
            </div>
          ))}
        </div>
      )}

      {/* Apply modal */}
      {applying && (
        <Modal open onOpenChange={(o) => !o && setApplying(null)} title={`套用「${applying.name}」`} size="sm">
          <div className="space-y-3">
            <p className="text-xs text-ink-secondary">選擇要套用到的 Agent（global scope）：</p>
            <select
              value={applyAgent}
              onChange={(e) => setApplyAgent(e.target.value)}
              className="w-full px-3 py-2 rounded-xl border border-border bg-surface text-sm focus:outline-none focus:ring-2 focus:ring-brand/40"
            >
              {agents.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.displayName}
                </option>
              ))}
            </select>
            <div className="flex justify-end gap-2">
              <Button variant="ghost" size="sm" onClick={() => setApplying(null)}>
                取消
              </Button>
              <Button variant="primary" size="sm" onClick={handleApply} isLoading={busy}>
                套用 {applying.skillIds.length} 個技能
              </Button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  )
}
