import { useEffect } from 'react'
import type { ReactNode } from 'react'
import { getCurrentWebview } from '@tauri-apps/api/webview'
import { useAppStore, type AppTheme, ZOOM_STEP } from '@/app/store'

/** 監聽 Zustand 主題並立即同步到 html，不引入沒有使用到的全域資料框架。 */
function ThemeApplier() {
  const theme = useAppStore((s) => s.theme)

  useEffect(() => {
    const html = document.documentElement
    if (theme === 'default') {
      html.removeAttribute('data-theme')
    } else {
      html.setAttribute('data-theme', theme satisfies AppTheme)
    }
  }, [theme])

  return null
}

/**
 * 提供類似瀏覽器的整體視窗縮放：Ctrl + "+"/"-" 、Ctrl+0 重置、Ctrl+滑鼠滾輪。
 * 透過 Tauri webview.setZoom() 控制原生縮放，縮放比例持久化於 zustand + localStorage。
 */
function ZoomController() {
  const zoomLevel = useAppStore((s) => s.zoomLevel)

  useEffect(() => {
    void getCurrentWebview().setZoom(zoomLevel)
  }, [zoomLevel])

  useEffect(() => {
    const applyZoom = (next: number) => {
      useAppStore.getState().setZoomLevel(next)
    }

    const handleKeyDown = (event: KeyboardEvent) => {
      if (!event.ctrlKey && !event.metaKey) return
      const { zoomLevel: current } = useAppStore.getState()
      if (event.key === '+' || event.key === '=') {
        event.preventDefault()
        applyZoom(current + ZOOM_STEP)
      } else if (event.key === '-' || event.key === '_') {
        event.preventDefault()
        applyZoom(current - ZOOM_STEP)
      } else if (event.key === '0') {
        event.preventDefault()
        applyZoom(1.0)
      }
    }

    const handleWheel = (event: WheelEvent) => {
      if (!event.ctrlKey) return
      event.preventDefault()
      const { zoomLevel: current } = useAppStore.getState()
      applyZoom(current + (event.deltaY < 0 ? ZOOM_STEP : -ZOOM_STEP))
    }

    window.addEventListener('keydown', handleKeyDown)
    window.addEventListener('wheel', handleWheel, { passive: false })
    return () => {
      window.removeEventListener('keydown', handleKeyDown)
      window.removeEventListener('wheel', handleWheel)
    }
  }, [])

  return null
}

interface AppProvidersProps {
  children: ReactNode
}

export function AppProviders({ children }: AppProvidersProps) {
  return (
    <>
      <ThemeApplier />
      <ZoomController />
      {children}
    </>
  )
}
