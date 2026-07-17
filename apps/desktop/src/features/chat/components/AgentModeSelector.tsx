import { Bot, ListChecks, MessageCircle, Zap } from 'lucide-react'
import type { AgentMode } from '@modern-wingman/contracts'
import { cn } from '@/lib/utils'

interface AgentModeSelectorProps {
  value: AgentMode
  onChange: (mode: AgentMode) => void
  disabled?: boolean
}

const MODES: Array<{
  id: AgentMode
  label: string
  title: string
  icon: typeof MessageCircle
}> = [
  { id: 'ask', label: '詢問', title: '唯讀回答與分析', icon: MessageCircle },
  { id: 'plan', label: '規劃', title: '產生計畫，核准後才修改', icon: ListChecks },
  { id: 'auto', label: 'Auto', title: '可修改與驗證，高風險操作需核准', icon: Zap },
  { id: 'full_auto', label: '完全自動', title: '非受保護範圍內完整自動執行', icon: Bot },
]

export function AgentModeSelector({ value, onChange, disabled }: AgentModeSelectorProps) {
  return (
    <label className={cn('relative inline-flex items-center',disabled&&'opacity-50')}>
      <span className="sr-only">Agent 模式</span>
      <select
        aria-label="Agent 模式"
        value={value}
        disabled={disabled}
        onChange={(event)=>onChange(event.target.value as AgentMode)}
        className="h-8 rounded-lg border border-border bg-surface px-2.5 pr-7 text-xs font-medium text-ink focus:outline-none focus:ring-2 focus:ring-brand/40"
      >
        {MODES.map(mode=><option key={mode.id} value={mode.id}>{mode.label}</option>)}
      </select>
    </label>
  )
}
