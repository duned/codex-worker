// The actual login/session/logout functions and button bindings from the dashboard.
const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const html=require('./server-dashboard-source.cjs').readDashboard();
const source=html.slice(html.indexOf('// Administration requests'),html.indexOf('function renderWorkers('));
const binding=html.slice(html.indexOf("$('unlock').onclick="),html.indexOf("$('new-project').onclick="));
const flush=async()=>{for(let i=0;i<30;i++)await Promise.resolve()};
function setup(){
 const elements=new Map(),$=id=>{if(!elements.has(id))elements.set(id,{value:'',hidden:false,listeners:{},addEventListener(name,fn){this.listeners[name]=fn},click(){return this.onclick()}});return elements.get(id)};
 const requests=[],timers=new Map();let timerId=0,streamStarts=0,streamStops=0;
 const context=vm.createContext({...require('./server-dashboard-source.cjs').dashboardDependencies(),$,authenticated:false,workers:[],projects:[],Headers,AbortController,AbortSignal,Date,Set,Math,
  document:{querySelectorAll:()=>[]},stopWorkerStream:()=>streamStops++,streamWorkers:()=>streamStarts++,
  setTimeout:fn=>{const id=++timerId;timers.set(id,fn);return id},clearTimeout:id=>timers.delete(id),
  ...Object.fromEntries(['loadProjects','loadOverview','loadExecutions','loadNodes','loadProvisioning','loadCredentials'].map(name=>[name,()=>{}])),
  fetch:(path,options)=>new Promise((resolve,reject)=>{requests.push({path,options,resolve,reject});options.signal?.addEventListener('abort',()=>reject(Object.assign(Error(),{name:'AbortError'})))})});
 const onboarding=html.slice(html.indexOf('// Only the public request'),html.indexOf('async function openOnboarding'));
 vm.runInContext(onboarding+source+binding,context);
 const run=code=>vm.runInContext(code,context);
 const respond=(index,status=200,body={csrfToken:'csrf-test',expiresAtUtc:new Date(Date.now()+3600000).toISOString()},code=null)=>requests[index].resolve({ok:status>=200&&status<300,status,headers:new Headers(code?{'X-Codex-Administration-Error':code}:{}),json:async()=>body});
 return {$,requests,timers,context,run,respond,starts:()=>streamStarts,stops:()=>streamStops};
}

test('real sign-in and Enter use token only for login, and mutations use session CSRF',async()=>{
 const s=setup();s.$('token').value='transient-management';s.$('token').listeners.keydown({key:'Enter'});await flush();
 assert.equal(s.requests[0].options.headers.get('Authorization'),'Bearer transient-management');assert.equal(s.$('token').value,'');
 s.respond(0);await flush();assert.equal(s.starts(),1);assert.equal(s.$('administration-content').hidden,false);
 const mutation=s.run("api('/api/v1/projects',{method:'POST',body:'{}'})");
 assert.equal(s.requests[1].options.headers.get('Authorization'),null);assert.equal(s.requests[1].options.headers.get('X-Codex-CSRF'),'csrf-test');
 assert.equal(s.requests[1].options.credentials,'same-origin');assert.equal(s.requests[1].options.cache,'no-store');s.respond(1);await mutation;
});

test('reload restores the existing cookie session without a management token',async()=>{
 const s=setup();const restoring=s.run('restoreSession()');assert.equal(s.requests[0].path,'/api/v1/administration/session');
 assert.equal(s.requests[0].options.headers.get('Authorization'),null);s.respond(0);await restoring;assert.equal(s.starts(),1);assert.equal(s.context.authenticated,true);
});

test('rejected login and reload produce one sign-in state without retries',async()=>{
 for(const operation of ['signIn()','restoreSession()']){
  const s=setup();s.$('token').value='rejected';const pending=s.run(operation);s.respond(0,401,{},operation==='signIn()'?'administration-token-invalid':'administration-session-invalid');await pending;
  assert.equal(s.context.authenticated,false);assert.equal(s.starts(),0);assert.equal(s.timers.size,0);assert.equal(s.requests.length,1);assert.match(s.$('session-message').textContent,/rejected|No valid administration session/);
 }
});

test('transport failure stays distinct from session rejection and requires deliberate retry',async()=>{
 const s=setup();const pending=s.run('restoreSession()');s.requests[0].reject(Error('network'));await pending;
 assert.match(s.$('session-message').textContent,/connection unavailable/);assert.equal(s.requests.length,1);assert.equal(s.timers.size,0);
});

test('logout cancels pending requests and stream, clears session state, and revokes cookie on Server',async()=>{
 const s=setup();const restoring=s.run('restoreSession()');s.respond(0);await restoring;
 s.$('onboarding-authorization').value='ephemeral-authorization';
 const pending=s.run("api('/api/v1/workers')").catch(e=>e.name);
 const loggingOut=s.$('logout').click();assert.equal(s.requests[1].options.signal.aborted,true);
 assert.equal(s.context.authenticated,false);assert.equal(s.$('onboarding-authorization').value,'');assert.equal(s.timers.size,0);assert.equal(s.$('administration-content').hidden,true);
 assert.equal(s.requests[2].options.method,'DELETE');assert.equal(s.requests[2].options.headers['X-Codex-CSRF'],'csrf-test');
 s.respond(2,204);await loggingOut;assert.equal(await pending,'AbortError');assert.equal(s.stops(),2);
 await assert.rejects(s.run("api('/api/v1/workers')"),/Sign in/);assert.equal(s.requests.length,3);
});

test('expiry and API rejection stop streams and future dashboard requests',async()=>{
 for(const expired of [true,false]){
  const s=setup();const restoring=s.run('restoreSession()');s.respond(0);await restoring;
  if(expired){s.timers.values().next().value()}else{const pending=s.run("api('/api/v1/workers')");s.respond(1,401);await assert.rejects(pending,/sign in required/)}
  assert.equal(s.context.authenticated,false);assert.equal(s.stops(),2);assert.equal(s.timers.size,0);assert.match(s.$('session-message').textContent,/expired/);
 }
});

test('ordinary reads coalesce only while pending and isolate filters and sessions',async()=>{
 const s=setup();const restoring=s.run('restoreSession()');s.respond(0);await restoring;
 const a=s.run("api('/api/v1/executions?projectId=a')"),b=s.run("api('/api/v1/executions?projectId=a')"),c=s.run("api('/api/v1/executions?projectId=b')");
 assert.equal(s.requests.length,3);s.respond(1,200,[]);s.respond(2,200,[]);await Promise.all([a,b,c]);
 const fresh=s.run("api('/api/v1/executions?projectId=a')");assert.equal(s.requests.length,4);s.respond(3,200,[]);await fresh;
 const old=s.run("api('/api/v1/workers')").catch(e=>e.name);s.run('signOut()');assert.equal(await old,'AbortError');assert.equal(s.run('dashboardReads.size'),0);
 const next=s.run('restoreSession()');s.respond(5);await next;const read=s.run("api('/api/v1/workers')");assert.equal(s.requests.length,7);s.respond(6,200,[]);await read;
});

test('logout during JSON decoding ignores obsolete completion and old expiry callbacks',async()=>{
 const s=setup();const restoring=s.run('restoreSession()');s.respond(0);await restoring;
 const expiry=s.timers.values().next().value;let finish;
 const decoding=new Promise(resolve=>finish=resolve);const pending=s.run("api('/api/v1/workers')").catch(e=>e.name);
 s.requests[1].resolve({ok:true,status:200,json:()=>decoding});await flush();s.run('signOut()');
 const next=s.run('restoreSession()');s.respond(2);await next;expiry();assert.equal(s.context.authenticated,true);
 finish([{private:'obsolete'}]);assert.equal(await pending,'AbortError');assert.equal(s.starts(),2);
});

test('API diagnostics name the failed resource without exposing bodies or filter values',async()=>{
 const s=setup();const restoring=s.run('restoreSession()');s.respond(0);await restoring;
 const read=s.run("api('/api/v1/projects?secret=private-query')");s.respond(1,500,{error:'private-token authentication challenge'});
 await assert.rejects(read,e=>/GET \/api\/v1\/projects.*HTTP 500.*refresh/.test(e.message)&&!e.message.includes('private'));
 const invalid=s.run("api('/api/v1/nodes')");s.requests[2].resolve({ok:true,status:200,json:async()=>{throw Error('private-body')}});
 await assert.rejects(invalid,/GET \/api\/v1\/nodes: invalid Server data/);
});

test('pending read registry has a fixed bound and cancellation releases capacity',async()=>{
 const s=setup();const restoring=s.run('restoreSession()');s.respond(0);await restoring;
 const pending=Array.from({length:64},(_,i)=>s.run(`api('/api/v1/executions?offset=${i}')`).catch(e=>e.name));
 await assert.rejects(s.run("api('/api/v1/executions?offset=64')"),/capacity/);assert.equal(s.run('dashboardReads.size'),64);
 s.run('signOut()');await Promise.all(pending);assert.equal(s.run('dashboardReads.size'),0);
});

// Codes are fixed allowlisted guidance; proxy bodies/headers must never reach the UI.
test('configuration, token and unknown proxy rejection show distinct safe help without retries',async()=>{
 for(const [code,pattern] of [
  ['administration-origin-missing',/not configured/],
  ['administration-host-mismatch',/Host mismatch/],
  ['administration-origin-mismatch',/origin does not match/],
  ['administration-token-invalid',/token was rejected/],
  [null,/VPN management network/],['__proto__',/VPN management network/],['untrusted-secret',/VPN management network/]
 ]){
  for(const operation of ['signIn()','restoreSession()']){
   const s=setup();s.$('token').value='private-test-token';const pending=s.run(operation);
   s.respond(0,code==='administration-token-invalid'?401:403,{message:'raw-private-payload'},code);await pending;
   assert.match(s.$('session-message').textContent,pattern);
   assert.doesNotMatch(s.$('session-message').textContent,/raw-private-payload|private-test-token|untrusted-secret/);
   assert.equal(s.requests.length,1);assert.equal(s.starts(),0);assert.equal(s.timers.size,0);
   assert.equal(s.$('unlock').disabled,false);assert.equal(s.$('token').value,operation==='signIn()'?'':'private-test-token');
  }
 }
 assert.match(html,/sudo sed -n/);assert.match(html,/config set AdministrationOrigin/);
});
