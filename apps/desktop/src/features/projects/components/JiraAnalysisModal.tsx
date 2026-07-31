import { useEffect, useRef, useState } from 'react'
import { Loader2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Modal } from '@/components/ui/modal'
import { ProviderModelPicker } from '@/features/chat/components/ProviderModelPicker'
import {
  analyzeJiraIssue,
  atlassianErrorMessage,
  listLocalJiraFiles,
  previewJiraIssue,
  type JiraIssuePreview,
  type LocalJiraFileSummary,
} from '@/services/agent-api/atlassian'

/** 使用者輸入 HD-1128 或 NR-208（不含 INNES1 前綴）*/
const USER_INPUT_REGEX = /^(HD|NR)-[1-9][0-9]*$/
const JIRA_PREFIX = 'INNES1'

type Stage = 'input' | 'preview' | 'analyzing'

interface Props {
  projectId: string
  projectName: string
  onClose: () => void

  /** 分析完成後重新載入並跳轉至新建立的對話 */
  onConversationCreated: (conversationId: string,) => void | Promise<void>
}

export function JiraAnalysisModal({
  projectId,
  projectName,
  onClose,
  onConversationCreated,
}: Props) {
  const [stage, setStage] = useState<Stage>('input')
  const [userInput, setUserInput] = useState('')
  const [preview, setPreview] = useState<JiraIssuePreview | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [progress, setProgress] = useState<string[]>([])
  const [streamText, setStreamText] = useState('')
  const [localFiles, setLocalFiles] = useState<LocalJiraFileSummary[]>([])
  const [selectedProviderId, setSelectedProviderId] = useState<string | null>(null)
  const [selectedModelId, setSelectedModelId] = useState<string | null>(null)
  const abortRef = useRef<AbortController | null>(null)
  const [localPreviewKey, setLocalPreviewKey] = useState<string>('')

  // 嘗試載入本機測試檔案清單（Enabled=false 或目錄不存在時回傳空陣列）
  useEffect(() => {
    listLocalJiraFiles()
      .then(setLocalFiles)
      .catch(() => setLocalFiles([]))
  }, [])

  // 前端格式驗證（使用者輸入層）
  const normalizedInput = userInput.trim().toUpperCase()
  const isValidFormat = USER_INPUT_REGEX.test(normalizedInput)
  const fullKey = isValidFormat ? JIRA_PREFIX + normalizedInput : ''

  const handleReadIssue = async () => {
    if (!isValidFormat || busy) return
    setBusy(true)
    setError(null)
    try {
      const result = await previewJiraIssue(fullKey)
      setPreview(result)
      if (localFiles.length > 0 && preview?.key) {
        setLocalPreviewKey(preview.key)
      }
      setStage('preview')
    } catch (reason) {
      const msg = reason instanceof Error ? reason.message : String(reason)
      setError(atlassianErrorMessage(msg) !== `操作失敗：${msg}` ? atlassianErrorMessage(msg) : msg)
    } finally {
      setBusy(false)
    }
  }

  const handleAnalyze = async () => {
    if (!preview || busy) return
    setBusy(true)
    setError(null)
    setStage('analyzing')
    setProgress(['取得 JIRA 完整內容'])
    setStreamText('')

    abortRef.current = new AbortController()

    await analyzeJiraIssue(
      { projectId, jiraKey: fullKey, providerProfileId: selectedProviderId, modelId: selectedModelId, localFileKey: localPreviewKey },
      {
        onMeta: (_convId, _key, summary) => {
          setProgress((p) => [...p, `議題：${_key} ${summary}`])
        },
        onToken: (token) => {
          setStreamText((t) => t + token)
          // 更新進度到最後一步
          setProgress((p) => {
            const last = p[p.length - 1]
            if (!last?.startsWith('AI 生成中')) return [...p, 'AI 生成中…']
            return p
          })
        },
        onDone: (conversationId) => {
          setBusy(false)
          setProgress((p) => [...p, '分析完成！'])
          if (conversationId) {
            setTimeout(() => {
              void (async () => {
                try {
                  await onConversationCreated(conversationId)
                  onClose()
                } catch (reason) {
                  const message =
                    reason instanceof Error
                      ? reason.message
                      : String(reason)

                  setError(`分析已完成，但開啟專案對話失敗：${message}`)
                }
              })()
            }, 800)
          }
        },
        onError: (errCode) => {
          setBusy(false)
          setError(atlassianErrorMessage(errCode))
        },
      },
      abortRef.current.signal,
    )
  }

  const handleCancel = () => {
    abortRef.current?.abort()
    onClose()
  }

  return (
    <Modal open onOpenChange={(v) => !v && handleCancel()} title="分析 JIRA 議題">
      {/* ── 目前 Wingman 專案名稱 ── */}
      <p className="mb-4 text-xs text-ink-subtle">
        Wingman 專案：<span className="font-medium text-ink">{projectName}</span>
      </p>

      {/* ── 第一階段：輸入 + 預覽 ── */}
      {(stage === 'input' || stage === 'preview') && (
        <div className="space-y-4">
          <label className="block">
            <span className="text-xs font-medium text-ink-secondary">JIRA 議題編號</span>
            <input
              type="text"
              value={userInput}
              disabled={stage === 'preview' || busy}
              onChange={(e) => {
                setUserInput(e.target.value)
                setPreview(null)
                setStage('input')
                setError(null)
              }}
              placeholder="例如：HD-1128 或 NR-208"
              className="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 text-sm uppercase outline-none focus:border-brand disabled:opacity-60"
            />
            {userInput.trim() && !isValidFormat && (
              <p className="mt-1 text-xs text-red-500">
                格式不正確，請輸入 HD 或 NR 開頭、後接數字（例如：HD-1128）。
              </p>
            )}
            {isValidFormat && (
              <p className="mt-1 text-xs text-ink-subtle">
                完整 JIRA Key：<span className="font-mono font-medium">{fullKey}</span>
              </p>
            )}
          </label>

          {/* ── 本機測試檔案（LocalJiraFiles.Enabled=true 且有檔案時顯示） ── */}
          {localFiles.length > 0 && stage === 'input' && (
            <div className="rounded-lg border border-amber-200 bg-amber-50 p-3">
              <p className="mb-2 text-xs font-medium text-amber-700">本機測試檔案（開發模式）</p>
              <div className="space-y-1">
                {localFiles.map((f) => (
                  <button
                    key={f.key}
                    type="button"
                    className="flex w-full items-start gap-2 rounded px-2 py-1.5 text-left hover:bg-amber-100 disabled:opacity-50"
                    disabled={busy}
                    onClick={() => {
                      // 從完整 key (INNES1HD-1504) 取出去除前綴的部分 (HD-1504)
                      const shortKey = f.key.replace(/^INNES1/, '')
                      setUserInput(shortKey)
                      setPreview(null)
                      setStage('input')
                      setError(null)
                    }}
                  >
                    <span className="shrink-0 font-mono text-xs font-semibold text-amber-800">{f.key}</span>
                    <span className="truncate text-xs text-amber-700">{f.summary}</span>
                  </button>
                ))}
              </div>
            </div>
          )}

          {error && <p className="rounded-lg bg-red-50 px-3 py-2 text-xs text-red-600">{error}</p>}

          {/* ── 預覽資訊 ── */}
          {stage === 'preview' && preview && (
            <div className="rounded-xl border border-border bg-surface-alt p-4 space-y-1.5 text-sm">
              <div className="flex items-center justify-between">
                <span className="font-mono text-xs font-semibold text-brand">{preview.key}</span>
                <span className="text-xs text-ink-subtle">{preview.issueType}</span>
              </div>
              <p className="font-medium text-ink leading-snug">{preview.summary}</p>
              <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-ink-secondary">
                <span>狀態：{preview.status}</span>
                {preview.priority && <span>優先：{preview.priority}</span>}
                {preview.assignee && <span>負責人：{preview.assignee}</span>}
                {preview.updated && <span>更新：{preview.updated.slice(0, 10)}</span>}
                <span>專案：{preview.projectName}</span>
              </div>
            </div>
          )}

          {/* ── 模型選擇（預覽時顯示）── */}
          {stage === 'preview' && (
            <div>
              <p className="mb-1.5 text-xs font-medium text-ink-secondary">使用模型</p>
              <ProviderModelPicker
                selectedProviderId={selectedProviderId}
                selectedModel={selectedModelId}
                onProviderChange={setSelectedProviderId}
                onModelChange={setSelectedModelId}
              />
            </div>
          )}

          <div className="flex justify-end gap-2">
            <Button variant="ghost" onClick={handleCancel} disabled={busy}>取消</Button>
            {stage === 'input' && (
              <Button
                onClick={() => void handleReadIssue()}
                disabled={!isValidFormat || busy}
              >
                {busy ? <><Loader2 className="mr-1.5 h-4 w-4 animate-spin" />讀取中…</> : '讀取議題'}
              </Button>
            )}
            {stage === 'preview' && (
              <>
                <Button variant="outline" onClick={() => setStage('input')}>重新輸入</Button>
                <Button onClick={() => void handleAnalyze()} disabled={busy}>
                  確認分析
                </Button>
              </>
            )}
          </div>
        </div>
      )}

      {/* ── 第二階段：分析進度 ── */}
      {stage === 'analyzing' && (
        <div className="space-y-4">
          <div className="space-y-1.5">
            {progress.map((step, i) => (
              <div key={i} className="flex items-center gap-2 text-sm">
                {i === progress.length - 1 && busy
                  ? <Loader2 className="h-3.5 w-3.5 shrink-0 animate-spin text-brand" />
                  : <span className="h-3.5 w-3.5 shrink-0 text-center text-xs text-brand-green">✓</span>
                }
                <span className={i === progress.length - 1 ? 'text-ink' : 'text-ink-secondary'}>
                  {step}
                </span>
              </div>
            ))}
          </div>

          {streamText && (
            <div className="max-h-56 overflow-y-auto rounded-lg border border-border bg-surface-alt p-3 text-xs text-ink-secondary font-mono whitespace-pre-wrap">
              {streamText.slice(-2000)}{/* 只顯示最後 2000 字元避免 DOM 過大 */}
            </div>
          )}

          {error && <p className="rounded-lg bg-red-50 px-3 py-2 text-xs text-red-600">{error}</p>}

          <div className="flex justify-end">
            <Button variant="ghost" onClick={handleCancel} disabled={!busy}>
              取消分析
            </Button>
          </div>
        </div>
      )}
    </Modal>
  )
}
