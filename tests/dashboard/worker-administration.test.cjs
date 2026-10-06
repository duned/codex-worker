// Deterministic Worker administration dashboard projections; no browser or Server process required.
const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');

const html=require('./server-dashboard-source.cjs').readDashboard();
const start=html.indexOf('async function loadWorkerAdministration');
const end=html.indexOf('async function loadProjects',start);
const source=html.slice(start,end);

function setup(){
 class Element{constructor(dataset={}){this.dataset=dataset;this.innerHTML='';this.textContent='';this.onclick=null}}
 const elements=new Map();const $=id=>{if(!elements.has(id))elements.set(id,new Element());return elements.get(id)};
 const policyButtons=['Enabled','Draining','Disabled'].map(policy=>new Element({workerPolicy:policy}));
 const revokeButtons=['api','delivery'].map(kind=>new Element({workerRevoke:kind}));
 const calls=[];
 const worker={workerId:'worker-id',schedulingPolicy:'Draining',activeAssignments:2,authenticationCredentialStatus:'revoked',authenticationCredentialRevokedAtUtc:'2026-09-01T00:00:00Z'};
 const credentialDelivery={status:'active'};
 const readiness={canActivate:true,activationBlockingReasons:[]};
 const context=vm.createContext({...require('./server-dashboard-source.cjs').dashboardDependencies(),$,document:{querySelectorAll:selector=>selector==='[data-worker-policy]'?policyButtons:revokeButtons},
  esc:value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])),
  api:async(path,options)=>{calls.push({path,options});if(path.endsWith('/credential-access'))return credentialDelivery;if(path.endsWith('/diagnostics'))return readiness;return worker},
  loadOverview:async()=>{},loadWorker:async()=>{}});
 vm.runInContext(source,context);
 return {$,calls,policyButtons,revokeButtons,worker,readiness,context};
}

test('shows drain progress and separate Worker API and delivery authorization states',async()=>{
 const s=setup();await s.context.loadWorkerAdministration('worker-id');
 assert.match(s.$('worker-admin').innerHTML,/draining; 2 active assignment\(s\) keep their leases/);
 assert.match(s.$('worker-admin').innerHTML,/Worker API token/);
 assert.match(s.$('worker-admin').innerHTML,/revoked/);
 assert.match(s.$('worker-admin').innerHTML,/Delivery authorization/);
 assert.match(s.$('worker-admin').innerHTML,/Revoking a Worker API token denies calls using it/);
 assert.match(s.$('worker-admin').innerHTML,/it does not revoke delivery authorization/);
 assert.doesNotMatch(s.$('worker-admin').innerHTML,/shared Server registration-token fallback/);
 assert.deepEqual(s.policyButtons.map(button=>button.dataset.workerPolicy),['Enabled','Draining','Disabled']);
});

test('activation is disabled without authoritative evidence while drain and disable remain available',async()=>{
 const s=setup();s.readiness.canActivate=false;s.readiness.activationBlockingReasons=['Current revision and Codex preflight required'];
 await s.context.loadWorkerAdministration('worker-id');
 assert.match(s.$('worker-admin').innerHTML,/data-worker-policy="Enabled" disabled/);
 assert.match(s.$('worker-admin').innerHTML,/Current revision and Codex preflight required/);
 assert.doesNotMatch(s.$('worker-admin').innerHTML,/data-worker-policy="Draining" disabled/);
 assert.ok(s.calls.every(call=>!call.options));
});

test('policy and revocation controls call their separate management endpoints',async()=>{
 const s=setup();await s.context.loadWorkerAdministration('worker-id');
 await s.policyButtons[0].onclick();
 assert.ok(s.calls.some(call=>call.path==='/api/v1/workers/worker-id/scheduling-policy'&&call.options.method==='PUT'));
 await s.revokeButtons[0].onclick();
 await s.revokeButtons[1].onclick();
 assert.ok(s.calls.some(call=>call.path==='/api/v1/workers/worker-id/authentication/revoke'));
 assert.ok(s.calls.some(call=>call.path==='/api/v1/workers/worker-id/credential-access/revoke'));
});

test('a completed policy mutation cannot publish its old Worker after navigation changes context',async()=>{
 const s=setup();let current=true,resolveMutation;
 s.context.navigation={...s.context.navigation,capture:()=>0,isCurrent:()=>current};
 s.context.api=()=>new Promise(resolve=>{resolveMutation=resolve});
 let reloads=0;s.context.loadWorkerAdministration=async()=>reloads++;
 const pending=s.context.updateWorkerPolicy('old-worker','Draining');current=false;resolveMutation({});await pending;
 assert.equal(reloads,0);
});
