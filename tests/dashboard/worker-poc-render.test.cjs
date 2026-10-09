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
const output=buildSync({entryPoints:[path.join(frontend,'src/detail.jsx')],bundle:true,write:false,format:'cjs',platform:'node',jsx:'automatic',alias:{'@':path.join(frontend,'src/untitled')},external:['react','react/jsx-runtime','react-aria-components','@untitledui/icons','tailwind-merge']}).outputFiles[0].text;
const moduleResult={exports:{}};
vm.runInNewContext(output,{module:moduleResult,exports:moduleResult.exports,require:frontendRequire,URL});
const {WorkerDetail}=moduleResult.exports;
const {props,worker,node,capability,current,completed}=require('./worker-poc-fixtures.cjs');
const render=data=>renderToStaticMarkup(createElement(WorkerDetail,{...props,...data}));
test('Worker detail renders real identity, current stage, outcomes, links and capability tiles',()=>{
 const html=render({});
 for(const text of ['Build Worker North','worker-a','1 / 2','Connected','Ready','Fresh','Validating','2m 30s','Completed','1m 0s','https://github.com/owner/repo/issues/27','https://github.com/owner/repo/issues/26','Sample project','v1.2.3','GitHub CLI','.NET SDK','Capabilities &amp; provisioning','Readiness checks','Current stage'])assert.ok(html.includes(text),text);
 assert.ok(!html.includes('other-execution'));assert.ok(!html.includes('8 active'));
 assert.ok(html.includes('Issue #27'));
 assert.ok(!html.includes('Upcoming'));
});
test('The five-stage rail highlights only the reported current stage and does not invent history',()=>{
 for(const currentStage of ['Codex','Validation','Integration',null]){
  const html=render({executions:[{...current,currentStage}, {...completed,state:'Failed',durationMilliseconds:null,completedAtUtc:null,recoveryState:'LeaseExpiredUncertain'}]});
  const expected=currentStage==='Validation'?'Validating':currentStage==='Integration'?'Integrating':currentStage;
  assert.ok(html.includes(expected||'Not reported'));assert.ok(html.includes('Claimed'));assert.ok(html.includes('Preparing'));assert.ok(html.includes('Codex'));assert.ok(html.includes('Validating'));assert.ok(html.includes('Integrating'));
  assert.ok(html.includes('Only the reported current stage is available; stage history is not provided.'));
  assert.ok(html.includes('aria-current="step"')===!!currentStage);assert.ok(html.includes('Failed'));assert.ok(html.includes('Duration unavailable'));assert.ok(html.includes('LeaseExpiredUncertain'));assert.ok(html.includes('31/12/2025'));
 }
});
test('Capability tiles use each reported service version and authentication field',()=>{
 const service=(id,displayName,detectedVersion)=>({definition:{id,displayName,requiresAuthentication:true,requiresConfiguration:true},state:{installation:'Installed',health:'Healthy',authentication:'Satisfied',configuration:'Satisfied',detectedVersion},availableActions:[]});
 const services=[service('git','Git','2.43.0'),service('github-cli','GitHub CLI','2.65.0'),service('codex-cli','Codex CLI','0.157.0'),service('dotnet-sdk','.NET SDK','10.0')];
 const html=render({nodes:[{...node,capabilities:services}],diagnostics:{...props.diagnostics,projects:[{projectId:'project-a',projectName:'Sample project',isEligible:true,observationStatus:'worker-reported-current-revision'},{projectId:'project-b',projectName:'Other project',isEligible:false,observationStatus:'worker-reported-current-revision'}]}});
 for(const text of ['Git','GitHub CLI','Codex CLI','.NET SDK','v2.43.0','Token available','v0.157.0','10.0','Runtime version','1 eligible project(s)'])assert.ok(html.includes(text),text);
 for(const mark of ['poc-service-mark','poc-service-mark-github','poc-service-mark-terminal','poc-service-mark-dotnet'])assert.ok(html.includes(mark),mark);
});
test('Stale, missing, failed reads and absent resources remain understandable',()=>{
 const stale=render({observations:[{...worker,availability:'stale'}],nodes:[{...node,observationsStale:true,executionReadiness:'not-ready'}]});
 assert.ok(stale.includes('Stale'));assert.ok(stale.includes('not-ready'));
 const staleHeartbeat=render({observations:[{...worker,availability:'stale'}],nodes:[{...node,connectivity:'disconnected',observationsStale:false}]});
 assert.ok(staleHeartbeat.includes('Stale'));assert.ok(!staleHeartbeat.includes('>Fresh<'));assert.ok(!staleHeartbeat.includes('>Connected<'));
 const stopped=render({observations:[{...worker,availability:'offline'}],nodes:null});
 assert.ok(stopped.includes('>offline<'));assert.ok(stopped.includes('>Fresh<'));
 const unavailable=render({executions:null,nodes:null,diagnostics:null});
 for(const text of ['Unknown','Unavailable','Current execution data unavailable','Execution history unavailable','Capability observations unavailable'])assert.ok(unavailable.includes(text),text);
 assert.ok(render({observations:[]}).includes('Worker unavailable or deleted'));
 assert.ok(render({observations:null,loading:true}).includes('Loading current Worker observations'));
 assert.ok(render({executions:[]}).includes('Worker reports active work'));
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
 assert.ok(html.includes('&lt;script&gt;bad&lt;/script&gt;'));assert.ok(!html.includes('javascript:'));assert.ok(html.includes('Issue link unavailable'));
});

test('Control rail preserves guarded actions, disabled reasons and current control explanations',()=>{
 const html=render({administration:{worker:{...worker,schedulingPolicy:'Draining',authenticationCredentialStatus:'revoked',activeAssignments:2},actions:[{key:'Enabled',label:'Activate scheduling',reason:'Activation blocked: Codex preflight required'},{key:'Draining',label:'Drain worker',reason:'This scheduling policy is already applied.'},{key:'Disabled',label:'Deactivate',reason:''},{key:'revoke-api',label:'Revoke Worker API token',reason:'No active Worker API token is registered.'}],onAction:()=>{},onRefresh:()=>{}}});
 for(const text of ['poc-control-rail','2 active assignment(s) keep their leases','No active Worker API token','Activation blocked: Codex preflight required','Existing assignments and leases are not cancelled','does not revoke credential-delivery authorization','aria-describedby="poc-action-revoke-api"','Refresh authoritative state','Revoke API token'])assert.ok(html.includes(text),text);
 assert.match(html,/<button[^>]*disabled=""[^>]*>[\s\S]*?Activate scheduling/);
 assert.match(html,/<button(?![^>]* disabled="")[^>]*>[\s\S]*?Deactivate/);
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
 const second = { ...current, id: 'second-slot', currentStage: 'Codex', workReference: { type: 'github-issue', id: '28' } };
 const html = render({ observations: [{ ...worker, activeExecutions: 2, availableCapacity: 0 }], executions: [current, second, completed], administration: { worker, delivery: { status: 'active' }, actions: [{ key: 'revoke-delivery', label: 'Revoke delivery authorization', reason: '' }], onAction: () => {}, onRefresh: () => {} } });
 for (const text of ['2 / 2', 'Current executions', 'Validating', 'Codex', 'Issue #28', '/projects/project-a', 'Revoke delivery authorization', 'poc-delivery-effect', 'Already delivered credentials are not removed']) assert.ok(html.includes(text), text);
 assert.ok(!html.includes('Detail preview'));
});
