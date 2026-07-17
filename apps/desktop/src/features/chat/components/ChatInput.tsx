import { useState, memo } from 'react'
import { MessageComposer } from './MessageComposer'
import type { AgentMode } from '@modern-wingman/contracts'
import type { AttachmentReference } from '@/services/agent-api/client'

interface ChatInputProps {
  selectedProviderId: string | null
  selectedModel: string | null
  onProviderChange: (id: string | null) => void
  onModelChange: (model: string | null) => void
  isStreaming: boolean
  onSend: (text: string, attachments:AttachmentReference[]) => void
  onCancel: () => void
  agentMode: AgentMode
  onAgentModeChange: (mode: AgentMode) => void
  workspacePath?:string
}

export const ChatInput = memo(function ChatInput({
  selectedProviderId,
  selectedModel,
  onProviderChange,
  onModelChange,
  isStreaming,
  onSend,
  onCancel,
  agentMode,
  onAgentModeChange,
  workspacePath,
}: ChatInputProps) {
  const [inputValue, setInputValue] = useState('')

  return (
    <MessageComposer
      selectedProviderId={selectedProviderId}
      selectedModel={selectedModel}
      value={inputValue}
      onChange={setInputValue}
      onProviderChange={onProviderChange}
      onModelChange={onModelChange}
      onSubmit={onSend}
      onCancel={onCancel}
      busy={isStreaming}
      agentMode={agentMode}
      onAgentModeChange={onAgentModeChange}
      workspacePath={workspacePath}
    />
  )
})
