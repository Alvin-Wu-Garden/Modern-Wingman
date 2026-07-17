import { Fragment,useCallback,useEffect,useMemo,useState } from 'react'
import { ChevronDown,ChevronRight,Download,Loader2,Search } from 'lucide-react'
import { Button } from '@/components/ui/button'
import {
  exportAuditCsv,
  exportToolCallAuditCsv,
  getAuditFacets,
  getToolCallAuditFacets,
  listAuditEvents,
  listToolCallAudit,
  type AuditEvent,
  type AuditFacets,
  type AuditFilters,
  type ToolCallAudit,
  type ToolCallAuditFacets,
  type ToolCallFilters,
} from '@/services/agent-api/audit'

const toLocalDateTime=(date:Date)=>{
  const local=new Date(date.getTime()-date.getTimezoneOffset()*60_000)
  return local.toISOString().slice(0,16)
}
const initialRange=()=>{const now=new Date();return{from:toLocalDateTime(new Date(now.getTime()-3*60*60*1000)),to:toLocalDateTime(now)}}
const selectClass='min-w-0 rounded-lg border border-border bg-surface px-2.5 py-2 text-xs'
const safeDate=(value:string)=>{const date=new Date(value);return Number.isNaN(date.getTime())?'—':date.toLocaleString('zh-TW')}
const downloadBlob=(blob:Blob,name:string)=>{const url=URL.createObjectURL(blob);const anchor=document.createElement('a');anchor.href=url;anchor.download=name;anchor.click();URL.revokeObjectURL(url)}

function DateRange({from,to,onChange}:{from?:string;to?:string;onChange:(values:{from:string;to:string})=>void}){
  return <div className="grid gap-3 sm:grid-cols-2">
    <label className="text-xs font-medium text-ink-secondary">起始時間<input aria-label="起始時間" type="datetime-local" className={`${selectClass} mt-1 w-full`} value={from??''} onChange={event=>onChange({from:event.target.value,to:to??''})}/></label>
    <label className="text-xs font-medium text-ink-secondary">結束時間<input aria-label="結束時間" type="datetime-local" className={`${selectClass} mt-1 w-full`} value={to??''} onChange={event=>onChange({from:from??'',to:event.target.value})}/></label>
  </div>
}

export function AuditSettingsPanel(){
  const range=useMemo(initialRange,[])
  const [view,setView]=useState<'events'|'tools'>('events')
  const [eventDraft,setEventDraft]=useState<AuditFilters>({...range,limit:25,offset:0})
  const [eventQuery,setEventQuery]=useState<AuditFilters>({...range,limit:25,offset:0})
  const [toolDraft,setToolDraft]=useState<ToolCallFilters>({...range,limit:25,offset:0})
  const [toolQuery,setToolQuery]=useState<ToolCallFilters>({...range,limit:25,offset:0})
  const [items,setItems]=useState<AuditEvent[]>([])
  const [toolItems,setToolItems]=useState<ToolCallAudit[]>([])
  const [eventFacets,setEventFacets]=useState<AuditFacets>({eventTypes:[],targetTypes:[],targets:[],results:[],traceIds:[]})
  const [toolFacets,setToolFacets]=useState<ToolCallAuditFacets>({projects:[],runs:[],providers:[],tools:[],statuses:[]})
  const [total,setTotal]=useState(0)
  const [loading,setLoading]=useState(false)
  const [error,setError]=useState<string|null>(null)
  const [open,setOpen]=useState<string|null>(null)

  useEffect(()=>{void Promise.all([getAuditFacets(),getToolCallAuditFacets()]).then(([events,tools])=>{setEventFacets(events);setToolFacets(tools)}).catch(error=>setError(error instanceof Error?error.message:String(error)))},[])
  const loadEvents=useCallback(async()=>{setLoading(true);setError(null);try{const page=await listAuditEvents(eventQuery);setItems(page.items);setTotal(page.total)}catch(error){setError(error instanceof Error?error.message:String(error))}finally{setLoading(false)}},[eventQuery])
  const loadTools=useCallback(async()=>{setLoading(true);setError(null);try{const page=await listToolCallAudit(toolQuery);setToolItems(page.items);setTotal(page.total)}catch(error){setError(error instanceof Error?error.message:String(error))}finally{setLoading(false)}},[toolQuery])
  useEffect(()=>{if(view==='events')void loadEvents();else void loadTools()},[view,loadEvents,loadTools])
  const targets=eventFacets.targets.filter(option=>!eventDraft.targetType||option.group===eventDraft.targetType)
  const eventPage=(offset:number)=>{const next={...eventQuery,offset};setEventDraft(next);setEventQuery(next)}
  const toolPage=(offset:number)=>{const next={...toolQuery,offset};setToolDraft(next);setToolQuery(next)}

  return <div className="space-y-5">
    <div className="flex gap-1 border-b border-border"><Button size="sm" variant={view==='events'?'primary':'ghost'} onClick={()=>setView('events')}>安全事件</Button><Button size="sm" variant={view==='tools'?'primary':'ghost'} onClick={()=>setView('tools')}>工具呼叫</Button></div>
    {view==='events'&&<>
      <DateRange from={eventDraft.from} to={eventDraft.to} onChange={values=>setEventDraft({...eventDraft,...values,offset:0})}/>
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <label className="text-xs font-medium text-ink-secondary">事件類型<select className={`${selectClass} mt-1 w-full`} value={eventDraft.eventType??''} onChange={event=>setEventDraft({...eventDraft,eventType:event.target.value,offset:0})}><option value="">全部事件</option>{eventFacets.eventTypes.map(value=><option key={value} value={value}>{value}</option>)}</select></label>
        <label className="text-xs font-medium text-ink-secondary">目標類型<select className={`${selectClass} mt-1 w-full`} value={eventDraft.targetType??''} onChange={event=>setEventDraft({...eventDraft,targetType:event.target.value,targetId:'',offset:0})}><option value="">全部資源類型</option>{eventFacets.targetTypes.map(value=><option key={value} value={value}>{value}</option>)}</select><span className="mt-1 block font-normal text-ink-subtle">事件影響的資源，例如專案、Agent Run、供應商或版控設定。</span></label>
        <label className="text-xs font-medium text-ink-secondary">目標<select className={`${selectClass} mt-1 w-full`} value={eventDraft.targetId??''} onChange={event=>setEventDraft({...eventDraft,targetId:event.target.value,offset:0})}><option value="">全部目標</option>{targets.map(option=><option key={`${option.group}-${option.value}`} value={option.value}>{option.label}</option>)}</select></label>
        <label className="text-xs font-medium text-ink-secondary">結果<select className={`${selectClass} mt-1 w-full`} value={eventDraft.result??''} onChange={event=>setEventDraft({...eventDraft,result:event.target.value,offset:0})}><option value="">全部結果</option>{eventFacets.results.map(value=><option key={value} value={value}>{value}</option>)}</select></label>
        <details className="sm:col-span-2"><summary className="cursor-pointer py-2 text-xs font-medium text-ink-secondary">進階篩選：Trace ID</summary><select aria-label="Trace ID" className={`${selectClass} w-full`} value={eventDraft.traceId??''} onChange={event=>setEventDraft({...eventDraft,traceId:event.target.value,offset:0})}><option value="">全部 Trace</option>{eventFacets.traceIds.map(value=><option key={value} value={value}>{value}</option>)}</select><p className="mt-1 text-xs text-ink-subtle">Trace ID 用來串連同一次 Agent 任務產生的多筆事件。</p></details>
      </div>
      <div className="flex justify-end gap-2"><Button variant="outline" size="sm" onClick={async()=>downloadBlob(await exportAuditCsv(eventQuery),`wingman-security-events-${new Date().toISOString().slice(0,10)}.csv`)}><Download className="mr-1 h-4 w-4"/>下載 CSV</Button><Button size="sm" onClick={()=>setEventQuery({...eventDraft,offset:0})}><Search className="mr-1 h-4 w-4"/>查詢</Button></div>
      <div className="overflow-x-auto border border-border"><table className="w-full text-left text-xs"><thead className="bg-surface-muted text-ink-secondary"><tr><th className="w-8 p-2"></th><th className="p-2">時間</th><th className="p-2">事件</th><th className="p-2">目標</th><th className="p-2">動作</th><th className="p-2">結果</th></tr></thead><tbody>{items.map(item=><Fragment key={item.id}><tr className="border-t border-border"><td className="p-2"><button title="詳細資料" onClick={()=>setOpen(open===item.id?null:item.id)}>{open===item.id?<ChevronDown className="h-3.5 w-3.5"/>:<ChevronRight className="h-3.5 w-3.5"/>}</button></td><td className="whitespace-nowrap p-2">{safeDate(item.createdAt)}</td><td className="p-2">{item.eventType}</td><td className="p-2">{item.targetType}{item.targetId?` · ${item.targetId}`:''}</td><td className="p-2">{item.action}</td><td className="p-2">{item.result}</td></tr>{open===item.id&&<tr className="border-t border-border bg-surface-muted"><td></td><td colSpan={5} className="p-3"><p>Trace: {item.traceId??'—'}</p><pre className="mt-2 max-h-48 overflow-auto whitespace-pre-wrap">{item.detailsJson??'無詳細資料'}</pre></td></tr>}</Fragment>)}</tbody></table>{!loading&&items.length===0&&<p className="p-4 text-center text-sm text-ink-subtle">沒有符合條件的稽核事件。</p>}</div>
      <Pagination total={total} offset={eventQuery.offset??0} limit={eventQuery.limit??25} onPage={eventPage}/>
    </>}
    {view==='tools'&&<>
      <DateRange from={toolDraft.from} to={toolDraft.to} onChange={values=>setToolDraft({...toolDraft,...values,offset:0})}/>
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <Select label="專案" value={toolDraft.projectId} options={toolFacets.projects} onChange={value=>setToolDraft({...toolDraft,projectId:value,offset:0})}/>
        <Select label="Run" value={toolDraft.runId} options={toolFacets.runs} onChange={value=>setToolDraft({...toolDraft,runId:value,offset:0})}/>
        <StringSelect label="Provider" value={toolDraft.provider} options={toolFacets.providers} onChange={value=>setToolDraft({...toolDraft,provider:value,offset:0})}/>
        <StringSelect label="Tool" value={toolDraft.tool} options={toolFacets.tools} onChange={value=>setToolDraft({...toolDraft,tool:value,offset:0})}/>
        <StringSelect label="狀態" value={toolDraft.status} options={toolFacets.statuses} onChange={value=>setToolDraft({...toolDraft,status:value,offset:0})}/>
      </div>
      <div className="flex justify-end gap-2"><Button variant="outline" size="sm" onClick={async()=>downloadBlob(await exportToolCallAuditCsv(toolQuery),`wingman-tool-calls-${new Date().toISOString().slice(0,10)}.csv`)}><Download className="mr-1 h-4 w-4"/>下載 CSV</Button><Button size="sm" onClick={()=>setToolQuery({...toolDraft,offset:0})}><Search className="mr-1 h-4 w-4"/>查詢</Button></div>
      <div className="overflow-x-auto border border-border"><table className="w-full text-left text-xs"><thead className="bg-surface-muted text-ink-secondary"><tr><th className="p-2">時間</th><th className="p-2">Provider</th><th className="p-2">Tool</th><th className="p-2">Project / Run</th><th className="p-2">狀態</th><th className="p-2">耗時</th></tr></thead><tbody>{toolItems.map(item=><tr key={item.id} className="border-t border-border"><td className="whitespace-nowrap p-2">{safeDate(item.startedAt)}</td><td className="p-2">{item.provider}</td><td className="p-2">{item.toolName}</td><td className="p-2">{item.projectId??'—'} / {item.runId??'—'}</td><td className="p-2">{item.status}</td><td className="p-2">{item.durationMs??'—'} ms</td></tr>)}</tbody></table>{!loading&&toolItems.length===0&&<p className="p-4 text-center text-sm text-ink-subtle">沒有符合條件的工具呼叫。</p>}</div>
      <Pagination total={total} offset={toolQuery.offset??0} limit={toolQuery.limit??25} onPage={toolPage}/>
    </>}
    {loading&&<p className="flex items-center gap-2 text-xs text-ink-subtle"><Loader2 className="h-4 w-4 animate-spin"/>查詢中…</p>}
    {error&&<p className="text-xs text-danger">{error}</p>}
  </div>
}

function Pagination({total,offset,limit,onPage}:{total:number;offset:number;limit:number;onPage:(offset:number)=>void}){return <div className="flex items-center justify-between text-xs text-ink-subtle"><span>共 {total} 筆</span><div className="flex gap-2"><Button variant="ghost" size="sm" disabled={offset===0} onClick={()=>onPage(Math.max(0,offset-limit))}>上一頁</Button><Button variant="ghost" size="sm" disabled={offset+limit>=total} onClick={()=>onPage(offset+limit)}>下一頁</Button></div></div>}
function Select({label,value,options,onChange}:{label:string;value?:string;options:{value:string;label:string}[];onChange:(value:string)=>void}){return <label className="text-xs font-medium text-ink-secondary">{label}<select className={`${selectClass} mt-1 w-full`} value={value??''} onChange={event=>onChange(event.target.value)}><option value="">全部{label}</option>{options.map(option=><option key={option.value} value={option.value}>{option.label}</option>)}</select></label>}
function StringSelect({label,value,options,onChange}:{label:string;value?:string;options:string[];onChange:(value:string)=>void}){return <label className="text-xs font-medium text-ink-secondary">{label}<select className={`${selectClass} mt-1 w-full`} value={value??''} onChange={event=>onChange(event.target.value)}><option value="">全部{label}</option>{options.map(option=><option key={option} value={option}>{option}</option>)}</select></label>}
