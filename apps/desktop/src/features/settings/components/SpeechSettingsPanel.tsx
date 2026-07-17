import { useCallback, useEffect, useMemo, useState } from 'react'
import { open } from '@tauri-apps/plugin-dialog'
import { Check, Download, FolderOpen, Loader2, Mic, RefreshCw, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import {
  downloadSpeechModel,
  getSpeechStatus,
  importSpeechModel,
  saveSpeechSettings,
  type SpeechStatus,
} from '@/services/agent-api/speech'

function formatBytes(value: number | null) {
  if (!value) return '尚未安裝'
  const mb = value / 1024 / 1024
  return `${mb.toFixed(mb >= 100 ? 0 : 1)} MB`
}

function toMessage(err: unknown) {
  return err instanceof Error ? err.message : String(err)
}

export function SpeechSettingsPanel() {
  const [status, setStatus] = useState<SpeechStatus | null>(null)
  const [selectedModelId, setSelectedModelId] = useState('small-q5_1')
  const [sourceMode, setSourceMode] = useState<'default' | 'custom'>('default')
  const [customUrl, setCustomUrl] = useState('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const selectedModel = useMemo(() => (
    status?.models.find((model) => model.id === selectedModelId)
  ), [selectedModelId, status?.models])

  const loadStatus = useCallback(async () => {
    setError(null)
    try {
      const next = await getSpeechStatus()
      setStatus(next)
      setSelectedModelId(next.activeModelId)
    } catch (err) {
      setError(toMessage(err))
    }
  }, [])

  useEffect(() => {
    void loadStatus()
  }, [loadStatus])

  const handleLanguageChange = async (language: SpeechStatus['language']) => {
    if (!status) return
    setStatus({ ...status, language })
    try {
      setStatus(await saveSpeechSettings({ language }))
    } catch (err) {
      setError(toMessage(err))
      void loadStatus()
    }
  }

  const handleModelChange = async (modelId: string) => {
    setSelectedModelId(modelId)
    try {
      setStatus(await saveSpeechSettings({ activeModelId: modelId }))
    } catch (err) {
      setError(toMessage(err))
    }
  }

  const handleDownload = async () => {
    setLoading(true)
    setError(null)
    try {
      const url = sourceMode === 'custom' ? customUrl.trim() : null
      setStatus(await downloadSpeechModel(selectedModelId, url))
    } catch (err) {
      setError(toMessage(err))
    } finally {
      setLoading(false)
    }
  }

  const handleImport = async () => {
    setLoading(true)
    setError(null)
    try {
      const file = await open({
        multiple: false,
        title: '選擇 whisper.cpp 模型檔',
        filters: [{ name: 'Whisper model', extensions: ['bin'] }],
      })
      if (!file || typeof file !== 'string') return
      setStatus(await importSpeechModel(file, selectedModelId))
    } catch (err) {
      setError(toMessage(err))
    } finally {
      setLoading(false)
    }
  }

  if (!status) {
    return (
      <div className="flex items-center gap-2 text-sm text-ink-subtle">
        <Loader2 className="w-4 h-4 animate-spin" />
        讀取語音輸入設定中
        {error && <span className="text-red-500">{error}</span>}
      </div>
    )
  }

  return (
    <div className="space-y-5">
      <div className="flex items-start gap-3 rounded-xl border border-border bg-surface-alt px-4 py-3">
        <div
          className={cn(
            'mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-lg',
            status.ready ? 'bg-brand-green/10 text-brand-green' : 'bg-warning/10 text-warning',
          )}
        >
          {status.ready ? <Mic className="h-4 w-4" /> : <X className="h-4 w-4" />}
        </div>
        <div className="min-w-0 flex-1">
          <p className="text-sm font-medium text-ink">
            {status.ready ? '語音輸入已啟用' : '語音輸入尚未啟用'}
          </p>
          <p className="mt-1 text-xs leading-relaxed text-ink-secondary">
            {status.message ?? '對話框會顯示麥克風，點一下開始錄音，再點一下停止。'}
          </p>
          <p className="mt-1 truncate font-mono text-[11px] text-ink-subtle">
            模型路徑：{status.modelsDirectory}
          </p>
        </div>
        <Button variant="ghost" size="icon" onClick={loadStatus} disabled={loading} title="重新檢查">
          <RefreshCw className={cn('h-4 w-4', loading && 'animate-spin')} />
        </Button>
      </div>

      {error && (
        <div className="rounded-xl border border-red-200 bg-red-50 px-4 py-2 text-sm text-red-700">
          {error}
        </div>
      )}

      <div className="grid gap-4 sm:grid-cols-2">
        <label className="block">
          <span className="text-xs font-medium text-ink-secondary">語言</span>
          <select
            value={status.language}
            onChange={(event) => handleLanguageChange(event.target.value as SpeechStatus['language'])}
            className="mt-1 w-full rounded-xl border border-border bg-surface px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-brand/40"
          >
            <option value="auto">自動偵測</option>
            <option value="zh-TW">繁中 / 中文</option>
            <option value="en">英文</option>
          </select>
        </label>

        <label className="block">
          <span className="text-xs font-medium text-ink-secondary">模型</span>
          <select
            value={selectedModelId}
            onChange={(event) => handleModelChange(event.target.value)}
            className="mt-1 w-full rounded-xl border border-border bg-surface px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-brand/40"
          >
            {status.models.map((model) => (
              <option key={model.id} value={model.id}>
                {model.displayName}{model.recommended ? '（推薦）' : ''}
              </option>
            ))}
          </select>
        </label>
      </div>

      {selectedModel && (
        <div className="rounded-xl border border-border bg-surface px-4 py-3">
          <div className="flex items-start gap-3">
            <div className={cn('mt-0.5', selectedModel.installed ? 'text-brand-green' : 'text-ink-subtle')}>
              {selectedModel.installed ? <Check className="h-4 w-4" /> : <Download className="h-4 w-4" />}
            </div>
            <div className="min-w-0 flex-1">
              <div className="flex items-center gap-2">
                <p className="text-sm font-medium text-ink">{selectedModel.displayName}</p>
                <span className="rounded-full bg-surface-alt px-2 py-0.5 text-[11px] text-ink-subtle">
                  {formatBytes(selectedModel.installedSizeBytes)}
                </span>
              </div>
              <p className="mt-1 text-xs leading-relaxed text-ink-secondary">{selectedModel.description}</p>
              <p className="mt-1 font-mono text-[11px] text-ink-subtle">{selectedModel.fileName}</p>
            </div>
          </div>
        </div>
      )}

      <div className="space-y-3 rounded-xl border border-border bg-surface px-4 py-4">
        <div className="flex gap-2">
          <button
            type="button"
            onClick={() => setSourceMode('default')}
            className={cn(
              'rounded-lg px-3 py-1.5 text-xs transition-colors',
              sourceMode === 'default' ? 'bg-brand text-white' : 'text-ink-secondary hover:bg-surface-alt',
            )}
          >
            Hugging Face
          </button>
          <button
            type="button"
            onClick={() => setSourceMode('custom')}
            className={cn(
              'rounded-lg px-3 py-1.5 text-xs transition-colors',
              sourceMode === 'custom' ? 'bg-brand text-white' : 'text-ink-secondary hover:bg-surface-alt',
            )}
          >
            GitHub / 內網鏡像 URL
          </button>
        </div>

        {sourceMode === 'custom' && (
          <input
            type="text"
            value={customUrl}
            onChange={(event) => setCustomUrl(event.target.value)}
            placeholder="https://github.com/your-org/models/releases/download/.../ggml-small-q5_1.bin"
            className="w-full rounded-xl border border-border bg-surface px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-brand/40"
          />
        )}

        <div className="flex flex-wrap gap-2">
          <Button
            size="sm"
            onClick={handleDownload}
            isLoading={loading}
            leftIcon={<Download className="h-3.5 w-3.5" />}
            disabled={sourceMode === 'custom' && !customUrl.trim()}
          >
            下載模型
          </Button>
          <Button
            size="sm"
            variant="outline"
            onClick={handleImport}
            disabled={loading}
            leftIcon={<FolderOpen className="h-3.5 w-3.5" />}
          >
            匯入本機模型
          </Button>
        </div>
        <p className="text-xs leading-relaxed text-ink-subtle">
          若企業內網無法直連 Hugging Face，可由 IT 先下載模型或放到 GitHub Enterprise / 內網 mirror，再用 URL 或本機匯入。
        </p>
      </div>
    </div>
  )
}
