import { useEffect, useState } from 'react'
import { Check, ChevronDown, ChevronUp, Loader2, X } from 'lucide-react'
import { useProjectsStore } from '../store/useProjectsStore'

/** 摘要全部成功後仍保留數秒，讓使用者看見完成結果，再自動收起。 */
const COMPLETED_TOAST_DURATION_MS = 4000

/**
 * 全域顯示目前專案的 Community AI 摘要進度。
 *
 * 結構索引與 AI 補充刻意分開呈現：即使模型未設定或背景摘要失敗，使用者仍可立即
 * 使用 deterministic graph 問答。完成後可折疊或關閉；失敗狀態不會強制遮住 Composer。
 */
export function CommunitySummaryProgressToast() {
  const activeProjectId = useProjectsStore((state) => state.activeProjectId)
  const summary = useProjectsStore((state) => state.summaryProgress)
  const [dismissedToastKey, setDismissedToastKey] = useState<string | null>(null)
  const [collapsedKey, setCollapsedKey] = useState<string | null>(null)

  const belongsToActiveProject =
    Boolean(activeProjectId) && summary?.projectId === activeProjectId
  const working = Boolean(summary && (summary.queued > 0 || summary.running > 0))
  const completedSuccessfully = Boolean(
    summary &&
    !working &&
    summary.failed === 0 &&
    summary.completed >= summary.total,
  )
  // 失敗數也納入 key；同一專案重新索引或重試後，仍會顯示最新狀態。
  const completionKey = summary
    ? `${summary.projectId}:${summary.completed}/${summary.total}:${summary.failed}`
    : null

  useEffect(() => {
    if (!belongsToActiveProject || !summary?.structuralIndexAvailable) {
      setDismissedToastKey(null)
      setCollapsedKey(null)
      return
    }

    if (working || !completionKey) {
      // 執行中的進度不能被上一輪的關閉狀態隱藏。
      setDismissedToastKey(null)
      setCollapsedKey(null)
      return
    }

    const timer = window.setTimeout(() => {
      // 只有全數成功才自動收起；失敗狀態保留給使用者檢視與手動關閉。
      if (completedSuccessfully) setDismissedToastKey(completionKey)
    }, COMPLETED_TOAST_DURATION_MS)
    return () => window.clearTimeout(timer)
  }, [belongsToActiveProject, completedSuccessfully, completionKey, summary?.structuralIndexAvailable, working])

  if (
    !activeProjectId ||
    summary?.projectId !== activeProjectId ||
    !summary.structuralIndexAvailable ||
    dismissedToastKey === completionKey
  ) {
    return null
  }

  const collapsed = !working && collapsedKey === completionKey

  return (
    <aside
      className="fixed bottom-6 right-6 z-40 w-[min(20rem,calc(100vw-2rem))] rounded-xl border border-border bg-surface p-4 shadow-xl"
      aria-live="polite"
    >
      <div className="flex items-center justify-between gap-2">
        <p className="flex min-w-0 items-center gap-2 text-sm font-semibold text-ink">
          {working ? (
            <span className="h-2 w-2 shrink-0 rounded-full bg-emerald-500" />
          ) : summary.failed > 0 ? (
            <span className="h-2 w-2 shrink-0 rounded-full bg-amber-500" />
          ) : (
            <Check className="h-4 w-4 shrink-0 text-emerald-600" />
          )}
          <span className="truncate">結構索引可用</span>
        </p>
        <div className="flex shrink-0 items-center gap-1">
          {!working && (
            <button
              type="button"
              onClick={() => setCollapsedKey(collapsed ? null : completionKey)}
              className="rounded p-1 text-ink-subtle hover:bg-surface-alt hover:text-ink"
              title={collapsed ? '展開摘要進度' : '收合摘要進度'}
              aria-label={collapsed ? '展開摘要進度' : '收合摘要進度'}
            >
              {collapsed
                ? <ChevronDown className="h-3.5 w-3.5" />
                : <ChevronUp className="h-3.5 w-3.5" />}
            </button>
          )}
          <button
            type="button"
            onClick={() => setDismissedToastKey(completionKey)}
            className="rounded p-1 text-ink-subtle hover:bg-surface-alt hover:text-ink"
            title="關閉摘要進度"
            aria-label="關閉摘要進度"
          >
            <X className="h-3.5 w-3.5" />
          </button>
        </div>
      </div>
      {!collapsed && (
        <>
          <div className="mt-2 flex items-center gap-2 text-xs text-ink-secondary">
            {working ? (
              <Loader2 className="h-3.5 w-3.5 animate-spin text-brand" />
            ) : (
              <Check className="h-3.5 w-3.5 text-emerald-600" />
            )}
            <span>
              {working
                ? `AI 摘要背景補充中 ${summary.completed}/${summary.total}`
                : summary.failed > 0
                  ? `AI 摘要完成但有 ${summary.failed} 筆失敗`
                  : `AI 摘要已完成 ${summary.completed}/${summary.total}`}
              {summary.failed > 0
                ? ` · ${summary.failed} 筆保留結構模板`
                : ''}
            </span>
          </div>
          <div className="mt-2 h-1.5 overflow-hidden rounded-full bg-border">
            <div
              className="h-full rounded-full bg-brand transition-all"
              style={{ width: `${summary.percent}%` }}
            />
          </div>
        </>
      )}
    </aside>
  )
}
