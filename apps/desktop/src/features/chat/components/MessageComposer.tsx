import { type KeyboardEvent, memo, useEffect, useState } from 'react'
import { open } from '@tauri-apps/plugin-dialog'
import { FileText, Folder, GitCompare, Loader2, Mic, Paperclip, Send, Square, TriangleAlert, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { useSpeechToText } from '../hooks/useSpeechToText'
import { ProviderModelPicker } from './ProviderModelPicker'
import { AgentModeSelector } from './AgentModeSelector'
import type { AgentMode } from '@modern-wingman/contracts'
import { previewContext, type ContextPreview } from '@/services/agent-api/context'
import type { AttachmentReference } from '@/services/agent-api/client'

interface MessageComposerProps {
  selectedProviderId: string | null
  selectedModel: string | null
  value: string
  onChange: (value: string) => void
  onProviderChange: (id: string | null) => void
  onModelChange: (model: string | null) => void
  onSubmit: (text: string, attachments:AttachmentReference[]) => void | Promise<void>
  onCancel?: () => void
  busy?: boolean
  disabled?: boolean
  placeholder?: string
  containerClassName?: string
  innerClassName?: string
  agentMode?: AgentMode
  onAgentModeChange?: (mode: AgentMode) => void
  workspacePath?: string
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
  agentMode,
  onAgentModeChange,
  workspacePath,
}: MessageComposerProps) {
  const [contextPreview,setContextPreview]=useState<ContextPreview|null>(null)
  const [attachments,setAttachments]=useState<AttachmentReference[]>([])
  useEffect(()=>{if(!workspacePath||!/@(?:file|folder)(?::|\s)|@diff/i.test(value)){setContextPreview(null);return}const controller=new AbortController();const timer=setTimeout(()=>{void previewContext(value,workspacePath,controller.signal).then(setContextPreview).catch(()=>setContextPreview(null))},350);return()=>{clearTimeout(timer);controller.abort()}},[value,workspacePath])
  const speech = useSpeechToText((text) => {
    const trimmed = value.trim()
    onChange(trimmed ? `${trimmed} ${text}` : text)
  })

  const canSubmit = !busy && !disabled && (!!value.trim() || attachments.length>0)
  const showCancel = busy && !!onCancel

  const handleSubmit = () => {
    const trimmed = value.trim()
    if ((!trimmed && attachments.length===0) || busy || disabled) return
    onChange('')
    const selected=attachments
    setAttachments([])
    void onSubmit(trimmed,selected)
  }

  const chooseAttachments=async()=>{const selected=await open({multiple:true,title:'選擇附件',filters:[{name:'支援的附件',extensions:['png','jpg','jpeg','gif','webp','pdf','docx','txt','md','csv','json','xml','yaml','yml','cs','java','js','ts','tsx','py']}]});const paths=Array.isArray(selected)?selected:typeof selected==='string'?[selected]:[];setAttachments(current=>[...current,...paths.filter(path=>!current.some(item=>item.path===path)).slice(0,5-current.length).map(path=>({path,name:path.split(/[\\/]/).pop()??path,mediaType:null}))])}

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
        {contextPreview&&contextPreview.sources.length>0&&<div className="flex flex-wrap items-center gap-1.5 text-xs text-ink-subtle">{contextPreview.sources.map((source,index)=><span key={`${source.path}-${index}`} className="inline-flex max-w-52 items-center gap-1 border border-border bg-surface-alt px-2 py-1" title={source.path}>{source.kind==='folder'?<Folder className="h-3 w-3"/>:source.kind==='diff'?<GitCompare className="h-3 w-3"/>:<FileText className="h-3 w-3"/>}<span className="truncate">{source.path.split(/[\\/]/).pop()}</span></span>)}<span>約 {contextPreview.estimatedTokens.toLocaleString()} tokens</span>{contextPreview.truncated&&<span className="inline-flex items-center gap-1 text-warning"><TriangleAlert className="h-3 w-3"/>內容已截斷</span>}</div>}
        {attachments.length>0&&<div className="flex flex-wrap gap-1.5">{attachments.map(item=><span key={item.path} className="inline-flex max-w-52 items-center gap-1 border border-border bg-surface-alt px-2 py-1 text-xs text-ink-secondary" title={item.path}><FileText className="h-3 w-3"/><span className="truncate">{item.name}</span><button type="button" title="移除附件" onClick={()=>setAttachments(current=>current.filter(value=>value.path!==item.path))}><X className="h-3 w-3"/></button></span>)}</div>}
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
              {agentMode && onAgentModeChange && (
                <AgentModeSelector
                  value={agentMode}
                  onChange={onAgentModeChange}
                  disabled={busy || disabled}
                />
              )}
            </div>
            <div className="ml-auto flex shrink-0 items-center gap-1">
              <Button size="icon" variant="ghost" title="附加檔案" onClick={()=>void chooseAttachments()} disabled={busy||disabled||attachments.length>=5}><Paperclip className="h-4 w-4"/></Button>
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
