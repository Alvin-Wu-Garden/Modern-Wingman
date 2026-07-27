import { useEffect } from 'react'
import type { ReactNode } from 'react'
import { useAppStore, type AppTheme } from '@/app/store'

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

interface AppProvidersProps {
  children: ReactNode
}

export function AppProviders({ children }: AppProvidersProps) {
  return (
    <>
      <ThemeApplier />
      {children}
    </>
  )
}
