import { useEffect } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { useAppStore, type AppTheme } from '@/app/store'

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 5 * 60 * 1000,
      retry: 1,
    },
  },
})

/** Watches the Zustand theme and immediately writes data-theme to <html>. */
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
    <QueryClientProvider client={queryClient}>
      <ThemeApplier />
      {children}
    </QueryClientProvider>
  )
}
