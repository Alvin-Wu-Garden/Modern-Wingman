import { useState } from 'react'
import { Download, CheckCircle2, ChevronRight } from 'lucide-react'
import { cn } from '@/lib/utils'
import type { InstalledSkillInfo, SkillMeta } from '@modern-wingman/contracts'

// ── Source branding ───────────────────────────────────────────────────────────
// To add a new source: add one entry here and register the source in sources.rs

const SOURCE_META: Record<string, { avatarUrl: string; fallbackBg: string; letter: string }> = {
  'vercel-labs': {
    avatarUrl: 'https://github.com/vercel.png?size=32',
    fallbackBg: 'bg-black',
    letter: 'V',
  },
  'anthropics': {
    avatarUrl: 'https://github.com/anthropics.png?size=32',
    fallbackBg: 'bg-[#C17E61]',
    letter: 'A',
  },
  'remotion': {
    avatarUrl: 'https://github.com/remotion-dev.png?size=32',
    fallbackBg: 'bg-[#0b84f3]',
    letter: 'R',
  },
  'microsoft-azure': {
    avatarUrl: 'https://github.com/microsoft.png?size=32',
    fallbackBg: 'bg-[#0078D4]',
    letter: 'M',
  },
  'superpowers': {
    avatarUrl: 'https://github.com/obra.png?size=32',
    fallbackBg: 'bg-[#6f42c1]',
    letter: 'S',
  },
}

function SourceLogo({ sourceId }: { sourceId: string }) {
  const meta = SOURCE_META[sourceId]
  const [imgError, setImgError] = useState(false)

  if (!meta || imgError) {
    return (
      <div
        className={cn(
          'w-5 h-5 rounded-md flex items-center justify-center text-white text-[9px] font-bold shrink-0',
          meta?.fallbackBg ?? 'bg-gray-400'
        )}
      >
        {meta?.letter ?? '?'}
      </div>
    )
  }

  return (
    <img
      src={meta.avatarUrl}
      alt={sourceId}
      className="w-5 h-5 rounded-md object-cover shrink-0"
      onError={() => setImgError(true)}
    />
  )
}

// ── Card ─────────────────────────────────────────────────────────────────────

interface SkillCardProps {
  skill: SkillMeta
  description?: string
  isLoadingDescription?: boolean
  installed?: InstalledSkillInfo
  onSelect: (skill: SkillMeta) => void
  onInstall: (skill: SkillMeta) => void
  onUninstall: (installId: number) => void
}

export function SkillCard({
  skill,
  description,
  isLoadingDescription,
  installed,
  onSelect,
  onInstall,
  onUninstall,
}: SkillCardProps) {
  return (
    <div
      role="button"
      tabIndex={0}
      onClick={() => onSelect(skill)}
      onKeyDown={(e) => e.key === 'Enter' && onSelect(skill)}
      className={cn(
        'flex flex-col gap-3 rounded-2xl border p-4 bg-surface cursor-pointer',
        'transition-all duration-150 hover:shadow-md hover:border-brand/30 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand/40',
        installed ? 'border-brand/30' : 'border-border'
      )}
    >
      {/* Header row */}
      <div className="flex items-start justify-between gap-2">
        <div className="flex items-center gap-2 min-w-0">
          <SourceLogo sourceId={skill.sourceId} />
          <p className="text-sm font-semibold text-ink truncate">{skill.displayName}</p>
        </div>

        <div className="flex items-center gap-1.5 shrink-0">
          {installed && (
            <span className="flex items-center gap-1 text-xs font-medium text-brand bg-brand/10 rounded-full px-2 py-0.5">
              <CheckCircle2 className="w-3 h-3" />
              已安裝
            </span>
          )}
          <ChevronRight className="w-3.5 h-3.5 text-ink-subtle" />
        </div>
      </div>

      {/* Description — 2 lines from SKILL.md, with loading shimmer */}
      <div className="min-h-[2.5rem]">
        {isLoadingDescription && !description ? (
          <div className="space-y-1.5">
            <div className="h-3 w-full rounded bg-surface-alt animate-pulse" />
            <div className="h-3 w-3/4 rounded bg-surface-alt animate-pulse" />
          </div>
        ) : description ? (
          <p className="text-xs text-ink-secondary line-clamp-2 leading-relaxed">
            {description}
          </p>
        ) : (
          <p className="text-xs text-ink-subtle italic">暫無說明</p>
        )}
      </div>

      {/* Action row — stop propagation so clicking install doesn't open detail */}
      <div className="mt-auto flex gap-2" onClick={(e) => e.stopPropagation()}>
        {installed ? (
          <button
            type="button"
            onClick={() => onUninstall(installed.id)}
            className="text-xs text-ink-secondary hover:text-red-500 transition-colors"
          >
            移除
          </button>
        ) : (
          <button
            type="button"
            onClick={() => onInstall(skill)}
            className={cn(
              'flex items-center gap-1.5 text-xs font-medium px-3 py-1.5 rounded-xl',
              'bg-brand text-white hover:bg-brand/90 transition-colors duration-150'
            )}
          >
            <Download className="w-3.5 h-3.5" />
            安裝
          </button>
        )}
      </div>
    </div>
  )
}


