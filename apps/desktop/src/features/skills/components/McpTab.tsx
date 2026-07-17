import { useEffect, useState } from 'react'
import { CircleCheck, CircleX, Pencil, Plug, Plus, RefreshCw, Trash2, X } from 'lucide-react'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/modal'
import { useMcpStore } from '../store/useMcpStore'
import { useSkillsStore } from '../store/useSkillsStore'
import type { McpServer, UpsertMcpServerParams } from '@modern-wingman/contracts'
import { refreshMcpRuntime,type McpRuntimeStatus } from '@/services/agent-api/mcp-runtime'

const EMPTY_FORM: UpsertMcpServerParams = {
  name: '',
  transport: 'stdio',
  command: '',
  args: [],
  url: '',
  env: {},
  enabled: true,
}

function ServerFormModal({
  initial,
  onSave,
  onClose,
}: {
  initial: UpsertMcpServerParams
  onSave: (params: UpsertMcpServerParams) => Promise<void>
  onClose: () => void
}) {
  const [form, setForm] = useState<UpsertMcpServerParams>(initial)
  const [argsText, setArgsText] = useState(initial.args?.join(' ') ?? '')
  const [envText, setEnvText] = useState(
    Object.entries(initial.env ?? {})
      .map(([k, v]) => `${k}=${v}`)
      .join('\n'),
  )
  const [busy, setBusy] = useState(false)

  const handleSave = async () => {
    if (!form.name.trim()) return
    const env: Record<string, string> = {}
    for (const line of envText.split('\n')) {
      const idx = line.indexOf('=')
      if (idx > 0) env[line.slice(0, idx).trim()] = line.slice(idx + 1).trim()
    }
    setBusy(true)
    try {
      await onSave({
        ...form,
        args: argsText.trim() ? argsText.trim().split(/\s+/) : [],
        env,
        command: form.transport === 'stdio' ? form.command : undefined,
        url: form.transport !== 'stdio' ? form.url : undefined,
      })
      onClose()
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal open onOpenChange={(o) => !o && onClose()} title={form.id ? '編輯 MCP Server' : '新增 MCP Server'} size="md">
      <div className="space-y-3">
        <div>
          <label className="text-xs font-medium text-ink-secondary">名稱</label>
          <input
            type="text"
            value={form.name}
            onChange={(e) => setForm({ ...form, name: e.target.value })}
            placeholder="filesystem"
            className="mt-1 w-full px-3 py-2 rounded-xl border border-border bg-surface text-sm font-mono focus:outline-none focus:ring-2 focus:ring-brand/40"
          />
        </div>

        <div>
          <label className="text-xs font-medium text-ink-secondary">傳輸方式</label>
          <div className="mt-1 flex gap-1">
            {(['stdio', 'sse', 'http'] as const).map((t) => (
              <button
                key={t}
                type="button"
                onClick={() => setForm({ ...form, transport: t })}
                className={cn(
                  'px-3 py-1.5 rounded-xl text-xs font-medium transition-colors',
                  form.transport === t
                    ? 'bg-brand text-white'
                    : 'text-ink-secondary border border-border hover:bg-surface-alt',
                )}
              >
                {t}
              </button>
            ))}
          </div>
        </div>

        {form.transport === 'stdio' ? (
          <>
            <div>
              <label className="text-xs font-medium text-ink-secondary">指令</label>
              <input
                type="text"
                value={form.command ?? ''}
                onChange={(e) => setForm({ ...form, command: e.target.value })}
                placeholder="npx"
                className="mt-1 w-full px-3 py-2 rounded-xl border border-border bg-surface text-sm font-mono focus:outline-none focus:ring-2 focus:ring-brand/40"
              />
            </div>
            <div>
              <label className="text-xs font-medium text-ink-secondary">參數（空白分隔）</label>
              <input
                type="text"
                value={argsText}
                onChange={(e) => setArgsText(e.target.value)}
                placeholder="-y @modelcontextprotocol/server-filesystem C:/work"
                className="mt-1 w-full px-3 py-2 rounded-xl border border-border bg-surface text-sm font-mono focus:outline-none focus:ring-2 focus:ring-brand/40"
              />
            </div>
          </>
        ) : (
          <div>
            <label className="text-xs font-medium text-ink-secondary">URL</label>
            <input
              type="text"
              value={form.url ?? ''}
              onChange={(e) => setForm({ ...form, url: e.target.value })}
              placeholder="http://localhost:3001/sse"
              className="mt-1 w-full px-3 py-2 rounded-xl border border-border bg-surface text-sm font-mono focus:outline-none focus:ring-2 focus:ring-brand/40"
            />
          </div>
        )}

        <div>
          <label className="text-xs font-medium text-ink-secondary">環境變數（每行 KEY=VALUE）</label>
          <textarea
            value={envText}
            onChange={(e) => setEnvText(e.target.value)}
            rows={3}
            placeholder="API_KEY=xxx"
            className="mt-1 w-full px-3 py-2 rounded-xl border border-border bg-surface text-sm font-mono focus:outline-none focus:ring-2 focus:ring-brand/40"
          />
        </div>

        <div className="flex justify-end gap-2">
          <Button variant="ghost" size="sm" onClick={onClose}>
            取消
          </Button>
          <Button variant="primary" size="sm" onClick={handleSave} isLoading={busy}>
            儲存
          </Button>
        </div>
      </div>
    </Modal>
  )
}

/** MCP Registry tab: manage servers + link to agent configs. */
export function McpTab() {
  const { servers, loading, error, fetchServers, upsertServer, deleteServer, setAgentLink, clearError } =
    useMcpStore()
  const { agents, fetchAgents } = useSkillsStore()

  const [editing, setEditing] = useState<UpsertMcpServerParams | null>(null)
  const [runtime,setRuntime]=useState<McpRuntimeStatus>({servers:[],tools:[]})
  const [refreshing,setRefreshing]=useState(false)

  const refreshRuntime=async()=>{setRefreshing(true);try{setRuntime(await refreshMcpRuntime())}finally{setRefreshing(false)}}

  useEffect(() => {
    fetchServers()
    fetchAgents()
    void refreshRuntime()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // Agents that have an MCP config path (wingman itself + IDEs that support MCP)
  const mcpAgents = agents

  const toForm = (s: McpServer): UpsertMcpServerParams => ({
    id: s.id,
    name: s.name,
    transport: s.transport as 'stdio' | 'sse' | 'http',
    command: s.command ?? '',
    args: s.args,
    url: s.url ?? '',
    env: s.env,
    enabled: s.enabled,
  })
  const saveAndRefresh=async(value:UpsertMcpServerParams)=>{await upsertServer(value);await refreshRuntime()}
  const deleteAndRefresh=async(id:number)=>{await deleteServer(id);await refreshRuntime()}

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <p className="text-sm text-ink-secondary">
          管理 MCP Server 定義，並同步到各工具的設定檔與 Wingman Agent。
        </p>
        <div className="flex gap-1"><Button variant="ghost" size="icon" title="重新連線並探索 Tools" onClick={()=>void refreshRuntime()} disabled={refreshing}><RefreshCw className={cn('h-4 w-4',refreshing&&'animate-spin')}/></Button><Button variant="primary" size="sm" leftIcon={<Plus className="w-4 h-4" />} onClick={() => setEditing(EMPTY_FORM)}>
          新增 Server
        </Button></div>
      </div>

      {error && (
        <div className="flex items-center justify-between rounded-xl border border-red-200 bg-red-50 px-4 py-2.5">
          <p className="text-sm text-red-700">{error}</p>
          <button type="button" onClick={clearError} className="text-red-400 hover:text-red-600">
            <X className="w-4 h-4" />
          </button>
        </div>
      )}

      {loading ? (
        <div className="space-y-2">
          {Array.from({ length: 3 }).map((_, i) => (
            <div key={i} className="h-20 rounded-2xl bg-surface-alt animate-pulse border border-border" />
          ))}
        </div>
      ) : servers.length === 0 ? (
        <div className="text-center py-16 text-ink-subtle text-sm">
          尚無 MCP Server — 新增一個定義後可同步到所有支援的工具
        </div>
      ) : (
        <div className="space-y-2">
          {servers.map((server) => (
            <div key={server.id} className="rounded-2xl border border-border bg-surface p-4 space-y-3">
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-2 min-w-0">
                  <Plug className={cn('w-4 h-4', server.enabled ? 'text-brand' : 'text-ink-subtle')} />
                  <p className="text-sm font-semibold text-ink font-mono truncate">{server.name}</p>
                  <span className="text-xs px-1.5 py-0.5 rounded bg-surface-alt text-ink-subtle">{server.transport}</span>
                  {(()=>{const health=runtime.servers.find(item=>item.serverId===server.id);return health?(health.healthy?<span title={`${health.toolCount} tools`}><CircleCheck className="h-4 w-4 text-success"/></span>:<span title={health.error??'連線失敗'}><CircleX className="h-4 w-4 text-danger"/></span>):null})()}
                </div>
                <div className="flex items-center gap-1">
                  <button
                    type="button"
                    title="編輯"
                    onClick={() => setEditing(toForm(server))}
                    className="p-1.5 rounded-lg text-ink-subtle hover:bg-surface-alt hover:text-ink transition-colors"
                  >
                    <Pencil className="w-3.5 h-3.5" />
                  </button>
                  <button
                    type="button"
                    title="刪除"
                    onClick={() => void deleteAndRefresh(server.id)}
                    className="p-1.5 rounded-lg text-ink-subtle hover:bg-red-50 hover:text-red-500 transition-colors"
                  >
                    <Trash2 className="w-3.5 h-3.5" />
                  </button>
                </div>
              </div>

              <p className="text-xs text-ink-subtle font-mono truncate">
                {server.transport === 'stdio'
                  ? `${server.command ?? ''} ${server.args.join(' ')}`
                  : server.url}
              </p>

              {/* Agent link chips */}
              <div className="flex flex-wrap gap-1.5">
                {mcpAgents.map((agent) => {
                  const linked = server.linkedAgents.includes(agent.id)
                  return (
                    <button
                      key={agent.id}
                      type="button"
                      onClick={() => setAgentLink(server.id, agent.id, !linked)}
                      className={
                        linked
                          ? 'px-2.5 py-1 rounded-lg text-xs font-medium bg-brand/10 text-brand border border-brand/30'
                          : 'px-2.5 py-1 rounded-lg text-xs text-ink-subtle border border-border hover:bg-surface-alt transition-colors'
                      }
                    >
                      {agent.displayName}
                    </button>
                  )
                })}
              </div>
            </div>
          ))}
        </div>
      )}

      {editing && (
        <ServerFormModal
          initial={editing}
          onSave={saveAndRefresh}
          onClose={() => setEditing(null)}
        />
      )}
    </div>
  )
}
