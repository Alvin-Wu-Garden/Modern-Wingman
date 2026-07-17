import { useState } from 'react'
import {
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  CircleDot,
  ServerCog,
  Terminal,
  Wrench,
} from 'lucide-react'
import type { TimelineEvent } from '@/services/agent-api/client'

const asRecord = (value: unknown): Record<string, unknown> | null =>
  value !== null && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown>
    : null

function eventKind(event: TimelineEvent) {
  const name = (event.name ?? '').toLowerCase()
  if (name.includes('skill')) return 'skill'
  if (name.includes('mcp')) return 'mcp'
  if (name.includes('command') || name.includes('build') || name.includes('test')) return 'command'
  return event.type
}

function EventIcon({ kind }: { kind: string }) {
  if (kind === 'command') return <Terminal className="h-3.5 w-3.5 text-brand" />
  if (kind === 'skill') return <Wrench className="h-3.5 w-3.5 text-amber-600" />
  if (kind === 'mcp') return <ServerCog className="h-3.5 w-3.5 text-cyan-600" />
  if (kind === 'phase') return <CircleDot className="h-3.5 w-3.5 text-brand" />
  return <CheckCircle2 className="h-3.5 w-3.5 text-success" />
}

function StructuredDetails({ event, kind }: { event: TimelineEvent; kind: string }) {
  const data = asRecord(event.data)
  if (kind === 'command' && data) {
    const executable = data.executable ?? data.fileName
    const args = data.arguments
    const cwd = data.workingDirectory ?? data.cwd
    return <dl className="grid gap-2 border-t border-border px-3 py-2 text-xs sm:grid-cols-2">
      <div><dt className="text-ink-subtle">Executable</dt><dd className="break-all font-mono text-ink-secondary">{String(executable ?? event.name ?? 'command')}</dd></div>
      <div><dt className="text-ink-subtle">Working directory</dt><dd className="break-all font-mono text-ink-secondary">{String(cwd ?? 'current workspace')}</dd></div>
      <div className="sm:col-span-2"><dt className="text-ink-subtle">Arguments</dt><dd className="break-all font-mono text-ink-secondary">{Array.isArray(args) ? args.join(' ') : String(args ?? '')}</dd></div>
      {(data.durationMs !== undefined || data.exitCode !== undefined) && <div className="sm:col-span-2 flex gap-4 text-ink-secondary"><span>Duration: {String(data.durationMs ?? '—')} ms</span><span>Exit: {String(data.exitCode ?? '—')}</span></div>}
    </dl>
  }
  if ((kind === 'skill' || kind === 'mcp') && data) {
    return <dl className="grid gap-2 border-t border-border px-3 py-2 text-xs sm:grid-cols-2">
      {Object.entries(data).map(([key, value]) => <div key={key}><dt className="text-ink-subtle">{key}</dt><dd className="break-all font-mono text-ink-secondary">{typeof value === 'string' ? value : JSON.stringify(value)}</dd></div>)}
    </dl>
  }
  return <pre className="max-h-64 overflow-auto border-t border-border px-3 py-2 text-xs text-ink-secondary">{typeof event.data === 'string' ? event.data : JSON.stringify(event.data, null, 2)}</pre>
}

export function RunTimeline({ events,onApprovePlan }: { events: TimelineEvent[];onApprovePlan?:()=>Promise<void> }) {
  const [open, setOpen] = useState<Record<number, boolean>>({})
  const [collapsed,setCollapsed]=useState(true)
  if (events.length === 0) return null
  const latest=events[events.length-1]
  return <section className="shrink-0 border-t border-border bg-surface-alt px-6 py-2">
    <button type="button" onClick={()=>setCollapsed(value=>!value)} className="mx-auto flex w-full max-w-3xl items-center gap-2 text-left text-xs text-ink-secondary">
      {collapsed?<ChevronRight className="h-3.5 w-3.5"/>:<ChevronDown className="h-3.5 w-3.5"/>}
      <span className="font-semibold">Agent Timeline</span>
      <span className="truncate text-ink-subtle">{latest?.name??latest?.type}</span>
      <span className="ml-auto shrink-0">{events.length} 筆</span>
    </button>
    {!collapsed&&<div className="mx-auto mt-2 max-h-52 max-w-3xl space-y-1 overflow-y-auto pr-1">
      {events.map((event, index) => {
        const kind = eventKind(event)
        const expanded = open[index] ?? kind === 'plan'
        const parsedTime=new Date(event.timestamp)
        const displayTime=Number.isNaN(parsedTime.getTime())?'—':parsedTime.toLocaleTimeString()
        return <div key={`${event.callId ?? event.type}-${index}`} className="border border-border bg-surface">
          <button type="button" className="flex w-full items-center gap-2 px-3 py-2 text-left text-xs" onClick={() => setOpen(current => ({ ...current, [index]: !expanded }))}>
            {expanded ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
            <EventIcon kind={kind} />
            <span className="font-medium text-ink">{event.name ?? (event.type === 'tool_result' ? '工具執行結果' : event.type)}</span>
            <span className="ml-auto text-ink-subtle">{displayTime}</span>
          </button>
          {expanded && <StructuredDetails event={event} kind={kind} />}
          {expanded&&kind==='plan'&&onApprovePlan&&<div className="flex justify-end border-t border-border px-3 py-2"><button type="button" onClick={()=>void onApprovePlan()} className="border border-brand px-3 py-1.5 text-xs font-medium text-brand hover:bg-brand hover:text-white">核准並進入 Auto</button></div>}
        </div>
      })}
    </div>}
  </section>
}
