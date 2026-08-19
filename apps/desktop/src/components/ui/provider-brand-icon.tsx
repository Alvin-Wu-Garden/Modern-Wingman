import { Github, Route, Server } from 'lucide-react'
import { cn } from '@/lib/utils'
import type { ProviderInfo } from '@/services/agent-api/client'

const PROVIDER_ICON_BY_ID: Record<string, string> = {
  'openai-byok': '/assets/icons/ChatGPT-Logo.wine.svg',
  'anthropic-byok': '/assets/icons/anthropic.svg',
  'azure-openai-byok': '/assets/icons/azure-color.svg',
}

const FRAME_SIZE = {
  xs: 'h-5 w-5 rounded-md',
  sm: 'h-6 w-6 rounded-md',
  md: 'h-7 w-7 rounded-lg',
}

const IMAGE_SIZE = {
  xs: 'h-3.5 w-3.5',
  sm: 'h-4 w-4',
  md: 'h-[18px] w-[18px]',
}

interface ProviderBrandIconProps {
  provider: Pick<ProviderInfo, 'id'>
  size?: keyof typeof FRAME_SIZE
  className?: string
}

function getProviderIconSrc(provider: Pick<ProviderInfo, 'id'>) {
  return PROVIDER_ICON_BY_ID[provider.id] ?? null
}

export function ProviderBrandIcon({
  provider,
  size = 'md',
  className,
}: ProviderBrandIconProps) {
  const iconSrc = getProviderIconSrc(provider)
  const fallbackClass = cn(IMAGE_SIZE[size], 'text-ink-secondary')

  let fallbackIcon: React.ReactNode = <Server className={fallbackClass} />
  if (provider.id === 'copilot-default') {
    fallbackIcon = <Github className={fallbackClass} />
  } else if (provider.id.includes('openrouter')) {
    fallbackIcon = <Route className={fallbackClass} />
  }

  return (
    <span
      className={cn(
        'flex shrink-0 items-center justify-center border border-border bg-white',
        FRAME_SIZE[size],
        className,
      )}
    >
      {iconSrc ? (
        <img
          src={iconSrc}
          alt=""
          className={cn(IMAGE_SIZE[size], 'object-contain')}
          draggable={false}
        />
      ) : (
        fallbackIcon
      )}
    </span>
  )
}
