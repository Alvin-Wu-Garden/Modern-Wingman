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
  conversationId: string
  projectId: string | null
  turnId: string
  text: string
  profileId: string | null
  modelId: string | null
  attachments: AttachmentReference[]
}

interface ActiveConversationRun {
  conversationId: string
  projectId: string | null
  turnId: string
  assistantMessageId: string
  controller: AbortController
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
    options?: {
      conversationId?: string
      projectId?: string | null
      turnId?: string
    },
  ) => Promise<void>
  cancelStreaming: () => void
  retryLast: (
    profileId?: string | null,
    modelId?: string | null,
  ) => Promise<void>
  clearLastError: () => void
}

let activeRun: ActiveConversationRun | null = null
let openGeneration = 0
const listGenerations = new Map<string, number>()

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
    const listKey = projectId ?? 'general'
    const generation = (listGenerations.get(listKey) ?? 0) + 1
    listGenerations.set(listKey, generation)
    set({ isLoadingList: true })
    try {
      const loaded = projectId
        ? await listProjectConversations(projectId)
        : await listConversations()
      if (listGenerations.get(listKey) !== generation) return
      set((state) => {
        // 一般與專案列表分開載入，但在同一個 store 保留已載入的其他上下文。
        const retained = projectId
          ? state.conversations.filter((item) => item.projectId !== projectId)
          : state.conversations.filter((item) => item.projectId !== null)
        return { conversations: [...retained, ...loaded] }
      })
    } catch (error) {
      // 舊的列表請求不能覆蓋較新的串流錯誤或列表結果；只讓目前 generation
      // 的失敗更新共用錯誤狀態。
      if (listGenerations.get(listKey) === generation)
        set({ lastError: unexpectedError(error) })
    } finally {
      if (listGenerations.get(listKey) === generation)
        set({ isLoadingList: false })
    }
  },

  openConversation: async (id, projectId) => {
    if (activeRun && activeRun.conversationId !== id) return
    if (get().activeConversationId === id) return
    const knownConversation = get().conversations.find((item) => item.id === id)
    const resolvedProjectId = projectId === undefined
      ? knownConversation?.projectId ?? null
      : projectId
    const generation = ++openGeneration
    const conversation = await getConversation(id, resolvedProjectId)
    if (generation !== openGeneration) return
    set({
      activeConversationId: id,
      messages: conversation.messages,
      lastError: null,
    })
  },

  startNewConversation: async (projectId = null, profileId = null) => {
    if (activeRun) return get().activeConversationId ?? ''
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
    if (activeRun) return
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
    if (activeRun) return
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
    options,
  ) => {
    const { isStreaming } = get()
    const activeConversationId = options?.conversationId ?? get().activeConversationId
    if (!activeConversationId || isStreaming ||
        (!userMessage.trim() && attachments.length === 0)) return
    const activeConversation = get().conversations.find(
      (item) => item.id === activeConversationId,
    )
    const projectId = options?.projectId ?? activeConversation?.projectId ?? null
    if (options?.conversationId && get().activeConversationId !== options.conversationId)
      return

    const turnId = options?.turnId ?? crypto.randomUUID()
    const user: LocalMessage = {
      id: `local-${turnId}`,
      role: 'user',
      content: userMessage || `[附件：${attachments.map((item) => item.name).join('、')}]`,
      createdAt: new Date().toISOString(),
    }
    const assistant: LocalMessage = {
      id: `streaming-${turnId}`,
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

    const controller = new AbortController()
    activeRun = {
      conversationId: activeConversationId,
      projectId,
      turnId,
      assistantMessageId: assistant.id,
      controller,
    }
    const isCurrentRun = () =>
      activeRun?.conversationId === activeConversationId &&
      activeRun.projectId === projectId &&
      activeRun.turnId === turnId
    await sendMessage(
      activeConversationId,
      userMessage,
      profileId,
      modelId,
      attachments,
      {
        onToken: (token) => {
          if (!isCurrentRun()) return
          set((state) => ({
            messages: state.messages.map((message) =>
              message.id === assistant.id && message.streaming
                ? { ...message, content: message.content + token }
                : message),
          }))
        },
        onDone: () => {
          if (!isCurrentRun()) return
          activeRun = null
          set((state) => ({
            messages: state.messages.map((message) =>
              message.id === assistant.id ? { ...message, streaming: false } : message),
            isStreaming: false,
            lastFailedRequest: null,
          }))
          void get().loadConversations(projectId)
        },
        onError: (error) => {
          if (!isCurrentRun()) return
          activeRun = null
          set((state) => ({
          messages: state.messages.map((message) => {
            if (message.id !== assistant.id) return message
            if (!message.streaming) return message
            // 錯誤不能被包裝成 Assistant 正常回答；無論是否已有部分文字，
            // 都只保留已收到的內容，並由錯誤區顯示結構化錯誤。
            return {
              ...message,
              content: message.content,
              streaming: false,
              incomplete: true,
            }
          }),
          isStreaming: false,
          lastError: error,
          lastFailedRequest: error.retryable
            ? {
                text: userMessage,
                conversationId: activeConversationId,
                projectId,
                turnId,
                profileId,
                modelId,
                attachments,
              }
            : null,
        }))
        },
        onActivity: (activity) => {
          if (!isCurrentRun()) return
          set((state) => ({
          messages: state.messages.map((message) => {
            if (message.id !== assistant.id || !message.streaming) return message
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
        }))
        },
      },
      controller.signal,
      projectId,
      turnId,
    )
    if (activeRun?.turnId === turnId) {
      // sendMessage 會在正常 terminal/error 時清除 activeRun；這裡只處理
      // 連線層沒有回呼的極端情況，避免 UI 永遠維持忙碌，並且不能留下
      // 看似仍在串流中的 Assistant 訊息。
      activeRun = null
      set((state) => ({
        messages: state.messages.map((message) =>
          message.id === assistant.id && message.streaming
            ? { ...message, streaming: false, incomplete: true }
            : message),
        isStreaming: false,
      }))
    }
  },

  cancelStreaming: () => {
    const run = activeRun
    activeRun = null
    run?.controller.abort()
    set((state) => ({
      messages: state.messages.map((message) =>
        message.id === run?.assistantMessageId
          ? { ...message, streaming: false, incomplete: message.content.length > 0 }
          : message),
      isStreaming: false,
    }))
  },

  retryLast: async (profileId, modelId) => {
    const request = get().lastFailedRequest
    if (!request) return
    if (get().activeConversationId !== request.conversationId) {
      await get().openConversation(request.conversationId, request.projectId)
    }
    await get().send(
      request.text,
      profileId === undefined ? request.profileId : profileId,
      modelId === undefined ? request.modelId : modelId,
      request.attachments,
      {
        conversationId: request.conversationId,
        projectId: request.projectId,
        turnId: request.turnId,
      },
    )
  },

  clearLastError: () => set({ lastError: null }),
}))
