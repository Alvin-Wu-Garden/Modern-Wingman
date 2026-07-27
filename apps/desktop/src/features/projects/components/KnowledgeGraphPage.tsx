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
  caption: 'name' | 'role' | 'kind'
  halo: boolean
}

interface GraphContextMenu {
  x: number
  y: number
  node: GraphNode
}

interface CanvasTheme {
  background: string
  labelBackground: string
  labelText: string
  nodeStroke: string
  isDark: boolean
}

type GraphOperation = 'load' | 'query'

const DEFAULT_LIMIT = 1000
// 後端目前最多只接受 5,000 個節點，前端選項必須保持相同上限。
const LIMITS = [1000, 2000, 5000]
const NODE_PALETTE = ['#52D1DC', '#7C8CFF', '#30DAA2', '#FFC94F', '#FF7A90', '#B48CFF', '#FF9F43', '#8FE388']
const REL_PALETTE = ['#67E8F9', '#A7F3D0', '#FDE68A', '#F9A8D4', '#C4B5FD', '#FDBA74']
const VIEW_TABS: { value: ViewMode; icon: LucideIcon; label: string }[] = [
  { value: 'graph', icon: Network, label: 'Graph' },
  { value: 'table', icon: Table2, label: 'Table' },
  { value: 'raw', icon: Braces, label: 'Raw' },
]

const DEFAULT_QUERY = `MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion})
OPTIONAL MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion})-[r]->(m:GraphEntity {projectId: $projectId, graphVersion: $graphVersion})
RETURN n, r, m
LIMIT $limit`

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
    const caption = saved.caption === 'name' ||
      saved.caption === 'role' ||
      saved.caption === 'kind'
      ? saved.caption
      : fallback.caption
    return {
      nodeColors: { ...fallback.nodeColors, ...(saved.nodeColors ?? {}) },
      relationColors: { ...fallback.relationColors, ...(saved.relationColors ?? {}) },
      caption,
      halo: saved.halo ?? fallback.halo,
    }
  } catch {
    return fallback
  }
}

function saveStyles(projectId: string, styles: GraphStyleSettings) {
  localStorage.setItem(styleKey(projectId), JSON.stringify(styles))
}

function readCanvasTheme(): CanvasTheme {
  const root = document.documentElement
  const computed = getComputedStyle(root)
  const readToken = (name: string, fallback: string) => (
    computed.getPropertyValue(name).trim() || fallback
  )

  return {
    background: readToken('--color-surface-alt', '#EEF0F0'),
    labelBackground: readToken('--color-surface', '#FFFFFF'),
    labelText: readToken('--color-ink', '#0C0E1F'),
    nodeStroke: readToken('--color-ink-secondary', '#494A57'),
    isDark: root.dataset.theme === 'dark' || root.dataset.theme === 'glass',
  }
}

function mergeGraphData(base: CodeGraphVisualData | null, incoming: CodeGraphVisualData): CodeGraphVisualData {
  if (!base) return incoming

  const nodes = new Map(base.nodes.map((node) => [node.id, node]))
  const edges = new Map(base.edges.map((edge) => [edge.id, edge]))
  incoming.nodes.forEach((node) => {
    const existing = nodes.get(node.id)
    nodes.set(node.id, existing
      ? { ...node, degree: Math.max(existing.degree, node.degree) }
      : node)
  })
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

function cleanGraphData(data: CodeGraphVisualData | null): CodeGraphVisualData | null {
  if (!data) return null

  return {
    nodes: data.nodes.map((node) => ({
      id: node.id,
      kind: node.kind,
      role: node.role,
      name: node.name,
      filePath: node.filePath,
      startLine: node.startLine,
      endLine: node.endLine,
      language: node.language,
      degree: node.degree,
      properties: node.properties,
    })),
    edges: data.edges.map((edge) => ({
      id: edge.id,
      // ForceGraph 會把 source/target 原地換成節點物件；匯出前固定轉回 API 的 id。
      source: endpointId(edge.source),
      target: endpointId(edge.target),
      type: edge.type,
      properties: edge.properties,
    })),
    totalNodes: data.totalNodes,
    loadedNodes: data.loadedNodes,
    loadedEdges: data.loadedEdges,
    hasMore: data.hasMore,
  }
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

function darkenHex(hex: string, factor: number) {
  const normalized = hex.replace('#', '')
  const full = normalized.length === 3
    ? normalized.split('').map((char) => `${char}${char}`).join('')
    : normalized
  const value = Number.parseInt(full, 16)
  const channel = (shift: number) => Math.round(((value >> shift) & 255) * factor)
  return `rgb(${channel(16)}, ${channel(8)}, ${channel(0)})`
}

function renderValue(value: unknown) {
  if (value === null || value === undefined) return ''
  if (typeof value === 'string') return value
  if (typeof value === 'number' || typeof value === 'boolean') return String(value)
  return JSON.stringify(value)
}

function linkLayoutDistance(link: GraphLink, densityScale: number) {
  const base = link.type === 'ROUTES_TO' || link.type === 'HANDLES'
    ? 150
    : link.type === 'CALLS'
      ? 220
      : link.type === 'READS' || link.type === 'WRITES'
        ? 190
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
  const graphRequestGenerationRef = useRef(0)
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
  const [canvasTheme, setCanvasTheme] = useState<CanvasTheme>(() => readCanvasTheme())
  const [activeGraphOperation, setActiveGraphOperation] = useState<GraphOperation | null>(null)
  const [error, setError] = useState<string | null>(null)
  const loading = activeGraphOperation === 'load'
  const querying = activeGraphOperation === 'query'

  const loadGraph = useCallback(async () => {
    // 篩選、Cypher、展開共用同一代數，避免不同操作的舊回應覆蓋最新畫面。
    const requestGeneration = ++graphRequestGenerationRef.current
    setActiveGraphOperation('load')
    setError(null)
    try {
      const nextGraph = await getProjectGraph(project.id, {
        limit,
        kinds: selectedKinds,
        relations: selectedRelations,
      })
      if (requestGeneration !== graphRequestGenerationRef.current) return
      setGraph(nextGraph)
      setQueryResult(null)
      setSelectedNode(null)
      setSelectedLink(null)
      setContextMenu(null)
      setViewMode('graph')
      setTimeout(() => graphRef.current?.zoomToFit(700, 64), 120)
    } catch (err) {
      if (requestGeneration !== graphRequestGenerationRef.current) return
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      if (requestGeneration === graphRequestGenerationRef.current) setActiveGraphOperation(null)
    }
  }, [limit, project.id, selectedKinds, selectedRelations])

  useEffect(() => {
    const node = canvasWrapRef.current
    if (!node) return
    const observer = new ResizeObserver(([entry]) => {
      if (!entry) return
      setCanvasSize({
        // Canvas 尺寸不得大於父層，否則 Windows 最小視窗寬度時會被左右裁切。
        width: Math.max(1, Math.floor(entry.contentRect.width)),
        height: Math.max(1, Math.floor(entry.contentRect.height)),
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
    // AppProviders 會透過 html[data-theme] 切換主題；Canvas 不會自動解析
    // Tailwind class，因此在屬性變更時重新讀取同一組全域設計 token。
    const html = document.documentElement
    const syncCanvasTheme = () => setCanvasTheme(readCanvasTheme())
    const observer = new MutationObserver(syncCanvasTheme)
    observer.observe(html, { attributes: true, attributeFilter: ['data-theme'] })
    syncCanvasTheme()
    return () => observer.disconnect()
  }, [])

  useEffect(() => {
    const load = async () => {
      setError(null)
      try {
        const nextSchema = await getProjectGraphSchema(project.id)
        setSchema(nextSchema)
        const nextStyles = loadStyles(project.id, nextSchema)
        setStyles(nextStyles)
      } catch (err) {
        setError(err instanceof Error ? err.message : String(err))
      }
    }
    void load()
  }, [project.id])

  useEffect(() => {
    // 種類、關係與載入上限都屬於即時篩選條件；任一條件改變便重抓圖譜，
    // 避免 UI 顯示已選取條件，Canvas 卻仍停留在舊資料。
    void loadGraph()
    return () => {
      // 條件切換或頁面卸載時，讓尚未完成的舊回應失效。
      graphRequestGenerationRef.current += 1
    }
  }, [loadGraph])

  useEffect(() => {
    if (!schema) return
    saveStyles(project.id, styles)
  }, [project.id, schema, styles])

  const graphData = useMemo(() => ({
    // D3 會在節點加入座標並將 edge 端點換成物件，因此提供獨立 view-model，
    // 確保 React state 仍維持後端 API 的乾淨資料結構。
    nodes: (graph?.nodes ?? []).map((node) => ({ ...node })) as GraphNode[],
    links: (graph?.edges ?? []).map((edge) => ({
      ...edge,
      source: endpointId(edge.source),
      target: endpointId(edge.target),
    })) as GraphLink[],
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

  const runQuery = useCallback(async () => {
    const requestGeneration = ++graphRequestGenerationRef.current
    setActiveGraphOperation('query')
    setError(null)
    try {
      const result = await queryProjectGraph(project.id, queryText, limit)
      if (requestGeneration !== graphRequestGenerationRef.current) return
      setQueryResult(result)
      // 手動 Cypher 是一個新的檢視結果，不能殘留先前圖譜；只有「展開」才合併節點。
      setGraph(result.graph)
      setSelectedNode(null)
      setSelectedLink(null)
      setContextMenu(null)
      setViewMode(result.graph.nodes.length > 0 ? 'graph' : 'table')
      setTimeout(() => graphRef.current?.zoomToFit(700, 64), 120)
    } catch (err) {
      if (requestGeneration !== graphRequestGenerationRef.current) return
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      if (requestGeneration === graphRequestGenerationRef.current) setActiveGraphOperation(null)
    }
  }, [limit, project.id, queryText])

  const expandNodes = useCallback(async (mode: ExpandMode, node = selectedNode) => {
    if (!node) return
    const requestGeneration = ++graphRequestGenerationRef.current
    setActiveGraphOperation('load')
    setError(null)
    try {
      const nextGraph = await expandProjectGraphNeighbors(project.id, [node.id], {
        depth: 1,
        limit,
        mode,
      })
      if (requestGeneration !== graphRequestGenerationRef.current) return
      setGraph((current) => mergeGraphData(current, nextGraph))
      setQueryResult(null)
      setContextMenu(null)
      setViewMode('graph')
      setTimeout(() => graphRef.current?.zoomToFit(500, 64), 80)
    } catch (err) {
      if (requestGeneration !== graphRequestGenerationRef.current) return
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      if (requestGeneration === graphRequestGenerationRef.current) setActiveGraphOperation(null)
    }
  }, [limit, project.id, selectedNode])

  const searchNode = useCallback(() => {
    const keyword = searchText.trim().toLowerCase()
    if (!keyword) return
    // 搜尋 ForceGraph 的 view-model，才能取得模擬完成後的 x/y 並正確置中。
    const found = graphData.nodes.find((node) => (
      node.name.toLowerCase().includes(keyword) ||
      node.id.toLowerCase().includes(keyword) ||
      node.role.toLowerCase().includes(keyword) ||
      (node.filePath?.toLowerCase().includes(keyword) ?? false)
    ))
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
  }, [graphData.nodes, searchText])

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
    ctx.strokeStyle = selected ? canvasTheme.labelText : canvasTheme.nodeStroke
    ctx.stroke()

    const labelZoomThreshold = graphData.nodes.length > 2500
      ? 2.1
      : graphData.nodes.length > 900
        ? 1.55
        : 1.15
    const shouldDrawLabel = selected || hovered || globalScale > labelZoomThreshold
    if (shouldDrawLabel) {
      const label = styles.caption === 'role'
        ? graphNode.role
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
      ctx.globalAlpha = dimmed ? 0.2 : 0.92
      ctx.fillStyle = canvasTheme.labelBackground
      ctx.fillRect(x - metrics.width / 2 - 5 / globalScale, textY - 2 / globalScale, metrics.width + 10 / globalScale, fontSize + 5 / globalScale)
      ctx.globalAlpha = dimmed ? 0.2 : 1
      ctx.fillStyle = canvasTheme.labelText
      ctx.fillText(text, x, textY)
    }

    ctx.restore()
  }, [canvasTheme, graphData.nodes.length, hoverNode, nodeColor, selectedNeighborIds, selectedNode, styles.caption, styles.halo])

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
      JSON.stringify({ project, schema, graph: cleanGraphData(graph) }, null, 2),
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
    <div
      className="fixed inset-0 z-[80] flex flex-col bg-surface-alt text-ink"
      data-knowledge-graph-page
    >
      <div className="h-14 shrink-0 border-b border-border bg-surface px-4 flex items-center gap-3" data-glass-panel>
        <div className="h-9 w-9 rounded-lg bg-brand/12 border border-brand/30 flex items-center justify-center">
          <Network className="w-4 h-4 text-brand" />
        </div>
        <div className="min-w-0">
          <h1 className="text-sm font-semibold truncate">{project.name}</h1>
          <p className="text-[11px] text-ink-subtle font-mono truncate">{project.rootPath}</p>
        </div>
        <div className="ml-auto flex items-center gap-2">
          <Button variant="ghost" size="sm" className="whitespace-nowrap" onClick={loadGraph}>
            <RefreshCw className={cn('w-3.5 h-3.5', loading && 'animate-spin')} />
            重新載入
          </Button>
          <Button variant="ghost" size="sm" className="whitespace-nowrap" onClick={() => graphRef.current?.zoomToFit(650, 72)}>
            <Maximize2 className="w-3.5 h-3.5" />
            Fit
          </Button>
          <button
            type="button"
            onClick={onClose}
            title="關閉"
            className="h-9 w-9 rounded-lg text-ink-secondary hover:text-ink hover:bg-surface-alt transition-colors flex items-center justify-center"
          >
            <X className="w-5 h-5" />
          </button>
        </div>
      </div>

      {error && (
        <div className="mx-4 mt-3 shrink-0 rounded-lg border border-error/30 bg-error/10 px-3 py-2 text-sm text-error flex items-center justify-between">
          <span className="truncate">{error}</span>
          <button type="button" onClick={() => setError(null)} className="text-error/70 hover:text-error">
            <X className="w-4 h-4" />
          </button>
        </div>
      )}

      <div className="min-h-0 flex-1 grid grid-cols-[clamp(220px,20vw,288px)_minmax(0,1fr)_clamp(280px,25vw,360px)]">
        <aside className="min-h-0 border-r border-border bg-surface overflow-y-auto">
          <div className="p-4 space-y-4">
            <section className="space-y-2">
              <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-ink-secondary">
                <Database className="w-3.5 h-3.5" />
                Schema
              </div>
              <div className="grid grid-cols-2 gap-2">
                <div className="rounded-lg border border-border bg-surface-alt px-3 py-2">
                  <p className="text-[10px] text-ink-subtle">Nodes</p>
                  <p className="text-lg font-semibold">{schema?.totalNodes ?? graph?.totalNodes ?? 0}</p>
                </div>
                <div className="rounded-lg border border-border bg-surface-alt px-3 py-2">
                  <p className="text-[10px] text-ink-subtle">Edges</p>
                  <p className="text-lg font-semibold">{schema?.totalEdges ?? graph?.loadedEdges ?? 0}</p>
                </div>
              </div>
            </section>

            <section className="space-y-2">
              <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-ink-secondary">
                <Filter className="w-3.5 h-3.5" />
                Nodes
              </div>
              <button
                type="button"
                onClick={() => setSelectedKinds([])}
                className={cn(
                  'w-full rounded-lg px-3 py-1.5 text-left text-xs transition-colors',
                  selectedKinds.length === 0 ? 'bg-brand/15 text-brand' : 'text-ink-secondary hover:bg-surface-alt',
                )}
              >
                全部
              </button>
              <div className="space-y-1.5">
                {schema?.nodeKinds.map((facet, index) => {
                  const color = styles.nodeColors[facet.name] ?? NODE_PALETTE[index % NODE_PALETTE.length]
                  const active = selectedKinds.length === 0 || selectedKinds.includes(facet.name)
                  return (
                    <div key={facet.name} className="flex items-center gap-2 rounded-lg border border-border bg-surface-alt px-2 py-1.5">
                      <button
                        type="button"
                        onClick={() => toggleKind(facet.name)}
                        aria-pressed={active}
                        className={cn(
                          'min-w-0 flex flex-1 items-center gap-2 rounded px-1 py-0.5 text-left text-xs transition-opacity',
                          active ? 'text-ink opacity-100' : 'text-ink-subtle opacity-55',
                        )}
                      >
                        <span
                          aria-hidden="true"
                          className="h-3 w-3 shrink-0 rounded-full border border-border"
                          style={{ backgroundColor: color }}
                        />
                        <span className="min-w-0 flex-1 truncate">{facet.name}</span>
                        <span className="text-[10px] text-ink-subtle">{facet.count}</span>
                      </button>
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
              <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-ink-secondary">
                <ChevronDown className="w-3.5 h-3.5" />
                Relations
              </div>
              <button
                type="button"
                onClick={() => setSelectedRelations([])}
                className={cn(
                  'w-full rounded-lg px-3 py-1.5 text-left text-xs transition-colors',
                  selectedRelations.length === 0 ? 'bg-brand-green/15 text-brand-green' : 'text-ink-secondary hover:bg-surface-alt',
                )}
              >
                全部
              </button>
              <div className="space-y-1.5">
                {schema?.relationshipTypes.map((facet, index) => {
                  const color = styles.relationColors[facet.name] ?? REL_PALETTE[index % REL_PALETTE.length]
                  const active = selectedRelations.length === 0 || selectedRelations.includes(facet.name)
                  return (
                    <div key={facet.name} className="flex items-center gap-2 rounded-lg border border-border bg-surface-alt px-2 py-1.5">
                      <button
                        type="button"
                        onClick={() => toggleRelation(facet.name)}
                        aria-pressed={active}
                        className={cn(
                          'min-w-0 flex flex-1 items-center gap-2 rounded px-1 py-0.5 text-left text-xs transition-opacity',
                          active ? 'text-ink opacity-100' : 'text-ink-subtle opacity-55',
                        )}
                      >
                        <span
                          aria-hidden="true"
                          className="h-2.5 w-5 shrink-0 rounded-full border border-border"
                          style={{ backgroundColor: color }}
                        />
                        <span className="min-w-0 flex-1 truncate">{facet.name}</span>
                        <span className="text-[10px] text-ink-subtle">{facet.count}</span>
                      </button>
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
              <div className="text-xs font-semibold uppercase tracking-wide text-ink-secondary">Style</div>
              <label className="block text-[11px] text-ink-secondary">
                Caption
                <select
                  value={styles.caption}
                  onChange={(event) => setStyles((current) => ({ ...current, caption: event.target.value as GraphStyleSettings['caption'] }))}
                  className="mt-1 w-full rounded-lg border border-border bg-input-bg px-2 py-1.5 text-xs text-ink focus:outline-none focus:ring-2 focus:ring-brand/30"
                >
                  <option value="name">Name</option>
                  <option value="role">Role</option>
                  <option value="kind">Kind</option>
                </select>
              </label>
              <label className="flex items-center justify-between rounded-lg border border-border bg-surface-alt px-3 py-2 text-xs text-ink-secondary">
                Halo
                <input
                  type="checkbox"
                  checked={styles.halo}
                  onChange={(event) => setStyles((current) => ({ ...current, halo: event.target.checked }))}
                  className="h-4 w-4 accent-brand"
                />
              </label>
            </section>
          </div>
        </aside>

        <main className="min-w-0 min-h-0 flex flex-col">
          <div className="min-h-12 shrink-0 overflow-x-auto border-b border-border bg-surface-alt px-3 py-2 flex items-center gap-2">
            <div className="relative min-w-40 max-w-72 flex-1">
              <Search className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-ink-subtle" />
              <input
                value={searchText}
                onChange={(event) => setSearchText(event.target.value)}
                onKeyDown={(event) => event.key === 'Enter' && searchNode()}
                placeholder="搜尋節點"
                className="h-8 w-full rounded-lg border border-border bg-input-bg pl-8 pr-3 text-xs text-ink placeholder:text-ink-subtle focus:outline-none focus:ring-2 focus:ring-brand/30"
              />
            </div>
            <Button variant="ghost" size="sm" className="shrink-0" onClick={searchNode} title="搜尋">
              <Search className="w-3.5 h-3.5" />
            </Button>
            <select
              value={limit}
              onChange={(event) => setLimit(Number(event.target.value))}
              className="h-8 shrink-0 rounded-lg border border-border bg-input-bg px-2 text-xs text-ink focus:outline-none focus:ring-2 focus:ring-brand/30"
            >
              {LIMITS.map((value) => (
                <option key={value} value={value}>{value} nodes</option>
              ))}
            </select>
            <div className="ml-auto flex shrink-0 items-center gap-1">
              <Button variant="ghost" size="sm" className="whitespace-nowrap" onClick={() => void expandNodes('all')} disabled={!selectedNode}>
                <Expand className="w-3.5 h-3.5" />
                展開
              </Button>
              <Button variant="ghost" size="sm" className="whitespace-nowrap" onClick={exportPng}>
                <Download className="w-3.5 h-3.5" />
                PNG
              </Button>
              <Button variant="ghost" size="sm" className="whitespace-nowrap" onClick={exportJson}>
                <FileJson className="w-3.5 h-3.5" />
                JSON
              </Button>
            </div>
          </div>

          <div ref={canvasWrapRef} className="relative min-h-0 flex-1 overflow-hidden bg-surface-alt">
            <ForceGraph2D<GraphNode, GraphLink>
              ref={graphRef}
              graphData={graphData}
              nodeId="id"
              linkSource="source"
              linkTarget="target"
              width={canvasSize.width}
              height={canvasSize.height}
              backgroundColor={canvasTheme.background}
              warmupTicks={80}
              cooldownTicks={180}
              d3AlphaDecay={0.018}
              d3VelocityDecay={0.26}
              nodeRelSize={5}
              // 節點名稱已由 nodeCanvasObject 自繪；空字串可關閉套件內建 tooltip，
              // 避免 hover 時同一名稱顯示兩次。
              nodeLabel={() => ''}
              nodeCanvasObject={drawNode}
              nodePointerAreaPaint={paintNodePointer}
              linkColor={(link) => {
                const graphLink = link as GraphLink
                const source = endpointId(graphLink.source)
                const target = endpointId(graphLink.target)
                const dimmed = !!selectedNode && !selectedNeighborIds.has(source) && !selectedNeighborIds.has(target)
                const color = linkColor(graphLink)
                // 淺色 Canvas 上的 pastel 關係色需要提高對比，否則即使有 edge 也像沒有連線。
                return canvasTheme.isDark
                  ? hexToRgba(color, dimmed ? 0.12 : 0.56)
                  : dimmed
                    ? hexToRgba(color, 0.18)
                    : darkenHex(color, 0.58)
              }}
              linkWidth={(link) => {
                const graphLink = link as GraphLink
                const source = endpointId(graphLink.source)
                const target = endpointId(graphLink.target)
                if (selectedNode && (source === selectedNode.id || target === selectedNode.id)) {
                  return canvasTheme.isDark ? 1.9 : 2.2
                }
                return canvasTheme.isDark ? 0.8 : 1.05
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
                // 右鍵選單需限制在目前視窗內，避免靠近右下角的節點讓操作項目被裁掉。
                const menuWidth = 176
                const menuHeight = 180
                setContextMenu({
                  x: Math.max(8, Math.min(event.clientX, window.innerWidth - menuWidth - 8)),
                  y: Math.max(8, Math.min(event.clientY, window.innerHeight - menuHeight - 8)),
                  node: node as GraphNode,
                })
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
              <div className="absolute inset-0 pointer-events-none flex items-center justify-center bg-surface-alt/50">
                <div className="rounded-lg border border-border bg-surface/90 px-4 py-2 text-sm text-ink flex items-center gap-2 shadow-lg">
                  <Loader2 className="w-4 h-4 animate-spin text-brand" />
                  載入中
                </div>
              </div>
            )}

            {!loading && graph && graph.loadedNodes === 0 && (
              <div className="absolute inset-0 pointer-events-none flex items-center justify-center">
                <p className="rounded-lg border border-border bg-surface/90 px-4 py-3 text-sm text-ink-secondary shadow-sm">
                  沒有節點符合目前篩選或查詢
                </p>
              </div>
            )}

            <div className="absolute left-3 bottom-3 rounded-lg border border-border bg-surface/85 px-3 py-2 text-[11px] text-ink-secondary backdrop-blur">
              {graph?.loadedNodes ?? 0} / {graph?.totalNodes ?? 0} nodes · {graph?.loadedEdges ?? 0} edges
            </div>
          </div>
        </main>

        <aside className="min-h-0 border-l border-border bg-surface flex flex-col">
          <div className="shrink-0 border-b border-border p-3">
            <div className="flex rounded-lg border border-border bg-surface-alt p-0.5">
              {VIEW_TABS.map(({ value, icon: Icon, label }) => (
                <button
                  key={value}
                  type="button"
                  onClick={() => setViewMode(value)}
                  className={cn(
                    'flex-1 rounded-md px-2 py-1.5 text-xs font-medium transition-colors flex items-center justify-center gap-1.5',
                    viewMode === value ? 'bg-brand text-white' : 'text-ink-secondary hover:text-ink hover:bg-surface',
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
                  <p className="text-xs font-semibold uppercase tracking-wide text-ink-secondary">Inspector</p>
                  {selectedNode ? (
                    <div className="space-y-3">
                      <div>
                        <p className="text-sm font-semibold text-ink break-words">{selectedNode.name}</p>
                        <p className="text-xs text-brand mt-1">{selectedNode.kind}</p>
                      </div>
                      <code className="block rounded-lg border border-border bg-surface-alt px-3 py-2 text-[11px] leading-relaxed text-ink-secondary break-words">
                        {selectedNode.role}
                      </code>
                      {selectedNode.filePath && (
                        <p className="text-[11px] text-ink-subtle font-mono break-words">
                          {selectedNode.filePath}{selectedNode.startLine ? `:${selectedNode.startLine}` : ''}
                        </p>
                      )}
                      <div className="grid grid-cols-2 gap-2">
                        <Button variant="ghost" size="sm" onClick={() => void expandNodes('all')}>
                          全部鄰居
                        </Button>
                        <Button variant="ghost" size="sm" onClick={() => void expandNodes('same-file')}>
                          同檔案
                        </Button>
                        <Button variant="ghost" size="sm" onClick={() => void expandNodes('callers')}>
                          傳入關係
                        </Button>
                        <Button variant="ghost" size="sm" onClick={() => void expandNodes('callees')}>
                          傳出關係
                        </Button>
                      </div>
                      <div className="space-y-1.5">
                        {Object.entries(selectedNode.properties).slice(0, 24).map(([key, value]) => (
                          <div key={key} className="rounded-lg border border-border bg-surface-alt px-2.5 py-1.5">
                            <p className="text-[10px] text-ink-subtle">{key}</p>
                            <p className="text-[11px] text-ink-secondary break-words">{renderValue(value)}</p>
                          </div>
                        ))}
                      </div>
                    </div>
                  ) : selectedLink ? (
                    <div className="space-y-2">
                      <p className="text-sm font-semibold text-ink">{selectedLink.type}</p>
                      <p className="text-[11px] text-ink-subtle font-mono break-all">
                        {endpointId(selectedLink.source)} → {endpointId(selectedLink.target)}
                      </p>
                      {Object.entries(selectedLink.properties).map(([key, value]) => (
                        <div key={key} className="rounded-lg border border-border bg-surface-alt px-2.5 py-1.5">
                          <p className="text-[10px] text-ink-subtle">{key}</p>
                          <p className="text-[11px] text-ink-secondary break-words">{renderValue(value)}</p>
                        </div>
                      ))}
                    </div>
                  ) : (
                    <p className="text-sm text-ink-subtle">未選取</p>
                  )}
                </section>

                <section className="space-y-2">
                  <p className="text-xs font-semibold uppercase tracking-wide text-ink-secondary">Cypher</p>
                  <textarea
                    value={queryText}
                    onChange={(event) => setQueryText(event.target.value)}
                    spellCheck={false}
                    className="h-40 w-full resize-none rounded-lg border border-border bg-input-bg px-3 py-2 font-mono text-xs leading-relaxed text-ink placeholder:text-ink-subtle focus:outline-none focus:ring-2 focus:ring-brand/30"
                  />
                  <Button variant="primary" size="sm" className="w-full" onClick={runQuery} isLoading={querying} leftIcon={<Play className="w-3.5 h-3.5" />}>
                    執行 read-only 查詢
                  </Button>
                </section>
              </div>
            )}

            {viewMode === 'table' && (
              <div className="p-3">
                <div className="overflow-auto rounded-lg border border-border">
                  <table className="min-w-full text-left text-xs">
                    <thead className="bg-surface-alt text-ink-secondary">
                      <tr>
                        {(queryResult?.columns.length ? queryResult.columns : ['result']).map((column) => (
                          <th key={column} className="px-3 py-2 font-semibold">{column}</th>
                        ))}
                      </tr>
                    </thead>
                    <tbody>
                      {queryRows.length === 0 ? (
                        <tr>
                          <td className="px-3 py-8 text-center text-ink-subtle" colSpan={queryResult?.columns.length || 1}>
                            No rows
                          </td>
                        </tr>
                      ) : (
                        queryRows.map((row, rowIndex) => (
                          <tr key={rowIndex} className="border-t border-border">
                            {queryResult?.columns.map((column) => (
                              <td key={column} className="max-w-[220px] px-3 py-2 text-ink-secondary align-top">
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
              <pre className="m-3 overflow-auto rounded-lg border border-border bg-input-bg p-3 text-[11px] leading-relaxed text-ink-secondary">
                {JSON.stringify(queryResult ?? { graph }, null, 2)}
              </pre>
            )}
          </div>
        </aside>
      </div>

      {contextMenu && (
        <div
          className="fixed z-[90] min-w-[156px] rounded-lg border border-border bg-surface py-1 text-ink shadow-2xl"
          style={{ top: contextMenu.y, left: contextMenu.x }}
          onClick={(event) => event.stopPropagation()}
        >
          {[
            ['all', '展開全部鄰居'],
            ['callers', '展開傳入關係'],
            ['callees', '展開傳出關係'],
            ['same-file', '展開同檔案'],
          ].map(([mode, label]) => (
            <button
              key={mode}
              type="button"
              className="block w-full px-3 py-2 text-left text-sm text-ink-secondary hover:bg-surface-alt hover:text-ink"
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
