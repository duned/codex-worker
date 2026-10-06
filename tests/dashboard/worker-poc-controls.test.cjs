const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const {readDashboard,navigationStub}=require('./server-dashboard-source.cjs');
const html=readDashboard();
const source=html.slice(html.indexOf('// The PoC shares'),html.indexOf('function workerPreparation'));
function setup(){
 const calls=[],updates=[],confirmations=[];
 const worker={workerId:'worker-a',schedulingPolicy:'Disabled',authenticationCredentialStatus:'active',activeAssignments:2};
 const diagnostics={canActivate:true,activationBlockingReasons:[]};
 let confirmResult=true,mutation=async()=>{};
 const context=vm.createContext({authenticated:true,sessionGeneration:0,navigation:navigationStub,
  window:{codexWorkerPoc:{update:data=>updates.push(data)}},
  confirm:message=>{confirmations.push(message);return confirmResult},loadOverview:async()=>{},
  api:async(path,options)=>{
   calls.push({path,options});
   if(options){await mutation(path,options);if(path.endsWith('/authentication/revoke'))worker.authenticationCredentialStatus='revoked';else worker.schedulingPolicy=JSON.parse(options.body).policy;return {...worker}}
   return path.endsWith('/diagnostics')?{...diagnostics}:{...worker};
  }});
 vm.runInContext(source,context);
 return {context,calls,updates,confirmations,worker,diagnostics,controls:()=>updates.at(-1).administration,
  confirm:value=>confirmResult=value,mutate:handler=>mutation=handler};
}
test('Supported controls confirm effects and use existing scheduling and API authentication contracts',async()=>{
 const s=setup();await s.context.loadWorkerPoc('worker-a');
 for(const key of ['Enabled','Draining','Disabled','revoke-api']){
  await s.controls().onAction(key);
  assert.equal(s.controls().needsRefresh,false);
 }
 const writes=s.calls.filter(c=>c.options);
 assert.deepEqual(writes.map(c=>c.options.method),['PUT','PUT','PUT','POST']);
 assert.deepEqual(writes.slice(0,3).map(c=>JSON.parse(c.options.body).policy),['Enabled','Draining','Disabled']);
 assert.equal(writes.at(-1).path,'/api/v1/workers/worker-a/authentication/revoke');
 assert.equal(s.controls().worker.authenticationCredentialStatus,'revoked');
 assert.match(s.confirmations[0],/Existing assignments and leases are not cancelled/);
 assert.match(s.confirmations.at(-1),/Credential-delivery authorization and provider credentials are unchanged/);
 assert.ok(!s.calls.some(c=>c.path.includes('/credential-access')));
});
test('Cancellation, blocked activation, repeated policy and missing token never submit mutations',async()=>{
 const s=setup();await s.context.loadWorkerPoc('worker-a');s.confirm(false);
 await s.controls().onAction('Enabled');assert.equal(s.confirmations.length,1);
 s.confirm(true);s.diagnostics.canActivate=false;s.diagnostics.activationBlockingReasons=['Current Codex preflight required'];
 s.worker.authenticationCredentialStatus='not-configured';await s.context.loadWorkerPoc('worker-a');
 assert.match(s.controls().actions[0].reason,/Current Codex preflight required/);
 for(const key of ['Enabled','Disabled','revoke-api','delivery'])await s.controls().onAction(key);
 assert.equal(s.calls.filter(c=>c.options).length,0);
 assert.equal(s.confirmations.length,1);
 s.context.authenticated=false;await s.controls().onAction('Draining');
 assert.equal(s.calls.filter(c=>c.options).length,0);
});
test('Pending operations resist double submission and polling cannot unlock an uncertain response',async()=>{
 const s=setup();await s.context.loadWorkerPoc('worker-a');
 let rejectMutation;s.mutate(()=>new Promise((resolve,reject)=>{rejectMutation=reject}));
 const pending=s.controls().onAction('Enabled');
 assert.equal(s.controls().pending,true);
 await s.controls().onAction('Draining');await s.context.loadWorkerPoc('worker-a');
 assert.equal(s.controls().pending,true);
 rejectMutation(Error('private process detail'));await pending;
 assert.equal(s.controls().needsRefresh,true);
 assert.ok(!s.controls().message.includes('private process detail'));
 await s.context.loadWorkerPoc('worker-a');await s.controls().onAction('Enabled');
 assert.equal(s.controls().needsRefresh,true);
 assert.equal(s.calls.filter(c=>c.options).length,1);
 // Simulate a lost success response: refresh observes the committed policy, never replays it.
 s.worker.schedulingPolicy='Enabled';await s.controls().onRefresh();
 assert.equal(s.controls().needsRefresh,false);assert.equal(s.controls().worker.schedulingPolicy,'Enabled');
 await s.controls().onAction('Enabled');assert.equal(s.calls.filter(c=>c.options).length,1);
});
test('Rejected stale activation refreshes current Server reasons before any deliberate retry',async()=>{
 const s=setup();await s.context.loadWorkerPoc('worker-a');
 s.mutate(async()=>{s.diagnostics.canActivate=false;s.diagnostics.activationBlockingReasons=['Configuration revision changed'];throw Object.assign(Error('HTTP 409'),{httpStatus:409})});
 await s.controls().onAction('Enabled');assert.match(s.controls().message,/Server rejected activation/);await s.controls().onRefresh();
 assert.match(s.controls().actions[0].reason,/Configuration revision changed/);
 await s.controls().onAction('Enabled');assert.equal(s.calls.filter(c=>c.options).length,1);
});
test('Unavailable refresh retains the operation lock; logout and navigation reject late mutation publication',async()=>{
 const s=setup();await s.context.loadWorkerPoc('worker-a');
 s.mutate(async()=>{throw Error('unknown result')});await s.controls().onAction('Draining');
 s.context.api=async()=>{throw Error('read unavailable')};await s.controls().onRefresh();
 assert.equal(s.controls().needsRefresh,true);assert.equal(s.controls().worker,null);
 for(const invalidate of [c=>{c.authenticated=false;c.sessionGeneration++},c=>{c.navigation={...navigationStub,isCurrent:()=>false}}]){
  const t=setup();await t.context.loadWorkerPoc('worker-a');let finish;
  t.mutate(()=>new Promise(resolve=>{finish=resolve}));const pending=t.controls().onAction('Draining');
  const count=t.updates.length;invalidate(t.context);finish();await pending;assert.equal(t.updates.length,count);
 }
});

test('Missing readiness diagnostics block only activation, preserving independent registry controls',async()=>{
 const s=setup(),request=s.context.api;
 s.context.api=async(path,options)=>{if(path.endsWith('/diagnostics'))throw Error('diagnostics unavailable');return request(path,options)};
 await s.context.loadWorkerPoc('worker-a');
 assert.match(s.controls().actions[0].reason,/Readiness evidence unavailable/);
 assert.equal(s.controls().actions.find(a=>a.key==='Draining').reason,'');
 assert.equal(s.controls().actions.find(a=>a.key==='revoke-api').reason,'');
 await s.controls().onAction('revoke-api');
 assert.equal(s.controls().worker.authenticationCredentialStatus,'revoked');
});
