import { useEffect, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import { open } from '@tauri-apps/plugin-dialog'
import { ChevronDown, Database, FolderOpen, Loader2, PlugZap, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/modal'
import {
  deleteProjectDatabaseConfiguration,
  getProjectDatabaseConfigurations,
  listProjectSqlServerDatabases,
  saveProjectDatabaseConfiguration,
  testProjectDatabaseConnection,
  type SaveProjectDatabaseConfiguration,
} from '@/services/agent-api/projects'

interface ProjectDatabaseModalProps {
  projectId: string
  projectName: string
  onClose: () => void
}

type DatabaseProvider = SaveProjectDatabaseConfiguration['provider']

interface Feedback {
  provider: DatabaseProvider
  tone: 'success' | 'error'
  message: string
}

const catalogConnectionKeys: ReadonlyArray<keyof SaveProjectDatabaseConfiguration> = [
  'provider',
  'server',
  'port',
  'authentication',
  'username',
  'password',
  'trustServerCertificate',
]

/** 建立單一 Provider 的空白表單，避免 SQL Server 與 SQLite 共用可變物件。 */
const createEmptyConfiguration = (
  provider: DatabaseProvider,
): SaveProjectDatabaseConfiguration => ({
  provider,
  server: '',
  port: provider === 'SqlServer' ? 1433 : null,
  databaseName: '',
  authentication: provider === 'SqlServer' ? 'SqlPassword' : null,
  username: '',
  password: '',
  trustServerCertificate: true,
  sqlitePath: '',
})

type ProviderForms = Record<DatabaseProvider, SaveProjectDatabaseConfiguration>
type ProviderPasswordState = Record<DatabaseProvider, boolean>

const createEmptyForms = (): ProviderForms => ({
  SqlServer: createEmptyConfiguration('SqlServer'),
  Sqlite: createEmptyConfiguration('Sqlite'),
})

const createEmptyPasswordState = (): ProviderPasswordState => ({
  SqlServer: false,
  Sqlite: false,
})

const messageOf = (error: unknown) =>
  error instanceof Error ? error.message : String(error)

const inputClass =
  'w-full rounded-lg border border-border bg-surface px-3 py-2 text-sm text-ink outline-none focus:border-brand'

/**
 * 專案資料庫設定視窗。
 * SQL Server 與 SQLite 各自保存一份表單狀態；切換 Provider 不會覆蓋另一份設定。
 * 密碼欄留白代表沿用已保存的 DPAPI 密碼，前端永遠讀不到原始密碼。
 */
export function ProjectDatabaseModal({
  projectId,
  projectName,
  onClose,
}: ProjectDatabaseModalProps) {
  const [forms, setForms] = useState<ProviderForms>(createEmptyForms)
  const [hasPasswords, setHasPasswords] = useState<ProviderPasswordState>(
    createEmptyPasswordState,
  )
  const [activeProvider, setActiveProvider] = useState<DatabaseProvider>('SqlServer')
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [feedback, setFeedback] = useState<Feedback | null>(null)
  const [databaseNames, setDatabaseNames] = useState<string[]>([])
  const [catalogLoading, setCatalogLoading] = useState(false)
  const [databaseMenuOpen, setDatabaseMenuOpen] = useState(false)
  const [highlightedDatabase, setHighlightedDatabase] = useState(0)
  const catalogVersion = useRef(0)
  const databaseInputRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    let active = true
    void getProjectDatabaseConfigurations(projectId)
      .then((configurations) => {
        if (!active) return

        // /database/all 只回傳已設定 Provider；未設定的 Provider 保留空白表單，
        // 讓使用者能在同一個視窗分別建立 SQL Server 與 SQLite 設定。
        const nextForms = createEmptyForms()
        const nextPasswords = createEmptyPasswordState()
        for (const configuration of configurations) {
          nextForms[configuration.provider] = {
            provider: configuration.provider,
            server: configuration.server ?? '',
            port: configuration.port ?? (configuration.provider === 'SqlServer' ? 1433 : null),
            databaseName: configuration.databaseName ?? '',
            authentication:
              configuration.authentication ??
              (configuration.provider === 'SqlServer' ? 'SqlPassword' : null),
            username: configuration.username ?? '',
            password: '',
            trustServerCertificate: configuration.trustServerCertificate,
            sqlitePath: configuration.sqlitePath ?? '',
          }
          nextPasswords[configuration.provider] = configuration.hasPassword
        }
        // 優先開啟已設定的 SQL Server；若只有 SQLite，直接顯示 SQLite 設定。
        setActiveProvider(
          configurations.some((item) => item.provider === 'SqlServer')
            ? 'SqlServer'
            : configurations.some((item) => item.provider === 'Sqlite')
              ? 'Sqlite'
              : 'SqlServer',
        )
        setForms(nextForms)
        setHasPasswords(nextPasswords)
      })
      .catch((reason) => {
        if (active)
          setFeedback({
            provider: 'SqlServer',
            tone: 'error',
            message: messageOf(reason),
          })
      })
      .finally(() => active && setLoading(false))
    return () => {
      active = false
    }
  }, [projectId])

  const form = forms[activeProvider]
  const hasPassword = hasPasswords[activeProvider]

  const update = <K extends keyof SaveProjectDatabaseConfiguration>(
    key: K,
    value: SaveProjectDatabaseConfiguration[K],
  ) => {
    setForms((current) => ({
      ...current,
      [activeProvider]: { ...current[activeProvider], [key]: value },
    }))
    setFeedback(null)
    if (catalogConnectionKeys.includes(key)) {
      // 連線條件一變，先前取得的資料庫清單就可能失效，禁止沿用舊結果。
      catalogVersion.current += 1
      setDatabaseNames([])
      setDatabaseMenuOpen(false)
    }
  }

  const chooseSqlite = async () => {
    const selected = await open({
      directory: false,
      multiple: false,
      filters: [{ name: 'SQLite', extensions: ['db', 'sqlite', 'sqlite3'] }],
    })
    if (typeof selected === 'string') update('sqlitePath', selected)
  }

  /**
   * 儲存成功後立即關閉 Modal；驗證或寫入失敗時保留表單，
   * 讓使用者可直接依錯誤訊息修正，不必重新輸入密碼。
   */
  const save = async () => {
    if (busy) return
    setBusy(true)
    setFeedback(null)
    const provider = form.provider
    try {
      await saveProjectDatabaseConfiguration(projectId, {
        ...form,
        password: form.password || null,
      })
      onClose()
    } catch (reason) {
      setFeedback({ provider, tone: 'error', message: messageOf(reason) })
    } finally {
      setBusy(false)
    }
  }

  /**
   * 只測試目前表單的候選設定，不先儲存，也不改變已保存的 DPAPI 密碼。
   * 密碼留白時，後端可暫時沿用該專案既有密碼完成測試。
   */
  const testConnection = async () => {
    if (busy) return
    setBusy(true)
    setFeedback(null)
    const provider = form.provider
    try {
      const result = await testProjectDatabaseConnection(projectId, {
        ...form,
        password: form.password || null,
      })
      if (result.success)
        setFeedback({
          provider,
          tone: 'success',
          message: result.message ?? '資料庫連線成功。',
        })
      else
        setFeedback({
          provider,
          tone: 'error',
          message: result.error ?? '資料庫連線失敗。',
        })
    } catch (reason) {
      setFeedback({ provider, tone: 'error', message: messageOf(reason) })
    } finally {
      setBusy(false)
    }
  }

  /**
   * 以目前 SQL Server 候選連線讀取可使用的資料庫清單。
   * catalogVersion 可避免使用者修改主機或帳密後，較慢回來的舊請求覆蓋新狀態。
   */
  const loadDatabaseNames = async () => {
    if (catalogLoading || form.provider !== 'SqlServer') return
    if (
      !form.server?.trim() ||
      !form.port ||
      (form.authentication === 'SqlPassword' &&
        (!form.username?.trim() || (!form.password && !hasPassword)))
    ) {
      setFeedback({
        provider: 'SqlServer',
        tone: 'error',
        message: '請先填妥伺服器、連接埠及驗證資訊，再讀取資料庫清單。',
      })
      return
    }

    const requestVersion = catalogVersion.current
    setCatalogLoading(true)
    setFeedback(null)
    try {
      const names = await listProjectSqlServerDatabases(projectId, {
        ...form,
        password: form.password || null,
      })
      if (requestVersion !== catalogVersion.current) return
      setDatabaseNames(names)
      setHighlightedDatabase(0)
      databaseInputRef.current?.focus()
      setDatabaseMenuOpen(names.length > 0)
      if (names.length === 0) {
        setFeedback({
          provider: 'SqlServer',
          tone: 'error',
          message: '目前帳號沒有可選取的線上資料庫。',
        })
      }
    } catch (reason) {
      if (requestVersion === catalogVersion.current) {
        setFeedback({
          provider: 'SqlServer',
          tone: 'error',
          message: messageOf(reason),
        })
      }
    } finally {
      setCatalogLoading(false)
    }
  }

  const databaseQuery = form.databaseName?.trim().toLocaleLowerCase() ?? ''
  const filteredDatabaseNames = databaseNames.filter((name) =>
    name.toLocaleLowerCase().includes(databaseQuery),
  )

  /** 從自訂清單選取資料庫，保留輸入框焦點並關閉浮層。 */
  const selectDatabase = (name: string) => {
    update('databaseName', name)
    setDatabaseMenuOpen(false)
  }

  /** 提供方向鍵、Enter 與 Escape，避免自訂下拉選單只能使用滑鼠。 */
  const handleDatabaseKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Escape') {
      setDatabaseMenuOpen(false)
      return
    }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      if (!databaseMenuOpen) {
        setDatabaseMenuOpen(true)
        setHighlightedDatabase(
          event.key === 'ArrowDown' ? 0 : Math.max(0, filteredDatabaseNames.length - 1),
        )
        return
      }
      setDatabaseMenuOpen(true)
      const direction = event.key === 'ArrowDown' ? 1 : -1
      setHighlightedDatabase((current) =>
        Math.max(0, Math.min(filteredDatabaseNames.length - 1, current + direction)),
      )
      return
    }
    if (event.key === 'Enter' && databaseMenuOpen && filteredDatabaseNames.length > 0) {
      event.preventDefault()
      selectDatabase(filteredDatabaseNames[highlightedDatabase] ?? filteredDatabaseNames[0])
    }
  }

  const remove = async () => {
    if (busy) return
    setBusy(true)
    setFeedback(null)
    const provider = activeProvider
    try {
      // 只刪除目前分頁的 Provider，避免移除 SQL Server 時誤刪 SQLite 設定。
      await deleteProjectDatabaseConfiguration(projectId, provider)
      setForms((current) => ({
        ...current,
        [provider]: createEmptyConfiguration(provider),
      }))
      setHasPasswords((current) => ({ ...current, [provider]: false }))
      if (provider === 'SqlServer') {
        setDatabaseNames([])
        catalogVersion.current += 1
      }
      setFeedback({
        provider,
        tone: 'success',
        message: `${provider === 'SqlServer' ? 'SQL Server' : 'SQLite'} 設定已移除；既有知識圖譜不受影響。`,
      })
    } catch (reason) {
      setFeedback({
        provider: form.provider,
        tone: 'error',
        message: messageOf(reason),
      })
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal open onOpenChange={(value) => !value && onClose()} title={`${projectName} · 資料庫連線`}>
      {loading ? (
        <div className="flex items-center justify-center py-12 text-sm text-ink-subtle">
          <Loader2 className="mr-2 h-4 w-4 animate-spin" />
          讀取設定中…
        </div>
      ) : (
        <div className="space-y-4">
          <div className="grid grid-cols-2 gap-2 rounded-xl bg-surface-alt p-1">
            {(['SqlServer', 'Sqlite'] as const).map((provider) => (
              <button
                key={provider}
                type="button"
                onClick={() => {
                  setActiveProvider(provider)
                  setFeedback(null)
                }}
                className={`rounded-lg px-3 py-2 text-sm ${
                  activeProvider === provider
                    ? 'bg-surface font-medium text-brand shadow-sm'
                    : 'text-ink-secondary'
                }`}
              >
                {provider === 'SqlServer' ? 'SQL Server' : 'SQLite'}
              </button>
            ))}
          </div>

          {form.provider === 'SqlServer' ? (
            <>
              <div className="grid grid-cols-[1fr_120px] gap-3">
                <Field label="伺服器">
                  <input
                    value={form.server ?? ''}
                    onChange={(event) => update('server', event.target.value)}
                    placeholder="127.0.0.1 或主機名稱"
                    className={inputClass}
                  />
                </Field>
                <Field label="連接埠">
                  <input
                    type="number"
                    value={form.port ?? ''}
                    onChange={(event) => update('port', Number(event.target.value) || null)}
                    className={inputClass}
                  />
                </Field>
              </div>
              <Field label="驗證方式">
                <select
                  value={form.authentication ?? 'SqlPassword'}
                  onChange={(event) =>
                    update(
                      'authentication',
                      event.target.value as 'SqlPassword' | 'IntegratedSecurity',
                    )}
                  className={inputClass}
                >
                  <option value="SqlPassword">SQL Server 帳號密碼</option>
                  <option value="IntegratedSecurity">Windows 整合驗證</option>
                </select>
              </Field>
              {form.authentication === 'SqlPassword' && (
                <div className="grid grid-cols-2 gap-3">
                  <Field label="使用者名稱">
                    <input
                      value={form.username ?? ''}
                      onChange={(event) => update('username', event.target.value)}
                      className={inputClass}
                    />
                  </Field>
                  <Field label={hasPassword ? '密碼（留白沿用）' : '密碼'}>
                    <input
                      type="password"
                      value={form.password ?? ''}
                      onChange={(event) => update('password', event.target.value)}
                      placeholder={hasPassword ? '已安全保存' : ''}
                      className={inputClass}
                    />
                  </Field>
                </div>
              )}
              <div className="flex items-center gap-2 text-sm text-ink-secondary">
                <input
                  type="checkbox"
                  aria-label="信任伺服器憑證"
                  checked={form.trustServerCertificate ?? true}
                  onChange={(event) => update('trustServerCertificate', event.target.checked)}
                />
                <span>信任伺服器憑證（適用內網或開發環境）</span>
              </div>
              <Field label="資料庫名稱">
                <div className="relative">
                    <input
                      ref={databaseInputRef}
                      role="combobox"
                      aria-autocomplete="list"
                      aria-expanded={databaseMenuOpen}
                      aria-controls={`project-databases-${projectId}`}
                      value={form.databaseName ?? ''}
                      onChange={(event) => {
                        update('databaseName', event.target.value)
                        setHighlightedDatabase(0)
                        setDatabaseMenuOpen(databaseNames.length > 0)
                      }}
                      onFocus={() => {
                        if (databaseNames.length > 0) {
                          setDatabaseMenuOpen(true)
                        } else {
                          // 聚焦時自動載入候選清單，不要求使用者再操作額外按鈕。
                          void loadDatabaseNames()
                        }
                      }}
                      onBlur={() => setDatabaseMenuOpen(false)}
                      onKeyDown={handleDatabaseKeyDown}
                      placeholder="輸入關鍵字或從清單選取"
                      autoComplete="off"
                      className={`${inputClass} pr-10`}
                    />
                    <button
                      type="button"
                      aria-label="展開資料庫清單"
                      className="absolute inset-y-0 right-0 flex w-10 items-center justify-center rounded-r-lg text-ink-secondary hover:bg-surface-alt hover:text-ink"
                      onMouseDown={(event) => event.preventDefault()}
                      onClick={() => {
                        if (databaseNames.length > 0) {
                          const nextOpen = !databaseMenuOpen
                          databaseInputRef.current?.focus()
                          setDatabaseMenuOpen(nextOpen)
                        } else {
                          const alreadyFocused =
                            document.activeElement === databaseInputRef.current
                          databaseInputRef.current?.focus()
                          // focus 本身會自動載入；已在欄位內時才需要直接重試。
                          if (alreadyFocused)
                            void loadDatabaseNames()
                        }
                      }}
                    >
                      <ChevronDown
                        className={`h-4 w-4 transition-transform ${
                          databaseMenuOpen ? 'rotate-180' : ''
                        }`}
                      />
                    </button>
                    {databaseMenuOpen && (
                      <div
                        id={`project-databases-${projectId}`}
                        role="listbox"
                        className="absolute left-0 right-0 top-full z-[70] mt-1 max-h-52 overflow-y-auto rounded-lg border border-border bg-surface p-1 shadow-xl"
                      >
                        {filteredDatabaseNames.length > 0 ? (
                          filteredDatabaseNames.map((name, index) => (
                            <button
                              key={name}
                              type="button"
                              role="option"
                              aria-selected={name === form.databaseName}
                              className={`block w-full rounded-md px-3 py-2 text-left text-sm ${
                                index === highlightedDatabase
                                  ? 'bg-brand/10 text-brand'
                                  : 'text-ink hover:bg-surface-alt'
                              }`}
                              onMouseEnter={() => setHighlightedDatabase(index)}
                              onMouseDown={(event) => {
                                event.preventDefault()
                                selectDatabase(name)
                              }}
                            >
                              {name}
                            </button>
                          ))
                        ) : (
                          <p className="px-3 py-2 text-sm text-ink-subtle">
                            找不到相符資料庫，可直接使用目前輸入值。
                          </p>
                        )}
                      </div>
                    )}
                </div>
                <p className="mt-1 text-xs text-ink-subtle">
                  {catalogLoading
                    ? '正在讀取可用資料庫…'
                    : databaseNames.length > 0
                    ? `已載入 ${databaseNames.length} 個可用資料庫，可輸入關鍵字篩選。`
                    : '聚焦欄位會自動載入清單，也可以直接輸入資料庫名稱。'}
                </p>
              </Field>
            </>
          ) : (
            <Field label="SQLite 資料庫檔案">
              <div className="flex gap-2">
                <input
                  value={form.sqlitePath ?? ''}
                  readOnly
                  className={`${inputClass} min-w-0 flex-1 bg-surface-alt`}
                />
                <Button variant="outline" onClick={() => void chooseSqlite()}>
                  <FolderOpen className="mr-2 h-4 w-4" />
                  選擇
                </Button>
              </div>
              <p className="mt-1 text-xs text-ink-subtle">索引時只會以唯讀模式開啟檔案。</p>
            </Field>
          )}

          {feedback?.provider === form.provider && feedback.tone === 'success' && (
            <p className="rounded-lg bg-emerald-50 px-3 py-2 text-xs text-emerald-700">
              {feedback.message}
            </p>
          )}
          {feedback?.provider === form.provider && feedback.tone === 'error' && (
            <p className="rounded-lg bg-red-50 px-3 py-2 text-xs text-red-700">
              {feedback.message}
            </p>
          )}

          <div className="flex items-center justify-between border-t border-border pt-4">
            <Button variant="ghost" className="text-red-600" disabled={busy} onClick={() => void remove()}>
              <Trash2 className="mr-2 h-4 w-4" />
              移除設定
            </Button>
            <div className="flex gap-2">
              <Button variant="outline" disabled={busy} onClick={() => void save()}>
                <Database className="mr-2 h-4 w-4" />
                儲存
              </Button>
              <Button disabled={busy} onClick={() => void testConnection()}>
                {busy
                  ? <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                  : <PlugZap className="mr-2 h-4 w-4" />}
                測試連線
              </Button>
            </div>
          </div>
        </div>
      )}
    </Modal>
  )
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs font-medium text-ink-secondary">{label}</span>
      {children}
    </label>
  )
}
