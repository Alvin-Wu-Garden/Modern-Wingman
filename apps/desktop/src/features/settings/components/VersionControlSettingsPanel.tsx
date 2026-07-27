import { useCallback, useEffect, useState, type ReactNode } from 'react'
import { Eye, EyeOff, GitBranch, Loader2, Plus, RefreshCw, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import {
  deleteVcsProfile,
  listVcsProfiles,
  saveVcsProfile,
  testVcsProfile,
  type SaveVcsProfile,
  type VcsProfile,
} from '@/services/agent-api/vcs'

const emptyProfile: SaveVcsProfile = {
  name: '',
  vcsType: 'git',
  baseUrl: '',
  sslVerificationEnabled: true,
  defaultWorkspaceRoot: null,
  enabled: true,
  username: null,
  secretType: 'AccessToken',
  secretValue: null,
}

const messageOf = (error: unknown) =>
  error instanceof Error ? error.message : String(error)

const fieldClass =
  'mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 text-sm outline-none focus:border-brand'

/**
 * 遠端專案匯入只需要連線 Profile。
 * 已移除 commit 作者、protected refs、worktree 與 Shadow Git 等寫入流程設定。
 */
export function VersionControlSettingsPanel() {
  const [profiles, setProfiles] = useState<VcsProfile[]>([])
  const [form, setForm] = useState<SaveVcsProfile | null>(null)
  const [editing, setEditing] = useState<string | null>(null)
  const [showSecret, setShowSecret] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      setProfiles(await listVcsProfiles())
    } catch (reason) {
      setError(messageOf(reason))
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const edit = (profile: VcsProfile) => {
    setEditing(profile.id)
    setForm({
      name: profile.name,
      vcsType: profile.vcsType,
      baseUrl: profile.baseUrl,
      sslVerificationEnabled: profile.sslVerificationEnabled,
      defaultWorkspaceRoot: profile.defaultWorkspaceRoot,
      enabled: profile.enabled,
      username: profile.username,
      secretType: profile.vcsType === 'git' ? 'AccessToken' : 'Password',
      secretValue: null,
    })
  }

  const submit = async () => {
    if (!form || busy) return
    setBusy(true)
    setError(null)
    try {
      await saveVcsProfile(editing, form)
      setForm(null)
      setEditing(null)
      setNotice('連線設定已儲存。')
      await load()
    } catch (reason) {
      setError(messageOf(reason))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <p className="text-sm font-medium text-ink">Git／SVN 連線</p>
          <p className="text-xs text-ink-subtle">僅供 clone、pull、checkout 與 update 使用。</p>
        </div>
        <div className="flex gap-1">
          <Button variant="ghost" size="icon" title="重新載入" onClick={() => void load()}>
            <RefreshCw className="h-4 w-4" />
          </Button>
          <Button
            size="sm"
            onClick={() => {
              setEditing(null)
              setForm({ ...emptyProfile })
            }}
          >
            <Plus className="mr-1 h-4 w-4" />
            新增
          </Button>
        </div>
      </div>

      {profiles.map((profile) => (
        <div key={profile.id} className="flex items-center gap-3 rounded-xl border border-border bg-surface px-3 py-3">
          <GitBranch className="h-4 w-4 text-brand" />
          <button className="min-w-0 flex-1 text-left" onClick={() => edit(profile)}>
            <p className="text-sm font-medium text-ink">{profile.name}</p>
            <p className="truncate text-xs text-ink-subtle">
              {profile.vcsType.toUpperCase()} · {profile.baseUrl} · {profile.hasSecret ? '已設定憑證' : '未設定憑證'}
            </p>
          </button>
          <Button
            variant="ghost"
            size="sm"
            onClick={async () => {
              try {
                const result = await testVcsProfile(profile.id)
                result.success
                  ? setNotice(result.output || '連線成功。')
                  : setError(result.error ?? '連線失敗。')
              } catch (reason) {
                setError(messageOf(reason))
              }
            }}
          >
            測試
          </Button>
          <Button
            variant="ghost"
            size="icon"
            title="刪除"
            onClick={async () => {
              await deleteVcsProfile(profile.id)
              await load()
            }}
          >
            <Trash2 className="h-4 w-4" />
          </Button>
        </div>
      ))}

      {profiles.length === 0 && !form && (
        <p className="text-sm text-ink-subtle">尚未建立 Git 或 SVN 連線設定。</p>
      )}

      {form && (
        <div className="space-y-3 border-t border-border pt-4">
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="名稱">
              <input
                className={fieldClass}
                value={form.name}
                onChange={(event) => setForm({ ...form, name: event.target.value })}
              />
            </Field>
            <Field label="類型">
              <select
                className={fieldClass}
                value={form.vcsType}
                onChange={(event) => {
                  const vcsType = event.target.value as 'git' | 'svn'
                  setForm({
                    ...form,
                    vcsType,
                    secretType: vcsType === 'git' ? 'AccessToken' : 'Password',
                  })
                }}
              >
                <option value="git">Git</option>
                <option value="svn">SVN</option>
              </select>
            </Field>
          </div>
          <Field label="Base URL">
            <input
              className={fieldClass}
              value={form.baseUrl}
              onChange={(event) => setForm({ ...form, baseUrl: event.target.value })}
            />
          </Field>
          <Field label="預設專案根目錄">
            <input
              className={fieldClass}
              value={form.defaultWorkspaceRoot ?? ''}
              placeholder="例如 D:\Projects"
              onChange={(event) =>
                setForm({ ...form, defaultWorkspaceRoot: event.target.value || null })}
            />
          </Field>
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="帳號">
              <input
                className={fieldClass}
                value={form.username ?? ''}
                onChange={(event) => setForm({ ...form, username: event.target.value || null })}
              />
            </Field>
            <Field label={form.vcsType === 'git' ? 'Access Token' : '密碼'}>
              <span className="relative block">
                <input
                  type={showSecret ? 'text' : 'password'}
                  className={`${fieldClass} pr-10`}
                  value={form.secretValue ?? ''}
                  placeholder={editing ? '留白以保留現有憑證' : ''}
                  onChange={(event) =>
                    setForm({ ...form, secretValue: event.target.value || null })}
                />
                <button
                  type="button"
                  className="absolute right-2 top-2 text-ink-subtle"
                  onClick={() => setShowSecret((value) => !value)}
                >
                  {showSecret ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
                </button>
              </span>
            </Field>
          </div>
          <label className="flex items-center gap-2 text-sm text-ink">
            <input
              type="checkbox"
              checked={form.sslVerificationEnabled}
              onChange={(event) =>
                setForm({ ...form, sslVerificationEnabled: event.target.checked })}
            />
            驗證 SSL 憑證
          </label>
          <div className="flex justify-end gap-2">
            <Button variant="ghost" onClick={() => setForm(null)}>取消</Button>
            <Button disabled={busy || !form.name || !form.baseUrl} onClick={() => void submit()}>
              {busy && <Loader2 className="mr-1 h-4 w-4 animate-spin" />}
              儲存
            </Button>
          </div>
        </div>
      )}

      {notice && <p className="text-xs text-emerald-700">{notice}</p>}
      {error && <p className="text-xs text-red-700">{error}</p>}
    </div>
  )
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block text-sm text-ink">
      {label}
      {children}
    </label>
  )
}
