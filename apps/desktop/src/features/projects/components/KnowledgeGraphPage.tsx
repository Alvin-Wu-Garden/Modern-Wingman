import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type PointerEvent as ReactPointerEvent,
  type WheelEvent as ReactWheelEvent,
} from 'react'
import ForceGraph2D, {
  type ForceGraphMethods,
  type LinkObject,
  type NodeObject,
} from 'react-force-graph-2d'
import { save } from '@tauri-apps/plugin-dialog'
import { writeFile } from '@tauri-apps/plugin-fs'
import {
  Braces,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
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
import { useResizablePanel } from '@/components/layout/use-resizable-panel'
import { cn } from '@/lib/utils'
import {
  expandProjectGraphNeighbors,
  getProjectGraph,
  getProjectGraphSchema,
  queryProjectGraph,
  searchProjectGraph,
  type CodeGraphQueryResult,
  type CodeGraphSearchResult,
  type CodeGraphSchema,
  type CodeGraphVisualData,
  type CodeGraphVisualEdge,
  type CodeGraphVisualNode,
  type ProjectInfo,
} from '@/services/agent-api/projects'

type GraphNode = CodeGraphVisualNode & NodeObject<CodeGraphVisualNode>
type GraphLink = CodeGraphVisualEdge & LinkObject<CodeGraphVisualNode, CodeGraphVisualEdge>
type ViewMode = 'graph' | 'table' | 'raw'
type ExpandMode = 'all' | 'in' | 'out'
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
  caption: string
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

type GraphOperation = 'load' | 'query' | 'search'

interface ExpandedTableValue {
  column: string
  value: string
}

interface ColumnResizeState {
  column: string
  pointerId: number
  startX: number
  startWidth: number
}

interface NeighborExpansionFeedback {
  nodeId: string
  message: string
}

const DEFAULT_LIMIT = 1000
const DEFAULT_COLUMN_WIDTH = 220
const MIN_COLUMN_WIDTH = 120
const LONG_TABLE_VALUE_LENGTH = 320
const PNG_EXPORT_SCALE = 2
const PNG_EXPORT_MAX_DIMENSION = 4096
const NODE_PALETTE = ['#52D1DC', '#7C8CFF', '#30DAA2', '#FFC94F', '#FF7A90', '#B48CFF', '#FF9F43', '#8FE388']
const REL_PALETTE = ['#67E8F9', '#A7F3D0', '#FDE68A', '#F9A8D4', '#C4B5FD', '#FDBA74']
const VIEW_TABS: { value: ViewMode; icon: LucideIcon; label: string }[] = [
  { value: 'graph', icon: Network, label: '關聯圖' },
  { value: 'table', icon: Table2, label: '資料表' },
  { value: 'raw', icon: Braces, label: '原始資料' },
]

const styleKey = (projectId: string) => `modern-wingman:knowledge-graph:${projectId}:styles`

function defaultStyles(schema?: CodeGraphSchema): GraphStyleSettings {
  const nodeColors: Record<string, string> = {}
  const relationColors: Record<string, string> = {}

  schema?.facets.find((facet) => facet.target === 'node')?.values.forEach((value, index) => {
    nodeColors[value.token] = NODE_PALETTE[index % NODE_PALETTE.length]
  })
  schema?.facets.find((facet) => facet.target === 'edge')?.values.forEach((value, index) => {
    relationColors[value.token] = REL_PALETTE[index % REL_PALETTE.length]
  })

  return {
    nodeColors,
    relationColors,
    caption: schema?.captionOptions[0]?.id ?? 'caption',
    halo: true,
  }
}

function loadStyles(projectId: string, schema?: CodeGraphSchema): GraphStyleSettings {
  const fallback = defaultStyles(schema)
  try {
    const raw = localStorage.getItem(styleKey(projectId))
    if (!raw) return fallback
    const saved = JSON.parse(raw) as Partial<GraphStyleSettings>
    const validCaptions = new Set(schema?.captionOptions.map((option) => option.id) ?? [])
    const caption = saved.caption && validCaptions.has(saved.caption)
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
      ? {
          ...node,
          metrics: {
            ...existing.metrics,
            ...node.metrics,
            degree: Math.max(nodeDegree(existing), nodeDegree(node)),
          },
        }
      : node)
  })
  incoming.edges.forEach((edge) => edges.set(edge.id, edge))

  return {
    contractVersion: incoming.contractVersion,
    nodes: Array.from(nodes.values()),
    edges: Array.from(edges.values()),
    totalNodes: Math.max(base.totalNodes, incoming.totalNodes),
    loadedNodes: nodes.size,
    loadedEdges: edges.size,
    truncated: base.truncated || incoming.truncated,
  }
}

function endpointId(endpoint: string | number | { id?: string | number } | null | undefined) {
  if (endpoint && typeof endpoint === 'object') return String(endpoint.id)
  return String(endpoint)
}

function cypherString(value: string) {
  return value.replace(/\\/g, '\\\\').replace(/'/g, "\\'")
}

function cleanGraphData(data: CodeGraphVisualData | null): CodeGraphVisualData | null {
  if (!data) return null

  return {
    contractVersion: data.contractVersion,
    nodes: data.nodes.map((node) => ({
      id: node.id,
      labels: node.labels,
      caption: node.caption,
      category: node.category,
      properties: node.properties,
      metrics: node.metrics,
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
    truncated: data.truncated,
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

function nodeCategory(node: CodeGraphVisualNode) {
  return node.category || node.labels[0] || 'Node'
}

function nodeDegree(node: CodeGraphVisualNode) {
  return node.metrics?.degree ?? 0
}

function nodeCaption(node: CodeGraphVisualNode, option = 'caption') {
  if (option === 'caption') return node.caption
  if (option === 'category') return nodeCategory(node)
  if (option.startsWith('property:')) {
    const value = renderValue(node.properties[option.slice('property:'.length)])
    if (value) return value
  }
  return node.caption
}

function visualLimitOptions(totalNodes: number) {
  if (totalNodes <= 0) return [DEFAULT_LIMIT]
  if (totalNodes <= 1000) return [totalNodes]
  if (totalNodes <= 2000) return [1000, totalNodes]
  if (totalNodes <= 5000) return [1000, 2000, totalNodes]
  if (totalNodes <= 8000) return [1000, 2000, 5000, totalNodes]
  if (totalNodes <= 10000) return [1000, 2000, 5000, 8000, totalNodes]
  return [1000, 2000, 5000, 8000, 10000]
}

function onHorizontalWheel(event: ReactWheelEvent<HTMLDivElement>) {
  const viewport = event.currentTarget
  if (viewport.scrollWidth <= viewport.clientWidth) return
  if (Math.abs(event.deltaX) >= Math.abs(event.deltaY)) return
  event.preventDefault()
  viewport.scrollLeft += event.deltaY
}

function linkLayoutDistance(_link: GraphLink, densityScale: number) {
  return 185 * densityScale
}

function exportFilename(value: string) {
  return value.replace(/[<>:"/\\|?*\u0000-\u001F]/g, '-').trim() || 'knowledge-graph'
}

async function saveExportFile(
  defaultPath: string,
  bytes: Uint8Array,
  filterName: string,
  extension: string,
) {
  const path = await save({
    defaultPath,
    filters: [{ name: filterName, extensions: [extension] }],
  })
  if (!path) return null
  await writeFile(path, bytes)
  return path
}

async function createHighResolutionPng(
  source: HTMLCanvasElement,
  backgroundColor: string,
) {
  const longestEdge = Math.max(source.width, source.height)
  const scale = Math.min(
    PNG_EXPORT_SCALE,
    Math.max(1, PNG_EXPORT_MAX_DIMENSION / Math.max(1, longestEdge)),
  )
  const width = Math.round(source.width * scale)
  const height = Math.round(source.height * scale)
  const output = document.createElement('canvas')
  output.width = width
  output.height = height
  const context = output.getContext('2d')
  if (!context) throw new Error('無法建立高解析 PNG 畫布。')

  // ForceGraph 的背景色不保證寫入 PNG alpha channel；先使用目前主題的
  // 實際容器色鋪底，再合成畫布，確保匯出外觀與畫面一致。
  context.fillStyle = backgroundColor
  context.fillRect(0, 0, width, height)
  context.imageSmoothingEnabled = true
  context.imageSmoothingQuality = 'high'
  context.drawImage(source, 0, 0, width, height)

  const blob = await new Promise<Blob>((resolve, reject) => {
    output.toBlob(
      (value) => value ? resolve(value) : reject(new Error('無法產生高解析圖譜圖片。')),
      'image/png',
    )
  })
  return { blob, width, height }
}

export function KnowledgeGraphPage({ project, onClose }: KnowledgeGraphPageProps) {
  const filterPanel = useResizablePanel({
    storageKey: `modern-wingman:knowledge-graph:${project.id}:filters`,
    defaultWidth: 272,
    minWidth: 220,
    maxWidth: 440,
    collapsedWidth: 48,
    resizeFrom: 'end',
    collapseOnDoubleClick: true,
  })
  const inspectorPanel = useResizablePanel({
    storageKey: `modern-wingman:knowledge-graph:${project.id}:inspector`,
    defaultWidth: 340,
    minWidth: 280,
    maxWidth: 640,
    collapsedWidth: 48,
    resizeFrom: 'start',
    collapseOnDoubleClick: true,
  })
  const graphRef = useRef<ForceGraphMethods<GraphNode, GraphLink> | undefined>(undefined)
  const canvasWrapRef = useRef<HTMLDivElement>(null)
  const tableHeaderScrollRef = useRef<HTMLDivElement>(null)
  const columnResizeRef = useRef<ColumnResizeState | null>(null)
  const graphRequestGenerationRef = useRef(0)
  const graphAbortRef = useRef<AbortController | null>(null)
  const suppressNextBrowseReloadRef = useRef(false)
  const [canvasSize, setCanvasSize] = useState({ width: 900, height: 640 })
  const [schema, setSchema] = useState<CodeGraphSchema | null>(null)
  const [graph, setGraph] = useState<CodeGraphVisualData | null>(null)
  const [queryResult, setQueryResult] = useState<CodeGraphQueryResult | null>(null)
  const [selectedNode, setSelectedNode] = useState<GraphNode | null>(null)
  const [hoverNode, setHoverNode] = useState<GraphNode | null>(null)
  const [selectedLink, setSelectedLink] = useState<GraphLink | null>(null)
  const [contextMenu, setContextMenu] = useState<GraphContextMenu | null>(null)
  const [viewMode, setViewMode] = useState<ViewMode>('graph')
  const [queryText, setQueryText] = useState('')
  const [queryPanelOpen, setQueryPanelOpen] = useState(false)
  const [queryError, setQueryError] = useState<string | null>(null)
  const [queryMessage, setQueryMessage] = useState<string | null>(null)
  const [searchText, setSearchText] = useState('')
  const [searchResult, setSearchResult] = useState<CodeGraphSearchResult | null>(null)
  const [searchMessage, setSearchMessage] = useState<string | null>(null)
  const [activeSearchNodeId, setActiveSearchNodeId] = useState<string | null>(null)
  const [limit, setLimit] = useState(DEFAULT_LIMIT)
  const [selectedFilters, setSelectedFilters] = useState<Record<string, string[]>>({})
  const [openFacets, setOpenFacets] = useState<Record<string, boolean>>({})
  const [tableEntity, setTableEntity] = useState<'nodes' | 'edges'>('nodes')
  const [columnWidths, setColumnWidths] = useState<Record<string, number>>({})
  const [expandedTableValue, setExpandedTableValue] = useState<ExpandedTableValue | null>(null)
  const [filterPanelHoverExpanded, setFilterPanelHoverExpanded] = useState(false)
  const [expandedNeighborNodeIds, setExpandedNeighborNodeIds] = useState<Set<string>>(() => new Set())
  const [neighborExpansionFeedback, setNeighborExpansionFeedback] = useState<NeighborExpansionFeedback | null>(null)
  const [styles, setStyles] = useState<GraphStyleSettings>(() => defaultStyles())
  const [canvasTheme, setCanvasTheme] = useState<CanvasTheme>(() => readCanvasTheme())
  const [activeGraphOperation, setActiveGraphOperation] = useState<GraphOperation | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [exportFeedback, setExportFeedback] = useState<string | null>(null)
  const loading = activeGraphOperation === 'load'
  const querying = activeGraphOperation === 'query'
  const searching = activeGraphOperation === 'search'
  const openInspector = useCallback(() => inspectorPanel.expand(), [inspectorPanel.expand])

  const activeFilters = useMemo(() => Object.entries(selectedFilters)
    .filter(([, tokens]) => tokens.length > 0)
    .map(([facetId, tokens]) => ({ facetId, tokens })), [selectedFilters])

  const limitOptions = useMemo(
    // 載入級距描述的是完整圖譜的視覺化 budget，不應隨搜尋或 Cypher
    // 結果數改變；否則小型查詢結果會重寫 limit，接著觸發 loadGraph
    // 並把剛取得的查詢結果覆蓋掉。
    () => visualLimitOptions(schema?.totalNodes ?? 0),
    [schema?.totalNodes],
  )
  const selectedFilterLabels = useMemo(() => schema?.facets.flatMap((facet) =>
    (selectedFilters[facet.id] ?? []).map((token) => ({
      facetId: facet.id,
      token,
      label: facet.values.find((value) => value.token === token)?.label ?? token,
    }))) ?? [], [schema?.facets, selectedFilters])

  useEffect(() => {
    if (!limitOptions.includes(limit)) setLimit(limitOptions[0])
  }, [limit, limitOptions])

  const loadGraph = useCallback(async () => {
    // 篩選、Cypher、展開共用同一代數，避免不同操作的舊回應覆蓋最新畫面。
    const requestGeneration = ++graphRequestGenerationRef.current
    graphAbortRef.current?.abort()
    const controller = new AbortController()
    graphAbortRef.current = controller
    setActiveGraphOperation('load')
    setError(null)
    try {
      const nextGraph = await getProjectGraph(project.id, {
        limit,
        filters: activeFilters,
      }, controller.signal)
      if (requestGeneration !== graphRequestGenerationRef.current) return
      setGraph(nextGraph)
      setQueryResult(null)
      setQueryMessage(null)
      setQueryError(null)
      setExpandedNeighborNodeIds(new Set())
      setNeighborExpansionFeedback(null)
      setSelectedNode(null)
      setSelectedLink(null)
      setContextMenu(null)
      setViewMode('graph')
      setTimeout(() => graphRef.current?.zoomToFit(700, 64), 120)
    } catch (err) {
      if (requestGeneration !== graphRequestGenerationRef.current) return
      if (controller.signal.aborted) return
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      if (requestGeneration === graphRequestGenerationRef.current) setActiveGraphOperation(null)
    }
  }, [activeFilters, limit, project.id])

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
        if (expandedTableValue) setExpandedTableValue(null)
        else if (contextMenu) setContextMenu(null)
        else onClose()
      }
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [contextMenu, expandedTableValue, onClose])

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
        setLimit((current) => {
          const options = visualLimitOptions(nextSchema.totalNodes)
          return options.includes(current) ? current : options[0]
        })
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
    if (suppressNextBrowseReloadRef.current) {
      // Cypher 成功後會清除舊的瀏覽篩選；這是 UI 狀態整理，不可反過來
      // 觸發一般圖譜載入並覆蓋剛取得的 Cypher 結果。
      suppressNextBrowseReloadRef.current = false
      return
    }
    void loadGraph()
    return () => {
      // 條件切換或頁面卸載時，讓尚未完成的舊回應失效。
      graphRequestGenerationRef.current += 1
      graphAbortRef.current?.abort()
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
    styles.nodeColors[nodeCategory(node)] ?? '#9CA3AF'
  ), [styles.nodeColors])

  const linkColor = useCallback((link: GraphLink) => (
    styles.relationColors[link.type] ?? '#94A3B8'
  ), [styles.relationColors])

  const runQuery = useCallback(async () => {
    if (!queryText.trim()) {
      setQueryMessage(null)
      setQueryError('請先填入範例，或輸入唯讀 Cypher。')
      return
    }
    const requestGeneration = ++graphRequestGenerationRef.current
    setActiveGraphOperation('query')
    setQueryError(null)
    setQueryMessage(null)
    try {
      const result = await queryProjectGraph(project.id, queryText, limit)
      if (requestGeneration !== graphRequestGenerationRef.current) return
      // Cypher 與一般搜尋／篩選是互斥的結果來源。清除未套用的舊條件，
      // 並略過這次條件清除通常會觸發的一般圖譜重新載入。
      suppressNextBrowseReloadRef.current = true
      setSearchText('')
      setSearchResult(null)
      setSearchMessage(null)
      setActiveSearchNodeId(null)
      setSelectedFilters({})
      setQueryResult(result)
      // 手動 Cypher 是一個新的檢視結果，不能殘留先前圖譜；只有「展開」才合併節點。
      setGraph(result.graph)
      setExpandedNeighborNodeIds(new Set())
      setNeighborExpansionFeedback(null)
      setSelectedNode(null)
      setSelectedLink(null)
      setContextMenu(null)
      setViewMode(result.graph.nodes.length > 0 ? 'graph' : 'table')
      setQueryMessage(
        `查詢完成：${result.graph.loadedNodes} 個節點、${result.graph.loadedEdges} 個關係、${result.rows.length} 列資料。`,
      )
      setTimeout(() => graphRef.current?.zoomToFit(700, 64), 120)
    } catch (err) {
      if (requestGeneration !== graphRequestGenerationRef.current) return
      setQueryError(err instanceof Error ? err.message : String(err))
    } finally {
      if (requestGeneration === graphRequestGenerationRef.current) setActiveGraphOperation(null)
    }
  }, [limit, project.id, queryText])

  const openSelectionQuery = useCallback(() => {
    const target = selectedNode ? 'node' : selectedLink ? 'edge' : null
    if (!target) return
    const template = schema?.queryTemplates.find((item) => item.target === target)
    if (!template) return
    const replacements = selectedNode
      ? { nodeId: selectedNode.id }
      : {
          sourceId: endpointId(selectedLink?.source),
          targetId: endpointId(selectedLink?.target),
          edgeType: selectedLink?.type ?? '',
        }
    setQueryText(template.text.replace(/\{\{(\w+)\}\}/g, (_, key: string) =>
      cypherString(replacements[key as keyof typeof replacements] ?? ''),
    ))
    setQueryError(null)
    setQueryMessage(null)
    setQueryPanelOpen(true)
  }, [schema?.queryTemplates, selectedLink, selectedNode])

  const expandNodes = useCallback(async (mode: ExpandMode, node = selectedNode) => {
    if (!node) return
    const existingNodeIds = new Set((graph?.nodes ?? []).map((item) => item.id))
    const existingEdgeIds = new Set((graph?.edges ?? []).map((item) => item.id))
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
      const addedNodes = nextGraph.nodes.filter((item) => !existingNodeIds.has(item.id)).length
      const addedEdges = nextGraph.edges.filter((item) => !existingEdgeIds.has(item.id)).length
      setGraph((current) => mergeGraphData(current, nextGraph))
      if (mode === 'all') {
        setExpandedNeighborNodeIds((current) => {
          const next = new Set(current)
          next.add(node.id)
          return next
        })
      }
      setNeighborExpansionFeedback({
        nodeId: node.id,
        message: addedNodes === 0 && addedEdges === 0
          ? mode === 'all'
            ? '此節點的一階鄰居已全部載入。'
            : `此節點沒有新的${mode === 'in' ? '傳入' : '傳出'}鄰居。`
          : `已新增 ${addedNodes} 個節點、${addedEdges} 個關係。`,
      })
      setQueryResult(null)
      setQueryMessage(null)
      setQueryError(null)
      setContextMenu(null)
      setViewMode('graph')
      if (addedNodes > 0 || addedEdges > 0)
        setTimeout(() => graphRef.current?.zoomToFit(500, 64), 80)
    } catch (err) {
      if (requestGeneration !== graphRequestGenerationRef.current) return
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      if (requestGeneration === graphRequestGenerationRef.current) setActiveGraphOperation(null)
    }
  }, [graph?.edges, graph?.nodes, limit, project.id, selectedNode])

  const loadSearchHitGraph = useCallback(async (
    node: CodeGraphVisualNode,
    existingGeneration?: number,
  ) => {
    const requestGeneration = existingGeneration ?? ++graphRequestGenerationRef.current
    setActiveGraphOperation('load')
    setError(null)
    try {
      const nextGraph = await expandProjectGraphNeighbors(project.id, [node.id], {
        depth: 1,
        limit,
        mode: 'all',
      })
      if (requestGeneration !== graphRequestGenerationRef.current) return
      const selected = nextGraph.nodes.find((item) => item.id === node.id) ?? node
      setGraph(nextGraph)
      setQueryResult(null)
      setQueryMessage(null)
      setQueryError(null)
      setExpandedNeighborNodeIds(new Set([node.id]))
      setNeighborExpansionFeedback(null)
      setSelectedNode(selected as GraphNode)
      setActiveSearchNodeId(node.id)
      setSelectedLink(null)
      setViewMode('graph')
      setTimeout(() => graphRef.current?.zoomToFit(600, 72), 100)
    } catch (err) {
      if (requestGeneration !== graphRequestGenerationRef.current) return
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      if (requestGeneration === graphRequestGenerationRef.current) setActiveGraphOperation(null)
    }
  }, [limit, project.id])

  const searchNode = useCallback(async () => {
    const query = searchText.trim()
    if (!query || !schema?.capabilities.search) return
    const requestGeneration = ++graphRequestGenerationRef.current
    graphAbortRef.current?.abort()
    const controller = new AbortController()
    graphAbortRef.current = controller
    setActiveGraphOperation('search')
    setError(null)
    setSearchMessage(null)
    try {
      const result = await searchProjectGraph(
        project.id, query, activeFilters, 50, controller.signal,
      )
      if (requestGeneration !== graphRequestGenerationRef.current) return
      setSearchResult(result)
      if (result.items.length === 0) {
        setActiveSearchNodeId(null)
        setSearchMessage('找不到符合的節點。')
        return
      }
      setSearchMessage(`找到 ${result.items.length}${result.hasMore ? '+' : ''} 個節點，已顯示最佳結果。`)
      await loadSearchHitGraph(result.items[0].node, requestGeneration)
    } catch (err) {
      if (requestGeneration !== graphRequestGenerationRef.current || controller.signal.aborted) return
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      if (requestGeneration === graphRequestGenerationRef.current) setActiveGraphOperation(null)
    }
  }, [activeFilters, loadSearchHitGraph, project.id, schema?.capabilities.search, searchText])

  const openSearchHit = useCallback(async (node: CodeGraphVisualNode) => {
    await loadSearchHitGraph(node)
  }, [loadSearchHitGraph])

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
    const radius = Math.max(4.5, Math.min(17, 5 + Math.sqrt(nodeDegree(graphNode) + 1) * 1.4))

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
      const label = nodeCaption(graphNode, styles.caption)
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
    globalScale: number,
  ) => {
    const graphNode = node as GraphNode
    const visualRadius = Math.max(7, Math.min(22, 8 + Math.sqrt(nodeDegree(graphNode) + 1) * 1.6))
    // 命中直徑至少維持 32px；只放大透明互動區，不改變節點視覺尺寸。
    const radius = Math.max(visualRadius, 16 / globalScale)
    ctx.fillStyle = color
    ctx.beginPath()
    ctx.arc(graphNode.x ?? 0, graphNode.y ?? 0, radius, 0, Math.PI * 2)
    ctx.fill()
  }, [])

  const paintLinkPointer = useCallback((
    link: LinkObject<CodeGraphVisualNode, CodeGraphVisualEdge>,
    color: string,
    ctx: CanvasRenderingContext2D,
    globalScale: number,
  ) => {
    const source = link.source as GraphNode
    const target = link.target as GraphNode
    if (!Number.isFinite(source?.x) || !Number.isFinite(source?.y) ||
        !Number.isFinite(target?.x) || !Number.isFinite(target?.y)) return

    ctx.strokeStyle = color
    ctx.lineWidth = 12 / globalScale
    ctx.lineCap = 'round'
    ctx.beginPath()
    ctx.moveTo(source.x ?? 0, source.y ?? 0)
    ctx.lineTo(target.x ?? 0, target.y ?? 0)
    ctx.stroke()
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

  const exportJson = async () => {
    try {
      setExportFeedback(null)
      const content = JSON.stringify({ project, schema, graph: cleanGraphData(graph) }, null, 2)
      const path = await saveExportFile(
        `${exportFilename(project.name)}-knowledge-graph.json`,
        new TextEncoder().encode(content),
        'JSON',
        'json',
      )
      setExportFeedback(path ? `已儲存圖譜資料：${path}` : '已取消儲存圖譜資料。')
    } catch (reason) {
      setExportFeedback(reason instanceof Error ? `儲存失敗：${reason.message}` : '儲存圖譜資料失敗')
    }
  }

  const exportPng = async () => {
    const canvasWrap = canvasWrapRef.current
    const canvas = canvasWrap?.querySelector('canvas')
    if (!canvasWrap || !canvas) {
      setExportFeedback('請先切換到關聯圖，再儲存圖片。')
      return
    }
    try {
      setExportFeedback(null)
      const computedBackground = getComputedStyle(canvasWrap).backgroundColor
      const backgroundColor = computedBackground && computedBackground !== 'rgba(0, 0, 0, 0)'
        ? computedBackground
        : canvasTheme.background
      const { blob, width, height } = await createHighResolutionPng(canvas, backgroundColor)
      const path = await saveExportFile(
        `${exportFilename(project.name)}-knowledge-graph.png`,
        new Uint8Array(await blob.arrayBuffer()),
        'PNG 圖片',
        'png',
      )
      setExportFeedback(path
        ? `已儲存關聯圖（${width}×${height}）：${path}`
        : '已取消儲存關聯圖。')
    } catch (reason) {
      setExportFeedback(reason instanceof Error ? `儲存失敗：${reason.message}` : '儲存關聯圖失敗')
    }
  }

  const toggleFacet = (facetId: string, token: string, selection: 'single' | 'multiple') => {
    setSelectedFilters((current) => {
      const selected = current[facetId] ?? []
      const next = selected.includes(token)
        ? selected.filter((item) => item !== token)
        : selection === 'single' ? [token] : [...selected, token]
      return { ...current, [facetId]: next }
    })
  }

  const tableRows = useMemo<Record<string, unknown>[]>(() => {
    if (queryResult) return queryResult.rows
    if (tableEntity === 'edges') return (graph?.edges ?? []).map((edge) => ({
      id: edge.id,
      source: endpointId(edge.source),
      target: endpointId(edge.target),
      type: edge.type,
      ...edge.properties,
    }))
    return (graph?.nodes ?? []).map((node) => ({
      id: node.id,
      caption: node.caption,
      category: nodeCategory(node),
      labels: node.labels,
      ...node.properties,
    }))
  }, [graph?.edges, graph?.nodes, queryResult, tableEntity])
  const tableColumns = useMemo(() => queryResult?.columns ?? Array.from(
    tableRows.reduce((keys, row) => {
      Object.keys(row).forEach((key) => keys.add(key))
      return keys
    }, new Set<string>()),
  ).slice(0, 24), [queryResult?.columns, tableRows])
  const visibleTableColumns = tableColumns.length ? tableColumns : ['result']
  const selectedNodeExpanded = selectedNode
    ? expandedNeighborNodeIds.has(selectedNode.id)
    : false
  const columnWidth = useCallback(
    (column: string) => columnWidths[column] ?? DEFAULT_COLUMN_WIDTH,
    [columnWidths],
  )
  const tablePixelWidth = Math.max(
    visibleTableColumns.reduce((total, column) => total + columnWidth(column), 0),
    720,
  )

  useEffect(() => {
    // 欄寬只屬於目前這批表格資料；每次查詢、重新載入或切換節點／關係時重設。
    setColumnWidths({})
  }, [graph, queryResult, tableEntity])

  const startColumnResize = useCallback((
    column: string,
    event: ReactPointerEvent<HTMLDivElement>,
  ) => {
    if (event.button !== 0) return
    event.preventDefault()
    event.currentTarget.setPointerCapture(event.pointerId)
    columnResizeRef.current = {
      column,
      pointerId: event.pointerId,
      startX: event.clientX,
      startWidth: columnWidth(column),
    }
  }, [columnWidth])

  const resizeColumn = useCallback((event: ReactPointerEvent<HTMLDivElement>) => {
    const resizing = columnResizeRef.current
    if (!resizing || resizing.pointerId !== event.pointerId) return
    const width = Math.max(MIN_COLUMN_WIDTH, resizing.startWidth + event.clientX - resizing.startX)
    setColumnWidths((current) => ({ ...current, [resizing.column]: width }))
  }, [])

  const finishColumnResize = useCallback((event: ReactPointerEvent<HTMLDivElement>) => {
    if (columnResizeRef.current?.pointerId !== event.pointerId) return
    columnResizeRef.current = null
    if (event.currentTarget.hasPointerCapture(event.pointerId))
      event.currentTarget.releasePointerCapture(event.pointerId)
  }, [])

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

      <div className="min-h-0 flex flex-1">
        <aside
          style={filterPanel.panelStyle}
          onMouseEnter={() => {
            if (filterPanel.collapsed) setFilterPanelHoverExpanded(true)
          }}
          onMouseLeave={() => setFilterPanelHoverExpanded(false)}
          className="relative z-40 min-h-0 shrink-0"
        >
          <div
            style={{
              width: filterPanel.collapsed && filterPanelHoverExpanded
                ? filterPanel.width
                : filterPanel.panelStyle.width,
            }}
            className={cn(
              'absolute inset-y-0 left-0 border-r border-border bg-surface transition-[width] duration-200',
              filterPanel.collapsed && filterPanelHoverExpanded && 'shadow-xl',
            )}
          >
          <button
            type="button"
            aria-label={filterPanel.collapsed ? '固定展開篩選條件' : '收合篩選條件'}
            aria-expanded={!filterPanel.collapsed}
            title={filterPanel.collapsed ? '固定展開篩選條件' : '收合篩選條件'}
            onClick={() => {
              setFilterPanelHoverExpanded(false)
              filterPanel.toggleCollapsed()
            }}
            className="absolute right-0 top-4 z-30 flex h-7 w-7 translate-x-1/2 items-center justify-center rounded-full border border-border bg-surface text-ink-secondary shadow-sm transition-colors hover:bg-surface-alt hover:text-ink"
          >
            {filterPanel.collapsed
              ? <ChevronRight className="h-3.5 w-3.5" />
              : <ChevronLeft className="h-3.5 w-3.5" />}
          </button>
          {!filterPanel.collapsed && (
            <div
              {...filterPanel.resizeHandleProps}
              title="拖曳調整寬度；雙擊收合"
              className="absolute bottom-0 right-0 top-0 z-20 w-1 translate-x-1/2 cursor-col-resize touch-none outline-none hover:bg-brand/40 focus:bg-brand/50"
            />
          )}
          {!filterPanel.collapsed || filterPanelHoverExpanded ? (
          <div className="h-full overflow-y-auto p-4 space-y-4">
            <section className="space-y-2">
              <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-ink-secondary">
                <Database className="w-3.5 h-3.5" />
                圖譜概覽
              </div>
              <div className="grid grid-cols-2 gap-2">
                <div className="rounded-lg border border-border bg-surface-alt px-3 py-2">
                  <p className="text-[10px] text-ink-subtle">節點總數</p>
                  <p className="text-lg font-semibold">{schema?.totalNodes ?? graph?.totalNodes ?? 0}</p>
                </div>
                <div className="rounded-lg border border-border bg-surface-alt px-3 py-2">
                  <p className="text-[10px] text-ink-subtle">關係總數</p>
                  <p className="text-lg font-semibold">{schema?.totalEdges ?? graph?.loadedEdges ?? 0}</p>
                </div>
              </div>
            </section>

            {schema?.facets.map((facet, facetIndex) => {
              const selected = selectedFilters[facet.id] ?? []
              const isEdge = facet.target === 'edge'
              return (
                <details
                  key={facet.id}
                  open={openFacets[facet.id] ?? true}
                  onToggle={(event) => {
                    const open = event.currentTarget.open
                    setOpenFacets((current) => current[facet.id] === open
                      ? current
                      : { ...current, [facet.id]: open })
                  }}
                  className="group rounded-lg border border-transparent open:border-border open:bg-surface-alt/40"
                >
                  <summary className="flex cursor-pointer list-none items-center gap-2 rounded-lg px-2 py-2 text-xs font-semibold uppercase tracking-wide text-ink-secondary hover:bg-surface-alt">
                    {isEdge ? <Network className="w-3.5 h-3.5" /> : <Filter className="w-3.5 h-3.5" />}
                    <span className="min-w-0 flex-1 truncate">{facet.label}</span>
                    {selected.length > 0 && <span className="rounded-full bg-brand/15 px-1.5 py-0.5 text-[10px] text-brand">{selected.length}</span>}
                    <ChevronDown className="h-3.5 w-3.5 transition-transform group-open:rotate-180" />
                  </summary>
                  <div className="space-y-2 px-2 pb-2">
                    <p className="text-[10px] leading-relaxed text-ink-subtle">{facet.description}</p>
                  <button
                    type="button"
                    onClick={() => setSelectedFilters((current) => ({ ...current, [facet.id]: [] }))}
                    className={cn(
                      'w-full rounded-lg px-3 py-1.5 text-left text-xs transition-colors',
                      selected.length === 0 ? 'bg-brand/15 text-brand' : 'text-ink-secondary hover:bg-surface-alt',
                    )}
                  >
                    不限
                  </button>
                  <div className="space-y-1.5">
                    {facet.values.map((value, valueIndex) => {
                      const palette = isEdge ? REL_PALETTE : NODE_PALETTE
                      const colors = isEdge ? styles.relationColors : styles.nodeColors
                      const color = colors[value.token] ?? palette[(facetIndex + valueIndex) % palette.length]
                      const active = selected.length === 0 || selected.includes(value.token)
                      return (
                        <div key={value.token} className="flex items-center gap-2 rounded-lg border border-border bg-surface-alt px-2 py-1.5">
                          <button
                            type="button"
                            onClick={() => toggleFacet(facet.id, value.token, facet.selection)}
                            aria-pressed={active}
                            className={cn(
                              'min-w-0 flex flex-1 items-center gap-2 rounded px-1 py-0.5 text-left text-xs transition-opacity',
                              active ? 'text-ink opacity-100' : 'text-ink-subtle opacity-55',
                            )}
                          >
                            <span
                              aria-hidden="true"
                              className={cn('shrink-0 border border-border', isEdge ? 'h-2.5 w-5 rounded-full' : 'h-3 w-3 rounded-full')}
                              style={{ backgroundColor: color }}
                            />
                            <span className="min-w-0 flex-1 truncate">{value.label}</span>
                            <span className="text-[10px] text-ink-subtle">{value.count}</span>
                          </button>
                          <input
                            type="color"
                            value={color}
                            onChange={(event) => isEdge
                              ? updateRelationColor(value.token, event.target.value)
                              : updateNodeColor(value.token, event.target.value)}
                            className="h-5 w-6 rounded border-0 bg-transparent p-0"
                            title={`${value.label} color`}
                          />
                        </div>
                      )
                    })}
                  </div>
                  </div>
                </details>
              )
            })}

            <section className="space-y-2 rounded-lg border border-border bg-surface-alt/40 p-3">
              <div>
                <div className="text-xs font-semibold text-ink-secondary">圖形顯示</div>
                <p className="mt-1 text-[10px] leading-relaxed text-ink-subtle">只調整關聯圖的標籤與視覺效果，不會改變圖譜資料。</p>
              </div>
              <label className="block text-[11px] text-ink-secondary">
                節點標籤顯示
                <select
                  value={styles.caption}
                  onChange={(event) => setStyles((current) => ({ ...current, caption: event.target.value }))}
                  className="mt-1 w-full rounded-lg border border-border bg-input-bg px-2 py-1.5 text-xs text-ink focus:outline-none focus:ring-2 focus:ring-brand/30"
                >
                  {schema?.captionOptions.map((option) => (
                    <option key={option.id} value={option.id}>{option.label}</option>
                  ))}
                </select>
              </label>
              <label className="flex items-center justify-between rounded-lg border border-border bg-surface-alt px-3 py-2 text-xs text-ink-secondary">
                顯示節點光暈
                <input
                  type="checkbox"
                  checked={styles.halo}
                  onChange={(event) => setStyles((current) => ({ ...current, halo: event.target.checked }))}
                  className="h-4 w-4 accent-brand"
                />
              </label>
            </section>
          </div>
          ) : (
            <div className="flex h-full items-start justify-center pt-14" title="篩選條件已收合；移入滑鼠可暫時展開">
              <Filter className="h-4 w-4 text-ink-subtle" />
            </div>
          )}
          </div>
        </aside>

        <main className="min-w-0 min-h-0 flex flex-col">
          <div onWheel={onHorizontalWheel} className="min-h-12 shrink-0 overflow-x-auto border-b border-border bg-surface-alt px-3 py-2 flex items-center gap-2">
            <div className="relative min-w-48 max-w-96 flex-1">
              <Search className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-ink-subtle" />
              <input
                value={searchText}
                onChange={(event) => {
                  setSearchText(event.target.value)
                  setSearchResult(null)
                  setSearchMessage(null)
                }}
                onKeyDown={(event) => {
                  if (event.key !== 'Enter' || event.nativeEvent.isComposing) return
                  event.preventDefault()
                  void searchNode()
                }}
                placeholder="全域搜尋節點"
                className="h-8 w-full rounded-lg border border-border bg-input-bg pl-8 pr-3 text-xs text-ink placeholder:text-ink-subtle focus:outline-none focus:ring-2 focus:ring-brand/30"
              />
            </div>
            <Button variant="ghost" size="sm" className="shrink-0" onClick={() => void searchNode()} title="搜尋" isLoading={searching}>
              <Search className="w-3.5 h-3.5" />
            </Button>
            <select
              value={limit}
              onChange={(event) => setLimit(Number(event.target.value))}
              className="h-8 shrink-0 rounded-lg border border-border bg-input-bg px-2 text-xs text-ink focus:outline-none focus:ring-2 focus:ring-brand/30"
            >
              {limitOptions.map((value) => (
                <option key={value} value={value}>{value} nodes</option>
              ))}
            </select>
            {(schema?.totalNodes ?? 0) > 5000 && (
              <span className="whitespace-nowrap text-[10px] text-ink-subtle">搜尋涵蓋全部 {schema?.totalNodes} 筆</span>
            )}
            {searchMessage && (
              <span className={cn(
                'whitespace-nowrap text-[10px]',
                searchResult?.items.length ? 'text-brand' : 'font-medium text-error',
              )}>
                {searchMessage}
              </span>
            )}
            <div className="ml-auto flex shrink-0 items-center gap-1 rounded-lg border border-border bg-surface p-0.5">
              {VIEW_TABS.map(({ value, icon: Icon, label }) => (
                <button
                  key={value}
                  type="button"
                  onClick={() => setViewMode(value)}
                  title={value === 'graph' ? '查看節點與關係圖' : value === 'table' ? '以完整寬度查看目前資料' : '查看通用 Viewer JSON'}
                  className={cn(
                    'flex items-center gap-1.5 rounded-md px-2.5 py-1.5 text-xs font-medium transition-colors',
                    viewMode === value ? 'bg-brand text-white' : 'text-ink-secondary hover:bg-surface-alt hover:text-ink',
                  )}
                >
                  <Icon className="h-3.5 w-3.5" />
                  {label}
                </button>
              ))}
            </div>
            {viewMode === 'graph' && (
              <div className="flex shrink-0 items-center gap-1 border-l border-border pl-2">
                <Button variant="ghost" size="sm" className="whitespace-nowrap" onClick={() => graphRef.current?.zoomToFit(650, 72)} title="將目前關聯圖縮放並置中到可視範圍">
                  <Maximize2 className="w-3.5 h-3.5" />
                  將目前結果置中
                </Button>
                <Button
                  variant="ghost"
                  size="sm"
                  className="whitespace-nowrap"
                  onClick={() => void expandNodes('all')}
                  disabled={!selectedNode || selectedNodeExpanded}
                  title={!selectedNode
                    ? '請先選取一個節點'
                    : selectedNodeExpanded
                      ? '此節點的一階鄰居已載入'
                      : '載入選取節點的一階鄰居'}
                >
                  <Expand className="w-3.5 h-3.5" />
                  {selectedNodeExpanded ? '已展開' : '展開鄰居'}
                </Button>
              </div>
            )}
            <div className="flex shrink-0 items-center gap-1 border-l border-border pl-2">
              <span className="text-[10px] font-medium text-ink-subtle">儲存</span>
              {viewMode === 'graph' && (
                <Button variant="ghost" size="sm" className="whitespace-nowrap" onClick={() => void exportPng()} title="選擇路徑並儲存目前看到的關聯圖圖片">
                  <Download className="w-3.5 h-3.5" />
                  關聯圖快照
                </Button>
              )}
              <Button variant="ghost" size="sm" className="whitespace-nowrap" onClick={() => void exportJson()} title="選擇路徑並儲存目前圖譜資料，供備份或除錯使用">
                <FileJson className="w-3.5 h-3.5" />
                圖譜資料
              </Button>
            </div>
          </div>

          {neighborExpansionFeedback && selectedNode?.id === neighborExpansionFeedback.nodeId && (
            <div className="flex shrink-0 items-center justify-between border-b border-border bg-surface px-3 py-1.5 text-[10px] text-brand">
              <span>{neighborExpansionFeedback.message}</span>
              <button
                type="button"
                onClick={() => setNeighborExpansionFeedback(null)}
                className="text-ink-subtle hover:text-ink"
                title="關閉訊息"
              >
                <X className="h-3 w-3" />
              </button>
            </div>
          )}

          {exportFeedback && (
            <div className="shrink-0 border-b border-border bg-surface px-3 py-1.5 text-[10px] text-brand flex items-center justify-between">
              <span>{exportFeedback}</span>
              <button type="button" onClick={() => setExportFeedback(null)} className="text-ink-subtle hover:text-ink" title="關閉訊息">
                <X className="h-3 w-3" />
              </button>
            </div>
          )}

          {queryResult && (
            <div className="flex shrink-0 items-center justify-between gap-3 border-b border-brand/25 bg-brand/10 px-3 py-2">
              <div className="min-w-0">
                <p className="text-xs font-semibold text-brand">目前顯示：Cypher 查詢結果</p>
                <p className="truncate text-[10px] text-ink-secondary">
                  {queryResult.graph.loadedNodes} 個節點 · {queryResult.graph.loadedEdges} 個關係 · {queryResult.rows.length} 列資料；此結果只來自右側 Cypher 查詢。
                </p>
              </div>
              <Button
                variant="outline"
                size="sm"
                className="shrink-0"
                onClick={() => void loadGraph()}
              >
                返回一般瀏覽
              </Button>
            </div>
          )}

          {selectedFilterLabels.length > 0 && (
            <div className="shrink-0 border-b border-border bg-surface px-3 py-1.5 flex items-center gap-2 overflow-x-auto">
              <span className="shrink-0 text-[10px] font-medium text-ink-subtle">目前條件</span>
              {selectedFilterLabels.map((item) => (
                <button
                  key={`${item.facetId}:${item.token}`}
                  type="button"
                  onClick={() => toggleFacet(item.facetId, item.token, 'multiple')}
                  className="flex shrink-0 items-center gap-1 rounded-full border border-brand/25 bg-brand/10 px-2 py-1 text-[10px] text-brand"
                  title="移除此條件"
                >
                  {item.label}
                  <X className="h-3 w-3" />
                </button>
              ))}
              <button type="button" onClick={() => setSelectedFilters({})} className="shrink-0 text-[10px] text-ink-subtle hover:text-ink">
                清除全部
              </button>
            </div>
          )}

          {searchResult && searchResult.items.length > 0 && (
            <div className="shrink-0 border-b border-border bg-surface px-3 py-2">
              <div className="mb-1.5 flex items-center justify-between">
                <p className="text-[10px] font-medium text-ink-secondary">
                  搜尋結果 · 點選其他節點即可切換其關聯圖
                </p>
                <button type="button" onClick={() => setSearchResult(null)} className="text-ink-subtle hover:text-ink" title="關閉搜尋結果">
                  <X className="h-3.5 w-3.5" />
                </button>
              </div>
              <div onWheel={onHorizontalWheel} className="flex gap-2 overflow-x-auto pb-0.5">
                {searchResult.items.map(({ node, score }) => (
                  <button
                    key={node.id}
                    type="button"
                    onClick={() => void openSearchHit(node)}
                    className={cn(
                      'w-56 shrink-0 rounded-lg border px-2.5 py-2 text-left transition-colors',
                      activeSearchNodeId === node.id
                        ? 'border-brand bg-brand/10'
                        : 'border-border bg-surface-alt hover:border-brand/40 hover:bg-brand/5',
                    )}
                  >
                    <span className="block truncate text-xs font-medium text-ink">{node.caption}</span>
                    <span className="block truncate text-[10px] text-ink-subtle">
                      {nodeCategory(node)} · score {score.toFixed(2)}
                    </span>
                  </button>
                ))}
              </div>
            </div>
          )}

          <div ref={canvasWrapRef} className={cn('relative min-h-0 flex-1 overflow-hidden bg-surface-alt', viewMode !== 'graph' && 'hidden')}>
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
              linkPointerAreaPaint={paintLinkPointer}
              linkHoverPrecision={12}
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
                openInspector()
                setSelectedNode(node as GraphNode)
                setSelectedLink(null)
                setContextMenu(null)
              }}
              onNodeRightClick={(node, event) => {
                event.preventDefault()
                openInspector()
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
                openInspector()
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

          {viewMode === 'table' && (
            <div className="min-h-0 flex flex-1 flex-col overflow-hidden bg-surface p-4">
              <div className="mb-3 flex shrink-0 items-center justify-between gap-3">
                <div>
                  <h2 className="text-sm font-semibold text-ink">目前資料的表格檢視</h2>
                  <p className="text-[11px] text-ink-subtle">顯示目前搜尋、篩選或查詢載入的資料。</p>
                </div>
                {!queryResult && (
                  <div className="flex min-w-56 gap-1 rounded-lg border border-border bg-surface-alt p-1">
                    {(['nodes', 'edges'] as const).map((value) => (
                      <button
                        key={value}
                        type="button"
                        onClick={() => setTableEntity(value)}
                        className={cn(
                          'flex-1 rounded-md px-3 py-1.5 text-xs',
                          tableEntity === value ? 'bg-surface text-ink shadow-sm' : 'text-ink-secondary',
                        )}
                      >
                        {value === 'nodes' ? '節點' : '關係'}
                      </button>
                    ))}
                  </div>
                )}
              </div>
              <div className="min-h-0 flex flex-1 flex-col overflow-hidden rounded-lg border border-border">
                <div ref={tableHeaderScrollRef} className="shrink-0 overflow-hidden bg-surface-alt">
                  <table className="table-fixed text-left text-xs" style={{ minWidth: '100%', width: tablePixelWidth }}>
                    <colgroup>
                      {visibleTableColumns.map((column) => <col key={column} style={{ width: columnWidth(column) }} />)}
                    </colgroup>
                    <thead className="text-ink-secondary">
                      <tr>
                        {visibleTableColumns.map((column) => (
                          <th key={column} className="relative whitespace-nowrap border-b border-border px-3 py-2 pr-4 font-semibold">
                            <span className="block overflow-hidden text-ellipsis">{column}</span>
                            <div
                              role="separator"
                              aria-label={`調整 ${column} 欄寬`}
                              aria-orientation="vertical"
                              onPointerDown={(event) => startColumnResize(column, event)}
                              onPointerMove={resizeColumn}
                              onPointerUp={finishColumnResize}
                              onPointerCancel={finishColumnResize}
                              className="absolute bottom-0 right-0 top-0 z-10 w-2 translate-x-1/2 cursor-col-resize touch-none hover:bg-brand/35"
                            />
                          </th>
                        ))}
                      </tr>
                    </thead>
                  </table>
                </div>
                <div
                  className="min-h-0 flex-1 overflow-auto overscroll-contain"
                  onScroll={(event) => {
                    if (tableHeaderScrollRef.current)
                      tableHeaderScrollRef.current.scrollLeft = event.currentTarget.scrollLeft
                  }}
                >
                <table className="table-fixed text-left text-xs" style={{ minWidth: '100%', width: tablePixelWidth }}>
                  <colgroup>
                    {visibleTableColumns.map((column) => <col key={column} style={{ width: columnWidth(column) }} />)}
                  </colgroup>
                  <tbody>
                    {tableRows.length === 0 ? (
                    <tr>
                      <td className="px-3 py-10 text-center text-ink-subtle" colSpan={visibleTableColumns.length}>沒有資料</td>
                    </tr>
                    ) : tableRows.map((row, rowIndex) => (
                      <tr key={rowIndex} className="border-t border-border hover:bg-surface-alt/70">
                        {visibleTableColumns.map((column) => {
                          const value = renderValue(row[column])
                          const isLong = value.length > LONG_TABLE_VALUE_LENGTH
                          return (
                            <td key={column} className="px-3 py-2 text-ink-secondary align-top">
                              <div className={cn('relative whitespace-pre-wrap break-words', isLong && 'pb-6')}>
                                <span className={cn('block', isLong && 'max-h-32 overflow-hidden')}>{value}</span>
                                {isLong && (
                                  <button
                                    type="button"
                                    onClick={() => setExpandedTableValue({ column, value })}
                                    title="在大視窗檢視完整內容"
                                    aria-label={`檢視 ${column} 的完整內容`}
                                    className="absolute bottom-0 right-0 flex h-5 w-5 items-center justify-center rounded border border-border bg-surface text-ink-secondary shadow-sm hover:border-brand/40 hover:text-brand"
                                  >
                                    <Maximize2 className="h-3 w-3" />
                                  </button>
                                )}
                              </div>
                            </td>
                          )
                        })}
                      </tr>
                    ))}
                  </tbody>
                </table>
                </div>
              </div>
            </div>
          )}

          {viewMode === 'raw' && (
            <div className="min-h-0 flex-1 overflow-auto bg-surface p-4">
              <div className="mb-3">
                <h2 className="text-sm font-semibold text-ink">Viewer JSON</h2>
                <p className="text-[11px] text-ink-subtle">供除錯、匯出與確認後端橋接結果使用；一般探索建議使用關聯圖或資料表。</p>
              </div>
              <pre className="min-h-full overflow-auto rounded-lg border border-border bg-input-bg p-4 text-xs leading-relaxed text-ink-secondary">
                {JSON.stringify(queryResult ?? { schema, graph }, null, 2)}
              </pre>
            </div>
          )}
        </main>

        <aside
          style={inspectorPanel.panelStyle}
          className="relative min-h-0 shrink-0 border-l border-border bg-surface flex flex-col transition-[width] duration-200"
        >
          <button
            type="button"
            aria-label={inspectorPanel.collapsed ? '展開選取內容' : '收合選取內容'}
            aria-expanded={!inspectorPanel.collapsed}
            title={inspectorPanel.collapsed ? '展開選取內容' : '收合選取內容'}
            onClick={inspectorPanel.toggleCollapsed}
            className="absolute left-0 top-4 z-30 flex h-7 w-7 -translate-x-1/2 items-center justify-center rounded-full border border-border bg-surface text-ink-secondary shadow-sm transition-colors hover:bg-surface-alt hover:text-ink"
          >
            {inspectorPanel.collapsed
              ? <ChevronLeft className="h-3.5 w-3.5" />
              : <ChevronRight className="h-3.5 w-3.5" />}
          </button>
          {!inspectorPanel.collapsed && (
            <div
              {...inspectorPanel.resizeHandleProps}
              title="拖曳調整寬度；雙擊收合"
              className="absolute bottom-0 left-0 top-0 z-20 w-1 -translate-x-1/2 cursor-col-resize touch-none outline-none hover:bg-brand/40 focus:bg-brand/50"
            />
          )}
          {inspectorPanel.collapsed ? (
            <div className="flex flex-1 items-start justify-center pt-14" title="選取內容已收合">
              <Network className="h-4 w-4 text-ink-subtle" />
            </div>
          ) : (
          <>
          <div className="shrink-0 border-b border-border px-4 py-3">
            <p className="text-xs font-semibold uppercase tracking-wide text-ink-secondary">選取內容</p>
            <p className="mt-1 text-[10px] text-ink-subtle">點選關聯圖中的節點或連線，以查看屬性與展開關係。</p>
          </div>
          <div className="min-h-0 flex-1 overflow-y-auto p-4 space-y-4">
            <section className="space-y-3">
              {selectedNode ? (
                <div className="space-y-3">
                  <div>
                    <p className="text-sm font-semibold text-ink break-words">{selectedNode.caption}</p>
                    <p className="text-xs text-brand mt-1">{nodeCategory(selectedNode)}</p>
                  </div>
                  {schema?.capabilities.neighbors && (
                    <div className="grid grid-cols-3 gap-1.5">
                      <Button variant="ghost" size="sm" disabled={selectedNodeExpanded} onClick={() => void expandNodes('all')}>
                        {selectedNodeExpanded ? '已展開' : '鄰居'}
                      </Button>
                      <Button variant="ghost" size="sm" disabled={selectedNodeExpanded} onClick={() => void expandNodes('in')}>傳入</Button>
                      <Button variant="ghost" size="sm" disabled={selectedNodeExpanded} onClick={() => void expandNodes('out')}>傳出</Button>
                    </div>
                  )}
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
                <div className="rounded-lg border border-dashed border-border bg-surface-alt px-3 py-6 text-center">
                  <Network className="mx-auto h-5 w-5 text-ink-subtle" />
                  <p className="mt-2 text-xs text-ink-secondary">尚未選取節點或關係</p>
                </div>
              )}
              {(selectedNode || selectedLink) && schema?.capabilities.rawQuery &&
                schema.queryTemplates.some((template) =>
                  template.target === (selectedNode ? 'node' : 'edge')) && (
                <Button variant="outline" size="sm" className="w-full" onClick={openSelectionQuery}>
                  以 Cypher 查詢此{selectedNode ? '節點' : '關係'}
                </Button>
              )}
            </section>

            {schema?.capabilities.rawQuery && (
              <details
                open={queryPanelOpen}
                onToggle={(event) => setQueryPanelOpen(event.currentTarget.open)}
                className="group rounded-lg border border-border bg-surface-alt"
              >
                <summary className="flex cursor-pointer list-none items-center justify-between px-3 py-2 text-xs font-medium text-ink-secondary">
                  Cypher 查詢（進階）
                  <ChevronDown className="h-3.5 w-3.5 transition-transform group-open:rotate-180" />
                </summary>
                <div className="space-y-2 border-t border-border p-3">
                  <p className="text-[10px] leading-relaxed text-ink-subtle">
                    直接查詢 Neo4j 中目前專案的實體圖譜。一般探索請使用搜尋與篩選；系統會自動套用結果筆數上限，不需要撰寫 LIMIT。
                  </p>
                  {schema.queryTemplates.length > 0 && (
                    <div className="flex flex-wrap gap-1.5">
                      {schema.queryTemplates.filter((template) =>
                        !template.target || template.target === 'manual').map((template) => (
                        <button
                          key={template.id}
                          type="button"
                          onClick={() => {
                            setQueryText(template.text)
                            setQueryError(null)
                            setQueryMessage(null)
                          }}
                          className="rounded-md border border-border bg-surface px-2 py-1 text-[10px] text-ink-secondary hover:border-brand/40 hover:text-ink"
                        >
                          填入「{template.label}」範例
                        </button>
                      ))}
                    </div>
                  )}
                  <textarea
                    value={queryText}
                    onChange={(event) => {
                      setQueryText(event.target.value)
                      setQueryError(null)
                      setQueryMessage(null)
                    }}
                    onKeyDown={(event) => {
                      if (event.key !== 'Enter' || event.shiftKey || event.nativeEvent.isComposing) return
                      event.preventDefault()
                      void runQuery()
                    }}
                    placeholder="按上方按鈕填入範例，或在此輸入唯讀 Cypher…"
                    spellCheck={false}
                    className="h-44 w-full resize-y rounded-lg border border-border bg-input-bg px-3 py-2 font-mono text-xs leading-relaxed text-ink focus:outline-none focus:ring-2 focus:ring-brand/30"
                  />
                  <p className="text-[10px] leading-relaxed text-ink-subtle">
                    {schema.queryHelp}
                  </p>
                  <Button variant="primary" size="sm" className="w-full" onClick={runQuery} isLoading={querying} leftIcon={<Play className="w-3.5 h-3.5" />}>
                    執行唯讀查詢
                  </Button>
                  {queryError && (
                    <div className="rounded-md border border-error/30 bg-error/10 px-2.5 py-2 text-[10px] leading-relaxed text-error">
                      {queryError}
                    </div>
                  )}
                  {queryMessage && (
                    <div className="rounded-md border border-brand/25 bg-brand/10 px-2.5 py-2 text-[10px] leading-relaxed text-brand">
                      {queryMessage}
                    </div>
                  )}
                </div>
              </details>
            )}
          </div>
          </>
          )}
        </aside>
      </div>

      {expandedTableValue && (
        <div
          className="fixed inset-0 z-[110] flex items-center justify-center bg-black/45 p-6"
          onMouseDown={(event) => {
            if (event.target === event.currentTarget) setExpandedTableValue(null)
          }}
        >
          <div
            role="dialog"
            aria-modal="true"
            aria-label={`${expandedTableValue.column} 完整內容`}
            className="flex max-h-[85vh] w-full max-w-4xl flex-col overflow-hidden rounded-xl border border-border bg-surface shadow-2xl"
          >
            <div className="flex shrink-0 items-center justify-between gap-3 border-b border-border px-4 py-3">
              <div className="min-w-0">
                <p className="text-sm font-semibold text-ink">完整內容</p>
                <p className="truncate text-[11px] text-ink-subtle">欄位：{expandedTableValue.column}</p>
              </div>
              <button
                type="button"
                onClick={() => setExpandedTableValue(null)}
                aria-label="關閉完整內容"
                className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg text-ink-secondary hover:bg-surface-alt hover:text-ink"
              >
                <X className="h-4 w-4" />
              </button>
            </div>
            <pre className="min-h-0 flex-1 overflow-auto whitespace-pre-wrap break-words bg-input-bg p-4 text-xs leading-relaxed text-ink-secondary">
              {expandedTableValue.value}
            </pre>
          </div>
        </div>
      )}

      {contextMenu && (
        <div
          className="fixed z-[90] min-w-[156px] rounded-lg border border-border bg-surface py-1 text-ink shadow-2xl"
          style={{ top: contextMenu.y, left: contextMenu.x }}
          onClick={(event) => event.stopPropagation()}
        >
          {[
            ['all', '展開全部鄰居'],
            ['in', '展開傳入關係'],
            ['out', '展開傳出關係'],
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
