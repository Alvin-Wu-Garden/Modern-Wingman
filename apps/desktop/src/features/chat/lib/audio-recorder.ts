export interface RecordedAudio {
  blob: Blob
  durationMs: number
}

export class WavRecorder {
  private stream: MediaStream | null = null
  private audioContext: AudioContext | null = null
  private processor: ScriptProcessorNode | null = null
  private source: MediaStreamAudioSourceNode | null = null
  private chunks: Float32Array[] = []
  private startedAt = 0

  async start() {
    this.stopTracks()
    this.chunks = []
    this.stream = await navigator.mediaDevices.getUserMedia({
      audio: {
        echoCancellation: true,
        noiseSuppression: true,
        channelCount: 1,
      },
    })
    this.audioContext = new AudioContext()
    this.source = this.audioContext.createMediaStreamSource(this.stream)
    this.processor = this.audioContext.createScriptProcessor(4096, 1, 1)

    this.processor.onaudioprocess = (event) => {
      const input = event.inputBuffer.getChannelData(0)
      this.chunks.push(new Float32Array(input))
    }

    this.source.connect(this.processor)
    this.processor.connect(this.audioContext.destination)
    this.startedAt = Date.now()
  }

  async stop(): Promise<RecordedAudio> {
    const durationMs = Date.now() - this.startedAt
    const sampleRate = this.audioContext?.sampleRate ?? 48000
    const samples = flattenAudio(this.chunks)
    const resampled = resample(samples, sampleRate, 16000)
    const wav = encodeWav(resampled, 16000)
    await this.cleanup()
    return {
      blob: new Blob([wav], { type: 'audio/wav' }),
      durationMs,
    }
  }

  async cleanup() {
    this.processor?.disconnect()
    this.source?.disconnect()
    this.processor = null
    this.source = null
    if (this.audioContext?.state !== 'closed') {
      await this.audioContext?.close().catch(() => undefined)
    }
    this.audioContext = null
    this.stopTracks()
  }

  private stopTracks() {
    this.stream?.getTracks().forEach((track) => track.stop())
    this.stream = null
  }
}

function flattenAudio(chunks: Float32Array[]) {
  const total = chunks.reduce((sum, chunk) => sum + chunk.length, 0)
  const result = new Float32Array(total)
  let offset = 0
  chunks.forEach((chunk) => {
    result.set(chunk, offset)
    offset += chunk.length
  })
  return result
}

function resample(input: Float32Array, fromRate: number, toRate: number) {
  if (fromRate === toRate) return input
  const ratio = fromRate / toRate
  const outputLength = Math.floor(input.length / ratio)
  const output = new Float32Array(outputLength)

  for (let i = 0; i < outputLength; i += 1) {
    const sourceIndex = i * ratio
    const left = Math.floor(sourceIndex)
    const right = Math.min(left + 1, input.length - 1)
    const fraction = sourceIndex - left
    output[i] = input[left] * (1 - fraction) + input[right] * fraction
  }

  return output
}

function encodeWav(samples: Float32Array, sampleRate: number) {
  const bytesPerSample = 2
  const blockAlign = bytesPerSample
  const buffer = new ArrayBuffer(44 + samples.length * bytesPerSample)
  const view = new DataView(buffer)

  writeString(view, 0, 'RIFF')
  view.setUint32(4, 36 + samples.length * bytesPerSample, true)
  writeString(view, 8, 'WAVE')
  writeString(view, 12, 'fmt ')
  view.setUint32(16, 16, true)
  view.setUint16(20, 1, true)
  view.setUint16(22, 1, true)
  view.setUint32(24, sampleRate, true)
  view.setUint32(28, sampleRate * blockAlign, true)
  view.setUint16(32, blockAlign, true)
  view.setUint16(34, 16, true)
  writeString(view, 36, 'data')
  view.setUint32(40, samples.length * bytesPerSample, true)

  let offset = 44
  for (let i = 0; i < samples.length; i += 1) {
    const sample = Math.max(-1, Math.min(1, samples[i]))
    view.setInt16(offset, sample < 0 ? sample * 0x8000 : sample * 0x7fff, true)
    offset += 2
  }

  return buffer
}

function writeString(view: DataView, offset: number, value: string) {
  for (let i = 0; i < value.length; i += 1) {
    view.setUint8(offset + i, value.charCodeAt(i))
  }
}
