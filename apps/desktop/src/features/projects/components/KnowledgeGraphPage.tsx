import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import ForceGraph2D, {
  type ForceGraphMethods,
  type LinkObject,
  type NodeObject,
} from 'react-force-graph-2d'
import {
  Braces,
  ChevronDown,
  Database,
  Download,
  Expand,
  FileJson,
  Filter,
  Loader2,
  Maximize2,
  Network,
  Play,
  RefreshCw,
  Search,
  Table2,
  type LucideIcon,
  X,
} from 'lucide-react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import {
  expandProjectGraphNeighbors,
  getProjectGraph,
  getProjectGraphSchema,
  queryProjectGraph,
  type CodeGraphQueryResult,
  type CodeGraphSchema,
  type CodeGraphVisualData,
  type CodeGraphVisualEdge,
  type CodeGraphVisualNode,
  type ProjectInfo,
} from '@/services/agent-api/projects'

type GraphNode = CodeGraphVisualNode & NodeObject<CodeGraphVisualNode>
type GraphLink = CodeGraphVisualEdge & LinkObject<CodeGraphVisualNode, CodeGraphVisualEdge>
type ViewMode = 'graph' | 'table' | 'raw'
type ExpandMode = 'all' | 'callers' | 'callees' | 'same-file'
type TunableForce = {
  strength?: (value: number | ((item: GraphLink) => number)) => unknown
  distance?: (value: number | ((item: GraphLink) => number)) => unknown
  distanceMin?: (value: number) => unknown
  distanceMax?: (value: number) => unknown
  iterations?: (value: number) => unknown
}

interface KnowledgeGraphPageProps {
  project: ProjectInfo
  onClose: () => void
}

interface GraphStyleSettings {
  nodeColors: Record<string, string>
  relationColors: Record<string, string>
  caption: 'name' | 'signature' | 'kind'
  halo: boolean
}

interface GraphContextMenu {
  x: number
  y: number
  node: GraphNode
}

const DEFAULT_LIMIT = 1000
const LIMITS = [1000, 2000, 5000, 10000]
const NODE_PALETTE = ['#52D1DC', '#7C8CFF', '#30DAA2', '#FFC94F', '#FF7A90', '#B48CFF', '#FF9F43', '#8FE388']
const REL_PALETTE = ['#67E8F9', '#A7F3D0', '#FDE68A', '#F9A8D4', '#C4B5FD', '#FDBA74']
const VIEW_TABS: { value: ViewMode; icon: LucideIcon; label: string }[] = [
  { value: 'graph', icon: Network, label: 'Graph' },
  { value: 'table', icon: Table2, label: 'Table' },
  { value: 'raw', icon: Braces, label: 'Raw' },
]

const DEFAULT_QUERY = `MATCH (n:CodeNode {projectId: $projectId})
OPTIONAL MATCH (n)-[r]->(m:CodeNode {projectId: $projectId})
RETURN n, r, m
LIMIT 100`

const styleKey = (projectId: string) => `modern-wingman:knowledge-graph:${projectId}:styles`

function defaultStyles(schema?: CodeGraphSchema): GraphStyleSettings {
  const nodeColors: Record<string, string> = {}
  const relationColors: Record<string, string> = {}

  schema?.nodeKinds.forEach((kind, index) => {
    nodeColors[kind.name] = NODE_PALETTE[index % NODE_PALETTE.length]
  })
  schema?.relationshipTypes.forEach((type, index) => {
    relationColors[type.name] = REL_PALETTE[index % REL_PALETTE.length]
  })

  return {
    nodeColors,
    relationColors,
    caption: 'name',
    halo: true,
  }
}

function loadStyles(projectId: string, schema?: CodeGraphSchema): GraphStyleSettings {
  const fallback = defaultStyles(schema)
  try {
    const raw = localStorage.getItem(styleKey(projectId))
    if (!raw) return fallback
    const saved = JSON.parse(raw) as Partial<GraphStyleSettings>
    return {
      nodeColors: { ...fallback.nodeColors, ...(saved.nodeColors ?? {}) },
      relationColors: { ...fallback.relationColors, ...(saved.relationColors ?? {}) },
      caption: saved.caption ?? fallback.caption,
      halo: saved.halo ?? fallback.halo,
    }
  } catch {
    return fallback
  }
}

function saveStyles(projectId: string, styles: GraphStyleSettings) {
  localStorage.setItem(styleKey(projectId), JSON.stringify(styles))
}

function mergeGraphData(base: CodeGraphVisualData | null, incoming: CodeGraphVisualData): CodeGraphVisualData {
  if (!base) return incoming

  const nodes = new Map(base.nodes.map((node) => [node.id, node]))
  const edges = new Map(base.edges.map((edge) => [edge.id, edge]))
  incoming.nodes.forEach((node) => nodes.set(node.id, node))
  incoming.edges.forEach((edge) => edges.set(edge.id, edge))

  return {
    nodes: Array.from(nodes.values()),
    edges: Array.from(edges.values()),
    totalNodes: Math.max(base.totalNodes, incoming.totalNodes),
    loadedNodes: nodes.size,
    loadedEdges: edges.size,
    hasMore: base.hasMore || incoming.hasMore,
  }
}

function endpointId(endpoint: string | number | { id?: string | number } | null | undefined) {
  if (endpoint && typeof endpoint === 'object') return String(endpoint.id)
  return String(endpoint)
}

function hexToRgba(hex: string, alpha: number) {
  const normalized = hex.replace('#', '')
  const full = normalized.length === 3
    ? normalized.split('').map((char) => `${char}${char}`).join('')
    : normalized
  const value = Number.parseInt(full, 16)
  const r = (value >> 16) & 255
  const g = (value >> 8) & 255
  const b = value & 255
  return `rgba(${r}, ${g}, ${b}, ${alpha})`
}

function renderValue(value: unknown) {
  if (value === null || value === undefined) return ''
  if (typeof value === 'string') return value
  if (typeof value === 'number' || typeof value === 'boolean') return String(value)
  return JSON.stringify(value)
}

function linkLayoutDistance(link: GraphLink, densityScale: number) {
  const base = link.type === 'CONTAINS'
    ? 150
    : link.type === 'CALLS'
      ? 220
      : link.type === 'IMPLEMENTS' || link.type === 'INHERITS'
        ? 200
        : 185
  return base * densityScale
}

function downloadText(filename: string, content: string, type: string) {
  const blob = new Blob([content], { type })
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = filename
  anchor.click()
  URL.revokeObjectURL(url)
}

export function KnowledgeGraphPage({ project, onClose }: KnowledgeGraphPageProps) {
  const graphRef = useRef<ForceGraphMethods<GraphNode, GraphLink> | undefined>(undefined)
  const canvasWrapRef = useRef<HTMLDivElement>(null)
  const [canvasSize, setCanvasSize] = useState({ width: 900, height: 640 })
  const [schema, setSchema] = useState<CodeGraphSchema | null>(null)
  const [graph, setGraph] = useState<CodeGraphVisualData | null>(null)
  const [queryResult, setQueryResult] = useState<CodeGraphQueryResult | null>(null)
  const [selectedNode, setSelectedNode] = useState<GraphNode | null>(null)
  const [hoverNode, setHoverNode] = useState<GraphNode | null>(null)
  const [selectedLink, setSelectedLink] = useState<GraphLink | null>(null)
  const [contextMenu, setContextMenu] = useState<GraphContextMenu | null>(null)
  const [viewMode, setViewMode] = useState<ViewMode>('graph')
  const [queryText, setQueryText] = useState(DEFAULT_QUERY)
  const [searchText, setSearchText] = useState('')
  const [limit, setLimit] = useState(DEFAULT_LIMIT)
  const [selectedKinds, setSelectedKinds] = useState<string[]>([])
  const [selectedRelations, setSelectedRelations] = useState<string[]>([])
  const [styles, setStyles] = useState<GraphStyleSettings>(() => defaultStyles())
  const [loading, setLoading] = useState(false)
  const [querying, setQuerying] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const node = canvasWrapRef.current
    if (!node) return
    const observer = new ResizeObserver(([entry]) => {
      if (!entry) return
      setCanvasSize({
        width: Math.max(320, Math.floor(entry.contentRect.width)),
        height: Math.max(320, Math.floor(entry.contentRect.height)),
      })
    })
    observer.observe(node)
    return () => observer.disconnect()
  }, [])

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        if (contextMenu) setContextMenu(null)
        else onClose()
      }
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [contextMenu, onClose])

  useEffect(() => {
    const load = async () => {
      setLoading(true)
      setError(null)
      try {
        const nextSchema = await getProjectGraphSchema(project.id)
        setSchema(nextSchema)
        const nextStyles = loadStyles(project.id, nextSchema)
        setStyles(nextStyles)
        const nextGraph = await getProjectGraph(project.id, { limit })
        setGraph(nextGraph)
        setTimeout(() => graphRef.current?.zoomToFit(700, 64), 120)
      } catch (err) {
        setError(err instanceof Error ? err.message : String(err))
      } finally {
        setLoading(false)
      }
    }
    void load()
  }, [project.id, limit])

  useEffect(() => {
    if (!schema) return
    saveStyles(project.id, styles)
  }, [project.id, schema, styles])

  const graphData = useMemo(() => ({
    nodes: (graph?.nodes ?? []) as GraphNode[],
    links: (graph?.edges ?? []) as GraphLink[],
  }), [graph])

  useEffect(() => {
    const graphApi = graphRef.current
    if (!graphApi || graphData.nodes.length === 0) return

    const densityScale = Math.min(2.35, Math.max(1.2, Math.sqrt(graphData.nodes.length / 700)))
    const charge = graphApi.d3Force('charge') as TunableForce | undefined
    charge?.strength?.(-420 * densityScale)
    charge?.distanceMin?.(32)
    charge?.distanceMax?.(980 * densityScale)

    const link = graphApi.d3Force('link') as TunableForce | undefined
    link?.distance?.((item) => linkLayoutDistance(item as GraphLink, densityScale))
    link?.strength?.(0.035)
    link?.iterations?.(1)

    graphApi.d3ReheatSimulation()
  }, [graphData.nodes.length, graphData.links.length])

  const selectedNeighborIds = useMemo(() => {
    const ids = new Set<string>()
    if (!selectedNode || !graph) return ids
    ids.add(selectedNode.id)
    graph.edges.forEach((edge) => {
      const source = endpointId(edge.source)
      const target = endpointId(edge.target)
      if (source === selectedNode.id) ids.add(target)
      if (target === selectedNode.id) ids.add(source)
    })
    return ids
  }, [graph, selectedNode])

  const nodeColor = useCallback((node: GraphNode) => (
    styles.nodeColors[node.kind] ?? '#9CA3AF'
  ), [styles.nodeColors])

  const linkColor = useCallback((link: GraphLink) => (
    styles.relationColors[link.type] ?? '#94A3B8'
  ), [styles.relationColors])

  const loadGraph = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const nextGraph = await getProjectGraph(project.id, {
        limit,
        kinds: selectedKinds,
        relations: selectedRelations,
      })
      setGraph(nextGraph)
      setSelectedNode(null)
      setSelectedLink(null)
      setTimeout(() => graphRef.current?.zoomToFit(700, 64), 120)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setLoading(false)
    }
  }, [limit, project.id, selectedKinds, selectedRelations])

  const runQuery = useCallback(async () => {
    setQuerying(true)
    setError(null)
    try {
      const result = await queryProjectGraph(project.id, queryText, limit)
      setQueryResult(result)
      setGraph((current) => mergeGraphData(current, result.graph))
      setViewMode(result.graph.nodes.length > 0 ? 'graph' : 'table')
      setTimeout(() => graphRef.current?.zoomToFit(700, 64), 120)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setQuerying(false)
    }
  }, [limit, project.id, queryText])

  const expandNodes = useCallback(async (mode: ExpandMode, node = selectedNode) => {
    if (!node) return
    setLoading(true)
    setError(null)
    try {
      const nextGraph = await expandProjectGraphNeighbors(project.id, [node.id], {
        depth: 1,
        limit,
        mode,
      })
      setGraph((current) => mergeGraphData(current, nextGraph))
      setContextMenu(null)
      setTimeout(() => graphRef.current?.zoomToFit(500, 64), 80)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setLoading(false)
    }
  }, [limit, project.id, selectedNode])

  const searchNode = useCallback(() => {
    const keyword = searchText.trim().toLowerCase()
    if (!keyword || !graph) return
    const found = graph.nodes.find((node) => (
      node.name.toLowerCase().includes(keyword) ||
      node.id.toLowerCase().includes(keyword) ||
      (node.signature?.toLowerCase().includes(keyword) ?? false) ||
      (node.filePath?.toLowerCase().includes(keyword) ?? false)
    )) as GraphNode | undefined
    if (!found) {
      setError('目前載入的圖譜中找不到符合節點。')
      return
    }
    setSelectedNode(found)
    setSelectedLink(null)
    if (typeof found.x === 'number' && typeof found.y === 'number') {
      graphRef.current?.centerAt(found.x, found.y, 700)
      graphRef.current?.zoom(3.4, 700)
    }
  }, [graph, searchText])

  const drawNode = useCallback((
    node: NodeObject<GraphNode>,
    ctx: CanvasRenderingContext2D,
    globalScale: number,
  ) => {
    const graphNode = node as GraphNode
    const x = graphNode.x ?? 0
    const y = graphNode.y ?? 0
    const color = nodeColor(graphNode)
    const selected = selectedNode?.id === graphNode.id
    const hovered = hoverNode?.id === graphNode.id
    const dimmed = !!selectedNode && !selectedNeighborIds.has(graphNode.id)
    const radius = Math.max(4.5, Math.min(17, 5 + Math.sqrt((graphNode.degree ?? 0) + 1) * 1.4))

    ctx.save()
    ctx.globalAlpha = dimmed ? 0.2 : 1

    if (styles.halo) {
      const haloRadius = radius * (selected || hovered ? 5 : 3.3)
      const gradient = ctx.createRadialGradient(x, y, radius * 0.7, x, y, haloRadius)
      gradient.addColorStop(0, hexToRgba(color, selected || hovered ? 0.62 : 0.38))
      gradient.addColorStop(0.55, hexToRgba(color, selected || hovered ? 0.22 : 0.12))
      gradient.addColorStop(1, hexToRgba(color, 0))
      ctx.fillStyle = gradient
      ctx.beginPath()
      ctx.arc(x, y, haloRadius, 0, Math.PI * 2)
      ctx.fill()
    }

    ctx.shadowColor = color
    ctx.shadowBlur = selected || hovered ? 20 : 10
    ctx.fillStyle = color
    ctx.beginPath()
    ctx.arc(x, y, radius, 0, Math.PI * 2)
    ctx.fill()

    ctx.shadowBlur = 0
    ctx.lineWidth = selected ? 2.2 / globalScale : 1.2 / globalScale
    ctx.strokeStyle = selected ? '#ffffff' : 'rgba(255,255,255,0.72)'
    ctx.stroke()

    const labelZoomThreshold = graphData.nodes.length > 2500
      ? 2.1
      : graphData.nodes.length > 900
        ? 1.55
        : 1.15
    const shouldDrawLabel = selected || hovered || globalScale > labelZoomThreshold
    if (shouldDrawLabel) {
      const label = styles.caption === 'signature'
        ? graphNode.signature || graphNode.name
        : styles.caption === 'kind'
          ? graphNode.kind
          : graphNode.name
      const fontSize = Math.max(8, 11 / globalScale)
      ctx.font = `600 ${fontSize}px -apple-system, BlinkMacSystemFont, Segoe UI, sans-serif`
      ctx.textAlign = 'center'
      ctx.textBaseline = 'top'
      const text = label.length > 34 ? `${label.slice(0, 31)}...` : label
      const metrics = ctx.measureText(text)
      const textY = y + radius + 4 / globalScale
      ctx.fillStyle = 'rgba(5,10,18,0.76)'
      ctx.fillRect(x - metrics.width / 2 - 5 / globalScale, textY - 2 / globalScale, metrics.width + 10 / globalScale, fontSize + 5 / globalScale)
      ctx.fillStyle = 'rgba(255,255,255,0.92)'
      ctx.fillText(text, x, textY)
    }

    ctx.restore()
  }, [hoverNode, nodeColor, selectedNeighborIds, selectedNode, styles.caption, styles.halo])

  const paintNodePointer = useCallback((
    node: NodeObject<GraphNode>,
    color: string,
    ctx: CanvasRenderingContext2D,
  ) => {
    const graphNode = node as GraphNode
    const radius = Math.max(7, Math.min(22, 8 + Math.sqrt((graphNode.degree ?? 0) + 1) * 1.6))
    ctx.fillStyle = color
    ctx.beginPath()
    ctx.arc(graphNode.x ?? 0, graphNode.y ?? 0, radius, 0, Math.PI * 2)
    ctx.fill()
  }, [])

  const updateNodeColor = (kind: string, color: string) => {
    setStyles((current) => ({
      ...current,
      nodeColors: { ...current.nodeColors, [kind]: color },
    }))
  }

  const updateRelationColor = (type: string, color: string) => {
    setStyles((current) => ({
      ...current,
      relationColors: { ...current.relationColors, [type]: color },
    }))
  }

  const exportJson = () => {
    downloadText(
      `${project.name}-knowledge-graph.json`,
      JSON.stringify({ project, schema, graph, queryResult }, null, 2),
      'application/json',
    )
  }

  const exportPng = () => {
    const canvas = canvasWrapRef.current?.querySelector('canvas')
    if (!canvas) return
    const anchor = document.createElement('a')
    anchor.href = canvas.toDataURL('image/png')
    anchor.download = `${project.name}-knowledge-graph.png`
    anchor.click()
  }

  const toggleKind = (kind: string) => {
    setSelectedKinds((current) => (
      current.includes(kind)
        ? current.filter((item) => item !== kind)
        : [...current, kind]
    ))
  }

  const toggleRelation = (relation: string) => {
    setSelectedRelations((current) => (
      current.includes(relation)
        ? current.filter((item) => item !== relation)
        : [...current, relation]
    ))
  }

  const queryRows = queryResult?.rows ?? []

  return (
    <div className="fixed inset-0 z-[80] flex flex-col bg-[#070B12] text-white">
      <div className="h-14 shrink-0 border-b border-white/10 bg-[#0B101A] px-4 flex items-center gap-3">
        <div className="h-9 w-9 rounded-lg bg-cyan-400/12 border border-cyan-300/30 flex items-center justify-center">
          <Network className="w-4 h-4 text-cyan-200" />
        </div>
        <div className="min-w-0">
          <h1 className="text-sm font-semibold truncate">{project.name}</h1>
          <p className="text-[11px] text-white/45 font-mono truncate">{project.rootPath}</p>
        </div>
        <div className="ml-auto flex items-center gap-2">
          <Button variant="ghost" size="sm" className="text-white/70 hover:bg-white/10 hover:text-white" onClick={loadGraph}>
            <RefreshCw className={cn('w-3.5 h-3.5', loading && 'animate-spin')} />
            重新載入
          </Button>
          <Button variant="ghost" size="sm" className="text-white/70 hover:bg-white/10 hover:text-white" onClick={() => graphRef.current?.zoomToFit(650, 72)}>
            <Maximize2 className="w-3.5 h-3.5" />
            Fit
          </Button>
          <button
            type="button"
            onClick={onClose}
            title="關閉"
            className="h-9 w-9 rounded-lg text-white/65 hover:text-white hover:bg-white/10 transition-colors flex items-center justify-center"
          >
            <X className="w-5 h-5" />
          </button>
        </div>
      </div>

      {error && (
        <div className="mx-4 mt-3 shrink-0 rounded-lg border border-red-300/30 bg-red-500/10 px-3 py-2 text-sm text-red-100 flex items-center justify-between">
          <span className="truncate">{error}</span>
          <button type="button" onClick={() => setError(null)} className="text-red-100/70 hover:text-red-50">
            <X className="w-4 h-4" />
          </button>
        </div>
      )}

      <div className="min-h-0 flex-1 grid grid-cols-[288px_minmax(0,1fr)_360px]">
        <aside className="min-h-0 border-r border-white/10 bg-[#0B101A] overflow-y-auto">
          <div className="p-4 space-y-4">
            <section className="space-y-2">
              <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-white/50">
                <Database className="w-3.5 h-3.5" />
                Schema
              </div>
              <div className="grid grid-cols-2 gap-2">
                <div className="rounded-lg border border-white/10 bg-white/[0.04] px-3 py-2">
                  <p className="text-[10px] text-white/45">Nodes</p>
                  <p className="text-lg font-semibold">{schema?.totalNodes ?? graph?.totalNodes ?? 0}</p>
                </div>
                <div className="rounded-lg border border-white/10 bg-white/[0.04] px-3 py-2">
                  <p className="text-[10px] text-white/45">Edges</p>
                  <p className="text-lg font-semibold">{schema?.totalEdges ?? graph?.loadedEdges ?? 0}</p>
                </div>
              </div>
            </section>

            <section className="space-y-2">
              <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-white/50">
                <Filter className="w-3.5 h-3.5" />
                Nodes
              </div>
              <button
                type="button"
                onClick={() => setSelectedKinds([])}
                className={cn(
                  'w-full rounded-lg px-3 py-1.5 text-left text-xs transition-colors',
                  selectedKinds.length === 0 ? 'bg-cyan-400/15 text-cyan-100' : 'text-white/55 hover:bg-white/7',
                )}
              >
                全部
              </button>
              <div className="space-y-1.5">
                {schema?.nodeKinds.map((facet, index) => {
                  const color = styles.nodeColors[facet.name] ?? NODE_PALETTE[index % NODE_PALETTE.length]
                  const active = selectedKinds.length === 0 || selectedKinds.includes(facet.name)
                  return (
                    <div key={facet.name} className="flex items-center gap-2 rounded-lg border border-white/10 bg-white/[0.03] px-2 py-1.5">
                      <button
                        type="button"
                        onClick={() => toggleKind(facet.name)}
                        className={cn('h-3 w-3 rounded-full border border-white/50', active ? 'opacity-100' : 'opacity-30')}
                        style={{ backgroundColor: color }}
                        title={facet.name}
                      />
                      <button
                        type="button"
                        onClick={() => toggleKind(facet.name)}
                        className={cn('min-w-0 flex-1 text-left text-xs truncate', active ? 'text-white/85' : 'text-white/35')}
                      >
                        {facet.name}
                      </button>
                      <span className="text-[10px] text-white/35">{facet.count}</span>
                      <input
                        type="color"
                        value={color}
                        onChange={(event) => updateNodeColor(facet.name, event.target.value)}
                        className="h-5 w-6 rounded border-0 bg-transparent p-0"
                        title={`${facet.name} color`}
                      />
                    </div>
                  )
                })}
              </div>
            </section>

            <section className="space-y-2">
              <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-white/50">
                <ChevronDown className="w-3.5 h-3.5" />
                Relations
              </div>
              <button
                type="button"
                onClick={() => setSelectedRelations([])}
                className={cn(
                  'w-full rounded-lg px-3 py-1.5 text-left text-xs transition-colors',
                  selectedRelations.length === 0 ? 'bg-emerald-400/15 text-emerald-100' : 'text-white/55 hover:bg-white/7',
                )}
              >
                全部
              </button>
              <div className="space-y-1.5">
                {schema?.relationshipTypes.map((facet, index) => {
                  const color = styles.relationColors[facet.name] ?? REL_PALETTE[index % REL_PALETTE.length]
                  const active = selectedRelations.length === 0 || selectedRelations.includes(facet.name)
                  return (
                    <div key={facet.name} className="flex items-center gap-2 rounded-lg border border-white/10 bg-white/[0.03] px-2 py-1.5">
                      <button
                        type="button"
                        onClick={() => toggleRelation(facet.name)}
                        className={cn('h-2.5 w-5 rounded-full border border-white/40', active ? 'opacity-100' : 'opacity-30')}
                        style={{ backgroundColor: color }}
                        title={facet.name}
                      />
                      <button
                        type="button"
                        onClick={() => toggleRelation(facet.name)}
                        className={cn('min-w-0 flex-1 text-left text-xs truncate', active ? 'text-white/85' : 'text-white/35')}
                      >
                        {facet.name}
                      </button>
                      <span className="text-[10px] text-white/35">{facet.count}</span>
                      <input
                        type="color"
                        value={color}
                        onChange={(event) => updateRelationColor(facet.name, event.target.value)}
                        className="h-5 w-6 rounded border-0 bg-transparent p-0"
                        title={`${facet.name} color`}
                      />
                    </div>
                  )
                })}
              </div>
            </section>

            <section className="space-y-2">
              <div className="text-xs font-semibold uppercase tracking-wide text-white/50">Style</div>
              <label className="block text-[11px] text-white/45">
                Caption
                <select
                  value={styles.caption}
                  onChange={(event) => setStyles((current) => ({ ...current, caption: event.target.value as GraphStyleSettings['caption'] }))}
                  className="mt-1 w-full rounded-lg border border-white/10 bg-[#111827] px-2 py-1.5 text-xs text-white focus:outline-none focus:ring-2 focus:ring-cyan-300/30"
                >
                  <option value="name">Name</option>
                  <option value="signature">Signature</option>
                  <option value="kind">Kind</option>
                </select>
              </label>
              <label className="flex items-center justify-between rounded-lg border border-white/10 bg-white/[0.03] px-3 py-2 text-xs text-white/70">
                Halo
                <input
                  type="checkbox"
                  checked={styles.halo}
                  onChange={(event) => setStyles((current) => ({ ...current, halo: event.target.checked }))}
                  className="h-4 w-4 accent-cyan-300"
                />
              </label>
            </section>
          </div>
        </aside>

        <main className="min-w-0 min-h-0 flex flex-col">
          <div className="h-12 shrink-0 border-b border-white/10 bg-[#080D15] px-3 flex items-center gap-2">
            <div className="relative w-72">
              <Search className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-white/35" />
              <input
                value={searchText}
                onChange={(event) => setSearchText(event.target.value)}
                onKeyDown={(event) => event.key === 'Enter' && searchNode()}
                placeholder="搜尋節點"
                className="h-8 w-full rounded-lg border border-white/10 bg-white/[0.05] pl-8 pr-3 text-xs text-white placeholder:text-white/30 focus:outline-none focus:ring-2 focus:ring-cyan-300/30"
              />
            </div>
            <Button variant="ghost" size="sm" className="text-white/70 hover:bg-white/10 hover:text-white" onClick={searchNode}>
              <Search className="w-3.5 h-3.5" />
            </Button>
            <select
              value={limit}
              onChange={(event) => setLimit(Number(event.target.value))}
              className="h-8 rounded-lg border border-white/10 bg-white/[0.05] px-2 text-xs text-white focus:outline-none focus:ring-2 focus:ring-cyan-300/30"
            >
              {LIMITS.map((value) => (
                <option key={value} value={value}>{value} nodes</option>
              ))}
            </select>
            <Button variant="ghost" size="sm" className="text-white/70 hover:bg-white/10 hover:text-white" onClick={loadGraph}>
              <Filter className="w-3.5 h-3.5" />
              套用
            </Button>
            <div className="ml-auto flex items-center gap-1">
              <Button variant="ghost" size="sm" className="text-white/70 hover:bg-white/10 hover:text-white" onClick={() => void expandNodes('all')} disabled={!selectedNode}>
                <Expand className="w-3.5 h-3.5" />
                展開
              </Button>
              <Button variant="ghost" size="sm" className="text-white/70 hover:bg-white/10 hover:text-white" onClick={exportPng}>
                <Download className="w-3.5 h-3.5" />
                PNG
              </Button>
              <Button variant="ghost" size="sm" className="text-white/70 hover:bg-white/10 hover:text-white" onClick={exportJson}>
                <FileJson className="w-3.5 h-3.5" />
                JSON
              </Button>
            </div>
          </div>

          <div ref={canvasWrapRef} className="relative min-h-0 flex-1 overflow-hidden bg-[#05080D]">
            <ForceGraph2D<GraphNode, GraphLink>
              ref={graphRef}
              graphData={graphData}
              nodeId="id"
              linkSource="source"
              linkTarget="target"
              width={canvasSize.width}
              height={canvasSize.height}
              backgroundColor="#05080D"
              warmupTicks={80}
              cooldownTicks={180}
              d3AlphaDecay={0.018}
              d3VelocityDecay={0.26}
              nodeRelSize={5}
              nodeCanvasObject={drawNode}
              nodePointerAreaPaint={paintNodePointer}
              linkColor={(link) => {
                const graphLink = link as GraphLink
                const source = endpointId(graphLink.source)
                const target = endpointId(graphLink.target)
                const dimmed = !!selectedNode && !selectedNeighborIds.has(source) && !selectedNeighborIds.has(target)
                return hexToRgba(linkColor(graphLink), dimmed ? 0.12 : 0.56)
              }}
              linkWidth={(link) => {
                const graphLink = link as GraphLink
                const source = endpointId(graphLink.source)
                const target = endpointId(graphLink.target)
                return selectedNode && (source === selectedNode.id || target === selectedNode.id) ? 1.9 : 0.8
              }}
              linkDirectionalArrowLength={3.5}
              linkDirectionalArrowRelPos={0.92}
              linkDirectionalParticles={(link) => {
                const graphLink = link as GraphLink
                const source = endpointId(graphLink.source)
                const target = endpointId(graphLink.target)
                return selectedNode && (source === selectedNode.id || target === selectedNode.id) ? 2 : 0
              }}
              linkDirectionalParticleWidth={1.8}
              linkDirectionalParticleSpeed={0.004}
              linkDirectionalParticleColor={(link) => linkColor(link as GraphLink)}
              onNodeClick={(node) => {
                setSelectedNode(node as GraphNode)
                setSelectedLink(null)
                setContextMenu(null)
              }}
              onNodeRightClick={(node, event) => {
                event.preventDefault()
                setSelectedNode(node as GraphNode)
                setSelectedLink(null)
                setContextMenu({ x: event.clientX, y: event.clientY, node: node as GraphNode })
              }}
              onNodeHover={(node) => setHoverNode(node as GraphNode | null)}
              onLinkClick={(link) => {
                setSelectedLink(link as GraphLink)
                setSelectedNode(null)
                setContextMenu(null)
              }}
              onBackgroundClick={() => {
                setSelectedNode(null)
                setSelectedLink(null)
                setContextMenu(null)
              }}
              showPointerCursor
            />

            {loading && (
              <div className="absolute inset-0 pointer-events-none flex items-center justify-center bg-[#05080D]/35">
                <div className="rounded-lg border border-white/10 bg-black/50 px-4 py-2 text-sm text-white/80 flex items-center gap-2">
                  <Loader2 className="w-4 h-4 animate-spin text-cyan-200" />
                  載入中
                </div>
              </div>
            )}

            <div className="absolute left-3 bottom-3 rounded-lg border border-white/10 bg-black/45 px-3 py-2 text-[11px] text-white/60 backdrop-blur">
              {graph?.loadedNodes ?? 0} / {graph?.totalNodes ?? 0} nodes · {graph?.loadedEdges ?? 0} edges
            </div>
          </div>
        </main>

        <aside className="min-h-0 border-l border-white/10 bg-[#0B101A] flex flex-col">
          <div className="shrink-0 border-b border-white/10 p-3">
            <div className="flex rounded-lg border border-white/10 bg-white/[0.04] p-0.5">
              {VIEW_TABS.map(({ value, icon: Icon, label }) => (
                <button
                  key={value}
                  type="button"
                  onClick={() => setViewMode(value)}
                  className={cn(
                    'flex-1 rounded-md px-2 py-1.5 text-xs font-medium transition-colors flex items-center justify-center gap-1.5',
                    viewMode === value ? 'bg-cyan-300 text-[#061018]' : 'text-white/55 hover:text-white hover:bg-white/7',
                  )}
                >
                  <Icon className="w-3.5 h-3.5" />
                  {label}
                </button>
              ))}
            </div>
          </div>

          <div className="min-h-0 flex-1 overflow-y-auto">
            {viewMode === 'graph' && (
              <div className="p-4 space-y-4">
                <section className="space-y-2">
                  <p className="text-xs font-semibold uppercase tracking-wide text-white/50">Inspector</p>
                  {selectedNode ? (
                    <div className="space-y-3">
                      <div>
                        <p className="text-sm font-semibold text-white break-words">{selectedNode.name}</p>
                        <p className="text-xs text-cyan-200 mt-1">{selectedNode.kind}</p>
                      </div>
                      {selectedNode.signature && (
                        <code className="block rounded-lg border border-white/10 bg-white/[0.04] px-3 py-2 text-[11px] leading-relaxed text-white/70 break-words">
                          {selectedNode.signature}
                        </code>
                      )}
                      {selectedNode.filePath && (
                        <p className="text-[11px] text-white/45 font-mono break-words">
                          {selectedNode.filePath}{selectedNode.startLine ? `:${selectedNode.startLine}` : ''}
                        </p>
                      )}
                      <div className="grid grid-cols-2 gap-2">
                        <Button variant="ghost" size="sm" className="text-white/70 hover:bg-white/10 hover:text-white" onClick={() => void expandNodes('all')}>
                          全部鄰居
                        </Button>
                        <Button variant="ghost" size="sm" className="text-white/70 hover:bg-white/10 hover:text-white" onClick={() => void expandNodes('same-file')}>
                          同檔案
                        </Button>
                        <Button variant="ghost" size="sm" className="text-white/70 hover:bg-white/10 hover:text-white" onClick={() => void expandNodes('callers')}>
                          呼叫者
                        </Button>
                        <Button variant="ghost" size="sm" className="text-white/70 hover:bg-white/10 hover:text-white" onClick={() => void expandNodes('callees')}>
                          被呼叫
                        </Button>
                      </div>
                      <div className="space-y-1.5">
                        {Object.entries(selectedNode.properties).slice(0, 24).map(([key, value]) => (
                          <div key={key} className="rounded-lg border border-white/10 bg-white/[0.03] px-2.5 py-1.5">
                            <p className="text-[10px] text-white/35">{key}</p>
                            <p className="text-[11px] text-white/70 break-words">{renderValue(value)}</p>
                          </div>
                        ))}
                      </div>
                    </div>
                  ) : selectedLink ? (
                    <div className="space-y-2">
                      <p className="text-sm font-semibold text-white">{selectedLink.type}</p>
                      <p className="text-[11px] text-white/45 font-mono break-all">
                        {endpointId(selectedLink.source)} → {endpointId(selectedLink.target)}
                      </p>
                      {Object.entries(selectedLink.properties).map(([key, value]) => (
                        <div key={key} className="rounded-lg border border-white/10 bg-white/[0.03] px-2.5 py-1.5">
                          <p className="text-[10px] text-white/35">{key}</p>
                          <p className="text-[11px] text-white/70 break-words">{renderValue(value)}</p>
                        </div>
                      ))}
                    </div>
                  ) : (
                    <p className="text-sm text-white/45">未選取</p>
                  )}
                </section>

                <section className="space-y-2">
                  <p className="text-xs font-semibold uppercase tracking-wide text-white/50">Cypher</p>
                  <textarea
                    value={queryText}
                    onChange={(event) => setQueryText(event.target.value)}
                    spellCheck={false}
                    className="h-40 w-full resize-none rounded-lg border border-white/10 bg-[#05080D] px-3 py-2 font-mono text-xs leading-relaxed text-white/80 placeholder:text-white/30 focus:outline-none focus:ring-2 focus:ring-cyan-300/30"
                  />
                  <Button variant="primary" size="sm" className="w-full" onClick={runQuery} isLoading={querying} leftIcon={<Play className="w-3.5 h-3.5" />}>
                    執行 read-only 查詢
                  </Button>
                </section>
              </div>
            )}

            {viewMode === 'table' && (
              <div className="p-3">
                <div className="overflow-auto rounded-lg border border-white/10">
                  <table className="min-w-full text-left text-xs">
                    <thead className="bg-white/[0.06] text-white/50">
                      <tr>
                        {(queryResult?.columns.length ? queryResult.columns : ['result']).map((column) => (
                          <th key={column} className="px-3 py-2 font-semibold">{column}</th>
                        ))}
                      </tr>
                    </thead>
                    <tbody>
                      {queryRows.length === 0 ? (
                        <tr>
                          <td className="px-3 py-8 text-center text-white/35" colSpan={queryResult?.columns.length || 1}>
                            No rows
                          </td>
                        </tr>
                      ) : (
                        queryRows.map((row, rowIndex) => (
                          <tr key={rowIndex} className="border-t border-white/10">
                            {queryResult?.columns.map((column) => (
                              <td key={column} className="max-w-[220px] px-3 py-2 text-white/70 align-top">
                                <span className="line-clamp-4 break-words">{renderValue(row[column])}</span>
                              </td>
                            ))}
                          </tr>
                        ))
                      )}
                    </tbody>
                  </table>
                </div>
              </div>
            )}

            {viewMode === 'raw' && (
              <pre className="m-3 overflow-auto rounded-lg border border-white/10 bg-[#05080D] p-3 text-[11px] leading-relaxed text-white/65">
                {JSON.stringify(queryResult ?? { graph }, null, 2)}
              </pre>
            )}
          </div>
        </aside>
      </div>

      {contextMenu && (
        <div
          className="fixed z-[90] min-w-[156px] rounded-lg border border-white/10 bg-[#101827] py-1 shadow-2xl"
          style={{ top: contextMenu.y, left: contextMenu.x }}
          onClick={(event) => event.stopPropagation()}
        >
          {[
            ['all', '展開全部鄰居'],
            ['callers', '展開呼叫者'],
            ['callees', '展開被呼叫'],
            ['same-file', '展開同檔案'],
          ].map(([mode, label]) => (
            <button
              key={mode}
              type="button"
              className="block w-full px-3 py-2 text-left text-sm text-white/75 hover:bg-white/10 hover:text-white"
              onClick={() => void expandNodes(mode as ExpandMode, contextMenu.node)}
            >
              {label}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}
