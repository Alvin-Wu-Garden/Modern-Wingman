import { useEffect, useMemo, useState } from 'react'
import Markdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import rehypeRaw from 'rehype-raw'
import { ArrowLeft, Download, CheckCircle2 } from 'lucide-react'
import { cn } from '@/lib/utils'
import { useSkillsStore } from '../store/useSkillsStore'
import type { InstalledSkillInfo, SkillMeta } from '@modern-wingman/contracts'

// ── Tailwind prose components for react-markdown ──────────────────────────────

/* eslint-disable @typescript-eslint/no-explicit-any */
const mdComponents = {
  h1: ({ children }: any) => (
    <h1 className="text-xl font-bold text-ink mb-4 mt-0 pb-2 border-b border-border">{children}</h1>
  ),
  h2: ({ children }: any) => (
    <h2 className="text-base font-semibold text-ink mb-3 mt-6 first:mt-0">{children}</h2>
  ),
  h3: ({ children }: any) => (
    <h3 className="text-sm font-semibold text-ink mb-2 mt-4">{children}</h3>
  ),
  h4: ({ children }: any) => (
    <h4 className="text-sm font-medium text-ink mb-2 mt-3">{children}</h4>
  ),
  p: ({ children }: any) => (
    <p className="text-sm text-ink-secondary mb-3 leading-relaxed">{children}</p>
  ),
  ul: ({ children }: any) => (
    <ul className="list-disc list-outside pl-5 mb-3 space-y-1">{children}</ul>
  ),
  ol: ({ children }: any) => (
    <ol className="list-decimal list-outside pl-5 mb-3 space-y-1">{children}</ol>
  ),
  li: ({ node, children, className, ...props }: any) => {
    // GFM task list items have a checkbox input as first child
    const isTask = className === 'task-list-item'
    return (
      <li
        className={cn(
          'text-sm text-ink-secondary leading-relaxed',
          isTask && 'list-none flex items-start gap-2',
        )}
        {...props}
      >
        {children}
      </li>
    )
  },
  input: ({ type, checked, disabled, ...props }: any) => {
    if (type === 'checkbox') {
      return (
        <input
          type="checkbox"
          checked={checked}
          readOnly
          className="mt-0.5 accent-brand shrink-0 cursor-default"
          {...props}
        />
      )
    }
    return <input type={type} {...props} />
  },
  blockquote: ({ children }: any) => (
    <blockquote className="border-l-4 border-brand/30 pl-4 italic text-ink-subtle mb-3 text-sm">
      {children}
    </blockquote>
  ),
  pre: ({ children }: any) => (
    <pre className="bg-surface-alt rounded-xl p-4 overflow-x-auto mb-3 text-xs font-mono border border-border leading-relaxed">
      {children}
    </pre>
  ),
  code: ({ className, children, node, ...props }: any) => {
    const isBlock = Boolean(className)
    if (isBlock) {
      return (
        <code className={cn('font-mono text-xs text-ink', className)} {...props}>
          {children}
        </code>
      )
    }
    return (
      <code className="font-mono text-xs bg-surface-alt px-1.5 py-0.5 rounded text-brand border border-border/50" {...props}>
        {children}
      </code>
    )
  },
  strong: ({ children }: any) => (
    <strong className="font-semibold text-ink">{children}</strong>
  ),
  em: ({ children }: any) => <em className="italic text-ink-secondary">{children}</em>,
  del: ({ children }: any) => (
    <del className="line-through text-ink-subtle">{children}</del>
  ),
  a: ({ href, children }: any) => (
    <a
      href={href}
      className="text-brand hover:underline"
      target="_blank"
      rel="noopener noreferrer"
    >
      {children}
    </a>
  ),
  img: ({ src, alt }: any) => (
    <img
      src={src}
      alt={alt ?? ''}
      className="max-w-full rounded-xl my-3 border border-border"
    />
  ),
  hr: () => <hr className="border-border my-5" />,
  // ── GFM Tables ─────────────────────────────────────────────────────────────
  table: ({ children }: any) => (
    <div className="overflow-x-auto mb-4 rounded-xl border border-border">
      <table className="w-full text-sm border-collapse">{children}</table>
    </div>
  ),
  thead: ({ children }: any) => (
    <thead className="bg-surface-alt">{children}</thead>
  ),
  tbody: ({ children }: any) => (
    <tbody className="divide-y divide-border">{children}</tbody>
  ),
  tr: ({ children }: any) => (
    <tr className="hover:bg-surface-alt/50 transition-colors">{children}</tr>
  ),
  th: ({ children, style }: any) => (
    <th
      className="text-left text-xs font-semibold text-ink px-3 py-2.5 border-b border-border"
      style={style}
    >
      {children}
    </th>
  ),
  td: ({ children, style }: any) => (
    <td className="text-xs text-ink-secondary px-3 py-2.5" style={style}>{children}</td>
  ),
}
/* eslint-enable @typescript-eslint/no-explicit-any */

// ── SKILL.md frontmatter types & parser ──────────────────────────────────────

interface SkillFrontmatter {
  name?: string
  description?: string
  license?: string
  compatibility?: string
  metadata?: Record<string, string>
  'allowed-tools'?: string
}

/**
 * Browser-compatible SKILL.md frontmatter parser.
 *
 * Handles all real-world YAML patterns used by Anthropic, Vercel, Remotion,
 * and Microsoft Azure skills repos — without any Node.js deps.
 *
 * Supported formats:
 *   1. Single-line value:          key: value
 *   2. Single-line quoted:         key: "value"  /  key: 'value'
 *   3. Multi-line unquoted (Anthropic): value continues on next lines until
 *      the next top-level key or blank line
 *   4. Multi-line double-quoted (Microsoft/pptx): "opening quote ...
 *      ... continuation lines ... closing quote"
 *   5. Block mapping (metadata:):  indented key: value pairs beneath the key
 */
function parseSkillContent(raw: string): { fm: SkillFrontmatter; body: string } {
  const trimmed = raw.trimStart()
  if (!trimmed.startsWith('---')) return { fm: {}, body: raw }

  const rest = trimmed.slice(3) // strip opening ---
  const closeIdx = rest.search(/\n---[ \t]*(\r?\n|$)/)
  if (closeIdx === -1) return { fm: {}, body: raw }

  const yamlBlock = rest.slice(0, closeIdx)
  const body = rest.slice(closeIdx).replace(/^\n---[ \t]*(\r?\n)?/, '').trimStart()

  return { fm: parseSimpleYaml(yamlBlock), body }
}

/** Returns true if `line` begins a new top-level YAML key (e.g. "name: "). */
function isTopLevelKey(line: string): boolean {
  return /^[a-zA-Z][a-zA-Z0-9-]*\s*:/.test(line)
}

/** Returns true if `line` is indented (sub-object entry). */
function isIndented(line: string): boolean {
  return /^[ \t]+\S/.test(line)
}

function parseSimpleYaml(yaml: string): SkillFrontmatter {
  const fm: SkillFrontmatter = {}
  const lines = yaml.split(/\r?\n/)
  let i = 0

  while (i < lines.length) {
    const line = lines[i]
    if (!line.trim() || line.trim().startsWith('#')) { i++; continue }

    const topMatch = line.match(/^([a-zA-Z][a-zA-Z0-9-]*)\s*:\s*(.*)?$/)
    if (!topMatch) { i++; continue }

    const key = topMatch[1]
    let inlineVal = topMatch[2]?.trim() ?? ''
    i++

    if (!inlineVal) {
      // ── Block mapping (e.g. metadata:) ──────────────────────────────────
      const subObj: Record<string, string> = {}
      while (i < lines.length && isIndented(lines[i])) {
        const subMatch = lines[i].match(/^[ \t]+([a-zA-Z][a-zA-Z0-9-]*)\s*:\s*(.*)?$/)
        if (subMatch) {
          subObj[subMatch[1]] = (subMatch[2] ?? '').trim().replace(/^["']|["']$/g, '')
        }
        i++
      }
      if (Object.keys(subObj).length > 0) {
        ;(fm as Record<string, unknown>)[key] = subObj
      }
      continue
    }

    // ── Quoted multi-line (Microsoft / pptx style) ───────────────────────
    const openQuote = inlineVal[0]
    if (openQuote === '"' || openQuote === "'") {
      // Check if the quote is already closed on this same line
      const tail = inlineVal.slice(1)
      const closeOnSameLine =
        tail.length > 0 &&
        tail.endsWith(openQuote) &&
        !tail.endsWith('\\' + openQuote)

      if (closeOnSameLine) {
        // e.g. version: "1.0"
        ;(fm as Record<string, unknown>)[key] = tail
          .slice(0, -1)
          .replace(/\\"/g, '"')
          .replace(/\\'/g, "'")
      } else {
        // Opening quote without closing → multi-line quoted scalar
        const parts: string[] = [tail]
        let closed = false
        while (i < lines.length && !closed) {
          const nextLine = lines[i]
          i++
          const trimmedEnd = nextLine.trimEnd()
          if (
            trimmedEnd.endsWith(openQuote) &&
            !trimmedEnd.endsWith('\\' + openQuote)
          ) {
            parts.push(trimmedEnd.slice(0, -1).trim())
            closed = true
          } else {
            parts.push(nextLine.trim())
          }
        }
        ;(fm as Record<string, unknown>)[key] = parts
          .filter((s) => s.length > 0)
          .join(' ')
          .replace(/\\"/g, '"')
          .replace(/\\'/g, "'")
      }
      continue
    }

    // ── Plain scalar — may continue on subsequent non-key lines ──────────
    // (Anthropic style: description wraps across multiple bare lines)
    const parts: string[] = [inlineVal]
    while (i < lines.length) {
      const nextLine = lines[i]
      const trimmedNext = nextLine.trim()
      if (!trimmedNext) break               // blank line terminates
      if (isTopLevelKey(nextLine)) break    // next key terminates
      if (isIndented(nextLine)) break       // indented block terminates
      parts.push(trimmedNext)
      i++
    }
    ;(fm as Record<string, unknown>)[key] = parts.join(' ')
  }

  return fm
}

// ── Metadata summary table ────────────────────────────────────────────────────

function SkillMetaTable({ fm }: { fm: SkillFrontmatter }) {
  type Row = { label: string; value: React.ReactNode }
  const rows: Row[] = []

  if (fm.name) {
    rows.push({
      label: 'name',
      value: <code className="font-mono text-xs text-brand">{fm.name}</code>,
    })
  }
  if (fm.description) {
    rows.push({ label: 'description', value: fm.description })
  }
  if (fm.license) {
    rows.push({ label: 'license', value: fm.license })
  }
  if (fm.compatibility) {
    rows.push({ label: 'compatibility', value: fm.compatibility })
  }
  if (fm['allowed-tools']) {
    const tools = fm['allowed-tools'].split(/\s+/).filter(Boolean)
    rows.push({
      label: 'allowed-tools',
      value: (
        <div className="flex flex-wrap gap-1">
          {tools.map((t) => (
            <span
              key={t}
              className="inline-flex items-center text-[11px] font-mono px-2 py-0.5 rounded-md bg-brand/10 text-brand border border-brand/20"
            >
              {t}
            </span>
          ))}
        </div>
      ),
    })
  }
  if (fm.metadata && typeof fm.metadata === 'object') {
    Object.entries(fm.metadata).forEach(([k, v]) => {
      rows.push({ label: k, value: String(v) })
    })
  }

  if (rows.length === 0) return null

  return (
    <div className="mb-6 rounded-xl border border-border overflow-hidden">
      <div className="px-3 py-2 bg-surface-alt border-b border-border">
        <span className="text-xs font-semibold text-ink-subtle uppercase tracking-wide">Skill Metadata</span>
      </div>
      <table className="w-full text-sm">
        <tbody className="divide-y divide-border">
          {rows.map(({ label, value }) => (
            <tr key={label} className="hover:bg-surface-alt/40 transition-colors">
              <td className="text-xs font-mono font-medium text-ink-subtle pl-3 pr-4 py-2.5 w-36 whitespace-nowrap align-top">
                {label}
              </td>
              <td className="text-xs text-ink-secondary pr-3 py-2.5 leading-relaxed align-top">
                {value}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

// ── Component ─────────────────────────────────────────────────────────────────

interface SkillDetailPageProps {
  skill: SkillMeta
  installedRecord?: InstalledSkillInfo
  githubPat?: string
  onBack: () => void
  onInstall: (skill: SkillMeta) => void
  onUninstall: (installId: number) => void
}

export function SkillDetailPage({
  skill,
  installedRecord,
  githubPat,
  onBack,
  onInstall,
  onUninstall,
}: SkillDetailPageProps) {
  const { readmeCache, readmeLoading, readmeErrors, fetchReadme, clearReadmeError } = useSkillsStore()
  const skillId = `${skill.sourceId}/${skill.skillName}`
  const content = readmeCache[skillId]
  const isLoading = readmeLoading[skillId] ?? false
  // Use error from store (set by both background fetch and explicit fetch)
  const fetchError = readmeErrors[skillId] || null

  const parsed = useMemo(
    () => (content != null && content.length > 0 ? parseSkillContent(content) : null),
    [content],
  )

  useEffect(() => {
    // Only fetch if not cached and not already in-flight
    if (content !== undefined) return
    fetchReadme(skill.sourceId, skill.skillName, githubPat)
  }, [skill.sourceId, skill.skillName, githubPat, content, fetchReadme])

  const handleRetry = () => {
    clearReadmeError(skill.sourceId, skill.skillName)
    fetchReadme(skill.sourceId, skill.skillName, githubPat)
  }

  return (
    <div className="flex-1 flex flex-col overflow-hidden">
      {/* ── Header ─────────────────────────────────────────────────────── */}
      <div className="px-5 pt-5 pb-4 border-b border-border flex items-center gap-3 shrink-0">
        <button
          type="button"
          onClick={onBack}
          className="p-1.5 rounded-xl text-ink-secondary hover:bg-surface-alt hover:text-ink transition-colors shrink-0"
          title="返回"
        >
          <ArrowLeft className="w-4 h-4" />
        </button>

        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <SourceLogoInline sourceId={skill.sourceId} />
            <h1 className="text-base font-bold text-ink truncate">{skill.displayName}</h1>
          </div>
          <p className="text-xs text-ink-subtle mt-0.5 font-mono">{skill.sourceId}</p>
          {skill.description && (
            <p className="text-xs text-ink-secondary mt-1.5 leading-relaxed line-clamp-2">{skill.description}</p>
          )}
        </div>

        <div className="shrink-0">
          {installedRecord ? (
            <div className="flex items-center gap-2">
              <span className="flex items-center gap-1 text-xs font-medium text-brand bg-brand/10 rounded-full px-2.5 py-1">
                <CheckCircle2 className="w-3.5 h-3.5" />
                已安裝
              </span>
              <button
                type="button"
                onClick={() => onUninstall(installedRecord.id)}
                className="text-xs text-ink-secondary hover:text-red-500 transition-colors px-2"
              >
                移除
              </button>
            </div>
          ) : (
            <button
              type="button"
              onClick={() => onInstall(skill)}
              className="flex items-center gap-1.5 text-sm font-medium px-4 py-2 rounded-xl bg-brand text-white hover:bg-brand/90 transition-colors"
            >
              <Download className="w-4 h-4" />
              安裝
            </button>
          )}
        </div>
      </div>

      {/* ── Content ────────────────────────────────────────────────────── */}
      <div className="flex-1 overflow-y-auto px-6 py-5">
        {isLoading && (
          <div className="space-y-3 max-w-2xl">
            {[80, 60, 100, 75, 50, 90].map((w, i) => (
              <div
                key={i}
                className="h-3 rounded bg-surface-alt animate-pulse"
                style={{ width: `${w}%` }}
              />
            ))}
          </div>
        )}

        {fetchError && !isLoading && (
          <div className="rounded-2xl border border-red-200 bg-red-50 px-4 py-3 mb-4">
            <p className="text-sm font-medium text-red-700 mb-1">載入失敗</p>
            <p className="text-xs text-red-600 font-mono break-all">{fetchError}</p>
            <button
              type="button"
              onClick={handleRetry}
              className="mt-2 text-xs text-red-600 underline hover:no-underline"
            >
              重試
            </button>
          </div>
        )}

        {!isLoading && !fetchError && content !== undefined && (
          parsed != null ? (
            <div className="max-w-2xl mx-auto">
              <SkillMetaTable fm={parsed.fm} />
              {parsed.body.length > 0 && (
                <Markdown
                  components={mdComponents}
                  remarkPlugins={[remarkGfm]}
                  rehypePlugins={[rehypeRaw]}
                >
                  {parsed.body}
                </Markdown>
              )}
            </div>
          ) : (
            <p className="text-sm text-ink-subtle">此 skill 沒有可顯示的內容。</p>
          )
        )}
      </div>
    </div>
  )
}

// ── Inline source logo (reused in detail header) ──────────────────────────────

const SOURCE_META: Record<string, { avatarUrl: string; fallbackBg: string; letter: string }> = {
  'vercel-labs':    { avatarUrl: 'https://github.com/vercel.png?size=32',     fallbackBg: 'bg-black',       letter: 'V' },
  'anthropics':     { avatarUrl: 'https://github.com/anthropics.png?size=32', fallbackBg: 'bg-[#C17E61]',   letter: 'A' },
  'remotion':       { avatarUrl: 'https://github.com/remotion-dev.png?size=32', fallbackBg: 'bg-[#0b84f3]', letter: 'R' },
  'microsoft-azure':{ avatarUrl: 'https://github.com/microsoft.png?size=32',  fallbackBg: 'bg-[#0078D4]',  letter: 'M' },
  'superpowers':      { avatarUrl: 'https://github.com/obra.png?size=32',       fallbackBg: 'bg-[#6f42c1]',   letter: 'S' },
}

function SourceLogoInline({ sourceId }: { sourceId: string }) {
  const meta = SOURCE_META[sourceId]
  const [imgError, setImgError] = useState(false)

  if (!meta || imgError) {
    return (
      <div
        className={cn(
          'w-6 h-6 rounded-lg flex items-center justify-center text-white text-[10px] font-bold shrink-0',
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
      className="w-6 h-6 rounded-lg object-cover shrink-0"
      onError={() => setImgError(true)}
    />
  )
}
