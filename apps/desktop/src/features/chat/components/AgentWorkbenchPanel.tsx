import { useEffect, useMemo, useState } from 'react'
import { Braces, Copy, Files, PanelRightClose, PanelRightOpen, RotateCcw, Search, ShieldCheck, Terminal } from 'lucide-react'
import type { PendingApproval, RunChangeSet, TimelineEvent } from '@/services/agent-api/client'
import type { AgentMode } from '@modern-wingman/contracts'
import { getWorkspaceActionPreview, runWorkspaceAction, type WorkspaceActionPreview } from '@/services/agent-api/client'

type Tab = 'context' | 'changes' | 'output' | 'approvals'

type ChangedFile = RunChangeSet['files'][number]

function ChangedFileCard({file,diffMode,onAcceptFiles,onRestoreFiles,onUpdateHunks}:{file:ChangedFile;diffMode:'unified'|'side-by-side';onAcceptFiles:(paths:string[])=>Promise<void>;onRestoreFiles:(paths:string[])=>Promise<void>;onUpdateHunks:(path:string,hunkIndexes:number[],action:'accept'|'restore')=>Promise<void>}){
  const lines=file.unifiedDiff?.split('\n')??[]
  const before=lines.filter(line=>line.startsWith('-')&&!line.startsWith('---')).map(line=>line.slice(1))
  const after=lines.filter(line=>line.startsWith('+')&&!line.startsWith('+++')).map(line=>line.slice(1))
  return <details className="border border-border"><summary className="cursor-pointer px-2 py-1.5 text-xs">{file.originalPath&&<><code>{file.originalPath}</code><span className="mx-1 text-ink-subtle">→</span></>}<code>{file.relativePath}</code></summary><div className="flex justify-end gap-2 border-t border-border px-2 py-1"><button type="button" onClick={()=>void onAcceptFiles([file.relativePath])} className="text-[11px] text-success">保留檔案</button><button type="button" onClick={()=>void onRestoreFiles([file.relativePath])} className="text-[11px] text-danger">復原檔案</button></div>{file.hunks&&file.hunks.length>0?<div className="space-y-2 border-t border-border p-2">{file.hunks.map(hunk=><div key={hunk.index} className="border border-border"><div className="flex items-center justify-between bg-surface-alt px-2 py-1 text-[11px]"><code>@@ -{hunk.oldStart},{hunk.oldCount} +{hunk.newStart},{hunk.newCount}</code><span className="flex gap-2"><button type="button" className="text-success" onClick={()=>void onUpdateHunks(file.relativePath,[hunk.index],'accept')}>接受 hunk</button><button type="button" className="text-danger" onClick={()=>void onUpdateHunks(file.relativePath,[hunk.index],'restore')}>排除 hunk</button></span></div><pre className="max-h-48 overflow-auto p-2 text-[11px] text-ink-secondary">{hunk.lines.join('\n')}</pre></div>)}</div>:file.unifiedDiff&&(diffMode==='unified'?<pre className="max-h-64 overflow-auto border-t border-border p-2 text-[11px] text-ink-secondary">{file.unifiedDiff}</pre>:<div className="grid max-h-64 grid-cols-2 overflow-auto border-t border-border text-[11px]"><pre className="border-r border-border bg-red-500/5 p-2 text-ink-secondary">{before.join('\n')}</pre><pre className="bg-green-500/5 p-2 text-ink-secondary">{after.join('\n')}</pre></div>)}</details>
}

function ChangesPanel({changeSet,diffMode,setDiffMode,onRestore,onAcceptFiles,onRestoreFiles,onUpdateHunks}:{changeSet:RunChangeSet|null;diffMode:'unified'|'side-by-side';setDiffMode:(mode:'unified'|'side-by-side')=>void;onRestore:()=>Promise<void>;onAcceptFiles:(paths:string[])=>Promise<void>;onRestoreFiles:(paths:string[])=>Promise<void>;onUpdateHunks:(path:string,hunkIndexes:number[],action:'accept'|'restore')=>Promise<void>}){
  const order=['Added','Modified','Deleted','Renamed'] as const
  const labels:Record<(typeof order)[number],string>={Added:'新增',Modified:'修改',Deleted:'刪除',Renamed:'重新命名'}
  if(!changeSet?.files.length)return <p className="text-xs text-ink-subtle">尚無檔案變更</p>
  return <div className="space-y-3">{changeSet.validation&&<div className={`border px-2 py-1.5 text-xs ${changeSet.validation.status==='succeeded'?'border-green-500/40 text-success':'border-red-500/40 text-danger'}`}>驗證 {changeSet.validation.status} · attempt {changeSet.validation.attempt}{changeSet.validation.errorSanitized?` · ${changeSet.validation.errorSanitized}`:''}</div>}<div className="flex items-center justify-between"><div className="flex border border-border"><button type="button" onClick={()=>setDiffMode('unified')} className={`px-2 py-1 text-[11px] ${diffMode==='unified'?'bg-brand text-white':'text-ink-secondary'}`}>Unified</button><button type="button" onClick={()=>setDiffMode('side-by-side')} className={`px-2 py-1 text-[11px] ${diffMode==='side-by-side'?'bg-brand text-white':'text-ink-secondary'}`}>Side by side</button></div><button type="button" onClick={()=>void onRestore()} className="flex items-center gap-1 text-xs text-ink-secondary hover:text-danger"><RotateCcw className="h-3.5 w-3.5"/>復原全部</button></div>{order.map(kind=>{const files=changeSet.files.filter(file=>file.kind===kind);if(!files.length)return null;return <section key={kind} className="space-y-1"><div className="flex items-center justify-between text-[11px] font-medium text-ink-subtle"><span>{labels[kind]}</span><span>{files.length}</span></div>{files.map(file=><ChangedFileCard key={file.relativePath} file={file} diffMode={diffMode} onAcceptFiles={onAcceptFiles} onRestoreFiles={onRestoreFiles} onUpdateHunks={onUpdateHunks}/>)}</section>})}</div>
}

export function AgentWorkbenchPanel({
  runId,
  mode,
  providerId,
  modelId,
  changeSet,
  timeline,
  approvals,
  onRestore,
  onAcceptFiles,
  onRestoreFiles,
  onUpdateHunks,
}: {
  runId: string | null
  mode: AgentMode
  providerId: string | null
  modelId: string | null
  changeSet: RunChangeSet | null
  timeline: TimelineEvent[]
  approvals: PendingApproval[]
  onRestore: () => Promise<void>
  onAcceptFiles: (paths:string[]) => Promise<void>
  onRestoreFiles: (paths:string[]) => Promise<void>
  onUpdateHunks: (path:string,hunkIndexes:number[],action:'accept'|'restore') => Promise<void>
}) {
  const [collapsed, setCollapsed] = useState(false)
  const [tab, setTab] = useState<Tab>('context')
  const [query, setQuery] = useState('')
  const [diffMode,setDiffMode]=useState<'unified'|'side-by-side'>('unified')
  const [commitMessage,setCommitMessage]=useState('')
  const [actionStatus,setActionStatus]=useState<string|null>(null)
  const [actionPreview,setActionPreview]=useState<WorkspaceActionPreview|null>(null)
  const outputs = useMemo(() => timeline.filter(event => event.type === 'tool_result' && (!query || JSON.stringify(event.data).toLowerCase().includes(query.toLowerCase()))), [query, timeline])
  useEffect(()=>{if(!runId){setActionPreview(null);return}void getWorkspaceActionPreview(runId).then(setActionPreview).catch(()=>setActionPreview(null))},[runId])

  if (collapsed) return <aside className="hidden w-10 shrink-0 border-l border-border bg-surface md:flex md:flex-col md:items-center md:py-2"><button type="button" title="展開工作台" onClick={() => setCollapsed(false)} className="p-2 text-ink-secondary hover:text-brand"><PanelRightOpen className="h-4 w-4" /></button></aside>

  const tabs: { id: Tab; title: string; icon: typeof Braces; count?: number }[] = [
    { id: 'context', title: 'Context', icon: Braces },
    { id: 'changes', title: 'Changed Files', icon: Files, count: changeSet?.files.length },
    { id: 'output', title: 'Output', icon: Terminal, count: outputs.length },
    { id: 'approvals', title: 'Approvals', icon: ShieldCheck, count: approvals.length },
  ]
  const executeAction=async(action:'retain'|'discard'|'apply'|'commit'|'push'|'svn_commit')=>{if(!runId)return;setActionStatus('執行中…');try{let result=await runWorkspaceAction(runId,action,commitMessage);if(result.requiresProtectedConfirmation&&window.confirm(result.error??'目標受保護，是否繼續？'))result=await runWorkspaceAction(runId,action,commitMessage,true);setActionStatus(result.success?(result.output??'完成'):(result.error??'失敗'))}catch(error){setActionStatus(error instanceof Error?error.message:String(error))}}

  return <aside className="hidden w-80 shrink-0 border-l border-border bg-surface md:flex md:flex-col">
    <div className="flex items-center border-b border-border px-2 py-1.5">
      <div className="flex min-w-0 flex-1 overflow-x-auto">{tabs.map(item => <button key={item.id} type="button" title={item.title} onClick={() => setTab(item.id)} className={`relative p-2 ${tab === item.id ? 'text-brand' : 'text-ink-subtle hover:text-ink'}`}><item.icon className="h-4 w-4" />{item.count ? <span className="absolute right-0 top-0 text-[9px]">{item.count}</span> : null}</button>)}</div>
      <button type="button" title="收合工作台" onClick={() => setCollapsed(true)} className="p-2 text-ink-subtle hover:text-ink"><PanelRightClose className="h-4 w-4" /></button>
    </div>
    <div className="min-h-0 flex-1 overflow-y-auto p-3">
      {tab === 'context' && <div className="space-y-4"><dl className="space-y-3 text-xs"><div><dt className="text-ink-subtle">Run</dt><dd className="mt-0.5 break-all font-mono text-ink-secondary">{runId ?? '—'}</dd></div><div><dt className="text-ink-subtle">Agent mode</dt><dd className="mt-0.5 text-ink-secondary">{mode}</dd></div><div><dt className="text-ink-subtle">Provider / Model</dt><dd className="mt-0.5 break-all text-ink-secondary">{providerId ?? '—'} / {modelId ?? 'default'}</dd></div>{changeSet && <div><dt className="text-ink-subtle">Workspace</dt><dd className="mt-0.5 break-all font-mono text-ink-secondary">{changeSet.workspacePath}</dd></div>}{actionPreview&&<><div><dt className="text-ink-subtle">VCS target</dt><dd className="mt-0.5 break-all font-mono text-ink-secondary">{actionPreview.vcsType??'local'} · {actionPreview.remote??'—'} · {actionPreview.target??'—'}</dd></div><div><dt className="text-ink-subtle">Protected rule</dt><dd className={actionPreview.protected?'mt-0.5 text-danger':'mt-0.5 text-success'}>{actionPreview.protected?'受保護，Push/Commit 需要再次確認':'非受保護目標'}</dd></div></>}</dl>{runId&&<div className="space-y-2 border-t border-border pt-3"><input value={commitMessage} onChange={event=>setCommitMessage(event.target.value)} placeholder="Commit message" className="w-full border border-border bg-surface-alt px-2 py-1.5 text-xs"/><div className="grid grid-cols-3 gap-1">{(['apply','retain','discard','commit','push','svn_commit'] as const).map(action=><button key={action} type="button" onClick={()=>void executeAction(action)} className="border border-border px-1.5 py-1 text-[11px] text-ink-secondary hover:border-brand hover:text-brand">{action}</button>)}</div>{actionStatus&&<p className="max-h-24 overflow-auto whitespace-pre-wrap text-[11px] text-ink-subtle">{actionStatus}</p>}</div>}</div>}
      {tab === 'changes' && <ChangesPanel changeSet={changeSet} diffMode={diffMode} setDiffMode={setDiffMode} onRestore={onRestore} onAcceptFiles={onAcceptFiles} onRestoreFiles={onRestoreFiles} onUpdateHunks={onUpdateHunks}/>} 
      {tab === 'output' && <div className="space-y-2"><label className="relative block"><Search className="absolute left-2 top-2 h-3.5 w-3.5 text-ink-subtle"/><input value={query} onChange={event => setQuery(event.target.value)} placeholder="搜尋輸出" className="w-full border border-border bg-surface-alt py-1.5 pl-7 pr-2 text-xs"/></label>{outputs.map((event,index) => { const text=typeof event.data==='string'?event.data:JSON.stringify(event.data,null,2);return <div key={`${event.name}-${index}`} className="border border-border"><div className="flex items-center px-2 py-1.5 text-xs font-medium"><span className="min-w-0 flex-1 truncate">{event.name}</span><button type="button" title="複製輸出" onClick={() => void navigator.clipboard.writeText(text)}><Copy className="h-3.5 w-3.5"/></button></div><pre className="max-h-48 overflow-auto border-t border-border p-2 text-[11px] text-ink-secondary">{text}</pre></div>})}</div>}
      {tab === 'approvals' && <div className="space-y-2">{approvals.map(approval => <div key={approval.id} className="border border-amber-400/40 p-2 text-xs"><div className="flex items-center justify-between gap-2"><p className="font-medium text-ink">{approval.operation}</p><span className="text-[10px] uppercase text-amber-600">{approval.riskLevel}</span></div><p className="mt-1 text-ink-secondary">{approval.summary}</p>{approval.target && <div className="mt-2"><p className="text-[10px] text-ink-subtle">命令／目標</p><code className="block break-all text-ink-secondary">{approval.target}</code></div>}{approval.workingDirectory&&<div className="mt-2"><p className="text-[10px] text-ink-subtle">工作目錄</p><code className="block break-all text-ink-secondary">{approval.workingDirectory}</code></div>}<p className="mt-2 text-[10px] text-ink-subtle">可能影響：{approval.capabilities}</p></div>)}{approvals.length === 0 && <p className="text-xs text-ink-subtle">沒有待核准操作</p>}</div>}
    </div>
  </aside>
}
