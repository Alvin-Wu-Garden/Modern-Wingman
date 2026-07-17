const BASE_URL='http://localhost:5002'
export interface DevelopmentRuntime{kind:'python'|'node'|'powershell';available:boolean;version:string|null;source:string|null;executablePath:string|null}
export async function listDevelopmentRuntimes(){const response=await fetch(`${BASE_URL}/api/runtimes`);if(!response.ok)throw new Error(await response.text());return response.json() as Promise<DevelopmentRuntime[]>}
export interface RuntimeImportResult{kind:string;destinationPath:string;executablePath:string|null;fileCount:number}
async function importPath(endpoint:string,kind:DevelopmentRuntime['kind'],path:string){const response=await fetch(`${BASE_URL}${endpoint}`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({kind,path})});if(!response.ok){const body=await response.json().catch(()=>null) as {error?:string}|null;throw new Error(body?.error??`匯入失敗：HTTP ${response.status}`)}return response.json() as Promise<RuntimeImportResult>}
export const importDevelopmentRuntime=(kind:DevelopmentRuntime['kind'],path:string)=>importPath('/api/runtimes/import-path',kind,path)
export const importPackageCache=(kind:DevelopmentRuntime['kind'],path:string)=>importPath('/api/runtimes/package-cache/import-path',kind,path)
