import { useState } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { Prism as SyntaxHighlighter } from 'react-syntax-highlighter'
import { oneDark } from 'react-syntax-highlighter/dist/esm/styles/prism'
import { Check, Copy } from 'lucide-react'
import { cn } from '@/lib/utils'

/* ── CopyButton ── */
function CopyButton({ code }: { code: string }) {
  const [copied, setCopied] = useState(false)

  const handleCopy = async () => {
    await navigator.clipboard.writeText(code)
    setCopied(true)
    setTimeout(() => setCopied(false), 2000)
  }

  return (
    <button
      type="button"
      onClick={handleCopy}
      className={cn(
        'flex items-center gap-1 px-2 py-1 rounded text-xs transition-colors duration-150',
        copied
          ? 'text-brand-green'
          : 'text-zinc-400 hover:text-zinc-200',
      )}
      title={copied ? '已複製' : '複製'}
    >
      {copied ? <Check className="w-3.5 h-3.5" /> : <Copy className="w-3.5 h-3.5" />}
      <span>{copied ? '已複製' : '複製'}</span>
    </button>
  )
}

/* ── MarkdownRenderer ── */
interface MarkdownRendererProps {
  content: string
  /** 串流游標：仍在回應中時傳入 true */
  streaming?: boolean
}

export function MarkdownRenderer({ content, streaming }: MarkdownRendererProps) {
  return (
    <div className="prose prose-sm prose-zinc dark:prose-invert max-w-none break-words">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        components={{
          /* ── 程式碼區塊 ── */
          code({ className, children, ...props }) {
            const match = /language-(\w+)/.exec(className ?? '')
            const language = match ? match[1] : ''
            const isBlock = !!match || String(children).includes('\n')
            const codeText = String(children).replace(/\n$/, '')

            if (!isBlock) {
              // 行內程式碼
              return (
                <code
                  className="px-1.5 py-0.5 rounded bg-zinc-800 text-zinc-200 text-[0.8em] font-mono"
                  {...props}
                >
                  {children}
                </code>
              )
            }

            return (
              <div className="not-prose my-3 rounded-xl overflow-hidden border border-zinc-700/60 bg-zinc-900">
                {/* 標題列 */}
                <div className="flex items-center justify-between px-4 py-2 bg-zinc-800/80 border-b border-zinc-700/60">
                  <span className="text-xs font-mono font-medium text-zinc-400 uppercase tracking-wide">
                    {language || 'text'}
                  </span>
                  <CopyButton code={codeText} />
                </div>

                {/* 程式碼主體 */}
                <SyntaxHighlighter
                  style={oneDark}
                  language={language || 'text'}
                  PreTag="div"
                  customStyle={{
                    margin: 0,
                    padding: '1rem',
                    background: 'transparent',
                    fontSize: '0.8rem',
                    lineHeight: '1.6',
                  }}
                  codeTagProps={{ style: { fontFamily: 'var(--font-mono, monospace)' } }}
                >
                  {codeText}
                </SyntaxHighlighter>
              </div>
            )
          },

          /* ── 段落 ── */
          p({ children }) {
            return <p className="mb-2 last:mb-0 leading-relaxed">{children}</p>
          },

          /* ── 清單 ── */
          ul({ children }) {
            return <ul className="mb-2 pl-5 space-y-1 list-disc">{children}</ul>
          },
          ol({ children }) {
            return <ol className="mb-2 pl-5 space-y-1 list-decimal">{children}</ol>
          },

          /* ── 標題 ── */
          h1({ children }) {
            return <h1 className="text-base font-bold mt-4 mb-2">{children}</h1>
          },
          h2({ children }) {
            return <h2 className="text-sm font-bold mt-3 mb-1">{children}</h2>
          },
          h3({ children }) {
            return <h3 className="text-sm font-semibold mt-2 mb-1">{children}</h3>
          },

          /* ── 引用 ── */
          blockquote({ children }) {
            return (
              <blockquote className="border-l-2 border-brand/40 pl-3 my-2 text-ink-secondary italic">
                {children}
              </blockquote>
            )
          },

          /* ── 表格 ── */
          table({ children }) {
            return (
              <div className="my-3 max-w-full overflow-x-auto rounded-lg border border-border">
                <table className="w-full text-xs border-collapse">{children}</table>
              </div>
            )
          },
          th({ children }) {
            return (
              // 使用主題 token，避免 Light／Dark／Glass 主題下沿用固定黑底造成對比失衡。
              <th className="border-b border-border bg-surface-alt px-3 py-1.5 text-left font-semibold text-ink">
                {children}
              </th>
            )
          },
          td({ children }) {
            return (
              <td className="border-b border-border px-3 py-1.5 align-top text-ink-secondary">{children}</td>
            )
          },
        }}
      >
        {content}
      </ReactMarkdown>

      {/* 串流游標 */}
      {streaming && (
        <span className="inline-block w-1.5 h-4 ml-0.5 bg-current animate-pulse align-text-bottom" />
      )}
    </div>
  )
}
