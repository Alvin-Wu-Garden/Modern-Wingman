const BASE_URL = 'http://localhost:5002'

export interface SpeechModelSource {
  id: string
  displayName: string
  url: string
}

export interface SpeechModelInfo {
  id: string
  displayName: string
  fileName: string
  description: string
  installedSizeBytes: number | null
  installed: boolean
  recommended: boolean
  sources: SpeechModelSource[]
}

export interface SpeechStatus {
  ready: boolean
  engineAvailable: boolean
  enginePath: string | null
  modelsDirectory: string
  activeModelId: string
  language: 'auto' | 'zh-TW' | 'en'
  maxRecordingSeconds: number
  models: SpeechModelInfo[]
  message: string | null
}

export interface SpeechTranscriptionResult {
  text: string
  language: string
  durationMs: number
}

async function parseError(res: Response, fallback: string) {
  const body = await res.json().catch(() => null)
  return body?.error ?? body?.detail ?? fallback
}

export async function getSpeechStatus(): Promise<SpeechStatus> {
  const res = await fetch(`${BASE_URL}/api/speech/status`)
  if (!res.ok) throw new Error(await parseError(res, `getSpeechStatus: ${res.status}`))
  return res.json()
}

export async function saveSpeechSettings(settings: {
  language?: SpeechStatus['language']
  activeModelId?: string
}): Promise<SpeechStatus> {
  const res = await fetch(`${BASE_URL}/api/speech/settings`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(settings),
  })
  if (!res.ok) throw new Error(await parseError(res, `saveSpeechSettings: ${res.status}`))
  return res.json()
}

export async function downloadSpeechModel(modelId: string, url?: string | null): Promise<SpeechStatus> {
  const res = await fetch(`${BASE_URL}/api/speech/models/download`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ modelId, url: url || null }),
  })
  if (!res.ok) throw new Error(await parseError(res, `downloadSpeechModel: ${res.status}`))
  return res.json()
}

export async function importSpeechModel(path: string, modelId?: string): Promise<SpeechStatus> {
  const res = await fetch(`${BASE_URL}/api/speech/models/import-path`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path, modelId: modelId ?? null }),
  })
  if (!res.ok) throw new Error(await parseError(res, `importSpeechModel: ${res.status}`))
  return res.json()
}

export async function transcribeSpeech(audio: Blob): Promise<SpeechTranscriptionResult> {
  const res = await fetch(`${BASE_URL}/api/speech/transcribe`, {
    method: 'POST',
    headers: { 'Content-Type': audio.type || 'audio/wav' },
    body: audio,
  })
  if (!res.ok) throw new Error(await parseError(res, `transcribeSpeech: ${res.status}`))
  return res.json()
}
