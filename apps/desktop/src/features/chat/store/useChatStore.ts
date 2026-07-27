import { create } from 'zustand'
import {
  createConversation,
  deleteConversation,
  getConversation,
  listConversations,
  renameConversation,
  sendMessage,
  type AttachmentReference,
  type ConversationScope,
  type ConversationSummary,
  type MessageItem,
} from '@/services/agent-api/client'

export interface LocalMessage extends MessageItem {
  streaming?: boolean
}

interface FailedRequest {
  text: string
  profileId: string | null
  modelId: string | null
  attachments: AttachmentReference[]
}

interface ChatState {
  conversations: ConversationSummary[]
  activeConversationId: string | null
  messages: LocalMessage[]
  isLoadingList: boolean
  isStreaming: boolean
  lastError: string | null
  lastFailedRequest: FailedRequest | null

  loadConversations: () => Promise<void>
  openConversation: (id: string) => Promise<void>
  startNewConversation: (
    scope?: ConversationScope,
    projectId?: string | null,
    profileId?: string | null,
  ) => Promise<string>
  deleteConv: (id: string) => Promise<void>
  renameConv: (id: string, title: string) => Promise<void>
  send: (
    userMessage: string,
    profileId?: string | null,
    modelId?: string | null,
    attachments?: AttachmentReference[],
  ) => Promise<void>
  cancelStreaming: () => void
  retryLast: (
    profileId?: string | null,
    modelId?: string | null,
  ) => Promise<void>
  clearLastError: () => void
}

let abortController: AbortController | null = null

const errorMessage = (error: unknown) =>
  error instanceof Error ? error.message : String(error)

export const useChatStore = create<ChatState>((set, get) => ({
  conversations: [],
  activeConversationId: null,
  messages: [],
  isLoadingList: false,
  isStreaming: false,
  lastError: null,
  lastFailedRequest: null,

  loadConversations: async () => {
    set({ isLoadingList: true })
    try {
      set({ conversations: await listConversations() })
    } catch (error) {
      set({ lastError: errorMessage(error) })
    } finally {
      set({ isLoadingList: false })
    }
  },

  openConversation: async (id) => {
    if (get().activeConversationId === id) return
    const conversation = await getConversation(id)
    set({
      activeConversationId: id,
      messages: conversation.messages,
      lastError: null,
    })
  },

  startNewConversation: async (
    scope = 'general',
    projectId = null,
    profileId = null,
  ) => {
    const conversation = await createConversation(scope, projectId, profileId)
    set((state) => ({
      conversations: [conversation, ...state.conversations],
      activeConversationId: conversation.id,
      messages: [],
      lastError: null,
    }))
    return conversation.id
  },

  deleteConv: async (id) => {
    await deleteConversation(id)
    set((state) => {
      const conversations = state.conversations.filter((item) => item.id !== id)
      return {
        conversations,
        activeConversationId:
          state.activeConversationId === id ? null : state.activeConversationId,
        messages: state.activeConversationId === id ? [] : state.messages,
      }
    })
  },

  renameConv: async (id, title) => {
    await renameConversation(id, title)
    set((state) => ({
      conversations: state.conversations.map((conversation) =>
        conversation.id === id ? { ...conversation, title } : conversation),
    }))
  },

  send: async (
    userMessage,
    profileId = null,
    modelId = null,
    attachments = [],
  ) => {
    const { activeConversationId, isStreaming } = get()
    if (!activeConversationId || isStreaming ||
        (!userMessage.trim() && attachments.length === 0)) return

    const timestamp = Date.now()
    const user: LocalMessage = {
      id: `local-${timestamp}`,
      role: 'user',
      content: userMessage || `[附件：${attachments.map((item) => item.name).join('、')}]`,
      createdAt: new Date().toISOString(),
    }
    const assistant: LocalMessage = {
      id: `streaming-${timestamp}`,
      role: 'assistant',
      content: '',
      createdAt: new Date().toISOString(),
      streaming: true,
    }
    set((state) => ({
      messages: [...state.messages, user, assistant],
      isStreaming: true,
      lastError: null,
    }))

    abortController = new AbortController()
    await sendMessage(
      activeConversationId,
      userMessage,
      profileId,
      modelId,
      attachments,
      {
        onToken: (token) => set((state) => ({
          messages: state.messages.map((message) =>
            message.streaming
              ? { ...message, content: message.content + token }
              : message),
        })),
        onDone: () => {
          set((state) => ({
            messages: state.messages.map((message) =>
              message.streaming ? { ...message, streaming: false } : message),
            isStreaming: false,
          }))
          void get().loadConversations()
        },
        onError: (error) => set((state) => ({
          messages: state.messages.map((message) =>
            message.streaming
              ? { ...message, content: `[錯誤：${error}]`, streaming: false }
              : message),
          isStreaming: false,
          lastError: error,
          lastFailedRequest: {
            text: userMessage,
            profileId,
            modelId,
            attachments,
          },
        })),
      },
      abortController.signal,
    )
  },

  cancelStreaming: () => {
    abortController?.abort()
    set((state) => ({
      messages: state.messages.map((message) =>
        message.streaming ? { ...message, streaming: false } : message),
      isStreaming: false,
    }))
  },

  retryLast: async (profileId, modelId) => {
    const request = get().lastFailedRequest
    if (!request) return
    await get().send(
      request.text,
      profileId === undefined ? request.profileId : profileId,
      modelId === undefined ? request.modelId : modelId,
      request.attachments,
    )
  },

  clearLastError: () => set({ lastError: null }),
}))
