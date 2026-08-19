import { useEffect, useRef, useState } from 'react'
import { Bot, Plus, Trash2, User, XCircle } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { MarkdownRenderer } from '@/components/ui/markdown-renderer'
import { cn } from '@/lib/utils'
import { useChatStore } from '../store/useChatStore'
import { ActivityTimeline } from './ActivityTimeline'
import { CopyMessageButton } from './CopyMessageButton'
import { MessageComposer } from './MessageComposer'

interface ConversationPaneProps {
  title: string
  emptyText: string
  onNewConversation: () => void | Promise<void>
  onDeleteConversation?: () => void | Promise<void>
}

/**
 * 一般聊天與專案解析共用的完整對話畫面。
 * 訊息、Provider／Model、附件、語音與停止串流只在此實作一次。
 */
export function ConversationPane({
  title,
  emptyText,
  onNewConversation,
  onDeleteConversation,
}: ConversationPaneProps) {
  const [providerId, setProviderId] = useState<string | null>(null)
  const [modelId, setModelId] = useState<string | null>(null)
  const [input, setInput] = useState('')
  const endRef = useRef<HTMLDivElement>(null)
  const {
    messages,
    isStreaming,
    lastError,
    send,
    cancelStreaming,
    retryLast,
    clearLastError,
  } = useChatStore()

  useEffect(() => {
    endRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages, isStreaming])

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <header className="flex items-center justify-between border-b border-border bg-surface px-6 py-3.5">
        <div className="flex min-w-0 items-center gap-3">
          <div className="flex h-8 w-8 shrink-0 items-center justify-center rounded-xl bg-brand/10">
            <Bot className="h-4 w-4 text-brand" />
          </div>
          <div className="min-w-0">
            <p className="truncate text-sm font-semibold text-ink">{title}</p>
            <p className="text-xs font-medium text-brand-green">
              {isStreaming ? '● 回應中…' : '● 就緒'}
            </p>
          </div>
        </div>
        <div className="flex items-center gap-1">
          <Button
            variant="ghost"
            size="icon"
            title={isStreaming ? '回答完成後才能新增對話' : '新增對話'}
            disabled={isStreaming}
            onClick={() => void onNewConversation()}
          >
            <Plus className="h-4 w-4" />
          </Button>
          {onDeleteConversation && (
            <Button
              variant="ghost"
              size="icon"
              title={isStreaming ? '回答完成後才能刪除對話' : '刪除對話'}
              disabled={isStreaming}
              onClick={() => void onDeleteConversation()}
            >
              <Trash2 className="h-4 w-4 text-red-400" />
            </Button>
          )}
        </div>
      </header>

      <div className="flex-1 space-y-4 overflow-y-auto px-6 py-5">
        {messages.length === 0 && (
          <div className="flex h-full flex-col items-center justify-center gap-3 text-center text-ink-subtle">
            <Bot className="h-10 w-10 opacity-30" />
            <p className="text-sm">{emptyText}</p>
          </div>
        )}
        {messages.map((message) => (
          <div
            key={message.id}
            className={cn(
              'group flex gap-3',
              message.role === 'user' ? 'flex-row-reverse' : 'flex-row',
            )}
          >
            <div
              className={cn(
                'flex h-8 w-8 shrink-0 items-center justify-center rounded-xl',
                message.role === 'assistant'
                  ? 'bg-brand/10 text-brand'
                  : 'bg-ink/10 text-ink',
              )}
            >
              {message.role === 'assistant'
                ? <Bot className="h-4 w-4" />
                : <User className="h-4 w-4" />}
            </div>
            <div className="flex min-w-0 max-w-[78%] flex-col">
              <div
                className={cn(
                  'rounded-2xl px-4 py-3 text-sm leading-relaxed',
                  message.role === 'assistant'
                    ? 'bg-surface text-ink shadow-sm'
                    : 'bg-brand text-white',
                )}
              >
                {message.role === 'assistant'
                  ? <>
                      <ActivityTimeline
                        activities={message.activities ?? []}
                        streaming={message.streaming ?? false}
                      />
                      <MarkdownRenderer content={message.content} streaming={message.streaming} />
                      {message.incomplete && (
                        <p className="mt-2 text-xs font-medium text-red-500">
                          回答未完成，請參考下方錯誤說明。
                        </p>
                      )}
                    </>
                  : <p className="whitespace-pre-wrap">{message.content}</p>}
                <p
                  className={cn(
                    'mt-1.5 text-xs',
                    message.role === 'assistant' ? 'text-ink-subtle' : 'text-white/60',
                  )}
                >
                  {new Date(message.createdAt).toLocaleTimeString('zh-TW', {
                    hour: '2-digit',
                    minute: '2-digit',
                  })}
                </p>
              </div>
              <div className="mt-1 flex items-center opacity-0 transition-opacity group-hover:opacity-100 focus-within:opacity-100">
                <CopyMessageButton message={message} />
              </div>
            </div>
          </div>
        ))}
        <div ref={endRef} />
      </div>

      {lastError && (
        <div className="mx-6 mb-3 flex items-start gap-2 border border-red-400/40 bg-surface p-3">
          <XCircle className="mt-0.5 h-4 w-4 shrink-0 text-red-500" />
          <div className="min-w-0 flex-1">
            <p className="text-sm text-ink">
              {messages[messages.length - 1]?.incomplete
                ? `回答未完成：${lastError.message}`
                : lastError.message}
            </p>
            <div className="mt-2 flex gap-2">
              {lastError.retryable && (
                <Button
                  size="sm"
                  variant="outline"
                  disabled={isStreaming}
                  onClick={() => void retryLast()}
                >
                  重試
                </Button>
              )}
              <Button size="sm" variant="ghost" onClick={clearLastError}>
                關閉
              </Button>
            </div>
          </div>
        </div>
      )}

      <MessageComposer
        selectedProviderId={providerId}
        selectedModel={modelId}
        value={input}
        onChange={setInput}
        onProviderChange={setProviderId}
        onModelChange={setModelId}
        onSubmit={(text, attachments) => send(text, providerId, modelId, attachments)}
        onCancel={cancelStreaming}
        busy={isStreaming}
      />
    </div>
  )
}
