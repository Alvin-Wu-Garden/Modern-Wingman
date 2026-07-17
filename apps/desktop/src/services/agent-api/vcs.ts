const BASE_URL = 'http://localhost:5002'

export interface VcsProfile {
  id: string; name: string; vcsType: 'git' | 'svn'; baseUrl: string
  sslVerificationEnabled: boolean; defaultWorkspaceRoot: string | null
  commitAuthorName: string | null; commitAuthorEmail: string | null
  enabled: boolean; username: string | null; secretType: string | null; hasSecret: boolean
  lastTestStatus:string|null;lastTestError:string|null;lastTestedAt:string|null
}
export interface VcsRuntime { vcsType: 'Git' | 'Svn'; available: boolean; executablePath: string | null; version: string | null; source: string | null; error: string | null }
export interface SaveVcsProfile { name: string; vcsType: 'git' | 'svn'; baseUrl: string; sslVerificationEnabled: boolean; defaultWorkspaceRoot: string | null; commitAuthorName: string | null; commitAuthorEmail: string | null; enabled: boolean; username: string | null; secretType: 'AccessToken' | 'Password'; secretValue: string | null }
export interface VcsProtectedRef { id:string;vcsType:'git'|'svn';projectId:string|null;pattern:string;enabled:boolean }
export interface WorkspaceSettings { workspaceRoot:string;worktreeRoot:string;shadowGitRoot:string }

async function json<T>(response: Response): Promise<T> {
  if (!response.ok) { const body=await response.text(); throw new Error(body || `HTTP ${response.status}`) }
  return response.status === 204 ? undefined as T : response.json() as Promise<T>
}
export const listVcsProfiles=()=>fetch(`${BASE_URL}/api/vcs/profiles/`).then(json<VcsProfile[]>)
export const listVcsRuntimes=()=>fetch(`${BASE_URL}/api/vcs/runtimes`).then(json<VcsRuntime[]>)
export const saveVcsProfile=(id:string|null,value:SaveVcsProfile)=>fetch(`${BASE_URL}/api/vcs/profiles/${id??''}`,{method:id?'PUT':'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(value)}).then(json<VcsProfile>)
export const deleteVcsProfile=(id:string)=>fetch(`${BASE_URL}/api/vcs/profiles/${id}`,{method:'DELETE'}).then(json<void>)
export const testVcsProfile=(profile:VcsProfile)=>fetch(`${BASE_URL}/api/vcs/${profile.vcsType}/test`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({profileId:profile.id,repositoryUrl:profile.baseUrl})}).then(json<{success:boolean;output:string;error?:string}>)
export const listGitBranches=(profileId:string,repositoryUrl:string)=>fetch(`${BASE_URL}/api/vcs/git/branches`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({profileId,repositoryUrl})}).then(json<string[]>)
export const browseSvn=(profileId:string,repositoryUrl:string)=>fetch(`${BASE_URL}/api/vcs/svn/browse`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({profileId,repositoryUrl})}).then(json<{success:boolean;output:string;error?:string}>)
export const listProtectedRefs=()=>fetch(`${BASE_URL}/api/vcs/protected-refs/`).then(json<VcsProtectedRef[]>)
export const createProtectedRef=(vcsType:'git'|'svn',pattern:string)=>fetch(`${BASE_URL}/api/vcs/protected-refs/`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({vcsType,pattern})}).then(json<VcsProtectedRef>)
export const deleteProtectedRef=(id:string)=>fetch(`${BASE_URL}/api/vcs/protected-refs/${id}`,{method:'DELETE'}).then(json<void>)
export const getWorkspaceSettings=()=>fetch(`${BASE_URL}/api/settings/agent/workspace`).then(json<WorkspaceSettings>)
export const saveWorkspaceSettings=(settings:WorkspaceSettings)=>fetch(`${BASE_URL}/api/settings/agent/workspace`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(settings)}).then(json<WorkspaceSettings>)
