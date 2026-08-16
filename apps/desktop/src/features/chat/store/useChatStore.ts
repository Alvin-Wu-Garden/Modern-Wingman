import { create } from 'zustand'
import {
  createConversation,
  createProjectConversation,
  deleteConversation,
  getConversation,
  listConversations,
  listProjectConversations,
  renameConversation,
  sendMessage,
  type AttachmentReference,
  type ConversationSummary,
  type ConversationStreamError,
  type MessageItem,
  type AgentActivityEvent,
} from '@/services/agent-api/client'

export interface LocalMessage extends MessageItem {
  streaming?: boolean
  /** 串流在已有部分文字後中斷；不得把內容視為完整回答。 */
  incomplete?: boolean
  /** 本次串流期間的活動，不會寫入對話歷史。 */
  activities?: AgentActivityEvent[]
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
  lastError: ConversationStreamError | null
  lastFailedRequest: FailedRequest | null

  loadConversations: (projectId?: string | null) => Promise<void>
  openConversation: (id: string, projectId?: string | null) => Promise<void>
  startNewConversation: (
    projectId?: string | null,
    profileId?: string | null,
  ) => Promise<string>
  deleteConv: (id: string, projectId?: string | null) => Promise<void>
  renameConv: (id: string, title: string, projectId?: string | null) => Promise<void>
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

const unexpectedError = (error: unknown): ConversationStreamError => ({
  message: errorMessage(error),
  code: 'client_error',
  retryable: false,
  stage: null,
})

export const useChatStore = create<ChatState>((set, get) => ({
  conversations: [],
  activeConversationId: null,
  messages: [],
  isLoadingList: false,
  isStreaming: false,
  lastError: null,
  lastFailedRequest: null,

  loadConversations: async (projectId = null) => {
    set({ isLoadingList: true })
    try {
      const loaded = projectId
        ? await listProjectConversations(projectId)
        : await listConversations()
      set((state) => {
        // 一般與專案列表分開載入，但在同一個 store 保留已載入的其他上下文。
        const retained = projectId
          ? state.conversations.filter((item) => item.projectId !== projectId)
          : state.conversations.filter((item) => item.projectId !== null)
        return { conversations: [...retained, ...loaded] }
      })
    } catch (error) {
      set({ lastError: unexpectedError(error) })
    } finally {
      set({ isLoadingList: false })
    }
  },

  openConversation: async (id, projectId) => {
    if (get().activeConversationId === id) return
    const knownConversation = get().conversations.find((item) => item.id === id)
    const resolvedProjectId = projectId === undefined
      ? knownConversation?.projectId ?? null
      : projectId
    const conversation = await getConversation(id, resolvedProjectId)
    set({
      activeConversationId: id,
      messages: conversation.messages,
      lastError: null,
    })
  },

  startNewConversation: async (projectId = null, profileId = null) => {
    const conversation = projectId
      ? await createProjectConversation(projectId, profileId)
      : await createConversation(profileId)
    set((state) => ({
      conversations: [conversation, ...state.conversations],
      activeConversationId: conversation.id,
      messages: [],
      lastError: null,
    }))
    return conversation.id
  },

  deleteConv: async (id, projectId) => {
    const knownConversation = get().conversations.find((item) => item.id === id)
    const resolvedProjectId = projectId === undefined
      ? knownConversation?.projectId ?? null
      : projectId
    await deleteConversation(id, resolvedProjectId)
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

  renameConv: async (id, title, projectId) => {
    const knownConversation = get().conversations.find((item) => item.id === id)
    const resolvedProjectId = projectId === undefined
      ? knownConversation?.projectId ?? null
      : projectId
    await renameConversation(id, title, resolvedProjectId)
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
    const activeConversation = get().conversations.find(
      (item) => item.id === activeConversationId,
    )
    const projectId = activeConversation?.projectId ?? null

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
      activities: [],
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
            lastFailedRequest: null,
          }))
          void get().loadConversations(projectId)
        },
        onError: (error) => set((state) => ({
          messages: state.messages.map((message) => {
            if (!message.streaming) return message
            // 已經收到模型文字時保留部分回答，只結束串流並由錯誤區顯示
            // 「回答未完成」。沒有任何文字時才在對話泡泡中顯示錯誤。
            return {
              ...message,
              content: message.content.trim().length > 0
                ? message.content
                : `[錯誤：${error.message}]`,
              streaming: false,
              incomplete: message.content.trim().length > 0,
            }
          }),
          isStreaming: false,
          lastError: error,
          lastFailedRequest: error.retryable
            ? {
                text: userMessage,
                profileId,
                modelId,
                attachments,
              }
            : null,
        })),
        onActivity: (activity) => set((state) => ({
          messages: state.messages.map((message) => {
            if (!message.streaming) return message
            const activities = [...(message.activities ?? [])]
            const existingIndex = activities.findIndex((item) =>
              item.activityId === activity.activityId)
            if (existingIndex < 0) {
              activities.push(activity)
            } else {
              activities[existingIndex] = {
                ...activities[existingIndex],
                ...activity,
              }
            }
            return { ...message, activities }
          }),
        })),
      },
      abortController.signal,
      projectId,
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
