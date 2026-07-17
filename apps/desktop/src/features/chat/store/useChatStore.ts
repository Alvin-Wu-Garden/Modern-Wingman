import { create } from 'zustand'
import {
  listConversations,
  createConversation,
  getConversation,
  deleteConversation,
  renameConversation,
  sendMessage,
  type ConversationSummary,
  type MessageItem,
  type PendingApproval,
  listPendingApprovals,
  resolveApproval,
  getRunChangeSet,
  restoreRunChangeSet,
  type RunChangeSet,
  type TimelineEvent,
  listRunEvents,
  updateRunChangeFiles,
  updateRunChangeHunks,
  retryRunFromSafeStep,
  type AttachmentReference,
  type PersistedRunEvent,
} from '@/services/agent-api/client'
import type { AgentMode } from '@modern-wingman/contracts'

// ── Types ─────────────────────────────────────────────────────────────────────

export interface LocalMessage extends MessageItem {
  streaming?: boolean   // true when assistant is still typing
}

interface ChatState {
  // conversation list (sidebar)
  conversations: ConversationSummary[]
  isLoadingList: boolean

  // currently open conversation
  activeConversationId: string | null
  messages: LocalMessage[]
  isStreaming: boolean
  activeRunId: string | null
  pendingApprovals: PendingApproval[]
  changeSet: RunChangeSet | null
  timeline: TimelineEvent[]
  lastError:string|null
  lastFailedRequest:{text:string;profileId:string|null;modelId:string|null;agentMode:AgentMode;attachments:AttachmentReference[];projectId:string|null;includeUncommittedChanges:boolean}|null

  // actions
  loadConversations: () => Promise<void>
  openConversation: (id: string) => Promise<void>
  startNewConversation: (profileId?: string) => Promise<string>
  deleteConv: (id: string) => Promise<void>
  renameConv: (id: string, title: string) => Promise<void>
  send: (userMessage: string, profileId?: string | null, modelId?: string | null, agentMode?: AgentMode, attachments?:AttachmentReference[],projectId?:string|null,includeUncommittedChanges?:boolean) => Promise<void>
  cancelStreaming: () => void
  decideApproval: (approvalId: string, approved: boolean) => Promise<void>
  restoreChanges: () => Promise<void>
  acceptChangeFiles: (paths:string[]) => Promise<void>
  restoreChangeFiles: (paths:string[]) => Promise<void>
  updateChangeHunks: (path:string,hunkIndexes:number[],action:'accept'|'restore') => Promise<void>
  retryLast: (profileId?:string|null,modelId?:string|null) => Promise<void>
  retryFromSafeStep: (providerProfileId?:string|null) => Promise<void>
  clearLastError:()=>void
}

function toTimelineEvents({ event }: PersistedRunEvent): TimelineEvent[] {
  const payload = JSON.parse(event.payloadJson) as Record<string, unknown>
  const base = { callId: null, timestamp: event.timestamp }
  switch (event.eventType) {
    case 'run:phase':
      return [{ ...base, type: 'phase', name: String(payload.phase ?? 'phase'), data: payload.detail }]
    case 'run:plan':
      return [{ ...base, type: 'plan', name: '實作計畫', data: payload.plan }]
    case 'run:verify':
      return [{ ...base, type: 'verify', name: `驗證 ${payload.success ? '通過' : '失敗'}`, data: payload }]
    case 'run:tool-call':
      return [{ ...base, type: 'tool_call', name: String(payload.toolName ?? 'tool'), data: payload.toolInput }]
    case 'run:tool-result':
      return [{ ...base, type: 'tool_result', name: String(payload.toolName ?? 'tool'), data: payload.result }]
    case 'run:tool-output':
      return [{ ...base, type: 'tool_result', name: `${String(payload.toolName ?? 'tool')} · ${String(payload.stream ?? 'stdout')}`, data: payload.text }]
    default:
      return []
  }
}

const MAX_TIMELINE_EVENTS = 80

function timelineKey(event: TimelineEvent) {
  return `${event.type}|${event.callId ?? ''}|${event.name ?? ''}|${event.timestamp}|${JSON.stringify(event.data)}`
}

function appendTimeline(current: TimelineEvent[], incoming: TimelineEvent[]) {
  const seen = new Set(current.map(timelineKey))
  const unique = incoming.filter((event) => {
    const key = timelineKey(event)
    if (seen.has(key)) return false
    seen.add(key)
    return true
  })
  return [...current, ...unique].slice(-MAX_TIMELINE_EVENTS)
}

// ── Store ─────────────────────────────────────────────────────────────────────

let _abortController: AbortController | null = null
let _approvalPoll: ReturnType<typeof setInterval> | null = null
let _eventPoll: ReturnType<typeof setInterval> | null = null

function stopApprovalPolling() {
  if (_approvalPoll) clearInterval(_approvalPoll)
  _approvalPoll = null
}
function stopEventPolling(){if(_eventPoll)clearInterval(_eventPoll);_eventPoll=null}

export const useChatStore = create<ChatState>((set, get) => ({
  conversations: [],
  isLoadingList: false,
  activeConversationId: null,
  messages: [],
  isStreaming: false,
  activeRunId: null,
  pendingApprovals: [],
  changeSet: null,
  timeline: [],
  lastError:null,
  lastFailedRequest:null,

  // ── Load conversation list ───────────────────────────────────────────────

  loadConversations: async () => {
    set({ isLoadingList: true })
    try {
      const list = await listConversations()
      set({ conversations: list })
    } catch {
      // fail silently — service might not be ready yet
    } finally {
      set({ isLoadingList: false })
    }
  },

  // ── Open a conversation ──────────────────────────────────────────────────

  openConversation: async (id: string) => {
    if (get().activeConversationId === id) return
    try {
      const detail = await getConversation(id)
      set({
        activeConversationId: id,
        messages: detail.messages.map((m) => ({ ...m })),
      })
    } catch (e) {
      console.error('openConversation', e)
    }
  },

  // ── Start new conversation ───────────────────────────────────────────────

  startNewConversation: async (profileId?: string) => {
    const conv = await createConversation(profileId)
    set((s) => ({
      conversations: [conv, ...s.conversations],
      activeConversationId: conv.id,
      messages: [],
    }))
    return conv.id
  },

  // ── Rename conversation ─────────────────────────────────────────────────

  renameConv: async (id: string, title: string) => {
    await renameConversation(id, title)
    set((s) => ({
      conversations: s.conversations.map((c) =>
        c.id === id ? { ...c, title } : c
      ),
    }))
  },

  // ── Delete conversation ──────────────────────────────────────────────────

  deleteConv: async (id: string) => {
    await deleteConversation(id)
    set((s) => {
      const filtered = s.conversations.filter((c) => c.id !== id)
      const wasActive = s.activeConversationId === id
      return {
        conversations: filtered,
        activeConversationId: wasActive ? (filtered[0]?.id ?? null) : s.activeConversationId,
        messages: wasActive ? [] : s.messages,
      }
    })
  },

  // ── Send message ─────────────────────────────────────────────────────────

  send: async (userMessage: string, profileId?: string | null, modelId?: string | null, agentMode: AgentMode = 'plan', attachments:AttachmentReference[] = [],projectId:string|null=null,includeUncommittedChanges=true) => {
    const { activeConversationId, isStreaming } = get()
    if (!activeConversationId || isStreaming || (!userMessage.trim()&&attachments.length===0)) return

    // Optimistic user message
    const userMsg: LocalMessage = {
      id: `local-${Date.now()}`,
      role: 'user',
      content: userMessage || `[附件：${attachments.map(item=>item.name).join('、')}]`,
      createdAt: new Date().toISOString(),
    }
    const assistantPlaceholder: LocalMessage = {
      id: `streaming-${Date.now()}`,
      role: 'assistant',
      content: '',
      createdAt: new Date().toISOString(),
      streaming: true,
    }

    set((s) => ({
      messages: [...s.messages, userMsg, assistantPlaceholder],
      isStreaming: true,
      timeline: [],
      lastError:null,
    }))

    _abortController = new AbortController()

    await sendMessage(
      activeConversationId,
      userMessage,
      profileId ?? null,
      // onToken
      (token) => {
        set((s) => ({
          messages: s.messages.map((m) =>
            m.streaming ? { ...m, content: m.content + token } : m,
          ),
        }))
      },
      // onDone
      () => {
        set((s) => ({
          messages: s.messages.map((m) =>
            m.streaming ? { ...m, streaming: false } : m,
          ),
          isStreaming: false,
        }))
        // refresh conversation list (title might have changed)
        get().loadConversations()
      },
      // onError
      (err) => {
        set((s) => ({
          messages: s.messages.map((m) =>
            m.streaming ? { ...m, content: `[錯誤：${err}]`, streaming: false } : m,
          ),
          isStreaming: false,
          lastError:err,
          lastFailedRequest:{text:userMessage,profileId:profileId??null,modelId:modelId??null,agentMode,attachments,projectId,includeUncommittedChanges},
        }))
      },
      _abortController.signal,
      modelId ?? null,
      agentMode,
      (runInfo) => {
        const runId=runInfo.runId
        if (get().activeRunId === runId) return
        set({ activeRunId: runId, changeSet: null })
        stopApprovalPolling()
        const poll = async () => {
          try {
            set({ pendingApprovals: await listPendingApprovals(runId) })
          } catch {
            // The service may be transitioning; the next poll retries.
          }
        }
        void poll()
        _approvalPoll = setInterval(() => void poll(), 800)
        stopEventPolling()
        let after = 0
        const pollEvents = async () => {
          try {
            const events = await listRunEvents(runId, after)
            if (events.length) after = events[events.length - 1]?.sequence ?? after
            const mapped = events.flatMap(toTimelineEvents)
            if (mapped.length) set((state) => ({ timeline: appendTimeline(state.timeline, mapped) }))
          } catch {
            // The service may be restarting; the next poll retries.
          }
        }
        void pollEvents()
        _eventPoll = setInterval(() => void pollEvents(), 750)
      },
      (event) => set((state)=>({timeline:appendTimeline(state.timeline,[event])})),
      attachments,
      projectId,
      includeUncommittedChanges,
    )
    stopApprovalPolling()
    stopEventPolling()
    set({ pendingApprovals: [] })
    const completedRunId = get().activeRunId
    if (completedRunId) {
      try {
        set({ changeSet: await getRunChangeSet(completedRunId) })
      } catch {
        // Changeset is optional for read-only/chat runs.
      }
    }
  },

  cancelStreaming: () => {
    _abortController?.abort()
    stopApprovalPolling()
    stopEventPolling()
    set((s) => ({
      messages: s.messages.map((m) =>
        m.streaming ? { ...m, streaming: false } : m,
      ),
      isStreaming: false,
      pendingApprovals: [],
    }))
  },

  decideApproval: async (approvalId, approved) => {
    await resolveApproval(approvalId, approved, 'once')
    set((state) => ({
      pendingApprovals: state.pendingApprovals.filter((item) => item.id !== approvalId),
    }))
  },

  restoreChanges: async () => {
    const runId = get().activeRunId
    if (!runId) return
    await restoreRunChangeSet(runId)
    set({ changeSet: null })
  },
  acceptChangeFiles: async (paths) => {
    const runId=get().activeRunId;if(!runId)return;await updateRunChangeFiles(runId,paths,'accept');set({changeSet:await getRunChangeSet(runId)})
  },
  restoreChangeFiles: async (paths) => {
    const runId=get().activeRunId;if(!runId)return;await updateRunChangeFiles(runId,paths,'restore');set({changeSet:await getRunChangeSet(runId)})
  },
  updateChangeHunks: async (path,hunkIndexes,action) => {
    const runId=get().activeRunId;if(!runId)return;await updateRunChangeHunks(runId,path,hunkIndexes,action);set({changeSet:await getRunChangeSet(runId)})
  },
  retryLast: async (profileId,modelId) => {
    const request=get().lastFailedRequest;if(!request)return;await get().send(request.text,profileId===undefined?request.profileId:profileId,modelId===undefined?request.modelId:modelId,request.agentMode,request.attachments,request.projectId,request.includeUncommittedChanges)
  },
  retryFromSafeStep: async (providerProfileId) => {
    const runId=get().activeRunId;if(!runId)return;await retryRunFromSafeStep(runId,providerProfileId);set({lastError:null})
  },
  clearLastError:()=>set({lastError:null}),
}))
