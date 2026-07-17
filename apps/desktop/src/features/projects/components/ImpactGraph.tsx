import { useMemo } from 'react'
import type { ImpactResult } from '@/services/agent-api/projects'

interface LayoutNode {
  key: string
  name: string
  filePath: string | null
  x: number
  y: number
  isTarget: boolean
}

interface LayoutEdge {
  from: string
  to: string
}

const NODE_W = 168
const NODE_H = 44
const COL_GAP = 220
const ROW_GAP = 60

/**
 * 受影響呼叫鏈視覺化（P2）— 輕量 SVG 分層佈局，無第三方相依。
 * 目標在最右，呼叫者依距離往左分層。
 */
export function ImpactGraph({ result }: { result: ImpactResult }) {
  const { nodes, edges, width, height } = useMemo(() => {
    if (!result.target) return { nodes: [], edges: [], width: 0, height: 0 }

    // 距離目標的最短層數（chain 末端 = 目標）
    const depthMap = new Map<string, number>()
    depthMap.set(result.target.key, 0)
    const edgeSet = new Map<string, LayoutEdge>()

    for (const path of result.callChains) {
      const chain = path.chain
      // chain: [最外層 caller, ..., target]
      for (let i = 0; i < chain.length; i++) {
        const depth = chain.length - 1 - i
        const key = chain[i].key
        const prev = depthMap.get(key)
        if (prev === undefined || depth < prev) depthMap.set(key, depth)
        if (i < chain.length - 1) {
          const id = `${chain[i].key}→${chain[i + 1].key}`
          edgeSet.set(id, { from: chain[i].key, to: chain[i + 1].key })
        }
      }
    }

    // 依層分組（限 3 層、每層 8 個，避免爆炸）
    const byDepth = new Map<number, string[]>()
    for (const [key, depth] of depthMap) {
      if (depth > 3) continue
      if (!byDepth.has(depth)) byDepth.set(depth, [])
      const arr = byDepth.get(depth)!
      if (arr.length < 8) arr.push(key)
    }

    const maxDepth = Math.max(...byDepth.keys())
    const infoMap = new Map(
      [result.target, ...result.affectedMethods].map((n) => [n.key, n]),
    )

    const layoutNodes: LayoutNode[] = []
    let maxRows = 0
    for (const [depth, keys] of byDepth) {
      maxRows = Math.max(maxRows, keys.length)
      keys.forEach((key, row) => {
        const info = infoMap.get(key)
        layoutNodes.push({
          key,
          name: info?.name ?? key.split('.').pop() ?? key,
          filePath: info?.filePath ?? null,
          x: (maxDepth - depth) * COL_GAP + 10,
          y: row * ROW_GAP + 10,
          isTarget: depth === 0,
        })
      })
    }

    const nodeKeys = new Set(layoutNodes.map((n) => n.key))
    const layoutEdges = [...edgeSet.values()].filter(
      (e) => nodeKeys.has(e.from) && nodeKeys.has(e.to),
    )

    return {
      nodes: layoutNodes,
      edges: layoutEdges,
      width: (maxDepth + 1) * COL_GAP + 20,
      height: maxRows * ROW_GAP + 20,
    }
  }, [result])

  if (nodes.length <= 1) return null

  const nodeMap = new Map(nodes.map((n) => [n.key, n]))

  return (
    <div className="rounded-2xl border border-border bg-surface p-4 overflow-x-auto">
      <p className="text-sm font-semibold text-ink mb-3">呼叫鏈（誰會被影響）</p>
      <svg width={width} height={height} className="min-w-full">
        <defs>
          <marker id="arrow" viewBox="0 0 8 8" refX="7" refY="4" markerWidth="6" markerHeight="6" orient="auto">
            <path d="M0 0 L8 4 L0 8 z" className="fill-ink-subtle" opacity={0.5} />
          </marker>
        </defs>

        {edges.map((edge) => {
          const from = nodeMap.get(edge.from)
          const to = nodeMap.get(edge.to)
          if (!from || !to) return null
          const x1 = from.x + NODE_W
          const y1 = from.y + NODE_H / 2
          const x2 = to.x
          const y2 = to.y + NODE_H / 2
          const mx = (x1 + x2) / 2
          return (
            <path
              key={`${edge.from}-${edge.to}`}
              d={`M ${x1} ${y1} C ${mx} ${y1}, ${mx} ${y2}, ${x2} ${y2}`}
              fill="none"
              strokeWidth={1.5}
              markerEnd="url(#arrow)"
              className="stroke-ink-subtle"
              opacity={0.45}
            />
          )
        })}

        {nodes.map((node) => (
          <g key={node.key} transform={`translate(${node.x}, ${node.y})`}>
            <rect
              width={NODE_W}
              height={NODE_H}
              rx={10}
              className={node.isTarget ? 'fill-red-50 stroke-red-400' : 'fill-surface-alt stroke-ink-subtle/40'}
              strokeWidth={node.isTarget ? 2 : 1}
            />
            <text x={10} y={19} className={node.isTarget ? 'fill-red-600' : 'fill-ink'} fontSize={11} fontWeight={600} fontFamily="monospace">
              {node.name.length > 20 ? node.name.slice(0, 19) + '…' : node.name}
            </text>
            <text x={10} y={34} className="fill-ink-subtle" fontSize={9} fontFamily="monospace">
              {(node.filePath ?? '').split('/').pop()?.slice(0, 26) ?? ''}
            </text>
          </g>
        ))}
      </svg>
      <p className="text-xs text-ink-subtle mt-2">
        紅色 = 修改目標，箭頭方向 = 呼叫方向（左側呼叫者受影響）
      </p>
    </div>
  )
}
