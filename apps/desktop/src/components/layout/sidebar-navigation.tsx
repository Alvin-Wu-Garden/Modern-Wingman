import { type ReactNode, type MouseEvent, useState } from 'react'
import { cn } from '@/lib/utils'

export interface NavItem {
  id: string
  label: string
  icon?: ReactNode
  badge?: string | number
  onClick?: () => void
  onContextMenu?: (e: MouseEvent) => void
}

export interface NavSection {
  title?: string
  items: NavItem[]
}

export interface SidebarNavigationProps {
  sections: NavSection[]
  activeItemId?: string
  onItemClick?: (item: NavItem) => void
  header?: ReactNode
  onHeaderDoubleClick?: () => void
  footer?: ReactNode
  collapsed?: boolean
  style?: React.CSSProperties
  className?: string
}

/**
 * 側邊欄收合時的即時懸停提示。
 * 使用 position: fixed 定位，完全不受任何 overflow 容器裁切；
 * hover 時立即顯示，無延遲。
 */
export function SidebarTooltip({
  label,
  children,
  show = true,
}: {
  label: string
  children: ReactNode
  show?: boolean
}) {
  const [tip, setTip] = useState<{ top: number; left: number } | null>(null)

  return (
    <div
      onMouseEnter={show ? (e) => {
        const rect = e.currentTarget.getBoundingClientRect()
        setTip({ top: Math.round(rect.top + rect.height / 2), left: Math.round(rect.right + 8) })
      } : undefined}
      onMouseLeave={show ? () => setTip(null) : undefined}
    >
      {children}
      {show && tip && (
        <div
          style={{ position: 'fixed', top: tip.top, left: tip.left, transform: 'translateY(-50%)', zIndex: 9999, pointerEvents: 'none' }}
          className="whitespace-nowrap rounded-lg border border-brand/35 bg-surface px-2.5 py-1.5 text-sm font-medium text-ink shadow-lg shadow-brand/15"
        >
          {label}
          <span className="absolute -left-[5px] top-1/2 h-2.5 w-2.5 -translate-y-1/2 rotate-45 border-b border-l border-brand/35 bg-surface" />
        </div>
      )}
    </div>
  )
}

export function SidebarNavigation({
  sections,
  activeItemId,
  onItemClick,
  header,
  onHeaderDoubleClick,
  footer,
  collapsed = false,
  style,
  className,
}: SidebarNavigationProps) {
  return (
    <aside
      style={style}
      className={cn(
        'relative flex flex-col h-full',
        'bg-surface border-r border-border',
        'transition-[width] duration-200 shrink-0',
        'max-[640px]:!w-16',
        className
      )}
    >
      {/* Header */}
      {header && (
        <div
          className={cn(
            'relative flex h-16 shrink-0 select-none items-center border-b border-border px-4',
            collapsed && 'justify-center px-2',
            'max-[640px]:justify-center max-[640px]:px-2'
          )}
        >
          {header}
          {onHeaderDoubleClick && (
            <div
              aria-hidden="true"
              title="雙擊收合或展開側邊欄"
              onDoubleClick={onHeaderDoubleClick}
              className="absolute inset-x-0 bottom-0 h-2 cursor-pointer"
            />
          )}
        </div>
      )}

      {/* Nav sections */}
      <nav className="flex-1 overflow-y-auto p-3 space-y-5 max-[640px]:px-2">
        {sections.map((section, idx) => (
          <div key={idx}>
            {section.title && !collapsed && (
              <p className="px-3 mb-1.5 text-xs font-semibold text-ink-subtle uppercase tracking-wider select-none max-[640px]:hidden">
                {section.title}
              </p>
            )}
            <ul className="space-y-0.5">
              {section.items.map((item) => {
                const isActive = activeItemId === item.id
                return (
                  <li key={item.id}>
                    <SidebarTooltip label={item.label} show={collapsed}>
                      <button
                        type="button"
                        onClick={() => {
                          item.onClick?.()
                          onItemClick?.(item)
                        }}
                        onContextMenu={item.onContextMenu}
                        className={cn(
                          'w-full flex items-center gap-3 px-3 py-2.5 rounded-xl text-sm text-left',
                          'transition-all duration-150',
                          isActive
                            ? 'bg-brand/10 text-brand font-medium'
                            : 'text-ink-secondary hover:bg-surface-alt hover:text-ink',
                          collapsed && 'justify-center px-2',
                          'max-[640px]:justify-center max-[640px]:px-2'
                        )}
                      >
                        {item.icon && (
                          <span
                            className={cn(
                              'shrink-0 flex items-center justify-center',
                              isActive ? 'text-brand' : 'text-ink-subtle'
                            )}
                          >
                            {item.icon}
                          </span>
                        )}
                        {!collapsed && (
                          <>
                            <span className="flex-1 truncate max-[640px]:hidden">{item.label}</span>
                            {item.badge !== undefined && (
                              <span className="ml-auto shrink-0 min-w-[1.25rem] h-5 px-1.5 rounded-full bg-brand text-white text-xs font-medium flex items-center justify-center max-[640px]:hidden">
                                {item.badge}
                              </span>
                            )}
                          </>
                        )}
                      </button>
                    </SidebarTooltip>
                  </li>
                )
              })}
            </ul>
          </div>
        ))}
      </nav>

      {/* Footer */}
      {footer && (
        <div
          className={cn(
            'p-3 border-t border-border',
            collapsed && 'flex justify-center',
            'max-[640px]:flex max-[640px]:justify-center max-[640px]:px-2'
          )}
        >
          {footer}
        </div>
      )}
    </aside>
  )
}
