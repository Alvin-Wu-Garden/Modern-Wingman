import type { AnyRunEvent } from '@modern-wingman/contracts'
import { tauriListen } from '../tauri-bridge'
import type { UnlistenFn } from '@tauri-apps/api/event'

export function subscribeToRun(
  runId: string,
  onEvent: (event: AnyRunEvent) => void,
): Promise<UnlistenFn> {
  return tauriListen<AnyRunEvent>(`run:${runId}`, onEvent)
}
