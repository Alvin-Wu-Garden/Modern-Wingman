import { useEffect, useRef, useState } from 'react'
import { Database, MoreHorizontal, Trash2, BarChart2 } from 'lucide-react'
import { cn } from '@/lib/utils'

export interface ProjectMenuAction {
  onDatabaseSettings: () => void
  onAnalyzeJira: () => void
  onDeleteProject: () => void
}

/**
 * 專案漢堡選單（與右鍵選單共享同一套 action 定義）。
 * - 支援滑鼠點擊、Enter、Space 開啟
 * - 點擊外部或 Escape 關閉
 * - 刪除專案顯示危險樣式
 */
export function ProjectHamburgerMenu({
  actions,
  disabled,
}: {
  actions: ProjectMenuAction
  disabled?: boolean
}) {
  const [open, setOpen] = useState(false)
  const containerRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const close = (e: MouseEvent | KeyboardEvent) => {
      if (e instanceof KeyboardEvent && e.key !== 'Escape') return
      if (
        e instanceof MouseEvent &&
        containerRef.current?.contains(e.target as Node)
      ) return
      setOpen(false)
    }
    document.addEventListener('click', close)
    document.addEventListener('keydown', close)
    return () => {
      document.removeEventListener('click', close)
      document.removeEventListener('keydown', close)
    }
  }, [open])

  return (
    <div ref={containerRef} className="relative ml-1 shrink-0">
      <button
        type="button"
        aria-label="開啟專案操作選單"
        aria-haspopup="menu"
        aria-expanded={open}
        disabled={disabled}
        onClick={(e) => { e.stopPropagation(); setOpen((s) => !s) }}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault()
            setOpen((s) => !s)
          }
        }}
        className={cn(
          'flex h-6 w-6 items-center justify-center rounded-md text-ink-subtle',
          'hover:bg-surface-alt hover:text-ink focus-visible:outline-none',
          'focus-visible:ring-2 focus-visible:ring-brand',
          'transition-colors',
          disabled && 'pointer-events-none opacity-0',
        )}
      >
        <MoreHorizontal className="h-4 w-4" />
      </button>

      {open && (
        <div
          role="menu"
          className="absolute right-0 top-full z-50 mt-1 min-w-44 rounded-xl border border-border bg-surface p-1.5 shadow-xl"
          onClick={(e) => e.stopPropagation()}
        >
          <button
            role="menuitem"
            type="button"
            className="flex w-full items-center gap-2 rounded-lg px-3 py-2 text-sm text-ink hover:bg-surface-alt"
            onClick={() => { setOpen(false); actions.onDatabaseSettings() }}
          >
            <Database className="h-4 w-4" />
            資料庫連線設定
          </button>

          <div className="my-1 border-t border-border" />

          <button
            role="menuitem"
            type="button"
            className="flex w-full items-center gap-2 rounded-lg px-3 py-2 text-sm text-ink hover:bg-surface-alt"
            onClick={() => { setOpen(false); actions.onAnalyzeJira() }}
          >
            <BarChart2 className="h-4 w-4" />
            分析 JIRA 議題
          </button>

          <button
            role="menuitem"
            type="button"
            className="flex w-full items-center gap-2 rounded-lg px-3 py-2 text-sm text-red-600 hover:bg-red-50"
            onClick={() => { setOpen(false); actions.onDeleteProject() }}
          >
            <Trash2 className="h-4 w-4" />
            刪除專案
          </button>
        </div>
      )}
    </div>
  )
}
