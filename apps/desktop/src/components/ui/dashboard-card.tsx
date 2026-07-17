import { type ReactNode } from 'react'
import { cn } from '@/lib/utils'

interface TrendProps {
  value: number
  label: string
}

export interface DashboardCardProps {
  title: string
  value?: string | number
  description?: string
  icon?: ReactNode
  trend?: TrendProps
  footer?: ReactNode
  variant?: 'default' | 'outlined'
  className?: string
}

export function DashboardCard({
  title,
  value,
  description,
  icon,
  trend,
  footer,
  variant = 'default',
  className,
}: DashboardCardProps) {
  const isPositive = trend && trend.value >= 0

  return (
    <div
      className={cn(
        'rounded-2xl p-6 transition-all duration-200',
        variant === 'default' &&
          'bg-surface shadow-sm hover:shadow-md',
        variant === 'outlined' &&
          'bg-surface border border-border hover:shadow-sm',
        className
      )}
    >
      {/* Header */}
      <div className="flex items-start justify-between mb-4">
        <p className="text-sm font-medium text-ink-secondary">{title}</p>
        {icon && (
          <div className="flex items-center justify-center w-9 h-9 rounded-xl bg-surface-alt text-brand">
            {icon}
          </div>
        )}
      </div>

      {/* Value */}
      {value !== undefined && (
        <p className="text-2xl font-bold text-ink tracking-tight">{value}</p>
      )}

      {/* Description */}
      {description && (
        <p className="mt-1 text-sm text-ink-subtle">{description}</p>
      )}

      {/* Trend */}
      {trend && (
        <div className="mt-3 flex items-center gap-1.5">
          <span
            className={cn(
              'text-xs font-semibold',
              isPositive ? 'text-brand-green' : 'text-error'
            )}
          >
            {isPositive ? '↑' : '↓'} {Math.abs(trend.value)}%
          </span>
          <span className="text-xs text-ink-subtle">{trend.label}</span>
        </div>
      )}

      {/* Footer */}
      {footer && (
        <div className="mt-4 pt-4 border-t border-border">{footer}</div>
      )}
    </div>
  )
}
