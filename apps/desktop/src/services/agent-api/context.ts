const BASE_URL='http://localhost:5002'
export interface ContextPreview{sources:{kind:string;path:string;characters:number}[];estimatedTokens:number;truncated:boolean}
export async function previewContext(message:string,workspacePath:string,signal?:AbortSignal){const response=await fetch(`${BASE_URL}/api/context/preview`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({message,workspacePath}),signal});if(!response.ok)throw new Error(await response.text());return response.json() as Promise<ContextPreview>}
