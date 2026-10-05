const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const fs=require('node:fs');

test('Server Worker detail distinguishes eligibility from stale preparation observations and escapes identifiers',async()=>{
 const html=fs.readFileSync('src/CodexServer/dashboard.html','utf8');
 const source=html.slice(html.indexOf('async function loadWorker(id)'),html.indexOf('async function loadWorkerAdministration'));
 const elements=new Map();
 const $=id=>{if(!elements.has(id))elements.set(id,{innerHTML:'',textContent:''});return elements.get(id)};
 const worker={displayName:'Worker',availability:'online',capabilities:[],activeProjects:[]};
 const diagnostics={reasons:['Worker-observed project: failed'],projects:[{projectName:'<Project>',isEligible:true,
  missingRequirements:[],materializationState:'failed',workerReportedRevision:4,observationStatus:'stale-revision',
  diagnosticCode:'project-preparation-failed'}]};
 const context=vm.createContext({$,api:async path=>path.endsWith('/diagnostics')?diagnostics:worker,
  esc:value=>String(value??'').replaceAll('<','&lt;').replaceAll('>','&gt;')});
 vm.runInContext(source,context);
 await context.loadWorker('worker');
 const rendered=$('worker-detail').innerHTML;
 assert.match(rendered,/Worker observation: failed · revision 4 · stale-revision · project-preparation-failed/);
 assert.match(rendered,/>eligible</);
 assert.match(rendered,/&lt;Project&gt;/);
 assert.doesNotMatch(rendered,/<Project>/);
});

test('Worker dashboard displays safe managed retrieval and preparation observations',async()=>{
 const html=fs.readFileSync('src/CodexWorker/dashboard.html','utf8');
 const source=html.slice(html.indexOf('async function refresh()'),html.indexOf('async function life('));
 const elements=new Map();
 const $=id=>{if(!elements.has(id))elements.set(id,{innerHTML:'',textContent:''});return elements.get(id)};
 const status={managedDiagnostics:{retrieval:'retrieved',synchronization:'error',source:'server-retrieved',
  failureStage:'synchronization',diagnosticCode:'managed-local-configuration-invalid',failedProjectId:'<id>',failedProjectRevision:2,
  projects:[{projectId:'<id>',revision:2,state:'failed',diagnosticCode:'project-preparation-failed'}]}};
 const context=vm.createContext({$,statusEndpoint:'/api/status',api:async path=>path==='/api/status'?status:
  path==='/worker/drain'?{}:[],badge:value=>String(value??''),duration:()=>'',renderEvents:()=>{},
  esc:value=>String(value??'').replaceAll('<','&lt;').replaceAll('>','&gt;')});
 vm.runInContext(source,context);
 await context.refresh();
 const rendered=$('capacity').innerHTML;
 assert.match(rendered,/Retrieval: retrieved · Synchronization: error · Source: server-retrieved/);
 assert.match(rendered,/synchronization: managed-local-configuration-invalid/);
 assert.match(rendered,/&lt;id&gt; · revision 2 · failed · project-preparation-failed/);
 assert.doesNotMatch(rendered,/<id>/);
});
