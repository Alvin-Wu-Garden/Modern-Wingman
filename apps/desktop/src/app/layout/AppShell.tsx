import { type ReactNode } from 'react'
import { cn } from '@/lib/utils'

interface AppShellProps {
  sidebar: ReactNode
  children: ReactNode
  className?: string
}

export function AppShell({ sidebar, children, className }: AppShellProps) {
  return (
    <div
      className={cn(
        'flex h-full bg-surface-alt overflow-hidden border-t border-border',
        className
      )}
    >
      {sidebar}
      <main className="flex-1 flex flex-col min-w-0 overflow-hidden">
        {children}
      </main>
    </div>
  )
}
