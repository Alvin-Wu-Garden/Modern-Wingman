import { cn } from '@/lib/utils'
import type { SkillSourceInfo } from '@modern-wingman/contracts'

interface SourceTab {
  id: string
  label: string
}

const INSTALLED_TAB: SourceTab = { id: '__installed__', label: '已安裝' }

interface SourceTabsProps {
  sources: SkillSourceInfo[]
  activeId: string
  onChange: (id: string) => void
}

export function SourceTabs({ sources, activeId, onChange }: SourceTabsProps) {
  const tabs: SourceTab[] = [
    ...sources.map((s) => ({ id: s.id, label: s.displayName })),
    INSTALLED_TAB,
  ]

  return (
    <div className="flex items-center gap-1 overflow-x-auto pb-px shrink-0">
      {tabs.map((tab) => (
        <button
          key={tab.id}
          type="button"
          onClick={() => onChange(tab.id)}
          className={cn(
            'shrink-0 px-3.5 py-1.5 rounded-xl text-sm font-medium transition-colors duration-150 whitespace-nowrap',
            activeId === tab.id
              ? 'bg-brand text-white shadow-sm'
              : 'text-ink-secondary hover:bg-surface-alt hover:text-ink'
          )}
        >
          {tab.label}
        </button>
      ))}
    </div>
  )
}
