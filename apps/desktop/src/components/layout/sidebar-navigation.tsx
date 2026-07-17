import { type ReactNode, type MouseEvent } from 'react'
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
  footer?: ReactNode
  collapsed?: boolean
  className?: string
}

export function SidebarNavigation({
  sections,
  activeItemId,
  onItemClick,
  header,
  footer,
  collapsed = false,
  className,
}: SidebarNavigationProps) {
  return (
    <aside
      className={cn(
        'flex flex-col h-full',
        'bg-surface border-r border-border',
        'transition-all duration-200 shrink-0',
        collapsed ? 'w-16' : 'w-64 max-[640px]:w-16',
        className
      )}
    >
      {/* Header */}
      {header && (
        <div
          className={cn(
            'px-4 py-4 border-b border-border',
            collapsed && 'flex justify-center px-2',
            'max-[640px]:flex max-[640px]:justify-center max-[640px]:px-2'
          )}
        >
          {header}
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
                    <button
                      type="button"
                      title={item.label}
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
