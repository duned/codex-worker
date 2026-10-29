const {test}=require('node:test');
const assert=require('node:assert/strict');
const {createRequire}=require('node:module');
const path=require('node:path');
const vm=require('node:vm');
const frontend=path.resolve(__dirname,'../../src/CodexServer/worker-poc');
const frontendRequire=createRequire(path.join(frontend,'package.json'));
const {buildSync}=frontendRequire('esbuild');
const {renderToStaticMarkup}=frontendRequire('react-dom/server');
const {createElement}=frontendRequire('react');
const react=frontendRequire('react');
const {MemoryRouter}=frontendRequire('react-router-dom');
const output=buildSync({entryPoints:[path.join(frontend,'src/detail.jsx')],bundle:true,write:false,format:'cjs',platform:'node',jsx:'automatic',alias:{'@':path.join(frontend,'src/untitled')},external:['react-router-dom','../../shared/api/session','react','react/jsx-runtime','react-aria-components','@untitledui/icons','tailwind-merge']}).outputFiles[0].text;
const moduleResult={exports:{}};
const issueTitleApi={useApiRead(){throw Error('Worker rendering must not fetch Issue details.');},useApiMutation(){return {};},useSession(){return {};},useRuntime(){throw Error('Unexpected runtime access in static render.');}};
const context={module:moduleResult,exports:moduleResult.exports,require:id=>id==='../../shared/api/session'?issueTitleApi:frontendRequire(id),URL};
vm.runInNewContext(output,context);
const {WorkerDetail}=moduleResult.exports;
const openModule={exports:{}};
let openActionsPanel=false;
const openReact={...react,useState(initialValue){if(openActionsPanel&&initialValue===false){openActionsPanel=false;return [true,()=>{}];}return react.useState(initialValue);}};
const ariaComponents=frontendRequire('react-aria-components');
const openStateComponents={...ariaComponents,
 ModalOverlay:({children,isOpen,className})=>isOpen?createElement('div',{className},children):null,
 Modal:({children,className})=>createElement('div',{className},children),
 Dialog:({children,'aria-describedby':describedBy})=>createElement('div',{role:'dialog','aria-describedby':describedBy},children),
 Heading:({children})=>createElement('h2',null,children)
};
vm.runInNewContext(output,{...context,module:openModule,exports:openModule.exports,require:id=>id==='react'?openReact:id==='react-aria-components'?openStateComponents:context.require(id)});
const {WorkerDetail:OpenWorkerDetail}=openModule.exports;
const {props,worker,node,capability,current,completed}=require('./worker-poc-fixtures.cjs');
const render=data=>{return renderToStaticMarkup(createElement(MemoryRouter,null,createElement(WorkerDetail,{...props,...data})));};
const renderOpen=data=>{openActionsPanel=true;try{return renderToStaticMarkup(createElement(MemoryRouter,null,createElement(OpenWorkerDetail,{...props,...data})));}finally{openActionsPanel=false;}};
test('Worker detail renders real identity, current stage, outcomes, links and capability tiles',()=>{
 const html=render({});
 for(const text of ['Build Worker North','worker-a','1 / 2','Connected','Fresh','Synced','Managed settings','Active','Validating','2m 30s','Completed','1m 0s','/projects/project-a/issues/27','/projects/project-a/issues/26','Sample project','v1.2.3','GitHub CLI','.NET SDK','Capabilities &amp; provisioning','Readiness checks','Current stage'])assert.ok(html.includes(text),text);
 assert.ok(html.includes('aria-label="Connection: Connected"'));
 assert.ok(html.includes('aria-label="Heartbeat: Fresh"'));
 assert.ok(html.includes('aria-label="Managed settings: Synced"'));
 assert.ok(html.includes('aria-label="Scheduling: Active"'));
 assert.ok(!html.includes('other-execution'));assert.ok(!html.includes('8 active'));
 assert.ok(html.includes('#27 · Issue title 27'));
 assert.ok(!html.includes('Upcoming'));
});
test('Current execution retains its Issue number when the title cannot be loaded',()=>{
 const html=render({executions:[{...current,workReference:{...current.workReference,title:undefined}}]});
 assert.ok(html.includes('#27 · title unavailable'));
 assert.ok(html.includes('href="/projects/project-a/issues/27"'));
});
test('Readiness heartbeat uses the same current, stale and unknown status projection as Worker detail',()=>{
 const heartbeatText=html=>{const start=html.indexOf('poc-readiness-panel');const end=html.indexOf('poc-worker-extra',start);return html.slice(start,end<0?undefined:end);};
 assert.match(heartbeatText(render({})),/>Fresh</);
 assert.match(heartbeatText(render({observations:[{...worker,availability:'stale'}]})),/>Stale</);
 assert.match(heartbeatText(render({observations:[{...worker,lastHeartbeatAtUtc:undefined}]})),/>Unknown</);
});
test('Configuration failure is independent, unknown/stale evidence is never healthy, and safe diagnostics are discoverable',()=>{
 const html=render({ diagnostics:{...props.diagnostics,configurationSynchronization:'error',workerReportedManagedDiagnostics:{retrieval:'retrieved',synchronization:'error',source:'server-retrieved',failureStage:'contract-validation',diagnosticCode:'managed-contract-invalid'}} });
 assert.ok(html.includes('aria-label="Connection: Connected"'));
 assert.ok(html.includes('aria-label="Heartbeat: Fresh"'));
 assert.ok(html.includes('aria-label="Managed settings: Needs attention"'));
 assert.ok(html.includes('Scheduling: Active'));
 assert.ok(html.includes('Last reported failure: contract validation · managed contract invalid'));

 const unknown=render({ diagnostics:{...props.diagnostics,configurationSynchronization:'unknown'} });
 assert.ok(unknown.includes('aria-label="Managed settings: Standalone"'));
 const stale=render({ observations:[{...worker,availability:'stale',lastHeartbeatAtUtc:undefined}] });
 assert.ok(stale.includes('aria-label="Last heartbeat: Lost, heartbeat unavailable"'));
 assert.ok(stale.includes('aria-label="Managed settings: Unknown"'));
});
test('The execution rail keeps cumulative checks and renders durations only for completed or current stages',()=>{
 for(const currentStage of ['Codex','Validation','Integration',null]){
  const html=render({executions:[{...current,currentStage}, {...completed,state:'Failed',durationMilliseconds:null,completedAtUtc:null,recoveryState:'LeaseExpiredUncertain'}]});
  const expected=currentStage==='Validation'?'Validating':currentStage==='Integration'?'Integrating':currentStage;
  assert.ok(html.includes(expected||'Not reported'));assert.ok(html.includes('Claimed'));assert.ok(html.includes('Preparing'));assert.ok(html.includes('Codex'));assert.ok(html.includes('Validating'));assert.ok(html.includes('Integrating'));
  assert.ok(html.includes('Durations use Server-recorded stage transitions; older executions may not have timing data.'));
  assert.ok(html.includes('aria-current="step"')===!!currentStage);assert.ok(html.includes('Failed'));assert.ok(html.includes('Duration unavailable'));assert.ok(html.includes('LeaseExpiredUncertain'));assert.ok(html.includes('31/12/2025'));
 }
 const reporting=render({executions:[{...current,currentStage:'Reporting'}]});
 assert.ok(reporting.includes('aria-current="step"'));
 assert.ok(reporting.includes('>Reporting<'));
 assert.equal((reporting.match(/<svg viewBox="0 0 16 16" focusable="false"><path/g)||[]).length,5);
 assert.equal((reporting.match(/class="poc-stage-duration"/g)||[]).length,6);
 const integrating=render({executions:[{...current,currentStage:'Integration'}]});
 assert.equal((integrating.match(/<svg viewBox="0 0 16 16" focusable="false"><path/g)||[]).length,4);
 assert.equal((integrating.match(/class="poc-stage-duration"/g)||[]).length,5);
 assert.ok(integrating.includes('Duration unavailable'));
});
test('Capability tiles use each reported service version and authentication field',()=>{
 const service=(id,displayName,detectedVersion)=>({definition:{id,displayName,requiresAuthentication:true,requiresConfiguration:true},state:{installation:'Installed',health:'Healthy',authentication:'Satisfied',configuration:'Satisfied',detectedVersion},availableActions:[]});
 const services=[service('git','Git','2.43.0'),service('github-cli','GitHub CLI','2.65.0'),service('codex-cli','Codex CLI','0.157.0'),service('dotnet-sdk','.NET SDK','10.0')];
 const html=render({nodes:[{...node,capabilities:services}],diagnostics:{...props.diagnostics,projects:[{projectId:'project-a',projectName:'Sample project',isEligible:true,observationStatus:'worker-reported-current-revision'},{projectId:'project-b',projectName:'Other project',isEligible:false,observationStatus:'worker-reported-current-revision'}]}});
 for(const text of ['Git','GitHub CLI','Codex CLI','.NET SDK','v2.43.0','Token available','v0.157.0','10.0','Runtime version','1 eligible project(s)'])assert.ok(html.includes(text),text);
 for(const mark of ['poc-service-mark','poc-service-mark-github','poc-service-mark-terminal','poc-service-mark-dotnet'])assert.ok(html.includes(mark),mark);
});
test('Stale, missing, failed reads and absent resources remain understandable',()=>{
 const stale=render({observations:[worker],nodes:[{...node,observationsStale:true,executionReadiness:'not-ready'}]});
 assert.ok(stale.includes('Stale'));assert.ok(stale.includes('not-ready'));assert.ok(stale.includes('Ready'));
 const staleHeartbeat=render({observations:[{...worker,availability:'offline'}],nodes:[{...node,connectivity:'disconnected',observationsStale:true,executionReadiness:'not-ready'}]});
 assert.ok(staleHeartbeat.includes('Disconnected'));assert.ok(!staleHeartbeat.includes('>Connected<'));
 const stopped=render({observations:[{...worker,availability:'offline'}],nodes:null});
 assert.ok(stopped.includes('Disconnected'));assert.ok(stopped.includes('>Fresh<'));
 const missingNode=render({nodes:null});
 assert.ok(missingNode.includes('Unknown'));assert.ok(!missingNode.includes('>Connected<'));
 const unavailable=render({executions:null,nodes:null,diagnostics:null});
 for(const text of ['Unknown','Unavailable','Current execution data unavailable','Execution history unavailable','Capability observations unavailable'])assert.ok(unavailable.includes(text),text);
 assert.ok(render({observations:[]}).includes('Worker unavailable or deleted'));
 assert.ok(render({observations:null,loading:true}).includes('Loading current Worker observations'));
 assert.ok(render({executions:[]}).includes('Worker reports active work'));
});

test('Last reported checks survive disconnect while current execution readiness stays blocked',()=>{
 const offlineWorker={...worker,availability:'offline',platform:'Linux',hostResources:{measuredAtUtc:'2026-01-01T00:02:00Z',logicalCpuCount:8,totalMemoryBytes:16*1024**3,usedMemoryBytes:8*1024**3,diskTotalBytes:128*1024**3,diskAvailableBytes:64*1024**3,cpuUsagePercent:25,memoryUsagePercent:50,sampleSeconds:1}};
 const disconnectedNode={...node,connectivity:'disconnected',observationsStale:true,executionReadiness:'ready'};
 const passed=render({observations:[offlineWorker],nodes:[disconnectedNode],diagnostics:{...props.diagnostics,projects:[{projectId:'project-a',projectName:'Sample project',isEligible:true,missingRequirements:[],observationStatus:'worker-reported-current-revision'}]}});
 assert.ok(passed.includes('Disconnected'));
 assert.ok(passed.includes('Last reported execution preflight'));
 assert.ok(passed.includes('>Ready<'));
 assert.ok(passed.includes('Last reported project eligibility'));
 assert.ok(passed.includes('Current execution readiness'));
 assert.ok(passed.includes('not-ready'));
 for(const fact of ['Linux','8','16.0 GiB','128.0 GiB','Unavailable'])assert.ok(passed.includes(fact),fact);
 for(const liveValue of ['25.0%','50.0%','8.0 GiB','64.0 GiB'])assert.doesNotMatch(passed,new RegExp(`>${liveValue.replaceAll('.','\\.')}(?:<| ·)`),liveValue);

 const failedCapability={...capability,state:{...capability.state,health:'Failed',authentication:'Missing'}};
 const failed=render({observations:[offlineWorker],nodes:[{...disconnectedNode,capabilities:[failedCapability]}]});
 assert.ok(failed.includes('Failed'));assert.ok(failed.includes('Missing'));
 const failedReadiness=render({observations:[offlineWorker],nodes:[disconnectedNode],diagnostics:{...props.diagnostics,aiAgentReady:false,projects:[],capabilityObservationsCurrent:false}});
 assert.ok(failedReadiness.includes('Last reported execution preflight'));assert.ok(failedReadiness.includes('Not ready'));
 const noHistory=render({observations:[{...offlineWorker,lastHeartbeatAtUtc:undefined}],nodes:[disconnectedNode],diagnostics:{...props.diagnostics,aiAgentReady:false,projects:[]}});
 assert.ok(noHistory.includes('Not checked'));

 const reconnected=render({observations:[{...offlineWorker,availability:'online'}],nodes:[{...node,observationsStale:false}]});
 for(const liveValue of ['25.0%','50.0%','8.0 GiB','64.0 GiB'])assert.ok(reconnected.includes(liveValue),liveValue);
 const connectedButStale=render({observations:[{...offlineWorker,availability:'online'}],nodes:[{...node,observationsStale:true,executionReadiness:'not-ready'}]});
 assert.ok(connectedButStale.includes('Stale'));assert.ok(connectedButStale.includes('last sample'));assert.ok(connectedButStale.includes('25.0%'));
});
test('Current execution empty state reflects reported activity without reserving a timeline',()=>{
 const terminalOnly=[completed];
 const active=render({executions:terminalOnly});
 assert.ok(active.includes('Worker reports active work, but its execution is unavailable in this view.'));
 const idle=render({executions:terminalOnly,observations:[{...worker,activeAssignments:0,activeExecutions:0}]});
 assert.ok(idle.includes('No current execution is reported in the latest 50 requests.'));
 assert.ok(!idle.includes('Worker reports active work'));
});
test('Capabilities expose pending and failed operations without provisioning mutation controls or raw diagnostics',()=>{
 const request={nodeId:'worker-a',capabilityId:'codex-cli',action:'CheckAuthentication'};
 const html=render({nodeCommands:[{id:'pending',request,status:'Pending',createdAtUtc:'2026-01-01',diagnostic:'Pending'},{id:'failed',request,status:'Failed',createdAtUtc:'2025-12-31',diagnostic:'Denied',failureDetail:{description:'raw private detail'}}],nodes:[{...node,capabilities:[{...capability,state:{...capability.state,operation:{state:'Failed',action:'checkauthentication',diagnosticCode:'authentication-required'}}}]}]});
 assert.ok(html.includes('Pending'));assert.ok(html.includes('Failed'));assert.ok(html.includes('authentication-required'));
 assert.ok(!html.includes('CheckAuthentication</button>'));assert.ok(!html.includes('raw private detail'));
 const failed=render({nodeCommands:[{id:'failed',request,status:'Failed',createdAtUtc:'2026-01-01',diagnostic:'Denied'}]});
 assert.ok(failed.includes('Latest command:'));assert.ok(failed.includes('Failed'));
});
test('Resource text is escaped and unsafe Issue URLs never become links',()=>{
 const html=render({observations:[{...worker,displayName:'<script>bad</script>'}],executions:[{...current,workReference:{type:'github-issue',id:'27',url:'javascript:alert(1)'}}],projects:null});
 assert.ok(html.includes('&lt;script&gt;bad&lt;/script&gt;'));assert.ok(!html.includes('javascript:'));assert.ok(html.includes('href="/projects/project-a/issues/27"'));
 const unavailable=render({executions:[{...current,projectId:null}],projects:null});
 assert.ok(unavailable.includes('Issue link unavailable'));
 assert.ok(!unavailable.includes('href="https://github.com/'));
});

test('More actions side panel preserves scheduling guards, descriptions and direct status reload',()=>{
 const html=renderOpen({administration:{worker:{...worker,schedulingPolicy:'Draining',authenticationCredentialStatus:'revoked',activeAssignments:2},actions:[{key:'Enabled',label:'Activate scheduling',reason:'Activation blocked: Codex preflight required'},{key:'Draining',label:'Drain worker',reason:'This scheduling policy is already applied.'},{key:'Disabled',label:'Deactivate',reason:''},{key:'revoke-api',label:'Revoke Worker API token',reason:'No active Worker API token is registered.'}],onAction:()=>{},onRefresh:()=>{}},onRefreshStatus:()=>{}});
 for(const text of ['aria-expanded="true"','role="dialog"','Scheduling controls','Draining','Current executions continue to completion.','Activation blocked: Codex preflight required','Additional actions','No active Worker API token','id="poc-action-revoke-api-reason"','id="poc-action-Enabled-reason"','Worker status','Reload worker status','One direct read · no confirmation step','Revoke Worker API token'])assert.ok(html.includes(text),text);
 assert.ok(!html.includes('Revoke delivery authorization'));
});
test('Migration preview reuses Worker presentation without unfinished administration actions',()=>{
 const html=render({readOnly:true});
 assert.ok(html.includes('Build Worker North'));
 assert.ok(html.includes('Current execution'));
 assert.ok(!html.includes('Worker controls'));
 assert.ok(!html.includes('Activate scheduling'));
 assert.ok(!html.includes('Revoke API token'));
 assert.ok(html.includes('Open Worker detail and administration'));
});

test('Canonical detail preserves multiple actual slots, linked concurrent stages and independent delivery revocation', () => {
 const second = { ...current, id: 'second-slot', currentStage: 'Codex', workReference: { type: 'github-issue', id: '28', title: 'Issue title 28' } };
 const html = render({ observations: [{ ...worker, activeExecutions: 2, availableCapacity: 0 }], executions: [current, second, completed], administration: { worker, delivery: { status: 'active' }, actions: [{ key: 'revoke-delivery', label: 'Revoke delivery authorization', reason: '' }], onAction: () => {}, onRefresh: () => {} } });
 for (const text of ['2 / 2', 'Current execution', 'Validating', 'Codex', '#28 · Issue title 28', '/projects/project-a', 'Revoke delivery authorization', 'poc-delivery-effect', 'Already delivered credentials are not removed']) assert.ok(html.includes(text), text);
 assert.ok(!html.includes('Detail preview'));
});
test('Credential delivery repair exposes pending guidance and an accessible action without secrets',()=>{
 const html=renderOpen({administration:{worker,delivery:{status:'pending'},actions:[{key:'repair-delivery',label:'Provision or repair delivery authorization',reason:''}],onAction:()=>{},onRefresh:()=>{}}});
 assert.ok(html.includes('Provision or repair delivery authorization'));
 assert.ok(html.includes('Keep the Worker online'));
 assert.match(html, /<button[^>]*>[\s\S]*?Provision or repair delivery authorization[\s\S]*?<\/button>/);
 assert.ok(html.includes('role="status"'));
 assert.ok(!html.includes('CODEX_WORKER_CREDENTIAL_DELIVERY_TOKEN'));
});
