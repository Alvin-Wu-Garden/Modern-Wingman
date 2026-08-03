import { useEffect, useState } from 'react'
import { Check, CircleAlert, Info, Loader2 } from 'lucide-react'
import { cn } from '@/lib/utils'
import type { AgentActivityEvent } from '@/services/agent-api/client'

interface ActivityTimelineProps {
  activities: AgentActivityEvent[]
  streaming: boolean
}

/**
 * 顯示單次 Agent 回答的安全工作進度。
 * 這裡只呈現工具與階段摘要，不呈現模型私有推理、Prompt 或完整工具輸出。
 */
export function ActivityTimeline({ activities, streaming }: ActivityTimelineProps) {
  const [expanded, setExpanded] = useState(streaming)

  useEffect(() => {
    if (streaming) setExpanded(true)
    else if (activities.length > 0) setExpanded(false)
  }, [activities.length, streaming])

  if (activities.length === 0) return null

  const runningCount = activities.filter((activity) => activity.status === 'started').length
  const failedCount = activities.filter((activity) => activity.status === 'failed').length
  const completedCount = activities.filter((activity) => activity.status === 'completed').length
  const recentActivities = activities.slice(-12)

  return (
    <div className="mb-3 rounded-xl border border-border/80 bg-ink/[0.025] px-3 py-2">
      <button
        type="button"
        className="flex w-full items-center gap-2 text-left text-xs text-ink-muted"
        onClick={() => setExpanded((value) => !value)}
        aria-expanded={expanded}
      >
        {runningCount > 0
          ? <Loader2 className="h-3.5 w-3.5 animate-spin text-brand" />
          : failedCount > 0
            ? <CircleAlert className="h-3.5 w-3.5 text-red-500" />
            : <Check className="h-3.5 w-3.5 text-brand-green" />}
        <span className="font-medium text-ink">工作進度</span>
        <span className="ml-auto">
          {runningCount > 0
            ? '處理中…'
            : failedCount > 0
              ? '部分步驟失敗'
              : `已完成 ${completedCount} 個步驟`}
        </span>
      </button>

      {expanded && (
        <div className="mt-2 space-y-1.5 border-t border-border/60 pt-2">
          {recentActivities.map((activity) => (
            <div
              key={`${activity.activityId}-${activity.sequence}`}
              className="flex items-start gap-2 text-xs"
            >
              <ActivityIcon status={activity.status} />
              <div className="min-w-0 flex-1">
                <div className="flex items-center gap-2">
                  <span className={cn(
                    'font-medium',
                    activity.status === 'failed' ? 'text-red-600' : 'text-ink',
                  )}
                  >
                    {activity.label || activity.tool || 'Agent 工作步驟'}
                  </span>
                  {activity.tool && (
                    <span className="rounded bg-ink/5 px-1.5 py-0.5 font-mono text-[10px] text-ink-subtle">
                      {activity.tool}
                    </span>
                  )}
                  {activity.elapsedMs != null && (
                    <span className="text-ink-subtle">
                      {formatElapsed(activity.elapsedMs)}
                    </span>
                  )}
                </div>
                {activity.detail && (
                  <p className="mt-0.5 truncate text-ink-subtle" title={activity.detail}>
                    {activity.detail}
                  </p>
                )}
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

function ActivityIcon({ status }: { status: AgentActivityEvent['status'] }) {
  if (status === 'started')
    return <Loader2 className="mt-0.5 h-3.5 w-3.5 shrink-0 animate-spin text-brand" />
  if (status === 'failed')
    return <CircleAlert className="mt-0.5 h-3.5 w-3.5 shrink-0 text-red-500" />
  if (status === 'status')
    return <Info className="mt-0.5 h-3.5 w-3.5 shrink-0 text-blue-500" />
  return <Check className="mt-0.5 h-3.5 w-3.5 shrink-0 text-brand-green" />
}

function formatElapsed(milliseconds: number) {
  if (milliseconds < 1_000) return `${Math.max(1, Math.round(milliseconds))} ms`
  return `${(milliseconds / 1_000).toFixed(1)} 秒`
}
