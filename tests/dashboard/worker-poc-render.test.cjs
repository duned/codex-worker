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
test('PoC renders named Worker, its capacity, real stage, outcomes and canonical links',()=>{
 const html=render({});
 for(const text of ['Build Worker North','ID worker-a','1 / 2 active','1 active Server assignments','Validation','2m 30s','Completed','1m 0s','https://github.com/owner/repo/issues/27','https://github.com/owner/repo/issues/26','Sample project','1.2.3','Satisfied','Not applicable','checkauthentication, refresh'])assert.ok(html.includes(text),text);
 assert.ok(!html.includes('other-execution'));assert.ok(!html.includes('8 active'));
 assert.ok(html.includes('Issue title unavailable'));
 assert.ok(!html.includes('Upcoming'));
});
test('Reported stages and terminal failures stay distinct; unknown timing is honest',()=>{
 for(const currentStage of ['Codex','Validation','Integration',null]){
  const html=render({executions:[{...current,currentStage}, {...completed,state:'Failed',durationMilliseconds:null,completedAtUtc:null,recoveryState:'LeaseExpiredUncertain'}]});
  assert.ok(html.includes(currentStage||'Not reported'));assert.ok(html.includes('Failed'));assert.ok(html.includes('Duration unavailable'));assert.ok(html.includes('LeaseExpiredUncertain'));assert.ok(html.includes('Requested: 2025-12-31'));
 }
});
test('Stale, missing, failed reads and absent resources remain understandable',()=>{
 const stale=render({observations:[{...worker,availability:'stale'}],nodes:[{...node,observationsStale:true,executionReadiness:'not-ready'}]});
 assert.ok(stale.includes('Stale'));assert.ok(stale.includes('not-ready'));
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

test('Control rail separates token revocation, explains unavailable actions and associates descriptions',()=>{
 const html=render({administration:{worker:{...worker,schedulingPolicy:'Draining',authenticationCredentialStatus:'revoked',activeAssignments:2},actions:[{key:'Enabled',label:'Activate scheduling',reason:'Activation blocked: Codex preflight required'},{key:'Draining',label:'Drain worker',reason:'This scheduling policy is already applied.'},{key:'Disabled',label:'Deactivate',reason:''},{key:'revoke-api',label:'Revoke Worker API token',reason:'No active Worker API token is registered.'}],onAction:()=>{},onRefresh:()=>{}}});
 for(const text of ['poc-control-rail','2 active assignment(s) keep their leases','No active Worker API token','Activation blocked: Codex preflight required','Existing assignments and leases are not cancelled','does not revoke credential-delivery authorization','aria-describedby="poc-token-effect poc-action-revoke-api"','Refresh authoritative state'])assert.ok(html.includes(text),text);
 assert.match(html,/<button[^>]*disabled=""[^>]*>[\s\S]*?Activate scheduling/);
 assert.match(html,/<button(?![^>]* disabled="")[^>]*>[\s\S]*?Deactivate/);
 assert.ok(!html.includes('Revoke delivery authorization'));
});
