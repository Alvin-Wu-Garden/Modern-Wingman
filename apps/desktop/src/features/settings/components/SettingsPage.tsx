import { useState, useEffect, useCallback } from 'react'
import {
  DndContext,
  closestCenter,
  KeyboardSensor,
  PointerSensor,
  useSensor,
  useSensors,
  type DragEndEvent,
} from '@dnd-kit/core'
import {
  arrayMove,
  SortableContext,
  sortableKeyboardCoordinates,
  useSortable,
  verticalListSortingStrategy,
} from '@dnd-kit/sortable'
import { CSS } from '@dnd-kit/utilities'
import {
  Check,
  Bot,
  ChevronDown,
  ChevronUp,
  Eye,
  EyeOff,
  GripVertical,
  Key,
  ListChecks,
  Loader2,
  MessageCircle,
  Zap,
  X,
} from 'lucide-react'
import type { AgentMode } from '@modern-wingman/contracts'
import { useAppStore, type AppTheme } from '@/app/store'
import { Button } from '@/components/ui/button'
import { ProviderBrandIcon } from '@/components/ui/provider-brand-icon'
import { Textarea } from '@/components/ui/textarea'
import { cn } from '@/lib/utils'
import { SpeechSettingsPanel } from './SpeechSettingsPanel'
import { VersionControlSettingsPanel } from './VersionControlSettingsPanel'
import { AuditSettingsPanel } from './AuditSettingsPanel'
import { RuntimeSettingsPanel } from './RuntimeSettingsPanel'
import { useSkillsStore } from '@/features/skills/store/useSkillsStore'
import { McpTab } from '@/features/skills/components/McpTab'
import {
  listProviders,
  getProviderKeyStatus,
  setProviderKey,
  deleteProviderKey,
  setProviderBaseUrl,
  reorderProviders,
  validateKeyViaBackend,
  type ProviderInfo,
  type ProviderKeyStatus,
  type KeyValidationResult,
} from '@/services/agent-api/client'

/* ── Theme option definitions ── */
interface ThemeOption {
  id: AppTheme
  name: string
  description: string
  preview: React.ReactNode
}

const THEME_OPTIONS: ThemeOption[] = [
  {
    id: 'default',
    name: '預設',
description: 'Wingman 品牌配色',
    preview: (
      <div className="w-full h-full flex rounded-t-xl overflow-hidden">
        <div className="w-9 bg-[#014865] flex flex-col gap-1.5 p-1.5 pt-3">
          <div className="h-1.5 w-full rounded-sm bg-[#0093C1]/60" />
          <div className="h-1.5 w-4/5 rounded-sm bg-white/20" />
          <div className="h-1.5 w-4/5 rounded-sm bg-white/20" />
        </div>
        <div className="flex-1 bg-[#EEF0F0] flex flex-col gap-1.5 p-2">
          <div className="h-2 w-1/2 rounded-sm bg-[#0093C1]/40" />
          <div className="h-1.5 w-3/4 rounded-sm bg-[#D7D7DA]" />
          <div className="h-6 w-full rounded-lg bg-white mt-0.5" />
        </div>
      </div>
    ),
  },
  {
    id: 'light',
    name: '淺色',
    description: '標準白色模式',
    preview: (
      <div className="w-full h-full flex rounded-t-xl overflow-hidden">
        <div className="w-9 bg-gray-100 flex flex-col gap-1.5 p-1.5 pt-3">
          <div className="h-1.5 w-full rounded-sm bg-gray-400/50" />
          <div className="h-1.5 w-4/5 rounded-sm bg-gray-300/60" />
          <div className="h-1.5 w-4/5 rounded-sm bg-gray-300/60" />
        </div>
        <div className="flex-1 bg-white flex flex-col gap-1.5 p-2">
          <div className="h-2 w-1/2 rounded-sm bg-gray-400/50" />
          <div className="h-1.5 w-3/4 rounded-sm bg-gray-200" />
          <div className="h-6 w-full rounded-lg bg-gray-50 mt-0.5 border border-gray-100" />
        </div>
      </div>
    ),
  },
  {
    id: 'dark',
    name: '深色',
    description: '夜間深色模式',
    preview: (
      <div className="w-full h-full flex rounded-t-xl overflow-hidden bg-[#0d1117]">
        <div className="w-9 bg-[#161b22] flex flex-col gap-1.5 p-1.5 pt-3">
          <div className="h-1.5 w-full rounded-sm bg-[#58a6ff]/60" />
          <div className="h-1.5 w-4/5 rounded-sm bg-white/15" />
          <div className="h-1.5 w-4/5 rounded-sm bg-white/15" />
        </div>
        <div className="flex-1 bg-[#0d1117] flex flex-col gap-1.5 p-2">
          <div className="h-2 w-1/2 rounded-sm bg-white/30" />
          <div className="h-1.5 w-3/4 rounded-sm bg-white/10" />
          <div className="h-6 w-full rounded-lg bg-white/5 mt-0.5" />
        </div>
      </div>
    ),
  },
  {
    id: 'glass',
    name: '玻璃液態',
    description: 'iOS Liquid Glass',
    preview: (
      <div className="w-full h-full rounded-t-xl overflow-hidden bg-[#04070e] relative">
        <div className="absolute inset-0 bg-[radial-gradient(ellipse_55%_45%_at_10%_15%,rgba(48,90,210,0.40)_0%,transparent_65%)]" />
        <div className="absolute inset-0 bg-[radial-gradient(ellipse_40%_50%_at_88%_85%,rgba(95,42,190,0.30)_0%,transparent_65%)]" />
        <div className="absolute inset-0 flex">
          <div className="w-9 bg-[rgba(16,26,58,0.55)] border-r border-white/10 flex flex-col gap-1.5 p-1.5 pt-3 relative shadow-[inset_0_1.5px_0_rgba(255,255,255,0.18)]">
            <div className="h-1.5 w-full rounded-sm bg-white/40" />
            <div className="h-1.5 w-4/5 rounded-sm bg-white/18" />
            <div className="h-1.5 w-4/5 rounded-sm bg-white/18" />
          </div>
          <div className="flex-1 flex flex-col justify-center gap-1.5 p-2">
            <div className="h-2 w-1/2 rounded-sm bg-white/55" />
            <div className="h-1.5 w-3/4 rounded-sm bg-white/22" />
            <div className="h-5 w-full rounded-md bg-[rgba(16,26,58,0.50)] mt-0.5 border border-white/12" />
          </div>
        </div>
      </div>
    ),
  },
]

/* ── Section wrapper ── */
function SettingsSection({
  title,
  description,
  children,
}: {
  title: string
  description?: string
  children: React.ReactNode
}) {
  return (
    <section className="overflow-hidden rounded-2xl bg-surface shadow-sm">
      <div className="px-6 py-5 border-b border-border">
        <h2 className="text-base font-semibold text-ink">{title}</h2>
        {description && (
          <p className="mt-0.5 text-sm text-ink-secondary">{description}</p>
        )}
      </div>
      <div className="p-6">{children}</div>
    </section>
  )
}

/* ── Validation badge ── */
type ValidationState = 'idle' | 'validating' | KeyValidationResult

function ValidationBadge({ state, isGithub }: { state: ValidationState; isGithub?: boolean }) {
  if (state === 'idle') return null
  if (state === 'validating') {
    return (
      <span className="flex items-center gap-1 text-xs text-ink-subtle">
        <Loader2 className="w-3 h-3 animate-spin" /> 驗證中…
      </span>
    )
  }
  if (state.valid) {
    if (isGithub && state.scopes) {
      return (
        <span className="flex items-center gap-1 text-xs text-brand-green font-medium">
          <Check className="w-3 h-3" /> 有效 · scopes: <code className="font-mono">{state.scopes}</code>
        </span>
      )
    }
    return (
      <span className="flex items-center gap-1 text-xs text-brand-green font-medium">
        <Check className="w-3 h-3" /> 有效
      </span>
    )
  }
  return (
    <span className="flex items-center gap-1 text-xs text-red-400 font-medium">
      <X className="w-3 h-3" /> 無效{state.error ? ` · ${state.error}` : ''}
    </span>
  )
}

/* ── Sortable provider row ── */
interface ProviderRowProps {
  provider: ProviderInfo
  status: ProviderKeyStatus | undefined
  keyInput: string
  baseUrlInput: string
  keyVisible: boolean
  saving: boolean
  validation: ValidationState
  expanded: boolean
  onToggleExpanded: () => void
  onKeyChange: (val: string) => void
  onBaseUrlChange: (val: string) => void
  onKeyBlur: () => void
  onBaseUrlBlur: () => void
  onToggleVisible: () => void
  onSaveKey: () => void
  onDeleteKey: () => void
}

function SortableProviderRow(props: ProviderRowProps) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } =
    useSortable({ id: props.provider.id })

  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
    opacity: isDragging ? 0.5 : 1,
  }

  const isCopilotDefault = props.provider.kind === 'CopilotDefault'
  const isCustom = props.provider.id === 'custom-byok'
  const hasEnvVar = props.status?.hasEnvVar ?? false
  const hasStoredKey = props.status?.hasStoredKey ?? false
  const isGithub = props.provider.providerType === 'github' || props.provider.id.includes('github')

  // 有金鑰時以 dots 顯示，點擊才切換為可編輯
  const [isEditing, setIsEditing] = useState(false)
  useEffect(() => { setIsEditing(false) }, [hasStoredKey])
  const showStoredDots = hasStoredKey && !isEditing

  // 收合狀態由父層控制（支援展開全部/收合全部）
  const expanded = props.expanded
  const setExpanded = (_: boolean | ((prev: boolean) => boolean)) => props.onToggleExpanded()

  // 決定 header icon：驗證失敗 → 紅X；已儲存/驗證通過 → 綠勾；其餘 → 灰鑰匙
  const iconCls = 'w-3.5 h-3.5 shrink-0'
  let HeaderIcon: React.ReactNode
  if (!isCopilotDefault) {
    const v = props.validation
    if (typeof v === 'object' && !v.valid) {
      HeaderIcon = <X className={`${iconCls} text-red-400`} />
    } else if ((typeof v === 'object' && v.valid) || hasStoredKey || hasEnvVar) {
      HeaderIcon = <Check className={`${iconCls} text-brand-green`} />
    } else {
      HeaderIcon = <Key className={`${iconCls} text-ink-subtle`} />
    }
  } else {
    // CopilotDefault 永遠已啟用 → 綠勾
    HeaderIcon = <Check className={`${iconCls} text-brand-green`} />
  }

  return (
    <div ref={setNodeRef} style={style} className="rounded-xl border border-border bg-surface overflow-hidden">
      {/* Row header — 點擊任意處展開/收合 */}
      <div
        role="button"
        tabIndex={0}
        onClick={() => setExpanded((o) => !o)}
        onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && setExpanded((o) => !o)}
        className="flex items-center gap-2 px-4 py-3 cursor-pointer hover:bg-surface-alt/50 transition-colors select-none"
      >
        <button
          type="button"
          onClick={(e) => e.stopPropagation()}
          className="cursor-grab touch-none text-ink-subtle hover:text-ink"
          {...attributes}
          {...listeners}
          aria-label="拖動排序"
        >
          <GripVertical className="w-4 h-4" />
        </button>
        {HeaderIcon}
        <ProviderBrandIcon provider={props.provider} />
        <span className="text-sm font-medium text-ink flex-1">{props.provider.displayName}</span>
        {isCopilotDefault && (
          <span className="text-xs px-2 py-0.5 rounded-full bg-brand-green/10 text-brand-green font-medium">
            已啟用 ✓
          </span>
        )}
        {!isCopilotDefault && hasEnvVar && (
          <span className="text-xs px-2 py-0.5 rounded-full bg-brand/10 text-brand font-medium">
            環境變數
          </span>
        )}
        {!isCopilotDefault && !hasEnvVar && hasStoredKey && (
          <span className="text-xs px-2 py-0.5 rounded-full bg-brand-green/10 text-brand-green font-medium">
            已儲存
          </span>
        )}
        <button
          type="button"
          onClick={(e) => { e.stopPropagation(); setExpanded((o) => !o) }}
          className="text-ink-subtle hover:text-ink shrink-0 p-0.5"
          aria-label={expanded ? '收合' : '展開'}
        >
          {expanded ? <ChevronUp className="w-4 h-4" /> : <ChevronDown className="w-4 h-4" />}
        </button>
      </div>

      {/* 可收合的內容區 */}
      {expanded && (
        <div className="px-4 pb-4 space-y-3 border-t border-border/50 pt-3">
      {isCustom && !hasEnvVar && (
        <div>
          <label className="block text-xs text-ink-subtle mb-1">Base URL</label>
          <input
            type="text"
            value={props.baseUrlInput}
            onChange={(e) => props.onBaseUrlChange(e.target.value)}
            onBlur={props.onBaseUrlBlur}
            placeholder="http://localhost:11434/v1"
            className="w-full rounded-xl border border-border bg-surface px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-brand/40"
          />
        </div>
      )}

      {/* API Key input（CopilotDefault 顯示 PAT 選填欄位；BYOK 顯示 API Key）*/}
      <div className="space-y-1.5">
        {isCopilotDefault && (
          <p className="text-xs text-ink-subtle leading-relaxed">
            GitHub PAT（選填）— 填入後不需本機 Copilot CLI 登入。需含{' '}
            <code className="font-mono bg-surface-alt px-1 rounded">copilot</code>{' '}權限。
          </p>
        )}
          {hasEnvVar ? (
            <div className="relative">
              <input
                type="password"
                value="••••••••••••••••••••"
                disabled
                className="w-full rounded-xl border border-border bg-surface-alt px-3 py-2 text-sm text-ink-subtle cursor-not-allowed opacity-60"
              />
              <span className="absolute right-3 top-1/2 -translate-y-1/2 text-xs text-ink-subtle">
                由環境變數提供
              </span>
            </div>
          ) : (
            <div className="space-y-1">
              <div className="flex gap-2">
                <div className="relative flex-1">
                  {showStoredDots ? (
                    <input
                      type="text"
                      value="••••••••••••••••"
                      readOnly
                      onFocus={() => { setIsEditing(true); props.onKeyChange('') }}
                      className="w-full rounded-xl border border-border bg-surface-alt px-3 py-2 text-sm text-ink-subtle cursor-text focus:outline-none focus:ring-2 focus:ring-brand/40"
                    />
                  ) : (
                    <>
                      <input
                        type={props.keyVisible ? 'text' : 'password'}
                        value={props.keyInput}
                        onChange={(e) => props.onKeyChange(e.target.value)}
                        onBlur={props.onKeyBlur}
                        placeholder={
                          isCopilotDefault
                            ? 'ghp_… 或 github_pat_…'
                            : props.provider.id.includes('openrouter')
                              ? 'sk-or-v1-…'
                              : 'sk-…'
                        }
                        className="w-full rounded-xl border border-border bg-surface px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-brand/40 pr-10"
                      />
                      <button
                        type="button"
                        onClick={props.onToggleVisible}
                        className="absolute right-3 top-1/2 -translate-y-1/2 text-ink-subtle hover:text-ink"
                      >
                        {props.keyVisible ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
                      </button>
                    </>
                  )}
                </div>
                {showStoredDots ? (
                  <Button size="sm" variant="ghost" onClick={props.onDeleteKey} className="text-red-400 hover:text-red-500 shrink-0">
                    移除
                  </Button>
                ) : (
                  <>
                    <Button size="sm" onClick={props.onSaveKey} disabled={!props.keyInput.trim() || props.saving} className="shrink-0">
                      {props.saving ? '儲存中…' : '儲存'}
                    </Button>
                    {hasStoredKey && (
                      <Button size="sm" variant="ghost" onClick={props.onDeleteKey} className="text-red-400 hover:text-red-500 shrink-0">
                        移除
                      </Button>
                    )}
                  </>
                )}
              </div>
              <ValidationBadge state={props.validation} isGithub={isCopilotDefault || isGithub} />
            </div>
          )}
        </div>
        </div>
      )}
    </div>
  )
}

/* ── Main component ── */
type SettingsCategory = 'general' | 'agent' | 'data'
const SETTINGS_CATEGORIES: { id: SettingsCategory; label: string }[] = [
  { id: 'general', label: '一般' },
  { id: 'agent', label: 'Agent 設定' },
  { id: 'data', label: '資料稽核' },
]

const AGENT_MODE_OPTIONS: Array<{
  id: AgentMode
  label: string
  description: string
  icon: typeof MessageCircle
}> = [
  { id: 'ask', label: '詢問', description: '唯讀分析，不修改檔案或執行有副作用的工具。', icon: MessageCircle },
  { id: 'plan', label: '規劃', description: '先產生計畫，核准後才建立隔離工作區並修改。', icon: ListChecks },
  { id: 'auto', label: 'Auto', description: '可修改與驗證；高風險、推送與受保護操作仍需核准。', icon: Zap },
  { id: 'full_auto', label: '完全自動', description: '在非受保護範圍自動完成，安全政策與禁用規則仍然有效。', icon: Bot },
]

export function SettingsPage() {
  const [activeCategory, setActiveCategory] = useState<SettingsCategory>('general')
  const theme = useAppStore((s) => s.theme)
  const setTheme = useAppStore((s) => s.setTheme)
  const systemPrompt = useAppStore((s) => s.systemPrompt)
  const setSystemPrompt = useAppStore((s) => s.setSystemPrompt)
  const defaultAgentMode = useAppStore((s) => s.defaultAgentMode)
  const setDefaultAgentMode = useAppStore((s) => s.setDefaultAgentMode)
  const githubPat = useAppStore((s) => s.githubPat)
  const setGithubPat = useAppStore((s) => s.setGithubPat)
  const { agents, fetchAgents, updateAgentPath } = useSkillsStore()

  const [agentPathsOpen, setAgentPathsOpen] = useState(false)
  const [editingPaths, setEditingPaths] = useState<Record<string, string>>({})

  /* ── Provider state ── */
  const [providers, setProviders] = useState<ProviderInfo[]>([])
  const [keyStatuses, setKeyStatuses] = useState<Record<string, ProviderKeyStatus>>({})
  const [keyInputs, setKeyInputs] = useState<Record<string, string>>({})
  const [baseUrlInputs, setBaseUrlInputs] = useState<Record<string, string>>({})
  const [keyVisible, setKeyVisible] = useState<Record<string, boolean>>({})
  const [keySaving, setKeySaving] = useState<Record<string, boolean>>({})
  const [validations, setValidations] = useState<Record<string, ValidationState>>({})
  const [githubPatValidation, setGithubPatValidation] = useState<ValidationState>('idle')
  const [expandedRows, setExpandedRows] = useState<Record<string, boolean>>({})

  const allExpanded = providers.length > 0 && providers.every((p) => expandedRows[p.id])
  const toggleAllRows = () => {
    const next = !allExpanded
    setExpandedRows(Object.fromEntries(providers.map((p) => [p.id, next])))
  }

  const sensors = useSensors(
    useSensor(PointerSensor),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  )

  const loadProviders = useCallback(async () => {
    try {
      const list = await listProviders()
      setProviders(list)
      const statuses: Record<string, ProviderKeyStatus> = {}
      const baseUrls: Record<string, string> = {}
      await Promise.all(
        list.map(async (p) => {
          try {
            const s = await getProviderKeyStatus(p.id)
            statuses[p.id] = s
            if (s.storedBaseUrl) baseUrls[p.id] = s.storedBaseUrl
          } catch { /* ignore */ }
        }),
      )
      setKeyStatuses(statuses)
      setBaseUrlInputs((prev) => ({ ...baseUrls, ...prev }))
    } catch { /* service not ready */ }
  }, [])

  useEffect(() => {
    fetchAgents()
    loadProviders()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  /* ── Validate key on blur；驗證通過自動儲存 ── */
  const handleKeyBlur = useCallback(async (provider: ProviderInfo) => {
    const key = keyInputs[provider.id]?.trim()
    if (!key) return
    setValidations((s) => ({ ...s, [provider.id]: 'validating' }))
    const pt = provider.providerType ?? 'openai'
    const storedBase = baseUrlInputs[provider.id] || provider.baseUrl || undefined
    const result = await validateKeyViaBackend(pt, key, storedBase)
    setValidations((s) => ({ ...s, [provider.id]: result }))
    if (result.valid) {
      setKeySaving((s) => ({ ...s, [provider.id]: true }))
      try {
        await setProviderKey(provider.id, key)
        setKeyInputs((s) => ({ ...s, [provider.id]: '' }))
        await loadProviders()
      } finally {
        setKeySaving((s) => ({ ...s, [provider.id]: false }))
      }
    }
  }, [keyInputs, baseUrlInputs, loadProviders])

  /* ── Save key ── */
  const handleSaveKey = useCallback(async (profileId: string) => {
    const key = keyInputs[profileId]?.trim()
    if (!key) return
    setKeySaving((s) => ({ ...s, [profileId]: true }))
    try {
      await setProviderKey(profileId, key)
      setKeyInputs((s) => ({ ...s, [profileId]: '' }))
      await loadProviders()
    } finally {
      setKeySaving((s) => ({ ...s, [profileId]: false }))
    }
  }, [keyInputs, loadProviders])

  /* ── Delete key ── */
  const handleDeleteKey = useCallback(async (profileId: string) => {
    await deleteProviderKey(profileId)
    setValidations((s) => ({ ...s, [profileId]: 'idle' }))
    await loadProviders()
  }, [loadProviders])

  /* ── BaseURL blur → save ── */
  const handleBaseUrlBlur = useCallback(async (profileId: string) => {
    const url = baseUrlInputs[profileId]?.trim() ?? null
    await setProviderBaseUrl(profileId, url || null)
  }, [baseUrlInputs])

  /* ── Drag end → reorder ── */
  const handleDragEnd = useCallback(async (event: DragEndEvent) => {
    const { active, over } = event
    if (!over || active.id === over.id) return
    const oldIndex = providers.findIndex((p) => p.id === active.id)
    const newIndex = providers.findIndex((p) => p.id === over.id)
    const reordered = arrayMove(providers, oldIndex, newIndex)
    setProviders(reordered)
    await reorderProviders(reordered.map((p) => p.id))
  }, [providers])

  /* ── GitHub PAT blur ── */
  const handleGithubPatBlur = useCallback(async () => {
    const pat = githubPat.trim()
    if (!pat) { setGithubPatValidation('idle'); return }
    setGithubPatValidation('validating')
    const result = await validateKeyViaBackend('github', pat)
    setGithubPatValidation(result)
  }, [githubPat])

  const SYSTEM_PROMPT_MAX = 500

  return (
    <div className="flex-1 overflow-y-auto p-8">
      <div className="mx-auto flex max-w-4xl flex-col gap-6">
        <div className="mb-2">
          <h1 className="text-2xl font-bold text-ink">設定</h1>
          <p className="mt-1 text-sm text-ink-secondary">管理外觀、API 金鑰與 Agent 行為</p>
        </div>

        <div className="flex gap-1 overflow-x-auto border-b border-border" role="tablist" aria-label="設定分類">
          {SETTINGS_CATEGORIES.map(category => <button
            key={category.id}
            type="button"
            role="tab"
            aria-selected={activeCategory === category.id}
            onClick={() => setActiveCategory(category.id)}
            className={cn(
              'shrink-0 border-b-2 px-3 py-2 text-sm transition-colors',
              activeCategory === category.id
                ? 'border-brand font-medium text-brand'
                : 'border-transparent text-ink-secondary hover:text-ink',
            )}
          >{category.label}</button>)}
        </div>

        {/* ── Appearance ── */}
        <div className={activeCategory === 'general' ? 'contents' : 'hidden'}>
        <SettingsSection title="外觀" description="選擇介面主題風格">
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
            {THEME_OPTIONS.map((option) => {
              const isSelected = theme === option.id
              return (
                <button
                  key={option.id}
                  type="button"
                  onClick={() => setTheme(option.id)}
                  className={cn(
                    'relative rounded-2xl overflow-hidden border-2 text-left transition-all duration-200 cursor-pointer',
                    'hover:shadow-md focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand',
                    isSelected ? 'border-brand shadow-sm shadow-brand/20' : 'border-border hover:border-brand/30',
                  )}
                >
                  <div className="h-20 w-full bg-surface-alt">{option.preview}</div>
                  <div className="px-3 py-2.5 bg-surface">
                    <p className="text-xs font-semibold text-ink">{option.name}</p>
                    <p className="text-[11px] text-ink-subtle mt-0.5 leading-tight">{option.description}</p>
                  </div>
                  {isSelected && (
                    <span className="absolute top-2 right-2 w-5 h-5 rounded-full bg-brand flex items-center justify-center shadow-sm">
                      <Check className="w-3 h-3 text-white" strokeWidth={3} />
                    </span>
                  )}
                </button>
              )
            })}
          </div>
        </SettingsSection>

        <SettingsSection title="版本控制" description="管理 Git／Bitbucket、SVN 連線與 Portable CLI Runtime。">
          <VersionControlSettingsPanel />
        </SettingsSection>

        <SettingsSection
          title="語音輸入"
          description="本機離線 Speech-to-Text。下載或匯入模型後，對話框會顯示麥克風。"
        >
          <SpeechSettingsPanel />
        </SettingsSection>
        </div>

        {/* ── General settings (system prompt only) ── */}
        <div className={activeCategory === 'agent' ? 'contents' : 'hidden'}>
        <SettingsSection title="系統提示詞" description="定義 Agent 行為設定">
          <Textarea
            label="系統提示詞"
            placeholder="您是一位專業的 AI 助手…"
            value={systemPrompt}
            onChange={(e) => setSystemPrompt(e.target.value)}
            rows={6}
            showCount
            maxCount={SYSTEM_PROMPT_MAX}
            hint="定義 AI Agent 的角色、語氣與行為規範"
          />
        </SettingsSection>
        </div>

        {/* ── AI Provider API Keys ── */}
        <div className={activeCategory === 'agent' ? 'contents' : 'hidden'}>
        <SettingsSection
          title="AI 供應商 API 金鑰"
          description="拖動左側圖示可調整順序，新對話將依此順序列出供應商。"
        >
          {providers.length === 0 && (
            <p className="text-sm text-ink-subtle">載入中… 請確認 Agent Service 已啟動。</p>
          )}
          {providers.length > 0 && (
            <button
              type="button"
              onClick={toggleAllRows}
              className="flex items-center gap-2 text-sm text-ink-secondary hover:text-ink transition-colors mb-3"
            >
              {allExpanded ? <ChevronUp className="w-4 h-4" /> : <ChevronDown className="w-4 h-4" />}
              {allExpanded ? '收合全部' : '展開設定'}
            </button>
          )}
          <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={handleDragEnd}>
            <SortableContext items={providers.map((p) => p.id)} strategy={verticalListSortingStrategy}>
              <div className="space-y-3">
                {providers.map((provider) => (
                  <SortableProviderRow
                    key={provider.id}
                    provider={provider}
                    status={keyStatuses[provider.id]}
                    keyInput={keyInputs[provider.id] ?? ''}
                    baseUrlInput={baseUrlInputs[provider.id] ?? ''}
                    keyVisible={keyVisible[provider.id] ?? false}
                    saving={keySaving[provider.id] ?? false}
                    validation={validations[provider.id] ?? 'idle'}
                    expanded={expandedRows[provider.id] ?? false}
                    onToggleExpanded={() => setExpandedRows((s) => ({ ...s, [provider.id]: !s[provider.id] }))}
                    onKeyChange={(v) => setKeyInputs((s) => ({ ...s, [provider.id]: v }))}
                    onBaseUrlChange={(v) => setBaseUrlInputs((s) => ({ ...s, [provider.id]: v }))}
                    onKeyBlur={() => handleKeyBlur(provider)}
                    onBaseUrlBlur={() => handleBaseUrlBlur(provider.id)}
                    onToggleVisible={() => setKeyVisible((s) => ({ ...s, [provider.id]: !s[provider.id] }))}
                    onSaveKey={() => handleSaveKey(provider.id)}
                    onDeleteKey={() => handleDeleteKey(provider.id)}
                  />
                ))}
              </div>
            </SortableContext>
          </DndContext>
        </SettingsSection>
        </div>

        <div className={activeCategory === 'agent' ? 'contents' : 'hidden'}>
        <SettingsSection title="預設 Agent 模式" description="新對話與尚未個別設定的專案會使用此模式。">
          <div className="grid gap-3 sm:grid-cols-2">
            {AGENT_MODE_OPTIONS.map(option => {
              const Icon = option.icon
              const selected = defaultAgentMode === option.id
              return <button
                key={option.id}
                type="button"
                aria-pressed={selected}
                onClick={() => setDefaultAgentMode(option.id)}
                className={cn(
                  'flex min-h-24 items-start gap-3 rounded-lg border p-4 text-left transition-colors',
                  selected ? 'border-brand bg-brand/5' : 'border-border bg-surface hover:bg-surface-alt',
                )}
              >
                <Icon className={cn('mt-0.5 h-5 w-5 shrink-0', selected ? 'text-brand' : 'text-ink-subtle')} />
                <span>
                  <span className="block text-sm font-semibold text-ink">{option.label}</span>
                  <span className="mt-1 block text-xs leading-5 text-ink-secondary">{option.description}</span>
                </span>
              </button>
            })}
          </div>
        </SettingsSection>
        </div>

        <div className={activeCategory === 'agent' ? 'contents' : 'hidden'}>
        <SettingsSection title="開發 Runtime" description="Agent 執行 Skill 與專案工具時可用的 Python、Node.js 與 PowerShell。">
          <RuntimeSettingsPanel />
        </SettingsSection>
        </div>

        <div className={activeCategory === 'data' ? 'contents' : 'hidden'}>
        <SettingsSection title="資料與稽核" description="查詢 Agent、供應商、工具與版本控制的安全事件。">
          <AuditSettingsPanel />
        </SettingsSection>
        </div>

        <div className={activeCategory === 'agent' ? 'contents' : 'hidden'}>
        <SettingsSection title="MCP 伺服器" description="管理 MCP 連線、檢查健康狀態並重新連線。">
          <McpTab />
        </SettingsSection>
        </div>

        {/* ── Skills: GitHub PAT ── */}
        <div className={activeCategory === 'agent' ? 'contents' : 'hidden'}>
        <SettingsSection
          title="Skills 技能庫"
          description="GitHub Personal Access Token（選填）— 填入後可提升 API 請求上限，且若含 repo 權限可讓技能庫存取私有倉庫。"
        >
          <div className="space-y-1.5">
            <label className="block text-sm font-medium text-ink">
              GitHub Personal Access Token（選填）
            </label>
            <input
              type="password"
              value={githubPat}
              onChange={(e) => setGithubPat(e.target.value)}
              onBlur={handleGithubPatBlur}
              placeholder="ghp_..."
              className="w-full rounded-xl border border-border bg-surface px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-brand/40"
            />
            <ValidationBadge state={githubPatValidation} isGithub />
            <p className="text-xs text-ink-subtle">Token 僅存於本機記憶體，重啟後需重新填入。</p>
          </div>
        </SettingsSection>
        </div>

        {/* ── Skills: Agent Paths ── */}
        <div className={activeCategory === 'agent' ? 'contents' : 'hidden'}>
        <SettingsSection title="Agent 技能路徑" description="自訂各 AI Agent 的全域技能資料夾位置">
          <button
            type="button"
            onClick={() => setAgentPathsOpen((o) => !o)}
            className="flex items-center gap-2 text-sm text-ink-secondary hover:text-ink transition-colors"
          >
            {agentPathsOpen ? <ChevronUp className="w-4 h-4" /> : <ChevronDown className="w-4 h-4" />}
            {agentPathsOpen ? '收合' : '展開設定'}
          </button>
          {agentPathsOpen && (
            <div className="mt-4 space-y-4">
              {agents.map((agent) => {
                const editing = editingPaths[agent.id]
                const displayVal = editing !== undefined ? editing : (agent.customGlobalPath ?? '')
                return (
                  <div key={agent.id}>
                    <label className="block text-xs font-medium text-ink-secondary mb-1">
                      {agent.displayName}
                    </label>
                    <div className="flex gap-2 items-center">
                      <input
                        type="text"
                        value={displayVal}
                        onChange={(e) => setEditingPaths((p) => ({ ...p, [agent.id]: e.target.value }))}
                        placeholder={agent.globalSkillsPath}
                        className={cn(
                          'flex-1 rounded-xl border border-border bg-surface px-3 py-2 text-sm font-mono',
                          'placeholder:text-ink-subtle focus:outline-none focus:ring-2 focus:ring-brand/40',
                        )}
                      />
                      {editing !== undefined && (
                        <Button
                          size="sm"
                          onClick={async () => {
                            const val = editingPaths[agent.id]?.trim()
                            await updateAgentPath(agent.id, val || undefined)
                            setEditingPaths((p) => { const n = { ...p }; delete n[agent.id]; return n })
                          }}
                        >
                          儲存
                        </Button>
                      )}
                    </div>
                    <p className="mt-1 text-xs text-ink-subtle">預設：{agent.globalSkillsPath}</p>
                  </div>
                )
              })}
            </div>
          )}
        </SettingsSection>
        </div>

        <div className="pb-4" />
      </div>
    </div>
  )
}
