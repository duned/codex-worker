// The actual login/session/logout functions and button bindings from the dashboard.
const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const fs=require('node:fs');
const html=fs.readFileSync('src/CodexServer/dashboard.html','utf8');
const source=html.slice(html.indexOf('// Administration requests'),html.indexOf('function renderWorkers('));
const binding=html.slice(html.indexOf("$('unlock').onclick="),html.indexOf("$('new-project').onclick="));
const flush=async()=>{for(let i=0;i<30;i++)await Promise.resolve()};
function setup(){
 const elements=new Map(),$=id=>{if(!elements.has(id))elements.set(id,{value:'',hidden:false,listeners:{},addEventListener(name,fn){this.listeners[name]=fn},click(){return this.onclick()}});return elements.get(id)};
 const requests=[],timers=new Map();let timerId=0,streamStarts=0,streamStops=0;
 const context=vm.createContext({$,authenticated:false,workers:[],projects:[],Headers,AbortController,AbortSignal,Date,Set,Math,
  document:{querySelectorAll:()=>[]},stopWorkerStream:()=>streamStops++,streamWorkers:()=>streamStarts++,
  setTimeout:fn=>{const id=++timerId;timers.set(id,fn);return id},clearTimeout:id=>timers.delete(id),
  ...Object.fromEntries(['loadProjects','loadOverview','loadExecutions','loadNodes','loadProvisioning','loadCredentials'].map(name=>[name,()=>{}])),
  fetch:(path,options)=>new Promise((resolve,reject)=>{requests.push({path,options,resolve,reject});options.signal?.addEventListener('abort',()=>reject(Object.assign(Error(),{name:'AbortError'})))})});
 vm.runInContext(source+binding,context);
 const run=code=>vm.runInContext(code,context);
 const respond=(index,status=200,body={csrfToken:'csrf-test',expiresAtUtc:new Date(Date.now()+3600000).toISOString()})=>requests[index].resolve({ok:status>=200&&status<300,status,json:async()=>body});
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
  const s=setup();s.$('token').value='rejected';const pending=s.run(operation);s.respond(0,401);await pending;
  assert.equal(s.context.authenticated,false);assert.equal(s.starts(),0);assert.equal(s.timers.size,0);assert.equal(s.requests.length,1);assert.match(s.$('session-message').textContent,/rejected/);
 }
});

test('transport failure stays distinct from session rejection and requires deliberate retry',async()=>{
 const s=setup();const pending=s.run('restoreSession()');s.requests[0].reject(Error('network'));await pending;
 assert.match(s.$('session-message').textContent,/connection unavailable/);assert.equal(s.requests.length,1);assert.equal(s.timers.size,0);
});

test('logout cancels pending requests and stream, clears session state, and revokes cookie on Server',async()=>{
 const s=setup();const restoring=s.run('restoreSession()');s.respond(0);await restoring;
 const pending=s.run("api('/api/v1/workers')").catch(e=>e.name);
 const loggingOut=s.$('logout').click();assert.equal(s.requests[1].options.signal.aborted,true);
 assert.equal(s.context.authenticated,false);assert.equal(s.timers.size,0);assert.equal(s.$('administration-content').hidden,true);
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
