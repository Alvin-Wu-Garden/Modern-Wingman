import { useEffect, useState } from 'react'
import { Loader2 } from 'lucide-react'
import { useProjectsStore } from '../store/useProjectsStore'

/** 摘要全部成功後仍保留數秒，讓使用者看見完成結果，再自動收起。 */
const COMPLETED_TOAST_DURATION_MS = 4000

/**
 * 全域顯示目前專案的 Community AI 摘要進度。
 *
 * 結構索引與 AI 補充刻意分開呈現：即使模型未設定或背景摘要失敗，使用者仍可立即
 * 使用 deterministic graph 問答。元件放在應用程式外殼而非專案清單頁，因此切換到
 * 專案對話或其他畫面時也不會失去背景工作的可見性。
 */
export function CommunitySummaryProgressToast() {
  const activeProjectId = useProjectsStore((state) => state.activeProjectId)
  const summary = useProjectsStore((state) => state.summaryProgress)
  const [dismissedCompletionKey, setDismissedCompletionKey] = useState<string | null>(null)

  const belongsToActiveProject =
    Boolean(activeProjectId) && summary?.projectId === activeProjectId
  const working = Boolean(summary && (summary.queued > 0 || summary.running > 0))
  const completedSuccessfully = Boolean(
    summary &&
    !working &&
    summary.failed === 0 &&
    summary.completed >= summary.total,
  )
  // completed/total 一併放進 key；同一專案重新索引而摘要數量改變時，仍會重新顯示完成提示。
  const completionKey = summary
    ? `${summary.projectId}:${summary.completed}/${summary.total}`
    : null

  useEffect(() => {
    if (!belongsToActiveProject || !summary?.structuralIndexAvailable) {
      setDismissedCompletionKey(null)
      return
    }

    if (!completedSuccessfully || !completionKey) {
      // 摘要仍在執行，或存在失敗需要使用者注意時，不自動隱藏。
      setDismissedCompletionKey(null)
      return
    }

    const timer = window.setTimeout(
      () => setDismissedCompletionKey(completionKey),
      COMPLETED_TOAST_DURATION_MS,
    )
    return () => window.clearTimeout(timer)
  }, [belongsToActiveProject, completedSuccessfully, completionKey, summary?.structuralIndexAvailable])

  if (
    !activeProjectId ||
    summary?.projectId !== activeProjectId ||
    !summary.structuralIndexAvailable ||
    (completedSuccessfully && dismissedCompletionKey === completionKey)
  ) {
    return null
  }

  return (
    <aside
      className="fixed bottom-6 right-6 z-40 w-80 rounded-xl border border-border bg-surface p-4 shadow-xl"
      aria-live="polite"
    >
      <p className="flex items-center gap-2 text-sm font-semibold text-ink">
        <span className="h-2 w-2 rounded-full bg-emerald-500" />
        結構索引可用
      </p>
      <div className="mt-2 flex items-center gap-2 text-xs text-ink-secondary">
        {working ? (
          <Loader2 className="h-3.5 w-3.5 animate-spin text-brand" />
        ) : (
          <span className="h-3.5 w-3.5 text-center text-emerald-600">✓</span>
        )}
        <span>
          {working
            ? `AI 摘要背景補充中 ${summary.completed}/${summary.total}`
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
    </aside>
  )
}
