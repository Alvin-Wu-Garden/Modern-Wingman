import { useCallback, useEffect, useState } from 'react'
import { open } from '@tauri-apps/plugin-dialog'
import { CheckCircle2, CircleX, PackageOpen, RefreshCw, Terminal, Upload } from 'lucide-react'
import { Button } from '@/components/ui/button'
import {
  importDevelopmentRuntime,
  importPackageCache,
  listDevelopmentRuntimes,
  type DevelopmentRuntime,
} from '@/services/agent-api/runtimes'

const labels = { python: 'Python', node: 'Node.js', powershell: 'PowerShell' }

export function RuntimeSettingsPanel() {
  const [items, setItems] = useState<DevelopmentRuntime[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)

  const load = useCallback(async () => {
    setError(null)
    try { setItems(await listDevelopmentRuntimes()) }
    catch (err) { setError(err instanceof Error ? err.message : String(err)) }
  }, [])

  useEffect(() => { void load() }, [load])

  const chooseZip = async (title: string) => {
    const selected = await open({
      title,
      multiple: false,
      filters: [{ name: 'ZIP archive', extensions: ['zip'] }],
    })
    return typeof selected === 'string' ? selected : null
  }

  const importRuntime = async (kind: DevelopmentRuntime['kind']) => {
    const path = await chooseZip(`匯入 ${labels[kind]} Runtime`)
    if (!path) return
    setBusy(`runtime:${kind}`); setError(null)
    try { await importDevelopmentRuntime(kind, path); await load() }
    catch (err) { setError(err instanceof Error ? err.message : String(err)) }
    finally { setBusy(null) }
  }

  const importCache = async (kind: DevelopmentRuntime['kind']) => {
    const path = await chooseZip(`匯入 ${labels[kind]} 套件快取`)
    if (!path) return
    setBusy(`cache:${kind}`); setError(null)
    try { await importPackageCache(kind, path) }
    catch (err) { setError(err instanceof Error ? err.message : String(err)) }
    finally { setBusy(null) }
  }

  return <div className="space-y-3">
    <div className="flex justify-end">
      <Button variant="ghost" size="icon" title="重新偵測" onClick={() => void load()}>
        <RefreshCw className="h-4 w-4" />
      </Button>
    </div>
    <div className="grid gap-2 sm:grid-cols-3">
      {items.map(item => <div key={item.kind} className="border border-border bg-surface p-3">
        <div className="flex items-center gap-2">
          <Terminal className="h-4 w-4 text-brand" />
          <p className="text-sm font-medium text-ink">{labels[item.kind]}</p>
          {item.available
            ? <CheckCircle2 className="ml-auto h-4 w-4 text-success" />
            : <CircleX className="ml-auto h-4 w-4 text-danger" />}
        </div>
        <p className="mt-2 text-xs text-ink-secondary">
          {item.available ? `${item.version} · ${item.source}` : '找不到相容 Runtime'}
        </p>
        <p className="mt-1 truncate text-xs text-ink-subtle" title={item.executablePath ?? undefined}>
          {item.executablePath ?? '請匯入至 .Wingman/runtimes'}
        </p>
        <div className="mt-3 flex gap-1">
          <Button size="sm" variant="outline" disabled={busy !== null} onClick={() => void importRuntime(item.kind)}>
            <Upload className="mr-1.5 h-3.5 w-3.5" />匯入
          </Button>
          {item.kind !== 'powershell' && <Button size="sm" variant="ghost" disabled={busy !== null} onClick={() => void importCache(item.kind)}>
            <PackageOpen className="mr-1.5 h-3.5 w-3.5" />套件快取
          </Button>}
        </div>
      </div>)}
    </div>
    {error && <p className="text-xs text-danger">{error}</p>}
  </div>
}
