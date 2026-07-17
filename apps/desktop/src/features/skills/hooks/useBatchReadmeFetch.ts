import { useEffect, useRef } from 'react'
import type { SkillMeta } from '@modern-wingman/contracts'
import { useSkillsStore } from '../store/useSkillsStore'

const BATCH_SIZE = 3
const BATCH_DELAY_MS = 400

/**
 * Fetches SKILL.md descriptions for a tab's skills in throttled batches.
 * Extracted from SkillsPage (WS2 refactor) — keeps rate-limit-friendly
 * background fetching out of the component tree.
 */
export function useBatchReadmeFetch(
  activeTab: string,
  skills: SkillMeta[] | undefined,
  githubPat?: string,
) {
  const fetchReadme = useSkillsStore((s) => s.fetchReadme)
  const fetchedTabs = useRef(new Set<string>())

  useEffect(() => {
    if (!activeTab || !skills || skills.length === 0) return
    if (fetchedTabs.current.has(activeTab)) return
    fetchedTabs.current.add(activeTab)

    let cancelled = false

    const run = async () => {
      for (let i = 0; i < skills.length; i += BATCH_SIZE) {
        if (cancelled) break
        const batch = skills.slice(i, i + BATCH_SIZE)
        await Promise.allSettled(
          batch.map((s) => fetchReadme(s.sourceId, s.skillName, githubPat)),
        )
        if (i + BATCH_SIZE < skills.length && !cancelled) {
          await new Promise((res) => setTimeout(res, BATCH_DELAY_MS))
        }
      }
    }

    run()
    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeTab, skills])

  /** Allows the refresh button to re-trigger fetching for a tab. */
  const resetTab = (tab: string) => fetchedTabs.current.delete(tab)
  return { resetTab }
}
