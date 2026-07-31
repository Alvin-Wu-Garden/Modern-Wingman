import {
  type CSSProperties,
  type KeyboardEvent,
  type PointerEvent as ReactPointerEvent,
  useCallback,
  useEffect,
  useRef,
  useState,
} from 'react'

interface ResizablePanelOptions {
  storageKey: string
  defaultWidth: number
  minWidth: number
  maxWidth: number
  collapsedWidth?: number
}

interface StoredPanelPreference {
  collapsed?: boolean
  width?: number
}

const clamp = (value: number, min: number, max: number) =>
  Math.min(max, Math.max(min, value))

function readPreference(
  storageKey: string,
  defaultWidth: number,
  minWidth: number,
  maxWidth: number,
) {
  const fallback = { collapsed: false, width: defaultWidth }
  if (typeof window === 'undefined') return fallback

  try {
    const stored = JSON.parse(
      window.localStorage.getItem(storageKey) ?? '{}',
    ) as StoredPanelPreference
    return {
      collapsed: stored.collapsed === true,
      width: clamp(stored.width ?? defaultWidth, minWidth, maxWidth),
    }
  } catch {
    return fallback
  }
}

/**
 * Shared horizontal panel sizing behavior for the app navigation and contextual
 * sidebars. Width and collapsed state are persisted independently per panel.
 */
export function useResizablePanel({
  storageKey,
  defaultWidth,
  minWidth,
  maxWidth,
  collapsedWidth = 64,
}: ResizablePanelOptions) {
  const initialPreference = useRef(
    readPreference(storageKey, defaultWidth, minWidth, maxWidth),
  )
  const [width, setWidth] = useState(initialPreference.current.width)
  const [collapsed, setCollapsed] = useState(initialPreference.current.collapsed)
  const dragCleanupRef = useRef<(() => void) | null>(null)

  useEffect(() => {
    try {
      window.localStorage.setItem(storageKey, JSON.stringify({ collapsed, width }))
    } catch {
      // A disabled/full localStorage should not prevent the sidebar from working.
    }
  }, [collapsed, storageKey, width])

  useEffect(() => () => dragCleanupRef.current?.(), [])

  const toggleCollapsed = useCallback(() => {
    setCollapsed((current) => !current)
  }, [])

  const resetWidth = useCallback(() => {
    setWidth(defaultWidth)
  }, [defaultWidth])

  const startResize = useCallback((
    event: ReactPointerEvent<HTMLDivElement>,
  ) => {
    if (collapsed || event.button !== 0) return
    event.preventDefault()

    const startX = event.clientX
    const startWidth = width
    const previousCursor = document.body.style.cursor
    const previousUserSelect = document.body.style.userSelect

    document.body.style.cursor = 'col-resize'
    document.body.style.userSelect = 'none'

    const handleMove = (moveEvent: PointerEvent) => {
      setWidth(clamp(startWidth + moveEvent.clientX - startX, minWidth, maxWidth))
    }
    const cleanup = () => {
      window.removeEventListener('pointermove', handleMove)
      window.removeEventListener('pointerup', cleanup)
      window.removeEventListener('pointercancel', cleanup)
      document.body.style.cursor = previousCursor
      document.body.style.userSelect = previousUserSelect
      dragCleanupRef.current = null
    }

    dragCleanupRef.current?.()
    dragCleanupRef.current = cleanup
    window.addEventListener('pointermove', handleMove)
    window.addEventListener('pointerup', cleanup)
    window.addEventListener('pointercancel', cleanup)
  }, [collapsed, maxWidth, minWidth, width])

  const resizeWithKeyboard = useCallback((
    event: KeyboardEvent<HTMLDivElement>,
  ) => {
    if (collapsed) return
    const step = event.shiftKey ? 32 : 8

    if (event.key === 'ArrowLeft') {
      event.preventDefault()
      setWidth((current) => clamp(current - step, minWidth, maxWidth))
    } else if (event.key === 'ArrowRight') {
      event.preventDefault()
      setWidth((current) => clamp(current + step, minWidth, maxWidth))
    } else if (event.key === 'Home') {
      event.preventDefault()
      setWidth(minWidth)
    } else if (event.key === 'End') {
      event.preventDefault()
      setWidth(maxWidth)
    }
  }, [collapsed, maxWidth, minWidth])

  return {
    collapsed,
    panelStyle: {
      width: collapsed ? collapsedWidth : width,
    } satisfies CSSProperties,
    resetWidth,
    resizeHandleProps: {
      'aria-label': '調整側邊欄寬度',
      'aria-orientation': 'vertical' as const,
      'aria-valuemax': maxWidth,
      'aria-valuemin': minWidth,
      'aria-valuenow': width,
      onDoubleClick: resetWidth,
      onKeyDown: resizeWithKeyboard,
      onPointerDown: startResize,
      role: 'separator',
      tabIndex: collapsed ? -1 : 0,
    },
    toggleCollapsed,
    width,
  }
}
