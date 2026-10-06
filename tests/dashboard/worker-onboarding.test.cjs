const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const fs=require('node:fs');
const source=fs.readFileSync('src/CodexServer/dashboard-onboarding.js','utf8');
function setup(){
 const elements=new Map(),calls=[],timers=[];
 const $=id=>{if(!elements.has(id))elements.set(id,{value:'',hidden:false,type:'password',textContent:'',showModal(){},close(){this.onclose?.()}});return elements.get(id)};
 const request={contractVersion:1,workerId:'a'.repeat(32),operation:'enroll',server:'https://server.example'};
 $('onboarding-kind').value='enroll';$('onboarding-request').value=JSON.stringify(request);
 const workers=[];
 const context=vm.createContext({$,window:{location:{origin:request.server}},setTimeout:fn=>{timers.push(fn);return 1},clearTimeout:()=>{},api:async(path,options)=>{calls.push({path,options});return path==='/api/version'?{version:'0.23.1'}:options?{authorization:'transient-secret',lifetimeSeconds:900}:workers}});
 vm.runInContext(source,context);return {context,$,request,calls,workers,timers};
}
test('new and existing paths show pinned installer or identity-preserving association',async()=>{
 const s=setup();await s.context.openOnboarding();
 assert.match(s.$('onboarding-instruction').textContent,/\/v0\.23\.1\/.*--version 0\.23\.1.*--pair --start/);
 s.$('onboarding-kind').value='associate';await s.context.updateOnboardingInstruction();
 assert.match(s.$('onboarding-instruction').textContent,/--operation associate --pair/);
 assert.match(s.$('onboarding-local-note').textContent,/reconcile active leases/);
});
test('authorization is issued only for matching public request and cleared on close, expiry and progress',async()=>{
 const s=setup();await s.context.authorizeOnboarding();
 assert.equal(s.$('onboarding-authorization').value,'transient-secret');
 assert.equal(s.$('onboarding-authorization').type,'password');
 assert.doesNotMatch(s.calls[0].options.body,/transient-secret/);
 s.$('onboarding-dialog').onclose();assert.equal(s.$('onboarding-authorization').value,'');
 await s.context.authorizeOnboarding();s.timers.at(-1)();assert.equal(s.$('onboarding-authorization').value,'');
 s.$('onboarding-request').value=JSON.stringify({...s.request,server:'https://wrong.example'});
 const count=s.calls.length;await s.context.authorizeOnboarding();assert.equal(s.calls.length,count);
 assert.match(s.$('onboarding-message').textContent,/does not match/);
});
test('reload reconciles from same public request and only observed communication leads to preparation',async()=>{
 const s=setup();await s.context.checkOnboarding();assert.match(s.$('onboarding-message').textContent,/Not registered/);
 s.workers.push({workerId:s.request.workerId,authenticationCredentialStatus:'active',availability:'stale'});
 await s.context.checkOnboarding();assert.equal(s.$('onboarding-prepare').hidden,true);
 Object.assign(s.workers[0],{lastHeartbeatAtUtc:'2026-10-06T12:00:00Z',availability:'online'});
 await s.context.checkOnboarding();assert.equal(s.$('onboarding-prepare').hidden,false);
 assert.match(s.$('onboarding-message').textContent,/Execution readiness is not established/);
 assert.ok(s.calls.every(call=>!call.options));
});
test('a late authorization response after closing is never published',async()=>{
 const s=setup();let resolve;s.context.api=()=>new Promise(done=>resolve=done);
 const pending=s.context.authorizeOnboarding();s.$('onboarding-dialog').onclose();resolve({authorization:'late-secret',lifetimeSeconds:900});await pending;
 assert.equal(s.$('onboarding-authorization').value,'');
});

test('invalid public payloads and operation mismatches show safe instructions without authorization requests',async()=>{
 const s=setup();for(const value of ['null','[]','{}',JSON.stringify({...s.request,operation:'associate'})]){
  s.$('onboarding-request').value=value;await s.context.authorizeOnboarding();
  assert.match(s.$('onboarding-message').textContent,/Request does not match/);
 }
 assert.equal(s.calls.length,0);
});
