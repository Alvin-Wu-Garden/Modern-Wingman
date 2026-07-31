import { useCallback, useEffect, useState } from 'react'
import { Check, Eye, EyeOff, Loader2, RefreshCw, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import {
  deleteAtlassianConnection,
  getAtlassianSettings,
  validateAndSaveConnection,
  atlassianErrorMessage,
  type AtlassianAuthType,
  type AtlassianConnectionDto,
  type AtlassianServiceType,
  type ValidateConnectionInput,
} from '@/services/agent-api/atlassian'

const fieldClass =
  'mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 text-sm outline-none focus:border-brand'

function ConnectionForm({
  serviceType,
  label,
  existing,
  onSaved,
}: {
  serviceType: AtlassianServiceType
  label: string
  existing: AtlassianConnectionDto | null
  onSaved: () => void
}) {
  const [baseUrl, setBaseUrl] = useState(existing?.baseUrl ?? '')
  const [authType, setAuthType] = useState<AtlassianAuthType>(existing?.authType ?? 'bearer')
  const [username, setUsername] = useState(existing?.username ?? '')
  const [token, setToken] = useState('')         // 留空表示沿用既有 Token
  const [showToken, setShowToken] = useState(false)
  const [busy, setBusy] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  // 編輯已有設定時，欄位同步
  useEffect(() => {
    const defaultBaseUrl = (serviceType === 'jira' ? 'https://km.fubonlife.com.tw/jira' : serviceType === 'wiki' ? 'https://km.fubonlife.com.tw/confluence' : null) ?? '';
    setBaseUrl(existing?.baseUrl ?? defaultBaseUrl)
    setAuthType(existing?.authType ?? 'bearer')
    setUsername(existing?.username ?? '')
    setToken('')  // Token 永不回填
  }, [existing])

  const handleValidate = async () => {
    if (!baseUrl.trim()) {
      setError('請輸入服務網址。')
      return
    }
    if (authType === 'basic' && !username.trim()) {
      setError('Basic 驗證需要輸入使用者名稱。')
      return
    }
    if (!token.trim() && !existing?.hasSecret) {
      setError('請輸入 Token。')
      return
    }
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      const input: ValidateConnectionInput = {
        baseUrl: baseUrl.trim(),
        authType,
        username: authType === 'basic' ? username.trim() : null,
        token: token.trim() || null,
        apiVersion: null,
      }
      const result = await validateAndSaveConnection(serviceType, input)
      setToken('')  // 驗證成功後清空前端記憶體中的 Token
      setNotice(`驗證成功，已登入為：${result.displayName}`)
      onSaved()
    } catch (reason) {
      const msg = reason instanceof Error ? reason.message : String(reason)
      setError(atlassianErrorMessage(msg) !== `操作失敗：${msg}` ? atlassianErrorMessage(msg) : msg)
    } finally {
      setBusy(false)
    }
  }

  const handleDelete = async () => {
    if (!existing) return
    setDeleting(true)
    setError(null)
    try {
      await deleteAtlassianConnection(serviceType)
      setToken('')
      setNotice('連線設定已移除。')
      onSaved()
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason))
    } finally {
      setDeleting(false)
    }
  }

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between">
        <p className="text-sm font-medium text-ink">{label}</p>
        {existing?.verified && (
          <span className="flex items-center gap-1 rounded-full bg-brand/10 px-2 py-0.5 text-xs font-medium text-brand">
            <Check className="h-3 w-3" />
            已驗證 · {existing.verifiedDisplayName}
          </span>
        )}
      </div>

      <label className="block">
        <span className="text-xs font-medium text-ink-secondary">服務網址</span>
        <input
          type="url"
          value={baseUrl}
          onChange={(e) => setBaseUrl(e.target.value)}
          placeholder="https://your-host.example/jira"
          className={fieldClass}
        />
      </label>

      <label className="block">
        <span className="text-xs font-medium text-ink-secondary">驗證方式</span>
        <select
          value={authType}
          onChange={(e) => setAuthType(e.target.value as AtlassianAuthType)}
          className={fieldClass}
        >
          <option value="bearer">Bearer PAT</option>
          <option value="basic">Basic</option>
        </select>
      </label>

      {authType === 'basic' && (
        <label className="block">
          <span className="text-xs font-medium text-ink-secondary">使用者名稱</span>
          <input
            type="text"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            autoComplete="off"
            className={fieldClass}
          />
        </label>
      )}

      <label className="block">
        <span className="text-xs font-medium text-ink-secondary">
          PAT / API Token
          {existing?.hasSecret && !token && (
            <span className="ml-1 text-ink-subtle">（留空則沿用已儲存 Token）</span>
          )}
        </span>
        <div className="relative mt-1">
          <input
            type={showToken ? 'text' : 'password'}
            value={token}
            onChange={(e) => setToken(e.target.value)}
            placeholder={existing?.hasSecret ? '••••••••（已設定）' : '輸入 Token'}
            autoComplete="new-password"
            className="w-full rounded-lg border border-border bg-surface px-3 py-2 pr-10 text-sm outline-none focus:border-brand"
          />
          <button
            type="button"
            className="absolute right-2 top-1/2 -translate-y-1/2 text-ink-subtle hover:text-ink"
            onClick={() => setShowToken((s) => !s)}
            tabIndex={-1}
          >
            {showToken ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
          </button>
        </div>
      </label>

      {error && <p className="text-xs text-red-500">{error}</p>}
      {notice && <p className="text-xs text-brand-green">{notice}</p>}

      <div className="flex gap-2">
        <Button size="sm" onClick={() => void handleValidate()} disabled={busy || deleting}>
          {busy ? <><Loader2 className="mr-1 h-3.5 w-3.5 animate-spin" />驗證中…</> : '驗證連線'}
        </Button>
        {existing && (
          <Button
            size="sm"
            variant="ghost"
            className="text-red-500 hover:text-red-600"
            onClick={() => void handleDelete()}
            disabled={busy || deleting}
          >
            {deleting ? <><Loader2 className="mr-1 h-3.5 w-3.5 animate-spin" />移除中…</> : (
              <><Trash2 className="mr-1 h-3.5 w-3.5" />移除</>
            )}
          </Button>
        )}
      </div>
    </div>
  )
}

/**
 * 一般設定頁中的「Atlassian 連線設定」面板。
 * 放在版本控制區塊下方，包含 JIRA 與 Wiki 獨立的連線表單。
 */
export function AtlassianSettingsPanel() {
  const [settings, setSettings] = useState<{ jira: AtlassianConnectionDto | null; wiki: AtlassianConnectionDto | null } | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      const data = await getAtlassianSettings()
      setSettings(data)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason))
    }
  }, [])

  useEffect(() => { void load() }, [load])

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <p className="text-xs text-ink-subtle">
          設定後可使用「分析 JIRA 議題」功能，Token 以 Windows DPAPI 加密儲存於本機。
        </p>
        <Button variant="ghost" size="icon" title="重新載入" onClick={() => void load()}>
          <RefreshCw className="h-4 w-4" />
        </Button>
      </div>

      {error && <p className="text-xs text-red-500">{error}</p>}

      {settings !== undefined && (
        <>
          <div className="rounded-xl border border-border bg-surface-alt p-4">
            <ConnectionForm
              serviceType="jira"
              label="JIRA 連線"
              existing={settings?.jira ?? null}
              onSaved={() => void load()}
            />
          </div>
          <div className="rounded-xl border border-border bg-surface-alt p-4">
            <ConnectionForm
              serviceType="wiki"
              label="Wiki / Confluence 連線"
              existing={settings?.wiki ?? null}
              onSaved={() => void load()}
            />
          </div>
        </>
      )}
    </div>
  )
}
