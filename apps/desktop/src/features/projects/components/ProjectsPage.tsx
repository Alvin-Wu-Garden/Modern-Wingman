import { useEffect, useRef, useState } from 'react'
import {
  Database,
  CloudDownload,
  CircleHelp,
  FileText,
  FileSearch,
  FolderOpen,
  FolderGit2,
  Gauge,
  GitBranch,
  Loader2,
  Network,
  Play,
  Plus,
  Search,
  ShieldAlert,
  Trash2,
  X,
} from 'lucide-react'
import { open } from '@tauri-apps/plugin-dialog'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/modal'
import { MarkdownRenderer } from '@/components/ui/markdown-renderer'
import { MessageComposer } from '@/features/chat/components/MessageComposer'
import { RunTimeline } from '@/features/chat/components/RunTimeline'
import { listRunEvents,type TimelineEvent } from '@/services/agent-api/client'
import { type QAEntry, useProjectsStore } from '../store/useProjectsStore'
import { ImpactGraph } from './ImpactGraph'
import { KnowledgeGraphPage } from './KnowledgeGraphPage'
import { DataIntelligencePanel } from './DataIntelligencePanel'
import type { AgentMode } from '@modern-wingman/contracts'
import { browseSvn, listGitBranches, listVcsProfiles, type VcsProfile } from '@/services/agent-api/vcs'
import {
  getProjectImportProgress,
  getProjectIndexDiagnostics,
  type ProjectClarificationAnswer,
  type ProjectImportProgress,
  type ProjectIndexDiagnostics,
  type ProjectInfo,
} from '@/services/agent-api/projects'
import { useAppStore } from '@/app/store'

interface ProjectContextMenu {
  x: number
  y: number
  projectId: string
}

const humanize = (value: string | null | undefined) =>
  value ? value.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[_-]/g, ' ') : null

function freshnessPresentation(freshness: string | null | undefined) {
  switch (freshness?.toLowerCase()) {
    case 'fresh':
      return { label: '索引最新', className: 'bg-green-50 text-green-700' }
    case 'pendingchanges':
      return { label: '偵測到尚未索引的變更', className: 'bg-amber-50 text-amber-700' }
    case 'indexing':
      return { label: '索引更新中', className: 'bg-brand/10 text-brand' }
    case 'partial':
      return { label: '索引不完整', className: 'bg-amber-50 text-amber-700' }
    case 'stale':
      return { label: '索引可能過期', className: 'bg-amber-50 text-amber-700' }
    case 'failed':
      return { label: '索引失敗', className: 'bg-red-50 text-red-700' }
    default:
      return { label: '索引新鮮度未知', className: 'bg-surface-alt text-ink-secondary' }
  }
}

const hasUsableProjectIndex = (project: ProjectInfo | undefined) => Boolean(
  project?.indexManifestVersion && ['Indexed', 'PendingChanges', 'Partial', 'Stale', 'Indexing'].includes(project.indexStatus),
)

const projectIndexLabel = (project: ProjectInfo) => {
  if (project.indexStatus === 'Indexed') return `${project.nodeCount} 節點 · ${project.languages}`
  if (project.indexStatus === 'Indexing') return project.indexManifestVersion ? '重新索引中 · 上一版本可查詢' : '索引中…'
  if (project.indexStatus === 'PendingChanges') return `${project.pendingFileCount ?? 0} 個變更待同步`
  if (project.indexStatus === 'Partial') return '部分索引可用'
  if (project.indexStatus === 'Stale') return '使用上一個成功索引'
  if (project.indexStatus === 'Failed') return project.indexManifestVersion ? '更新失敗 · 上一版本可用' : '索引失敗'
  return '未索引'
}

function ChangeAnalysisPanels({
  entry,
  busy,
  onSubmitClarifications,
}: {
  entry: QAEntry
  busy: boolean
  onSubmitClarifications?: (sessionId: string, answers: ProjectClarificationAnswer[]) => Promise<void>
}) {
  const [answers, setAnswers] = useState<Record<string, string>>({})
  const evidencePack = entry.evidencePack
  const brief = entry.changeBrief ?? evidencePack?.brief
  const questions = (entry.clarificationQuestions ?? []).filter((item) => item.question?.trim())
  const evidence = evidencePack?.items ?? []
  const freshness = evidencePack?.freshness ?? entry.indexFreshness
  const manifestVersion = evidencePack?.manifestVersion ?? entry.indexManifestVersion
  const hasIndexContext = Boolean(freshness || manifestVersion || entry.indexStatus || entry.indexedAt)

  if (!brief && questions.length === 0 && !evidencePack && !hasIndexContext) return null

  const freshnessInfo = freshnessPresentation(freshness)
  const candidateAreas = brief?.candidateAreas?.filter(Boolean) ?? []
  const unknowns = brief?.unknowns?.filter(Boolean) ?? []
  const plan = entry.implementationPlan

  return (
    <div className="mt-3 space-y-3 border-t border-border pt-3">
      {hasIndexContext && (
        <section className="rounded-xl border border-border bg-surface-alt px-3 py-2.5">
          <div className="flex flex-wrap items-center gap-2">
            <Gauge className="h-3.5 w-3.5 text-ink-subtle" />
            <p className="text-xs font-medium text-ink">索引證據狀態</p>
            <span className={cn('rounded-md px-2 py-0.5 text-[11px] font-medium', freshnessInfo.className)}>
              {freshnessInfo.label}
            </span>
          </div>
          <p className="mt-1.5 text-xs text-ink-secondary">
            {manifestVersion && <>Manifest：<code className="font-mono">{manifestVersion}</code> · </>}
            {entry.indexedAt ? `最近索引：${new Date(entry.indexedAt).toLocaleString()}` : '請依證據狀態判斷分析可信度。'}
          </p>
        </section>
      )}

      {brief && (
        <section className="rounded-xl border border-border bg-surface p-3">
          <div className="flex flex-wrap items-center gap-2">
            <FileSearch className="h-3.5 w-3.5 text-brand" />
            <p className="text-xs font-semibold text-ink">變更簡報</p>
            {brief.classification?.changeKind && (
              <span className="rounded-md bg-brand/10 px-2 py-0.5 text-[11px] text-brand">
                {humanize(brief.classification.changeKind)}
              </span>
            )}
            {brief.classification?.analysisMode && (
              <span className="rounded-md bg-surface-alt px-2 py-0.5 text-[11px] text-ink-secondary">
                {humanize(brief.classification.analysisMode)}
              </span>
            )}
          </div>
          {(candidateAreas.length > 0 || unknowns.length > 0) && (
            <div className="mt-2 grid gap-2 text-xs sm:grid-cols-2">
              {candidateAreas.length > 0 && (
                <div>
                  <p className="text-ink-subtle">候選修改範圍</p>
                  <p className="mt-0.5 break-words text-ink-secondary">{candidateAreas.join('、')}</p>
                </div>
              )}
              {unknowns.length > 0 && (
                <div>
                  <p className="text-ink-subtle">尚待確認</p>
                  <p className="mt-0.5 break-words text-ink-secondary">{unknowns.join('、')}</p>
                </div>
              )}
            </div>
          )}
        </section>
      )}

      {questions.length > 0 && (
        <section className="rounded-xl border border-amber-200 bg-amber-50/50 p-3">
          <div className="flex items-center gap-2">
            <CircleHelp className="h-3.5 w-3.5 text-amber-700" />
            <p className="text-xs font-semibold text-ink">需要 IT 確認的問題（{questions.length}）</p>
          </div>
          <ol className="mt-2 space-y-2">
            {questions.map((item, index) => (
              <li key={`${item.priority ?? 'unknown'}-${item.question}-${index}`} className="rounded-lg border border-amber-200/70 bg-surface px-2.5 py-2">
                <div className="flex flex-wrap items-center gap-1.5">
                  <span className="rounded bg-amber-50 px-1.5 py-0.5 text-[10px] font-medium text-amber-700">
                    優先度 {item.priority ?? '—'}
                  </span>
                  {item.category && <span className="text-[10px] text-ink-subtle">{humanize(item.category)}</span>}
                  {item.isBlocking && <span className="text-[10px] font-medium text-red-600">需先確認</span>}
                </div>
                <p className="mt-1 text-xs font-medium text-ink">{index + 1}. {item.question}</p>
                {item.decisionImpact && <p className="mt-1 text-[11px] text-ink-secondary">影響決策：{item.decisionImpact}</p>}
              </li>
            ))}
          </ol>
          {entry.requiresClarification && entry.analysisSessionId && onSubmitClarifications && (
            <div className="mt-3 space-y-2 border-t border-amber-200 pt-3">
              {questions.filter((item) => item.category && item.question).map((item) => (
                <label key={item.category} className="block">
                  <span className="text-[11px] font-medium text-ink">{item.question}</span>
                  <textarea
                    value={answers[item.category ?? ''] ?? ''}
                    onChange={(event) => setAnswers((current) => ({ ...current, [item.category ?? '']: event.target.value }))}
                    rows={2}
                    className="mt-1 w-full resize-y rounded-lg border border-amber-200 bg-surface px-2.5 py-2 text-xs text-ink focus:outline-none focus:ring-2 focus:ring-brand/30"
                    placeholder="輸入 IT 確認結果…"
                  />
                </label>
              ))}
              <Button
                variant="primary"
                size="sm"
                isLoading={busy}
                disabled={!questions.some((item) => answers[item.category ?? '']?.trim())}
                onClick={async () => {
                  const values = questions
                    .filter((item) => item.category && answers[item.category]?.trim())
                    .map((item) => ({ category: item.category!, answer: answers[item.category!]!.trim() }))
                  await onSubmitClarifications(entry.analysisSessionId!, values)
                }}
              >
                提交澄清並繼續分析
              </Button>
            </div>
          )}
        </section>
      )}

      {plan && (
        <section className="rounded-xl border border-border bg-surface p-3">
          <div className="flex flex-wrap items-center gap-2">
            <GitBranch className="h-3.5 w-3.5 text-brand" />
            <p className="text-xs font-semibold text-ink">變更與驗證計畫</p>
            {plan.status && <span className="rounded bg-brand/10 px-2 py-0.5 text-[11px] text-brand">{humanize(plan.status)}</span>}
          </div>
          {(plan.modificationSteps?.length ?? 0) > 0 && (
            <ol className="mt-2 space-y-1.5">
              {plan.modificationSteps?.map((step, index) => (
                <li key={`${step.order}-${step.target}-${index}`} className="rounded-lg bg-surface-alt px-2.5 py-2 text-xs">
                  <p className="font-medium text-ink">{step.order ?? index + 1}. <code className="font-mono">{step.target}</code></p>
                  <p className="mt-0.5 text-ink-secondary">{step.action}</p>
                </li>
              ))}
            </ol>
          )}
          {(plan.risks?.length ?? 0) > 0 && <p className="mt-2 text-[11px] text-amber-700">風險：{plan.risks?.join('、')}</p>}
          {(plan.tests?.length ?? 0) > 0 && (
            <div className="mt-2 text-[11px] text-ink-secondary">
              <p className="font-medium text-ink">測試與驗收</p>
              {plan.tests?.map((test, index) => <p key={`${test.kind}-${index}`}>• {test.description}</p>)}
              {plan.acceptanceCriteria?.map((item, index) => <p key={`acceptance-${index}`}>✓ {item}</p>)}
            </div>
          )}
        </section>
      )}

      {evidencePack && (
        <section className="rounded-xl border border-border bg-surface p-3">
          <div className="flex flex-wrap items-center gap-2">
            <ShieldAlert className="h-3.5 w-3.5 text-brand" />
            <p className="text-xs font-semibold text-ink">分析證據</p>
            <span className="text-[11px] text-ink-subtle">{evidence.length} 項證據 · {evidencePack.paths?.length ?? 0} 條關係路徑</span>
            {evidencePack.truncated && <span className="text-[11px] text-amber-700">證據已依上限截斷</span>}
          </div>
          {evidence.length > 0 ? (
            <div className="mt-2 max-h-64 space-y-1.5 overflow-y-auto pr-1">
              {evidence.map((item, index) => (
                <div key={item.id ?? `${item.kind ?? 'evidence'}-${index}`} className="rounded-lg bg-surface-alt px-2.5 py-2">
                  <div className="flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[11px]">
                    <span className="font-medium text-ink">{item.summary ?? item.symbol ?? item.kind ?? '未命名證據'}</span>
                    {item.confidence && <span className="text-brand">{humanize(item.confidence)}</span>}
                    {item.sourceKind && <span className="text-ink-subtle">{humanize(item.sourceKind)}</span>}
                  </div>
                  {(item.filePath || item.reason) && (
                    <p className="mt-1 break-all text-[11px] text-ink-secondary">
                      {item.filePath && <><code className="font-mono">{item.filePath}{item.startLine ? `:${item.startLine}` : ''}</code>{item.reason ? ' · ' : ''}</>}
                      {item.reason}
                    </p>
                  )}
                </div>
              ))}
            </div>
          ) : (
            <p className="mt-2 text-xs text-ink-subtle">本次分析沒有可呈現的結構化證據。</p>
          )}
          {(evidencePack.capabilityGaps?.length ?? 0) > 0 && (
            <p className="mt-2 text-[11px] text-amber-700">尚未涵蓋：{evidencePack.capabilityGaps?.join('、')}</p>
          )}
        </section>
      )}
    </div>
  )
}

/**
 * 企業程式碼解析頁（WS3）：
 * 專案列表 → 索引（進度視覺化 FTUE）→ 問答（GraphRAG）→ Impact Analysis。
 */
export function ProjectsPage() {
  const defaultAgentMode = useAppStore((s) => s.defaultAgentMode)
  const {
    projects,
    activeProjectId,
    progress,
    qaHistory,
    impactResult,
    querying,
    activeRunId,
    error,
    fetchProjects,
    addProject,
    importRemoteProject,
    removeProject,
    setActiveProject,
    indexProject,
    ask,
    runImpact,
    makeAgentsMd,
    clearError,
  } = useProjectsStore()

  const [question, setQuestion] = useState('')
  const [selectedProviderId, setSelectedProviderId] = useState<string | null>(null)
  const [selectedModel, setSelectedModel] = useState<string | null>(null)
  const [agentMode,setAgentMode]=useState<AgentMode>(defaultAgentMode)
  const [impactSymbol, setImpactSymbol] = useState('')
  const [mode, setMode] = useState<'qa' | 'impact' | 'data'>('qa')
  const [agentsMd, setAgentsMd] = useState<string | null>(null)
  const [generating, setGenerating] = useState(false)
  const [projectContextMenu, setProjectContextMenu] = useState<ProjectContextMenu | null>(null)
  const [graphProjectId, setGraphProjectId] = useState<string | null>(null)
  const [addOpen,setAddOpen]=useState(false)
  const [sourceType,setSourceType]=useState<'local'|'git'|'svn'>('local')
  const [vcsProfiles,setVcsProfiles]=useState<VcsProfile[]>([])
  const [profileId,setProfileId]=useState('')
  const [repositoryUrl,setRepositoryUrl]=useState('')
  const [projectName,setProjectName]=useState('')
  const [destinationPath,setDestinationPath]=useState('')
  const [remoteRef,setRemoteRef]=useState('')
  const [remoteRefs,setRemoteRefs]=useState<string[]>([])
  const [importing,setImporting]=useState(false)
  const [importError,setImportError]=useState<string|null>(null)
  const [importProgress,setImportProgress]=useState<ProjectImportProgress|null>(null)
  const [projectTimeline,setProjectTimeline]=useState<TimelineEvent[]>([])
  const [indexDiagnostics,setIndexDiagnostics]=useState<ProjectIndexDiagnostics|null>(null)
  const importAbort=useRef<AbortController|null>(null)

  useEffect(() => {
    fetchProjects()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const activeProject = projects.find((p) => p.id === activeProjectId)
  const graphProject = projects.find((p) => p.id === graphProjectId)
  const history = activeProjectId ? (qaHistory[activeProjectId] ?? []) : []
  useEffect(()=>{if(!activeProjectId)return;const saved=localStorage.getItem(`wingman:project:${activeProjectId}:agent-mode`) as AgentMode|null;setAgentMode(saved??defaultAgentMode)},[activeProjectId,defaultAgentMode])
  useEffect(()=>{
    if(!activeProjectId){setIndexDiagnostics(null);return}
    let cancelled=false
    void getProjectIndexDiagnostics(activeProjectId)
      .then(value=>{if(!cancelled)setIndexDiagnostics(value)})
      .catch(()=>{if(!cancelled)setIndexDiagnostics(null)})
    return()=>{cancelled=true}
  },[activeProjectId,activeProject?.indexStatus,activeProject?.pendingFileCount])
  useEffect(()=>{if(activeProjectId)localStorage.setItem(`wingman:project:${activeProjectId}:agent-mode`,agentMode)},[activeProjectId,agentMode])
  useEffect(()=>{if(!activeRunId){setProjectTimeline([]);return}let cancelled=false;void listRunEvents(activeRunId).then(events=>{if(cancelled)return;setProjectTimeline(events.flatMap(({event})=>{const payload=JSON.parse(event.payloadJson) as Record<string,unknown>;if(event.eventType==='run:phase')return[{type:'phase',callId:null,name:String(payload.phase??'phase'),data:payload.detail,timestamp:event.timestamp} as TimelineEvent];if(event.eventType==='run:plan')return[{type:'plan',callId:null,name:'實作計畫',data:payload.plan,timestamp:event.timestamp} as TimelineEvent];if(event.eventType==='run:verify')return[{type:'verify',callId:null,name:'驗證結果',data:payload,timestamp:event.timestamp} as TimelineEvent];if(event.eventType==='run:tool-call')return[{type:'tool_call',callId:null,name:String(payload.toolName??'tool'),data:payload.toolInput,timestamp:event.timestamp} as TimelineEvent];if(event.eventType==='run:tool-result')return[{type:'tool_result',callId:null,name:String(payload.toolName??'tool'),data:payload.result,timestamp:event.timestamp} as TimelineEvent];if(event.eventType==='run:tool-output')return[{type:'tool_result',callId:null,name:`${String(payload.toolName??'tool')} · ${String(payload.stream??'stdout')}`,data:payload.text,timestamp:event.timestamp} as TimelineEvent];return[]}))}).catch(()=>setProjectTimeline([]));return()=>{cancelled=true}},[activeRunId])

  useEffect(() => {
    if (!projectContextMenu) return
    const close = () => setProjectContextMenu(null)
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') close()
    }
    document.addEventListener('click', close)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('click', close)
      document.removeEventListener('keydown', onKey)
    }
  }, [projectContextMenu])

  useEffect(()=>{if(!addOpen)return;void listVcsProfiles().then(setVcsProfiles).catch(err=>setImportError(err instanceof Error?err.message:String(err)))},[addOpen])

  const handleAddProject = () => setAddOpen(true)

  const chooseLocalProject = async () => {
    const dir = await open({ directory: true, title: '選擇專案根目錄（.NET / Java）' })
    if (!dir) return
    const project = await addProject('', dir as string)
    setAddOpen(false)
    setActiveProject(project.id)
    await indexProject(project.id)
  }

  const chooseDestination = async () => {
    const dir=await open({directory:true,title:'選擇空白目的資料夾'})
    if(dir)setDestinationPath(dir as string)
  }

  const loadRemoteRefs=async()=>{setImportError(null);try{if(sourceType==='git'){const refs=await listGitBranches(profileId,repositoryUrl);setRemoteRefs(refs);if(!remoteRef&&refs.length)setRemoteRef(refs[0]??'')}else{const result=await browseSvn(profileId,repositoryUrl);const xml=new DOMParser().parseFromString(result.output,'application/xml');setRemoteRefs(Array.from(xml.querySelectorAll('entry > name')).map(node=>node.textContent??'').filter(Boolean))}}catch(err){setImportError(err instanceof Error?err.message:String(err))}}

  const selectStandardSvnPath=async(segment:'trunk'|'branches'|'tags')=>{const base=repositoryUrl.replace(/\/$/,'');const target=`${base}/${segment}`;setImportError(null);if(segment==='trunk'){setRepositoryUrl(target);setRemoteRefs([]);return}try{const result=await browseSvn(profileId,target);const xml=new DOMParser().parseFromString(result.output,'application/xml');setRepositoryUrl(target);setRemoteRefs(Array.from(xml.querySelectorAll('entry > name')).map(node=>node.textContent??'').filter(Boolean))}catch(err){setImportError(err instanceof Error?err.message:String(err))}}

  const submitRemoteProject=async()=>{
    if(sourceType==='local')return
    const operationId=crypto.randomUUID()
    setImporting(true)
    setImportError(null)
    setImportProgress(null)
    importAbort.current=new AbortController()
    const poll=window.setInterval(()=>{void getProjectImportProgress(operationId).then(value=>{if(value)setImportProgress(value)}).catch(()=>undefined)},400)
    try{
      const project=await importRemoteProject({sourceType,name:projectName,profileId,repositoryUrl,ref:remoteRef||null,destinationPath,operationId},importAbort.current.signal)
      const finalProgress=await getProjectImportProgress(operationId).catch(()=>null)
      if(finalProgress)setImportProgress(finalProgress)
      setAddOpen(false)
      setActiveProject(project.id)
      await indexProject(project.id)
    }catch(err){
      if((err as Error).name==='AbortError')setImportProgress(current=>current?{...current,status:'cancelled',message:'已取消取得專案。',isError:false}:null)
      else setImportError(err instanceof Error?err.message:String(err))
    }finally{
      window.clearInterval(poll)
      setImporting(false)
      importAbort.current=null
    }
  }

  const handleAsk = async (q: string) => {
    if (!activeProjectId || !q.trim() || querying) return
    await ask(activeProjectId, q.trim(), selectedProviderId, selectedModel, agentMode)
  }

  const handleClarifications = async (sessionId: string, answers: ProjectClarificationAnswer[]) => {
    if (!activeProjectId || querying || answers.length === 0) return
    await ask(activeProjectId, '補充澄清資訊', selectedProviderId, selectedModel, agentMode, {
      analysisSessionId: sessionId,
      clarificationAnswers: answers,
      displayQuestion: answers.map((answer) => `${answer.category}：${answer.answer}`).join('\n'),
    })
  }

  const handleImpact = async () => {
    if (!activeProjectId || !impactSymbol.trim() || querying) return
    await runImpact(activeProjectId, impactSymbol.trim())
  }

  const handleAgentsMd = async () => {
    if (!activeProjectId) return
    setGenerating(true)
    try {
      const content = await makeAgentsMd(activeProjectId)
      setAgentsMd(content)
    } finally {
      setGenerating(false)
    }
  }

  return (
    <div className="flex-1 flex overflow-hidden max-[640px]:flex-col">
      {/* ── 專案列表側欄 ── */}
      <div className="w-64 border-r border-border flex flex-col shrink-0 max-[640px]:h-36 max-[640px]:w-full max-[640px]:border-r-0 max-[640px]:border-b">
        <div className="p-4 border-b border-border flex items-center justify-between">
          <p className="text-sm font-semibold text-ink">專案</p>
          <button
            type="button"
            title="新增專案"
            onClick={handleAddProject}
            className="p-1.5 rounded-lg text-ink-secondary hover:bg-surface-alt hover:text-brand transition-colors"
          >
            <Plus className="w-4 h-4" />
          </button>
        </div>
        <div className="flex-1 overflow-y-auto p-2 space-y-1 max-[640px]:flex max-[640px]:gap-2 max-[640px]:space-y-0 max-[640px]:overflow-x-auto">
          {projects.length === 0 && (
            <p className="text-xs text-ink-subtle text-center py-8 px-4">
              新增 .NET 或 Java 專案，開始解析程式碼知識
            </p>
          )}
          {projects.map((project) => (
            <button
              key={project.id}
              type="button"
              onClick={() => setActiveProject(project.id)}
              onContextMenu={(e) => {
                e.preventDefault()
                e.stopPropagation()
                setProjectContextMenu({ x: e.clientX, y: e.clientY, projectId: project.id })
              }}
              className={cn(
                'w-full flex items-start gap-2.5 px-3 py-2.5 rounded-xl text-left transition-colors max-[640px]:min-w-48',
                activeProjectId === project.id
                  ? 'bg-brand/10 text-brand'
                  : 'text-ink-secondary hover:bg-surface-alt',
              )}
            >
              <FolderGit2 className="w-4 h-4 mt-0.5 shrink-0" />
              <div className="min-w-0">
                <p className="text-sm font-medium truncate">{project.name}</p>
                <p className="text-xs text-ink-subtle truncate">
                  {projectIndexLabel(project)}
                </p>
                {project.vcsType && <p className="mt-0.5 truncate text-[11px] text-ink-subtle">
                  {project.vcsType.toUpperCase()} · {project.currentRef ?? project.revision ?? '—'} · {project.dirty == null ? '狀態未知' : project.dirty ? '有變更' : '乾淨'}
                </p>}
              </div>
            </button>
          ))}
        </div>
      </div>

      {/* ── 主區域 ── */}
      <div className="flex-1 flex flex-col overflow-hidden">
        {!activeProject ? (
          <div className="flex-1 flex items-center justify-center">
            <div className="text-center max-w-sm">
              <Database className="w-10 h-10 text-ink-subtle mx-auto mb-3" />
              <p className="text-sm font-medium text-ink">選擇或新增一個專案</p>
              <p className="text-xs text-ink-subtle mt-1.5">
                Wingman 會用 Roslyn / Java 分析器解析程式碼，建立 Neo4j 知識圖譜，
                讓你用自然語言問出專案 know-how。
              </p>
              <Button variant="primary" size="sm" className="mt-4" leftIcon={<Plus className="w-4 h-4" />} onClick={handleAddProject}>
                新增專案
              </Button>
            </div>
          </div>
        ) : (
          <>
            {/* Header */}
            <div className="px-6 pt-5 pb-4 border-b border-border shrink-0 max-[640px]:px-4 max-[640px]:pt-3">
              <div className="flex items-center justify-between max-[640px]:flex-col max-[640px]:items-stretch max-[640px]:gap-2">
                <div className="min-w-0">
                  <h1 className="text-lg font-bold text-ink truncate">{activeProject.name}</h1>
                  <p className="text-xs text-ink-subtle font-mono truncate">{activeProject.rootPath}</p>
                </div>
                <div className="flex items-center gap-1.5 shrink-0 max-[640px]:flex-wrap">
                  <Button
                    variant="outline" size="sm"
                    leftIcon={<Play className="w-3.5 h-3.5" />}
                    onClick={() => indexProject(activeProject.id)}
                    disabled={!!progress}
                  >
                    {activeProject.indexStatus === 'Indexed' ? '重新索引' : '開始索引'}
                  </Button>
                  <Button
                    variant="outline" size="sm"
                    leftIcon={<FileText className="w-3.5 h-3.5" />}
                    onClick={handleAgentsMd}
                    isLoading={generating}
                    disabled={!hasUsableProjectIndex(activeProject)}
                  >
                    生成 AGENTS.md
                  </Button>
                  <button
                    type="button"
                    title="移除專案"
                    onClick={() => removeProject(activeProject.id)}
                    className="p-2 rounded-lg text-ink-subtle hover:bg-red-50 hover:text-red-500 transition-colors"
                  >
                    <Trash2 className="w-4 h-4" />
                  </button>
                </div>
              </div>

              {/* 索引進度（FTUE P1）*/}
              {progress && (
                <div className="mt-3 rounded-xl border border-brand/30 bg-brand/5 p-3">
                  <div className="flex items-center gap-2">
                    <Loader2 className="w-4 h-4 text-brand animate-spin" />
                    <p className="text-sm text-ink">{progress.message}</p>
                    <span className="ml-auto text-xs font-mono text-brand">{progress.percent}%</span>
                  </div>
                  <div className="mt-2 h-1.5 rounded-full bg-surface-alt overflow-hidden">
                    <div
                      className="h-full bg-brand rounded-full transition-all duration-700"
                      style={{ width: `${progress.percent}%` }}
                    />
                  </div>
                </div>
              )}

              {(activeProject.indexManifestVersion || indexDiagnostics) && (
                <details className="mt-3 rounded-xl border border-border bg-surface-alt px-3 py-2 text-xs">
                  <summary className="cursor-pointer select-none font-medium text-ink">
                    索引診斷 · {activeProject.pendingFileCount ?? indexDiagnostics?.pendingFiles.length ?? 0} 個待同步檔案
                    {indexDiagnostics?.isStale ? <span className="ml-2 text-amber-700">使用上一個成功圖譜</span> : null}
                  </summary>
                  <div className="mt-2 space-y-1 text-ink-secondary">
                    <p>Manifest：<code className="break-all font-mono">{activeProject.indexManifestVersion ?? indexDiagnostics?.current?.version ?? '—'}</code></p>
                    {indexDiagnostics?.latestAttempt?.error && <p className="text-red-700">最近失敗：{indexDiagnostics.latestAttempt.error}</p>}
                    {(indexDiagnostics?.pendingFiles.length ?? 0) > 0 && <p>待同步：{indexDiagnostics?.pendingFiles.slice(0, 8).join('、')}{(indexDiagnostics?.pendingFiles.length ?? 0) > 8 ? '…' : ''}</p>}
                    {(indexDiagnostics?.latestAttempt?.files.filter(file => file.status.toLowerCase() !== 'indexed').length ?? 0) > 0 && (
                      <div>
                        <p className="font-medium text-ink">失敗／略過檔案</p>
                        {indexDiagnostics?.latestAttempt?.files.filter(file => file.status.toLowerCase() !== 'indexed').slice(0, 12).map(file => (
                          <p key={file.relativePath}><code className="font-mono">{file.relativePath}</code> · {file.status}{file.reason ? `：${file.reason}` : ''}</p>
                        ))}
                      </div>
                    )}
                  </div>
                </details>
              )}

              {/* 模式切換 */}
              <div className="mt-3 flex items-center gap-1">
                <button
                  type="button"
                  onClick={() => setMode('qa')}
                  className={cn(
                    'inline-flex items-center gap-1.5 px-3 py-1.5 rounded-xl text-xs font-medium transition-colors',
                    mode === 'qa' ? 'bg-brand text-white' : 'text-ink-secondary hover:bg-surface-alt',
                  )}
                >
                  <Search className="w-3.5 h-3.5" /> 知識問答
                </button>
                <button
                  type="button"
                  onClick={() => setMode('impact')}
                  className={cn(
                    'inline-flex items-center gap-1.5 px-3 py-1.5 rounded-xl text-xs font-medium transition-colors',
                    mode === 'impact' ? 'bg-brand text-white' : 'text-ink-secondary hover:bg-surface-alt',
                  )}
                >
                  <GitBranch className="w-3.5 h-3.5" /> 影響分析
                </button>
                <button
                  type="button"
                  onClick={() => setMode('data')}
                  className={cn(
                    'inline-flex items-center gap-1.5 px-3 py-1.5 rounded-xl text-xs font-medium transition-colors',
                    mode === 'data' ? 'bg-brand text-white' : 'text-ink-secondary hover:bg-surface-alt',
                  )}
                >
                  <Database className="w-3.5 h-3.5" /> 資料情報
                </button>
              </div>
            </div>

            {error && (
              <div className="mx-6 mt-3 flex items-center justify-between rounded-xl border border-red-200 bg-red-50 px-4 py-2.5">
                <p className="text-sm text-red-700">{error}</p>
                <button type="button" onClick={clearError} className="text-red-400 hover:text-red-600">
                  <X className="w-4 h-4" />
                </button>
              </div>
            )}

            {/* ── QA 模式 ── */}
            {mode === 'qa' && (
              <>
                <div className="flex-1 overflow-y-auto p-6 space-y-4">
                  {history.length === 0 && (
                    <div className="text-center py-12 text-ink-subtle text-sm">
                      {hasUsableProjectIndex(activeProject)
                        ? '問任何關於這個專案的問題，例如「訂單計算邏輯在哪些類別？」'
                        : '先完成索引，就能開始問答'}
                    </div>
                  )}
                  {history.map((entry, i) => (
                    <div key={i} className="space-y-2">
                      <div className="flex justify-end">
                        <div className="max-w-[80%] rounded-2xl bg-brand text-white px-4 py-2.5 text-sm">
                          {entry.question}
                        </div>
                      </div>
                      <div className="flex justify-start">
                        <div className="max-w-[85%] rounded-2xl border border-border bg-surface px-4 py-3 text-sm">
                          <MarkdownRenderer content={entry.answer} />
                          <ChangeAnalysisPanels entry={entry} busy={querying} onSubmitClarifications={handleClarifications} />
                        </div>
                      </div>
                    </div>
                  ))}
                  {querying && (
                    <div className="flex items-center gap-2 text-ink-subtle text-sm">
                      <Loader2 className="w-4 h-4 animate-spin" /> 正在查詢圖譜...
                    </div>
                  )}
                </div>
                <RunTimeline events={projectTimeline} />
                <MessageComposer
                  selectedProviderId={selectedProviderId}
                  selectedModel={selectedModel}
                  value={question}
                  onChange={setQuestion}
                  onProviderChange={setSelectedProviderId}
                  onModelChange={setSelectedModel}
                  onSubmit={handleAsk}
                  busy={querying}
                  disabled={!hasUsableProjectIndex(activeProject)}
                  placeholder="用自然語言問專案 know-how…"
                  containerClassName="px-6 pb-5 shrink-0"
                  workspacePath={activeProject.rootPath}
                  agentMode={agentMode}
                  onAgentModeChange={setAgentMode}
                />
              </>
            )}

            {/* ── Impact 模式（P2 視覺化）── */}
            {mode === 'impact' && (
              <div className="flex-1 overflow-y-auto p-6 space-y-4">
                <div className="flex items-center gap-2">
                  <input
                    type="text"
                    value={impactSymbol}
                    onChange={(e) => setImpactSymbol(e.target.value)}
                    onKeyDown={(e) => e.key === 'Enter' && handleImpact()}
                    placeholder="輸入要修改的方法/類別名稱，例如 CalculateTotal"
                    disabled={!hasUsableProjectIndex(activeProject) || querying}
                    className="flex-1 px-4 py-2.5 rounded-xl border border-border bg-surface text-sm font-mono placeholder:text-ink-subtle focus:outline-none focus:ring-2 focus:ring-brand/40 disabled:opacity-50"
                  />
                  <Button variant="primary" size="sm" onClick={handleImpact} isLoading={querying}>
                    分析影響
                  </Button>
                </div>

                {impactResult && (
                  impactResult.target === null ? (
                    <p className="text-sm text-ink-subtle text-center py-8">找不到符合的符號</p>
                  ) : (
                    <div className="space-y-4">
                      <div className="rounded-2xl border border-border bg-surface p-4">
                        <p className="text-xs text-ink-subtle">分析目標</p>
                        <p className="text-sm font-mono font-medium text-ink mt-1">
                          {impactResult.target.signature ?? impactResult.target.name}
                        </p>
                        <p className="text-xs text-ink-subtle mt-0.5">
                          {impactResult.target.filePath}:{impactResult.target.startLine}
                        </p>
                      </div>

                      {/* 呼叫鏈視覺化 */}
                      <ImpactGraph result={impactResult} />

                      {/* 受影響清單 */}
                      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
                        <div className="rounded-2xl border border-border bg-surface p-4">
                          <p className="text-sm font-semibold text-ink mb-2">
                            受影響方法（{impactResult.affectedMethods.length}）
                          </p>
                          <div className="space-y-1 max-h-64 overflow-y-auto">
                            {impactResult.affectedMethods.map((m) => (
                              <p key={m.key} className="text-xs font-mono text-ink-secondary truncate">
                                {m.name} <span className="text-ink-subtle">— {m.filePath}:{m.startLine}</span>
                              </p>
                            ))}
                          </div>
                        </div>
                        <div className="rounded-2xl border border-border bg-surface p-4">
                          <p className="text-sm font-semibold text-ink mb-2">建議驗證指令</p>
                          <div className="space-y-1.5 max-h-64 overflow-y-auto">
                            {impactResult.suggestedTestFilters.length === 0 ? (
                              <p className="text-xs text-ink-subtle">無測試建議（未偵測到相關測試類別）</p>
                            ) : (
                              impactResult.suggestedTestFilters.map((f, i) => (
                                <code key={i} className="block text-xs bg-surface-alt rounded-lg px-2.5 py-1.5 font-mono text-ink-secondary">
                                  {f}
                                </code>
                              ))
                            )}
                          </div>
                        </div>
                      </div>
                    </div>
                  )
                )}
              </div>
            )}

            {/* ── Data Intelligence（P3）── */}
            {mode === 'data' && <DataIntelligencePanel projectId={activeProject.id} />}
          </>
        )}
      </div>

      {/* AGENTS.md 預覽 */}
      <Modal open={addOpen} onOpenChange={setAddOpen} title="新增專案" size="lg" footer={sourceType==='local'?undefined:<><Button variant="ghost" onClick={()=>{if(importing)importAbort.current?.abort();else setAddOpen(false)}}>{importing?'取消取得':'取消'}</Button><Button onClick={()=>void submitRemoteProject()} disabled={importing||!profileId||!repositoryUrl||!destinationPath}>{importing&&<Loader2 className="mr-1.5 h-4 w-4 animate-spin"/>}{importing?'正在取得':'取得專案'}</Button></>}>
        <div className="mb-4 flex border-b border-border" role="tablist">
          {(['local','git','svn'] as const).map(item=><button key={item} type="button" role="tab" aria-selected={sourceType===item} onClick={()=>{setSourceType(item);setRemoteRefs([]);setRemoteRef('');setImportError(null)}} className={cn('border-b-2 px-4 py-2 text-sm',sourceType===item?'border-brand font-medium text-brand':'border-transparent text-ink-secondary')}>{item==='local'?'本機資料夾':item==='git'?'Git / Bitbucket':'SVN'}</button>)}
        </div>
        {sourceType==='local'?<button type="button" onClick={()=>void chooseLocalProject()} className="flex w-full items-center gap-3 border border-border bg-surface-alt p-4 text-left hover:border-brand/40"><FolderOpen className="h-5 w-5 text-brand"/><div><p className="text-sm font-medium text-ink">選擇既有資料夾</p><p className="text-xs text-ink-subtle">選取後建立專案並開始索引</p></div></button>:<div className="space-y-3">
          <div className="grid gap-3 sm:grid-cols-2"><label className="text-sm text-ink">專案名稱<input value={projectName} onChange={e=>setProjectName(e.target.value)} className="mt-1 w-full border border-border bg-surface px-3 py-2" placeholder="可留空"/></label><label className="text-sm text-ink">連線 Profile<select value={profileId} onChange={e=>setProfileId(e.target.value)} className="mt-1 w-full border border-border bg-surface px-3 py-2"><option value="">請選擇</option>{vcsProfiles.filter(profile=>profile.vcsType===sourceType).map(profile=><option key={profile.id} value={profile.id}>{profile.name}</option>)}</select></label></div>
          <label className="block text-sm text-ink">Repository URL<div className="mt-1 flex gap-2"><input value={repositoryUrl} onChange={e=>setRepositoryUrl(e.target.value)} className="min-w-0 flex-1 border border-border bg-surface px-3 py-2"/><Button variant="outline" size="sm" onClick={()=>void loadRemoteRefs()} disabled={!profileId||!repositoryUrl}><Search className="mr-1 h-4 w-4"/>{sourceType==='git'?'分支':'瀏覽'}</Button></div></label>
          {sourceType==='svn'&&<div><p className="mb-1 text-xs text-ink-subtle">標準 SVN Layout</p><div className="flex gap-2">{(['trunk','branches','tags'] as const).map(segment=><Button key={segment} type="button" variant="outline" size="sm" disabled={!profileId||!repositoryUrl} onClick={()=>void selectStandardSvnPath(segment)}>{segment}</Button>)}</div></div>}
          {sourceType==='git'?<label className="block text-sm text-ink">Branch<select value={remoteRef} onChange={e=>setRemoteRef(e.target.value)} className="mt-1 w-full border border-border bg-surface px-3 py-2"><option value="">請先取得分支</option>{remoteRefs.map(ref=><option key={ref} value={ref}>{ref}</option>)}</select></label>:remoteRefs.length>0&&<div className="max-h-32 overflow-y-auto border border-border p-1">{remoteRefs.map(ref=><button key={ref} type="button" className="block w-full px-2 py-1 text-left text-xs hover:bg-surface-alt" onClick={()=>{setRepositoryUrl(repositoryUrl.replace(/\/$/,'')+'/'+ref);setRemoteRefs([])}}>{ref}</button>)}</div>}
          <label className="block text-sm text-ink">目的資料夾<div className="mt-1 flex gap-2"><input value={destinationPath} readOnly className="min-w-0 flex-1 border border-border bg-surface-alt px-3 py-2"/><Button variant="outline" size="sm" onClick={()=>void chooseDestination()}><FolderOpen className="mr-1 h-4 w-4"/>選擇</Button></div></label>
          <div className="flex items-center gap-2 text-xs text-ink-subtle"><CloudDownload className="h-4 w-4"/>完成後會建立版控綁定並沿用既有索引流程</div>
          {importProgress&&<div role="status" className={cn('border px-3 py-2 text-xs',importProgress.isError?'border-danger/30 bg-danger/5 text-danger':'border-border bg-surface-alt text-ink-secondary')}>
            <div className="mb-1 flex items-center gap-2 font-medium text-ink">{importProgress.status==='running'&&<Loader2 className="h-3.5 w-3.5 animate-spin"/>}{sourceType==='git'?'Git clone':'SVN checkout'} · {importProgress.status}</div>
            <p className="break-words font-mono">{importProgress.message}</p>
          </div>}
        </div>}
        {importError&&<p className="mt-3 text-xs text-danger">{importError}</p>}
      </Modal>

      {agentsMd && (
        <Modal open onOpenChange={(o) => !o && setAgentsMd(null)} title="AGENTS.md 已生成" size="lg">
          <div className="max-h-[60vh] overflow-y-auto rounded-xl bg-surface-alt p-4">
            <MarkdownRenderer content={agentsMd} />
          </div>
          <p className="text-xs text-ink-subtle mt-2">已寫入專案根目錄的 AGENTS.md</p>
        </Modal>
      )}

      {projectContextMenu && (
        <div
          className="fixed z-50 min-w-[168px] rounded-xl border border-border bg-surface shadow-lg py-1 select-none"
          style={{ top: projectContextMenu.y, left: projectContextMenu.x }}
          onClick={(e) => e.stopPropagation()}
        >
          <button
            type="button"
            disabled={!hasUsableProjectIndex(projects.find((project) => project.id === projectContextMenu.projectId))}
            className="w-full flex items-center gap-2.5 px-3.5 py-2 text-sm text-ink hover:bg-surface-alt transition-colors text-left disabled:cursor-not-allowed disabled:opacity-45"
            onClick={() => {
              const project = projects.find((item) => item.id === projectContextMenu.projectId)
              if (!project || !hasUsableProjectIndex(project)) return
              setProjectContextMenu(null)
              setGraphProjectId(project.id)
            }}
          >
            <Network className="w-3.5 h-3.5 shrink-0 text-ink-subtle" />
            查看知識圖譜
          </button>
          {!hasUsableProjectIndex(projects.find((project) => project.id === projectContextMenu.projectId)) && (
            <p className="px-3.5 py-1.5 text-[11px] text-ink-subtle">需先完成索引</p>
          )}
        </div>
      )}

      {graphProject && (
        <KnowledgeGraphPage
          project={graphProject}
          onClose={() => setGraphProjectId(null)}
        />
      )}
    </div>
  )
}
