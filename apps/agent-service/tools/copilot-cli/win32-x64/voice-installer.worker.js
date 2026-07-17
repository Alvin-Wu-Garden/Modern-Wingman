(()=>{const stack=new Error().stack;stack&&(globalThis._sentryDebugIds=globalThis._sentryDebugIds||{},globalThis._sentryDebugIds[stack]="b9574cfd-c34f-573a-bb44-bcc9523e9ce0",globalThis._sentryDebugIdIdentifier="sentry-dbid-b9574cfd-c34f-573a-bb44-bcc9523e9ce0");})();

/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *--------------------------------------------------------------------------------------------*/
import __module from "module";
import __path from "path";
import __fs from "fs";
const __rootRequire = __module.createRequire(import.meta.url);
const __appPath = __fs.realpathSync(import.meta.dirname);
const __clipboardEntrypoint = __path.join(__appPath, "clipboard", "index.js");
const __foundryEntrypoint = __path.join(__appPath, "foundry-local-sdk", "index.js");
const __pvRecorderEntrypoint = __path.join(__appPath, "pvrecorder", "index.js");
const __clipboardRequire = __fs.existsSync(__clipboardEntrypoint)
    ? __module.createRequire(__clipboardEntrypoint)
    : __rootRequire;
const __foundryRequire = __fs.existsSync(__foundryEntrypoint)
    ? __module.createRequire(__foundryEntrypoint)
    : __rootRequire;
const __pvRecorderRequire = __fs.existsSync(__pvRecorderEntrypoint)
    ? __module.createRequire(__pvRecorderEntrypoint)
    : __rootRequire;
const __isVendoredNativeModule = (module) =>
    typeof module === "string" &&
    (module.startsWith("@teddyzhu/") || module === "foundry-local-sdk" || module === "@picovoice/pvrecorder-node");
const require = (module) => {
    let req = __rootRequire;
    if (typeof module === "string" && module.startsWith("@teddyzhu/")) {
        req = __clipboardRequire;
    }
    if (module === "foundry-local-sdk") {
        req = __foundryRequire;
    }
    if (module === "@picovoice/pvrecorder-node") {
        req = __pvRecorderRequire;
    }

    if (typeof module === "string" && (__module.isBuiltin(module) || __isVendoredNativeModule(module))) {
        return req(module);
    }

    const modulePath = __fs.realpathSync(req.resolve(module));
    const relativePath = __path.relative(__appPath, modulePath);

    if (relativePath.startsWith("..")) {
        throw new Error("Requiring module outside of application is a security concern; module: " + modulePath + ", app: " + __appPath);
    }

    return req(module);
};import __url from "url";
const __filename = __url.fileURLToPath(import.meta.url);
const __dirname = __path.dirname(__filename);
import{parentPort as T,workerData as B}from"node:worker_threads";var m=class{initialQueue=[];initialQueueResolvers=Promise.withResolvers();logWriter=null;writePromise=this.initialQueueResolvers.promise;setLogWriter(r){this.logWriter=r;for(let t of this.initialQueue)this.writePromise=this.logWriter.writeLog(t.method,t.message);this.initialQueue=[],this.initialQueueResolvers.resolve()}async flush(){await this.writePromise}async dispose(){await this.flush()}outputPath(){return this.logWriter?.outputPath()}logToLevel(r,t){this.logWriter?this.writePromise=this.logWriter.writeLog(r,t):this.initialQueue.push({method:r,message:t})}info(r){this.logToLevel("info",r)}debug(r){this.logToLevel("debug",r)}warning(r){this.logToLevel("warning",r)}error(r){this.logToLevel("error",r instanceof Error?r.message:r)}log(r){this.error(r)}isDebug(){return!1}shouldLog(r){return!0}notice(r){this.info(r instanceof Error?r.message:r)}startGroup(r,t){this.info(`--- Start of group: ${r} ---`)}endGroup(r){this.info("--- End of group ---")}},u=new m;import{createRequire as j}from"node:module";import*as n from"node:fs/promises";import*as a from"node:path";import{createHash as W}from"node:crypto";import{join as l,basename as oe}from"node:path";import{homedir as h}from"node:os";function A(){return process.env.XDG_CACHE_HOME||l(h(),".cache")}function b(){if(process.platform==="darwin")return l(h(),"Library","Caches","copilot");if(process.platform==="win32"){let e=process.env.LOCALAPPDATA||l(h(),".cache");return l(e,"copilot")}return l(A(),"copilot")}function D(e){if(e.includes("<!DOCTYPE")||e.includes("<html")){let r=Math.min(e.indexOf("<!DOCTYPE")!==-1?e.indexOf("<!DOCTYPE"):1/0,e.indexOf("<html")!==-1?e.indexOf("<html"):1/0),t=e.substring(0,r).trim();return t?`${t} [HTML error page omitted]`:"[HTML error page omitted]"}return e}function y(e){let r;if(e instanceof Error)r=String(e);else if(typeof e=="object"&&e!==null)try{r=JSON.stringify(e)??"[object]"}catch{return"[object with circular reference]"}else r=String(e);return D(r)}var H=1,I=".complete";var w={"win32-x64":"win-x64","win32-arm64":"win-arm64","linux-x64":"linux-x64","darwin-arm64":"osx-arm64"};function S(){return typeof __foundryRequire<"u"&&__foundryRequire||j(import.meta.url)}var f;function U(){if(f)return f;try{let e=S()("foundry-local-sdk/script/install-utils.cjs");if(typeof e.runInstall!="function")throw new Error(`Expected exports {runInstall: function}, got: ${JSON.stringify(Object.fromEntries(Object.entries(e).map(([r,t])=>[r,typeof t])))}`);return f=e,f}catch(e){throw new Error(`Failed to load foundry-local-sdk/script/install-utils.cjs: ${y(e)}. The upstream foundry-local-sdk installer may have changed shape \u2014 re-run the audit checklist in src/cli/voice/foundry/installer/nativeLoader.ts and update accordingly.`)}}var p;function J(){if(p)return p;try{let e=S()("foundry-local-sdk/deps_versions.json");if(typeof e["foundry-local-core"]?.nuget!="string"||typeof e.onnxruntime?.version!="string"||typeof e["onnxruntime-genai"]?.version!="string")throw new Error('deps_versions.json is missing one of the expected version keys: ["foundry-local-core"].nuget, .onnxruntime.version, ["onnxruntime-genai"].version');return p=e,p}catch(e){throw new Error(`Failed to load foundry-local-sdk/deps_versions.json: ${y(e)}. The upstream foundry-local-sdk installer may have changed shape \u2014 re-run the audit checklist in src/cli/voice/foundry/installer/nativeLoader.ts and update accordingly.`)}}function O(e=process.platform){let r=J();return[{name:"Microsoft.AI.Foundry.Local.Core",version:r["foundry-local-core"].nuget},{name:e==="linux"?"Microsoft.ML.OnnxRuntime.Gpu.Linux":"Microsoft.ML.OnnxRuntime.Foundry",version:r.onnxruntime.version},{name:"Microsoft.ML.OnnxRuntimeGenAI.Foundry",version:r["onnxruntime-genai"].version}]}function C(e){return e==="win32"?".dll":e==="darwin"?".dylib":".so"}function V(e,r){return a.join(e,`Microsoft.AI.Foundry.Local.Core${C(r)}`)}function q(e){let r=C(e),t=e==="win32"?"":"lib";return[`Microsoft.AI.Foundry.Local.Core${r}`,`${t}onnxruntime${r}`,`${t}onnxruntime-genai${r}`]}function G(e,r=process.platform,t=process.arch){let o=w[`${r}-${t}`];if(!o)throw new Error(`Voice mode not supported on ${r}-${t}`);let i=e??process.env.COPILOT_CACHE_HOME??b(),s=O(r),c=W("sha256").update(JSON.stringify({schema:H,artifacts:s})).digest("hex").slice(0,12);return a.join(i,"foundry",c,o)}async function _(e={}){let r=e.platform??process.platform,t=e.arch??process.arch,o=`${r}-${t}`;if(!w[o])throw new Error(`Voice mode is not supported on ${o}. Supported platforms: ${Object.keys(w).join(", ")}.`);let s=G(e.cacheRoot,r,t),c=V(s,r),d=q(r);return await M(s,d)?{corePath:c}:(e.onDownloadStart?.(),await Y(s,r,d,e.runInstall),{corePath:c})}async function M(e,r){return await v(a.join(e,I))?(await Promise.all(r.map(o=>v(a.join(e,o))))).every(Boolean):!1}async function v(e){try{return await n.access(e),!0}catch{return!1}}async function Y(e,r,t,o){let i=a.dirname(e);await n.mkdir(i,{recursive:!0});let s=a.join(i,`.tmp-${a.basename(e)}-${process.pid}-${Date.now()}`);await n.mkdir(s,{recursive:!0});try{let c=o??U().runInstall,d=O(r);await z(()=>c(d,{binDir:s}));for(let P of t)if(!await v(a.join(s,P)))throw new Error(`Foundry runtime download finished but required file is missing: ${P}. RID for ${r} may not be supported by the published packages.`);await n.writeFile(a.join(s,I),""),await K(s,e,t)}catch(c){throw await n.rm(s,{recursive:!0,force:!0}).catch(()=>{}),c}}async function K(e,r,t){try{await n.rename(e,r)}catch(o){let i=o.code;if(i==="ENOTEMPTY"||i==="EEXIST"||i==="EPERM"){if(await M(r,t)){await n.rm(e,{recursive:!0,force:!0}).catch(()=>{});return}await n.rm(r,{recursive:!0,force:!0}),await n.rename(e,r);return}throw o}}async function z(e){let r=process.stdout.write.bind(process.stdout),t=process.stderr.write.bind(process.stderr);process.stdout.write=(()=>!0),process.stderr.write=(()=>!0);try{return await e()}finally{process.stdout.write=r,process.stderr.write=t}}var E=class extends Error{constructor(t,o,i){super(t,i);this.code=o;this.name="VoiceBackendError"}};function N(e){return e instanceof E?{message:e.message,code:e.code}:e instanceof Error?{message:e.message}:{message:String(e)}}function R(e){return e instanceof Error?e:new Error(String(e))}var Q=16;function x(e){return $(e,new WeakSet,0)}function $(e,r,t){if(t>=Q)return"<cause chain truncated>";if(typeof e=="object"&&e!==null){if(r.has(e))return"<cyclic cause>";r.add(e)}if(!(e instanceof Error))return String(e);let o=e.stack??`${e.name}: ${e.message}`;if(e.cause===void 0)return o;let i=$(e.cause,r,t+1);return`${o}
Caused by: ${i}`}var k=16*1024,L=class{constructor(r){this.port=r}writeLog(r,t){let o={kind:"log",level:r,message:X(t)};try{this.port.postMessage(o)}catch{}return Promise.resolve()}outputPath(){return"<voice-worker>"}};function F(e,r=u){r.setLogWriter(new L(e))}function X(e){return e.length<=k?e:`${e.slice(0,k)}\u2026 [truncated, ${e.length-k} more chars]`}if(!T)throw new Error("voice-installer.worker.js must be loaded as a worker thread.");var g=T;F(g);var Z=B??{};async function ee(){try{let r={kind:"ok",location:await _({cacheRoot:Z.cacheRoot,onDownloadStart:()=>{let t={kind:"download-started"};g.postMessage(t)}})};g.postMessage(r)}catch(e){let r=R(e);u.error(`[voice-installer worker] install failed: ${x(r)}`);let t={kind:"error",error:N(r)};g.postMessage(t)}finally{setImmediate(()=>process.exit(0))}}ee().catch(e=>{u.error(`[voice-installer worker] fatal: ${x(e)}`),process.exit(1)});
//# sourceMappingURL=voice-installer.worker.js.map
