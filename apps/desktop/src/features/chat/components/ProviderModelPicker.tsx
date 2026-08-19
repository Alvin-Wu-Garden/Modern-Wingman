import { useState, useEffect, useRef } from 'react'
import { AlertCircle, ChevronDown, Loader2, RefreshCw } from 'lucide-react'
import { ProviderBrandIcon } from '@/components/ui/provider-brand-icon'
import { cn } from '@/lib/utils'
import {
  listProviders,
  listProviderModels,
  ProviderModelsRequestError,
  type ProviderInfo,
  type ModelGroup,
} from '@/services/agent-api/client'

interface Props {
  selectedProviderId: string | null
  selectedModel: string | null
  onProviderChange: (providerId: string | null) => void
  onModelChange: (model: string | null) => void
}

const PREFERRED_MODEL_ID = 'claude-sonnet-4.6'
const LATEST_OPENAI_MODEL_ID = 'gpt-5.6'
const COPILOT_STARTUP_RETRY_DELAYS_MS = [500, 1000, 1500, 2500, 3500, 5000]

function isVerifiedProvider(provider: ProviderInfo): boolean {
  // 後端 /api/providers 已一次回傳狀態；選擇器不再逐一呼叫 key-status。
  return provider.hasStoredKey === true
}

function selectDefaultModel(groups: ModelGroup[], provider: ProviderInfo | undefined): string | null {
  const models = groups.flatMap((group) => group.models)
  if (models.includes(PREFERRED_MODEL_ID)) return PREFERRED_MODEL_ID
  if (provider?.id === 'openai-byok' && models.includes(LATEST_OPENAI_MODEL_ID)) return LATEST_OPENAI_MODEL_ID
  if (provider?.modelId && models.includes(provider.modelId)) return provider.modelId
  return models[0] ?? null
}

export function ProviderModelPicker({
  selectedProviderId,
  selectedModel,
  onProviderChange,
  onModelChange,
}: Props) {
  const [providers, setProviders] = useState<ProviderInfo[]>([])
  const [providersLoaded, setProvidersLoaded] = useState(false)
  const [loadingProviders, setLoadingProviders] = useState(false)
  const [providerError, setProviderError] = useState<string | null>(null)
  const [modelGroups, setModelGroups] = useState<ModelGroup[]>([])
  const [loadingModels, setLoadingModels] = useState(false)
  const [modelError, setModelError] = useState<string | null>(null)
  const [modelReloadNonce, setModelReloadNonce] = useState(0)
  const modelCacheRef = useRef(new Map<string, ModelGroup[]>())
  const [providerOpen, setProviderOpen] = useState(false)
  const [modelOpen, setModelOpen] = useState(false)
  const providerRef = useRef<HTMLDivElement>(null)
  const modelRef = useRef<HTMLDivElement>(null)

  /* 首次載入時依設定頁排序，只保留已驗證並儲存成功的供應商。 */
  useEffect(() => {
    // 清單載入完成後不重複查詢；元件重新掛載時會重新讀取最新設定。
    if (providersLoaded) return

    let cancelled = false
    setLoadingProviders(true)
    setProviderError(null)

    void (async () => {
      try {
        // Provider Profile 只需讀取本機 SQLite；元件卸載時讓請求自然完成，
        // 由 cancelled 旗標阻止延遲回應更新已卸載的元件。
        const loadedProviders = await listProviders()
        if (cancelled) return

        const configuredProviders = loadedProviders.filter(isVerifiedProvider)
        setProviders(configuredProviders)
        setProvidersLoaded(true)

        const selectedIsConfigured = configuredProviders.some(
          (provider) => provider.id === selectedProviderId,
        )
        if (!selectedIsConfigured) {
          const firstConfigured = configuredProviders[0]
          onProviderChange(firstConfigured?.id ?? null)
          onModelChange(null)
        }
      } catch (error) {
        if (!cancelled) {
          setProviders([])
          setProvidersLoaded(true)
          setProviderError(error instanceof Error ? error.message : '無法載入供應商。')
          onProviderChange(null)
          onModelChange(null)
        }
      } finally {
        if (!cancelled) setLoadingProviders(false)
      }
    })()

    return () => {
      cancelled = true
    }
  }, [onModelChange, onProviderChange, providersLoaded, selectedProviderId])

  /* 點擊外部時關閉下拉選單。 */
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (providerRef.current && !providerRef.current.contains(e.target as Node)) setProviderOpen(false)
      if (modelRef.current && !modelRef.current.contains(e.target as Node)) setModelOpen(false)
    }
    document.addEventListener('mousedown', handler)
    return () => document.removeEventListener('mousedown', handler)
  }, [])

  /* 供應商變更時載入模型；所有 Provider 類型皆由後端處理。 */
  useEffect(() => {
    if (!providersLoaded || !selectedProviderId) {
      setModelGroups([])
      onModelChange(null)
      return
    }

    const cached = modelCacheRef.current.get(selectedProviderId)
    if (cached) {
      setModelError(null)
      setModelGroups(cached)
      onModelChange(selectDefaultModel(cached, providers.find((provider) => provider.id === selectedProviderId)))
      return
    }

    const controller = new AbortController()
    let cancelled = false
    let retryTimer: number | null = null
    setLoadingModels(true)
    setModelError(null)
    setModelGroups([])
    onModelChange(null)

    const loadModels = (attempt: number) => {
      void listProviderModels(selectedProviderId, controller.signal)
        .then((groups) => {
          if (cancelled) return

          if (groups.length === 0) {
            // 空清單可能是上游暫時失敗，不得快取成整個元件生命週期都無模型。
            setModelGroups([])
            setModelError('供應商沒有回傳可用模型，請點擊重新載入。')
            onModelChange(null)
            setLoadingModels(false)
            return
          }

          modelCacheRef.current.set(selectedProviderId, groups)
          setModelGroups(groups)
          onModelChange(selectDefaultModel(groups, providers.find((provider) => provider.id === selectedProviderId)))
          setLoadingModels(false)
        })
        .catch((error) => {
          if (cancelled || controller.signal.aborted) return

          const shouldRetryCopilotStartup =
            error instanceof ProviderModelsRequestError
            && error.code === 'copilot_runtime_not_ready'
            && error.retryable
            && attempt < COPILOT_STARTUP_RETRY_DELAYS_MS.length

          if (shouldRetryCopilotStartup) {
            retryTimer = window.setTimeout(
              () => loadModels(attempt + 1),
              COPILOT_STARTUP_RETRY_DELAYS_MS[attempt],
            )
            return
          }

          setModelGroups([])
          setModelError(error instanceof Error ? error.message : '無法載入模型。')
          onModelChange(null)
          setLoadingModels(false)
        })
    }

    loadModels(0)

    return () => {
      cancelled = true
      controller.abort()
      if (retryTimer !== null) {
        window.clearTimeout(retryTimer)
      }
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [modelReloadNonce, providersLoaded, selectedProviderId, providers])

  const selectedProvider = providers.find((p) => p.id === selectedProviderId)

  return (
    <div className="flex min-w-0 items-center gap-1.5">
      {/* Provider 選擇器 */}
      <div ref={providerRef} className="relative min-w-0">
        <button
          type="button"
          onClick={() => setProviderOpen((o) => !o)}
          className={cn(
            'flex h-8 items-center gap-1.5 rounded-lg px-2.5 text-xs font-medium',
            'border border-border bg-surface hover:bg-surface-alt transition-colors',
            'focus:outline-none focus:ring-2 focus:ring-brand/40',
          )}
        >
          {selectedProvider && <ProviderBrandIcon provider={selectedProvider} size="xs" />}
          {loadingProviders && <Loader2 className="h-3 w-3 animate-spin text-brand" />}
          <span className="max-w-[110px] truncate">
            {loadingProviders ? '載入供應商…' : selectedProvider?.displayName ?? '選擇供應商'}
          </span>
          <ChevronDown className="w-3 h-3 text-ink-subtle shrink-0" />
        </button>

        {providerOpen && providers.length > 0 && (
          <div className="absolute bottom-full left-0 z-50 mb-1 max-h-60 min-w-[190px] max-w-[min(280px,calc(100vw-2rem))] overflow-y-auto overflow-x-hidden rounded-xl border border-border bg-surface shadow-lg">
            {providers.map((p) => (
              <button
                key={p.id}
                type="button"
                onClick={() => { onProviderChange(p.id); setProviderOpen(false) }}
                className={cn(
                  'flex w-full items-center gap-2 text-left px-3 py-2 text-xs hover:bg-surface-alt transition-colors',
                  selectedProviderId === p.id ? 'text-brand font-semibold' : 'text-ink',
                )}
              >
                <ProviderBrandIcon provider={p} size="sm" />
                <span className="min-w-0 truncate">{p.displayName}</span>
              </button>
            ))}
          </div>
        )}
        {providerError && (
          <button
            type="button"
            onClick={() => {
              setProvidersLoaded(false)
              setProviderError(null)
            }}
            className="absolute left-0 top-full z-40 mt-1 flex max-w-[240px] items-center gap-1 rounded-lg border border-error/30 bg-surface px-2 py-1 text-[10px] text-error shadow-sm"
            title="重新載入供應商"
          >
            <AlertCircle className="h-3 w-3 shrink-0" />
            <span className="truncate">載入失敗，點擊重試</span>
            <RefreshCw className="h-3 w-3 shrink-0" />
          </button>
        )}
      </div>

      {/* 模型選擇器；有可用模型的 Provider 都會顯示 */}
      {selectedProvider && (
        <div ref={modelRef} className="relative min-w-0">
          <button
            type="button"
            onClick={() => {
              if (modelError && selectedProviderId) {
                modelCacheRef.current.delete(selectedProviderId)
                setModelReloadNonce((value) => value + 1)
                return
              }
              setModelOpen((o) => !o)
            }}
            disabled={loadingModels}
            className={cn(
              'flex h-8 items-center gap-1.5 rounded-lg px-2.5 text-xs font-medium',
              'border border-border bg-surface hover:bg-surface-alt transition-colors',
              'focus:outline-none focus:ring-2 focus:ring-brand/40',
            )}
          >
            {loadingModels ? (
              <Loader2 className="w-3 h-3 animate-spin" />
            ) : (
              <>
                <span className="max-w-[120px] truncate">{modelError ? '模型載入失敗' : selectedModel ?? '選擇模型'}</span>
                <ChevronDown className="w-3 h-3 text-ink-subtle shrink-0" />
              </>
            )}
          </button>

          {modelOpen && modelGroups.length > 0 && (
            <div className="absolute bottom-full left-0 z-50 mb-1 max-h-60 min-w-[200px] max-w-[min(320px,calc(100vw-2rem))] overflow-y-auto overflow-x-hidden rounded-xl border border-border bg-surface shadow-lg">
              {modelGroups.map((group) => (
                <div key={group.group}>
                  <div className="px-3 py-1.5 text-[10px] font-semibold text-ink-subtle uppercase tracking-wide bg-surface-alt">
                    {group.group}
                  </div>
                  {group.models.map((m) => (
                    <button
                      key={m}
                      type="button"
                      onClick={() => { onModelChange(m); setModelOpen(false) }}
                      className={cn(
                        'w-full text-left px-3 py-2 text-xs hover:bg-surface-alt transition-colors',
                        selectedModel === m ? 'text-brand font-semibold' : 'text-ink',
                      )}
                    >
                      {m}
                    </button>
                  ))}
                </div>
              ))}
            </div>
          )}
          {modelError && (
            <span className="absolute left-0 top-full z-40 mt-1 whitespace-nowrap text-[10px] text-error">
              點擊重新載入模型
            </span>
          )}
        </div>
      )}
    </div>
  )
}
