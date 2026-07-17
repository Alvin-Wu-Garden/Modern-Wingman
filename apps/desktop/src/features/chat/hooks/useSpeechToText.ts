import { useCallback, useEffect, useRef, useState } from 'react'
import { getSpeechStatus, transcribeSpeech, type SpeechStatus } from '@/services/agent-api/speech'
import { WavRecorder } from '../lib/audio-recorder'
import { normalizeSpeechText } from '../lib/text-normalizer'

type SpeechState = 'idle' | 'recording' | 'transcribing' | 'error'

export function useSpeechToText(onText: (text: string) => void) {
  const recorderRef = useRef<WavRecorder | null>(null)
  const timeoutRef = useRef<number | null>(null)
  const stateRef = useRef<SpeechState>('idle')
  const [status, setStatus] = useState<SpeechStatus | null>(null)
  const [state, setState] = useState<SpeechState>('idle')
  const [error, setError] = useState<string | null>(null)

  const setSpeechState = useCallback((next: SpeechState) => {
    stateRef.current = next
    setState(next)
  }, [])

  const refresh = useCallback(async () => {
    try {
      const next = await getSpeechStatus()
      setStatus(next)
    } catch {
      setStatus(null)
    }
  }, [])

  useEffect(() => {
    void refresh()
    return () => {
      if (timeoutRef.current) window.clearTimeout(timeoutRef.current)
      void recorderRef.current?.cleanup()
    }
  }, [refresh])

  const stopRecording = useCallback(async () => {
    const recorder = recorderRef.current
    if (!recorder || stateRef.current !== 'recording') return
    if (timeoutRef.current) {
      window.clearTimeout(timeoutRef.current)
      timeoutRef.current = null
    }
    setSpeechState('transcribing')
    setError(null)

    try {
      const recorded = await recorder.stop()
      recorderRef.current = null
      if (recorded.durationMs < 350) {
        setSpeechState('idle')
        return
      }
      const result = await transcribeSpeech(recorded.blob)
      const text = await normalizeSpeechText(result.text)
      if (text) onText(text)
      setSpeechState('idle')
    } catch (err) {
      await recorder.cleanup().catch(() => undefined)
      recorderRef.current = null
      setError(err instanceof Error ? err.message : String(err))
      setSpeechState('error')
    }
  }, [onText, setSpeechState])

  const startRecording = useCallback(async () => {
    if (!status?.ready || stateRef.current === 'transcribing') return
    setError(null)
    const recorder = new WavRecorder()
    recorderRef.current = recorder

    try {
      await recorder.start()
      setSpeechState('recording')
      timeoutRef.current = window.setTimeout(() => {
        void stopRecording()
      }, status.maxRecordingSeconds * 1000)
    } catch (err) {
      recorderRef.current = null
      await recorder.cleanup().catch(() => undefined)
      setError(err instanceof Error ? err.message : String(err))
      setSpeechState('error')
    }
  }, [setSpeechState, status?.maxRecordingSeconds, status?.ready, stopRecording])

  const toggleRecording = useCallback(async () => {
    if (state === 'recording') {
      await stopRecording()
    } else {
      await startRecording()
    }
  }, [startRecording, state, stopRecording])

  return {
    available: !!status?.ready,
    state,
    error,
    status,
    refresh,
    toggleRecording,
  }
}
