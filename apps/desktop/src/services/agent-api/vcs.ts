import { AGENT_API_BASE_URL } from './client'

export interface VcsProfile {
  id: string
  name: string
  vcsType: 'git' | 'svn'
  baseUrl: string
  sslVerificationEnabled: boolean
  defaultWorkspaceRoot: string | null
  enabled: boolean
  username: string | null
  secretType: string | null
  hasSecret: boolean
}

export interface SaveVcsProfile {
  name: string
  vcsType: 'git' | 'svn'
  baseUrl: string
  sslVerificationEnabled: boolean
  defaultWorkspaceRoot: string | null
  enabled: boolean
  username: string | null
  secretType: 'AccessToken' | 'Password'
  secretValue: string | null
}

async function json<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const body = await response.json().catch(() => null)
    throw new Error(body?.error ?? `版本控制請求失敗 (${response.status})`)
  }
  return response.status === 204 ? undefined as T : response.json() as Promise<T>
}

export const listVcsProfiles = () =>
  fetch(`${AGENT_API_BASE_URL}/api/vcs/profiles/`).then(json<VcsProfile[]>)

export const saveVcsProfile = (id: string | null, value: SaveVcsProfile) =>
  fetch(`${AGENT_API_BASE_URL}/api/vcs/profiles/${id ?? ''}`, {
    method: id ? 'PUT' : 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(value),
  }).then(json<VcsProfile>)

export const deleteVcsProfile = (id: string) =>
  fetch(`${AGENT_API_BASE_URL}/api/vcs/profiles/${id}`, {
    method: 'DELETE',
  }).then(json<void>)

export const testVcsProfile = (id: string) =>
  fetch(`${AGENT_API_BASE_URL}/api/vcs/profiles/${id}/test`, {
    method: 'POST',
  }).then(json<{ success: boolean; output: string; error?: string }>)
