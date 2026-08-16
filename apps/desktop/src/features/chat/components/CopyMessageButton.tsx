import { useState } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { renderToStaticMarkup } from 'react-dom/server'
import { Check, Copy } from 'lucide-react'
import { cn } from '@/lib/utils'
import type { LocalMessage } from '../store/useChatStore'

interface CopyMessageButtonProps {
  message: LocalMessage
}

/**
 * 複製整則訊息，並盡量保留表格／程式碼區塊等結構：
 * 助手訊息以 Markdown 渲染出「行內樣式」的 HTML 複製（貼到 Word 時仍保有框線與底色，
 * 因為 Word 不會套用我們的 Tailwind 樣式表，只認得 inline style），
 * 使用者訊息則以純文字＋保留換行的 HTML 複製。
 */
export function CopyMessageButton({ message }: CopyMessageButtonProps) {
  const [copied, setCopied] = useState(false)

  const handleCopy = async () => {
    const html = message.role === 'assistant'
      ? renderMarkdownToClipboardHtml(message.content)
      : plainTextToHtml(message.content)
    await copyRichText(html, message.content)
    setCopied(true)
    setTimeout(() => setCopied(false), 2000)
  }

  return (
    <button
      type="button"
      onClick={() => void handleCopy()}
      title={copied ? '已複製' : '複製整則訊息'}
      className={cn(
        'flex items-center gap-1 rounded px-1.5 py-1 text-xs transition-colors duration-150',
        copied ? 'text-brand-green' : 'text-ink-subtle hover:bg-surface-alt hover:text-ink',
      )}
    >
      {copied ? <Check className="h-3.5 w-3.5" /> : <Copy className="h-3.5 w-3.5" />}
    </button>
  )
}

async function copyRichText(html: string, plainText: string): Promise<void> {
  if (typeof ClipboardItem !== 'undefined' && navigator.clipboard?.write) {
    try {
      await navigator.clipboard.write([
        new ClipboardItem({
          'text/html': new Blob([html], { type: 'text/html' }),
          'text/plain': new Blob([plainText], { type: 'text/plain' }),
        }),
      ])
      return
    } catch {
      // 部分環境可能拒絕 text/html，退回純文字。
    }
  }
  await navigator.clipboard.writeText(plainText)
}

function plainTextToHtml(text: string): string {
  return `<div>${escapeHtml(text).replace(/\n/g, '<br />')}</div>`
}

function escapeHtml(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
}

function renderMarkdownToClipboardHtml(content: string): string {
  return renderToStaticMarkup(
    <ReactMarkdown
      remarkPlugins={[remarkGfm]}
      components={{
        table({ children }) {
          return (
            <table style={{ borderCollapse: 'collapse', width: '100%', fontSize: 13 }}>
              {children}
            </table>
          )
        },
        th({ children }) {
          return (
            <th
              style={{
                border: '1px solid #999',
                background: '#f2f2f2',
                padding: '6px 10px',
                textAlign: 'left',
                fontWeight: 600,
              }}
            >
              {children}
            </th>
          )
        },
        td({ children }) {
          return (
            <td style={{ border: '1px solid #999', padding: '6px 10px', verticalAlign: 'top' }}>
              {children}
            </td>
          )
        },
        code({ className, children }) {
          const match = /language-(\w+)/.exec(className ?? '')
          const isBlock = !!match || String(children).includes('\n')
          const codeText = String(children).replace(/\n$/, '')

          if (!isBlock) {
            return (
              <code
                style={{
                  background: '#f0f0f0',
                  padding: '1px 4px',
                  borderRadius: 3,
                  fontFamily: 'Consolas, monospace',
                  fontSize: '0.9em',
                }}
              >
                {children}
              </code>
            )
          }

          return (
            <pre
              style={{
                background: '#f5f5f5',
                border: '1px solid #ddd',
                borderRadius: 6,
                padding: 10,
                fontFamily: 'Consolas, monospace',
                fontSize: 13,
                margin: '8px 0',
                whiteSpace: 'pre-wrap',
              }}
            >
              <code>{codeText}</code>
            </pre>
          )
        },
        blockquote({ children }) {
          return (
            <blockquote style={{ borderLeft: '3px solid #999', paddingLeft: 10, margin: '8px 0', color: '#555' }}>
              {children}
            </blockquote>
          )
        },
        p({ children }) {
          return <p style={{ margin: '4px 0' }}>{children}</p>
        },
        ul({ children }) {
          return <ul style={{ margin: '4px 0', paddingLeft: 20 }}>{children}</ul>
        },
        ol({ children }) {
          return <ol style={{ margin: '4px 0', paddingLeft: 20 }}>{children}</ol>
        },
        h1({ children }) {
          return <h1 style={{ fontSize: 18, fontWeight: 700, margin: '12px 0 6px' }}>{children}</h1>
        },
        h2({ children }) {
          return <h2 style={{ fontSize: 16, fontWeight: 700, margin: '10px 0 4px' }}>{children}</h2>
        },
        h3({ children }) {
          return <h3 style={{ fontSize: 14, fontWeight: 600, margin: '8px 0 4px' }}>{children}</h3>
        },
      }}
    >
      {content}
    </ReactMarkdown>,
  )
}
