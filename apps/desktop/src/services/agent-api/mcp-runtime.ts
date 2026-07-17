const BASE_URL='http://localhost:5002'
export interface McpRuntimeHealth{serverId:number;serverName:string;healthy:boolean;error:string|null;checkedAt:string;toolCount:number}
export interface McpRuntimeStatus{servers:McpRuntimeHealth[];tools:Array<{serverId:number;serverName:string;name:string;description:string|null;readOnly:boolean}>}
async function read(response:Response){if(!response.ok)throw new Error(await response.text());return response.json() as Promise<McpRuntimeStatus>}
export const getMcpRuntimeStatus=()=>fetch(`${BASE_URL}/api/mcp/runtime/status`).then(read)
export const refreshMcpRuntime=()=>fetch(`${BASE_URL}/api/mcp/runtime/refresh`,{method:'POST'}).then(read)
