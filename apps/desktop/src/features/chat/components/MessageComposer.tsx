import { type ChangeEvent, type KeyboardEvent, memo, useRef, useState } from 'react'
import { FileText, Loader2, Mic, Paperclip, Send, Square, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { useSpeechToText } from '../hooks/useSpeechToText'
import { ProviderModelPicker } from './ProviderModelPicker'
import type { AttachmentReference } from '@/services/agent-api/client'

interface MessageComposerProps {
  selectedProviderId: string | null
  selectedModel: string | null
  value: string
  onChange: (value: string) => void
  onProviderChange: (id: string | null) => void
  onModelChange: (model: string | null) => void
  onSubmit: (text: string, attachments: AttachmentReference[]) => void | Promise<void>
  onCancel?: () => void
  busy?: boolean
  disabled?: boolean
  placeholder?: string
  containerClassName?: string
  innerClassName?: string
}

export const MessageComposer = memo(function MessageComposer({
  selectedProviderId,
  selectedModel,
  value,
  onChange,
  onProviderChange,
  onModelChange,
  onSubmit,
  onCancel,
  busy = false,
  disabled = false,
  placeholder = '輸入訊息… (Enter 送出，Shift+Enter 換行)',
  containerClassName = 'shrink-0 px-6 py-4 bg-surface border-t border-border',
  innerClassName = 'max-w-3xl mx-auto space-y-2',
}: MessageComposerProps) {
  const [attachments, setAttachments] = useState<AttachmentReference[]>([])
  const [attachmentError, setAttachmentError] = useState<string | null>(null)
  const attachmentInput = useRef<HTMLInputElement>(null)
  const speech = useSpeechToText((text) => {
    const trimmed = value.trim()
    onChange(trimmed ? `${trimmed} ${text}` : text)
  })

  const canSubmit = !busy && !disabled && (!!value.trim() || attachments.length > 0)
  const showCancel = busy && !!onCancel

  const handleSubmit = () => {
    const trimmed = value.trim()
    if ((!trimmed && attachments.length === 0) || busy || disabled) return
    onChange('')
    const selected = attachments
    setAttachments([])
    void onSubmit(trimmed, selected)
  }

  /**
   * 使用瀏覽器 File API 讀取使用者實際選取的內容，不把本機絕對路徑送給後端。
   * 這可在保留 CORS AllowAny 的前提下，避免 localhost API 成為任意檔案讀取入口。
   */
  const chooseAttachments = () => {
    attachmentInput.current?.click()
  }

  const handleAttachmentChange = async (event: ChangeEvent<HTMLInputElement>) => {
    const files = [...(event.target.files ?? [])]
    event.target.value = ''
    if (files.length === 0) return

    setAttachmentError(null)
    const availableSlots = Math.max(0, 5 - attachments.length)
    const selected = files
      .filter((file) => !attachments.some((item) => item.name === file.name))
      .slice(0, availableSlots)

    if (selected.some((file) => file.size > 10 * 1024 * 1024)) {
      setAttachmentError('單一附件不可超過 10 MB。')
      return
    }
    const currentBytes = attachments.reduce(
      (sum, item) => sum + Math.floor(item.contentBase64.length * 3 / 4),
      0,
    )
    if (currentBytes + selected.reduce((sum, file) => sum + file.size, 0) > 20 * 1024 * 1024) {
      setAttachmentError('單次問題的附件總容量不可超過 20 MB。')
      return
    }

    const additions = await Promise.all(selected.map(async (file) => ({
      name: file.name,
      mediaType: file.type || null,
      contentBase64: arrayBufferToBase64(await file.arrayBuffer()),
    })))
    setAttachments((current) => [...current, ...additions].slice(0, 5))
  }

  const handleKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault()
      handleSubmit()
    }
  }

  return (
    <div className={containerClassName}>
      <div className={innerClassName}>
        {speech.error && (
          <p className="text-xs text-red-500">{speech.error}</p>
        )}
        {attachmentError && (
          <p className="text-xs text-red-500">{attachmentError}</p>
        )}
        <input
          ref={attachmentInput}
          className="hidden"
          type="file"
          multiple
          accept=".png,.jpg,.jpeg,.gif,.webp,.pdf,.docx,.txt,.md,.csv,.json,.xml,.yaml,.yml,.cs,.java,.js,.ts,.tsx,.py"
          onChange={(event) => void handleAttachmentChange(event)}
        />
        {attachments.length > 0 && (
          <div className="flex flex-wrap gap-1.5">
            {attachments.map((item) => (
              <span
                key={item.name}
                className="inline-flex max-w-52 items-center gap-1 border border-border bg-surface-alt px-2 py-1 text-xs text-ink-secondary"
                title={item.name}
              >
                <FileText className="h-3 w-3" />
                <span className="truncate">{item.name}</span>
                <button
                  type="button"
                  title="移除附件"
                  onClick={() => setAttachments((current) =>
                    current.filter((value) => value.name !== item.name))}
                >
                  <X className="h-3 w-3" />
                </button>
              </span>
            ))}
          </div>
        )}
        <div className="rounded-xl border border-border bg-surface focus-within:ring-2 focus-within:ring-brand/40">
          <textarea
            rows={3}
            placeholder={placeholder}
            value={value}
            onChange={(e) => onChange(e.target.value)}
            onKeyDown={handleKeyDown}
            disabled={busy || disabled}
            className={cn(
              'block w-full min-h-[76px] max-h-40 resize-none rounded-t-xl bg-transparent px-3.5 pb-2 pt-3 text-sm leading-5',
              'placeholder:text-ink-subtle focus:outline-none',
              'disabled:cursor-not-allowed disabled:opacity-50',
            )}
          />
          <div className="flex flex-wrap items-end justify-between gap-2 px-2 pb-2">
            <div className="flex min-w-0 flex-wrap items-center gap-1.5">
              <ProviderModelPicker
                selectedProviderId={selectedProviderId}
                selectedModel={selectedModel}
                onProviderChange={onProviderChange}
                onModelChange={onModelChange}
              />
            </div>
            <div className="ml-auto flex shrink-0 items-center gap-1">
              <Button
                size="icon"
                variant="ghost"
                title="附加檔案"
                onClick={chooseAttachments}
                disabled={busy || disabled || attachments.length >= 5}
              >
                <Paperclip className="h-4 w-4" />
              </Button>
              {speech.available && !busy && !disabled && (
                <Button
                  size="icon"
                  variant={speech.state === 'recording' ? 'danger' : 'ghost'}
                  onClick={speech.toggleRecording}
                  disabled={speech.state === 'transcribing'}
                  className={cn(speech.state === 'recording' && 'animate-pulse')}
                  title={speech.state === 'recording' ? '停止錄音' : 'Speech to text'}
                >
                  {speech.state === 'transcribing' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Mic className="h-4 w-4" />}
                </Button>
              )}
              {showCancel ? (
                <Button size="icon" variant="ghost" title="停止回應" onClick={onCancel} className="text-red-400"><Square className="h-4 w-4 fill-current" /></Button>
              ) : (
                <Button size="icon" title="送出" onClick={handleSubmit} disabled={!canSubmit}>
                  {busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />}
                </Button>
              )}
            </div>
          </div>
        </div>
      </div>
    </div>
  )
})

/**
 * 分段轉換可避免大型附件在展開運算子時造成 JavaScript call stack overflow。
 */
function arrayBufferToBase64(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer)
  const chunkSize = 0x8000
  let binary = ''
  for (let offset = 0; offset < bytes.length; offset += chunkSize) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + chunkSize))
  }
  return btoa(binary)
}
