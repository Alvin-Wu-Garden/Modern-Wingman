import { useState, useEffect, useRef } from 'react'
import { ChevronDown, Loader2 } from 'lucide-react'
import { ProviderBrandIcon } from '@/components/ui/provider-brand-icon'
import { cn } from '@/lib/utils'
import {
  listProviders,
  getProviderKeyStatus,
  listProviderModels,
  type ProviderInfo,
  type ProviderKeyStatus,
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

function isVerifiedProvider(status: ProviderKeyStatus | null): boolean {
  if (!status) return false
  // 設定頁只會把後端真實驗證成功的 Key 寫入 DB；後續 runtime 錯誤不自動改動設定。
  return status.hasStoredKey || status.hasEnvVar
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
  const [modelGroups, setModelGroups] = useState<ModelGroup[]>([])
  const [loadingModels, setLoadingModels] = useState(false)
  const [providerOpen, setProviderOpen] = useState(false)
  const [modelOpen, setModelOpen] = useState(false)
  const providerRef = useRef<HTMLDivElement>(null)
  const modelRef = useRef<HTMLDivElement>(null)

  /* 首次載入時依設定頁排序，預選第一個已驗證可用的供應商。 */
  useEffect(() => {
    // 已有選擇且清單已載入時不重新查詢；新對話把選擇清為 null 才會重新初始化。
    if (selectedProviderId && providers.length > 0) return

    let cancelled = false

    void (async () => {
      try {
        const loadedProviders = await listProviders()
        const statuses = await Promise.all(
          loadedProviders.map(async (provider) => {
            try { return await getProviderKeyStatus(provider.id) }
            catch { return null }
          }),
        )
        if (cancelled) return

        setProviders(loadedProviders)
        if (!selectedProviderId) {
          const firstVerified = loadedProviders.find((_provider, index) =>
            isVerifiedProvider(statuses[index] ?? null))
          if (firstVerified) onProviderChange(firstVerified.id)
        }
      } catch {
        if (!cancelled) setProviders([])
      }
    })()

    return () => { cancelled = true }
  }, [onProviderChange, providers.length, selectedProviderId])

  /* Close dropdowns when clicking outside */
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (providerRef.current && !providerRef.current.contains(e.target as Node)) setProviderOpen(false)
      if (modelRef.current && !modelRef.current.contains(e.target as Node)) setModelOpen(false)
    }
    document.addEventListener('mousedown', handler)
    return () => document.removeEventListener('mousedown', handler)
  }, [])

  /* Load models when provider changes — backend handles all provider types */
  useEffect(() => {
    if (!selectedProviderId) { setModelGroups([]); return }

    setLoadingModels(true)

    listProviderModels(selectedProviderId)
      .then((groups) => {
        setModelGroups(groups)
        onModelChange(selectDefaultModel(groups, providers.find((provider) => provider.id === selectedProviderId)))
      })
      .catch(() => setModelGroups([]))
      .finally(() => setLoadingModels(false))
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedProviderId, providers])

  const selectedProvider = providers.find((p) => p.id === selectedProviderId)

  return (
    <div className="flex items-center gap-1.5">
      {/* Provider picker */}
      <div ref={providerRef} className="relative">
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
          <span className="max-w-[110px] truncate">
            {selectedProvider?.displayName ?? '選擇供應商'}
          </span>
          <ChevronDown className="w-3 h-3 text-ink-subtle shrink-0" />
        </button>

        {providerOpen && providers.length > 0 && (
          <div className="absolute bottom-full mb-1 left-0 z-50 min-w-[190px] rounded-xl border border-border bg-surface shadow-lg overflow-hidden">
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
      </div>

      {/* Model picker — shown for all providers that have models */}
      {selectedProvider && (
        <div ref={modelRef} className="relative">
          <button
            type="button"
            onClick={() => setModelOpen((o) => !o)}
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
                <span className="max-w-[120px] truncate">{selectedModel ?? '選擇模型'}</span>
                <ChevronDown className="w-3 h-3 text-ink-subtle shrink-0" />
              </>
            )}
          </button>

          {modelOpen && modelGroups.length > 0 && (
            <div className="absolute bottom-full mb-1 left-0 z-50 min-w-[200px] max-h-60 overflow-y-auto rounded-xl border border-border bg-surface shadow-lg">
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
        </div>
      )}
    </div>
  )
}
