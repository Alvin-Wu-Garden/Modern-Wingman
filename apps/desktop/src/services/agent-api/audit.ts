const BASE_URL='http://localhost:5002'
export interface AuditEvent{ id:string;traceId:string|null;actorType:string;actorId:string|null;eventType:string;targetType:string;targetId:string|null;action:string;result:string;machineName:string|null;detailsJson:string|null;createdAt:string }
export interface AuditPage{items:AuditEvent[];total:number;offset:number;limit:number}
export interface AuditFilters{from?:string;to?:string;eventType?:string;targetType?:string;targetId?:string;result?:string;traceId?:string;offset?:number;limit?:number}
export interface ToolCallAudit{ id:string;traceId:string;projectId:string|null;runId:string|null;provider:string;toolName:string;toolType:string;status:string;startedAt:string;durationMs:number|null;approvalRequired:boolean;approvalResult:string|null;error:string|null }
export interface ToolCallFilters{from?:string;to?:string;projectId?:string;runId?:string;provider?:string;tool?:string;status?:string;offset?:number;limit?:number}
export interface AuditFilterOption{value:string;label:string;group?:string|null}
export interface AuditFacets{eventTypes:string[];targetTypes:string[];targets:AuditFilterOption[];results:string[];traceIds:string[]}
export interface ToolCallAuditFacets{projects:AuditFilterOption[];runs:AuditFilterOption[];providers:string[];tools:string[];statuses:string[]}
const query=(filters:AuditFilters)=>{const p=new URLSearchParams();Object.entries(filters).forEach(([key,value])=>{if(value!==undefined&&value!=='')p.set(key,String(value))});return p.toString()}
export async function listAuditEvents(filters:AuditFilters){const response=await fetch(`${BASE_URL}/api/audit/events?${query(filters)}`);if(!response.ok)throw new Error(await response.text());return response.json() as Promise<AuditPage>}
export async function exportAuditCsv(filters:AuditFilters){const response=await fetch(`${BASE_URL}/api/audit/export.csv?${query(filters)}`);if(!response.ok)throw new Error(await response.text());return response.blob()}
export async function listToolCallAudit(filters:ToolCallFilters){const response=await fetch(`${BASE_URL}/api/audit/tool-calls?${query(filters)}`);if(!response.ok)throw new Error(await response.text());return response.json() as Promise<{items:ToolCallAudit[];total:number;offset:number;limit:number}>}
export async function exportToolCallAuditCsv(filters:ToolCallFilters){const response=await fetch(`${BASE_URL}/api/audit/tool-calls/export.csv?${query(filters)}`);if(!response.ok)throw new Error(await response.text());return response.blob()}
export async function getAuditFacets(){const response=await fetch(`${BASE_URL}/api/audit/facets`);if(!response.ok)throw new Error(await response.text());return response.json() as Promise<AuditFacets>}
export async function getToolCallAuditFacets(){const response=await fetch(`${BASE_URL}/api/audit/tool-calls/facets`);if(!response.ok)throw new Error(await response.text());return response.json() as Promise<ToolCallAuditFacets>}
