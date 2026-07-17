import type { ConverterFunction } from 'opencc-js'

let converter: ConverterFunction | null = null

export async function normalizeSpeechText(text: string) {
  if (!converter) {
    const OpenCC = (await import('opencc-js')).default
    converter = OpenCC.Converter({ from: 'cn', to: 'tw' })
  }
  return converter(text).replace(/\s+/g, ' ').trim()
}
