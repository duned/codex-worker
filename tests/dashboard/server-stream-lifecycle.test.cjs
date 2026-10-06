// Exercise the dashboard's real Connect/Enter entry points with coordinated fetch/read/timers.
const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const fs=require('node:fs');
const html=fs.readFileSync('src/CodexServer/dashboard.html','utf8');
const streamStart=html.indexOf('// One owner per page.');
const streamEnd=html.indexOf('function editProject(',streamStart);
const connectStart=html.indexOf("$('unlock').onclick=");
const connectEnd=html.indexOf("$('new-project').onclick=",connectStart);
const source=html.slice(streamStart,streamEnd)+html.slice(connectStart,connectEnd);
const deferred=()=>{let resolve,reject;const promise=new Promise((yes,no)=>{resolve=yes;reject=no});return {promise,resolve,reject}};
const flush=async()=>{for(let i=0;i<30;i++)await Promise.resolve()};

function setup(){
 const elements=new Map(),$=id=>{
  if(!elements.has(id))elements.set(id,{value:'',textContent:'',listeners:{},addEventListener(type,handler){this.listeners[type]=handler},click(){this.onclick()}});
  return elements.get(id);
 };
 const requests=[],timers=new Map(),events={},updates=[];let timerId=0,active=0,maximum=0;
 const context=vm.createContext({$,managementToken:'',AbortController,TextDecoder,Date,JSON,
  window:{addEventListener:(name,handler)=>events[name]=handler},
  setTimeout:handler=>{const id=++timerId;timers.set(id,handler);return id},clearTimeout:id=>timers.delete(id),
  renderWorkers:items=>updates.push(items),
  ...Object.fromEntries(['loadProjects','loadOverview','loadExecutions','loadNodes','loadProvisioning','loadCredentials'].map(name=>[name,()=>{}])),
  fetch:(path,options)=>{
   const response=deferred();active++;maximum=Math.max(maximum,active);
   const request={path,options,response,closed:false,close(){if(!this.closed){this.closed=true;active--}}};
   options.signal?.addEventListener('abort',()=>{request.close();if(!request.ignoreAbort)response.reject(Error('aborted'))},{once:true});
   requests.push(request);return response.promise;
  }
 });
 vm.runInContext(source,context);
 function respond(request,{ok=true,holdCancellation=false,staleRead=false}={}){
  let pending=null;const cancellation=deferred();
  const reader={cancelled:0,released:0,read(){pending=deferred();return pending.promise},cancel(){this.cancelled++;request.close();if(!staleRead)pending?.resolve({done:true});return holdCancellation?cancellation.promise:Promise.resolve()},releaseLock(){this.released++}};
  const body={cancelled:0,getReader:()=>reader,cancel(){this.cancelled++;request.close();return Promise.resolve()}};
  request.response.resolve({ok,body});
  return {reader,body,finishCancellation:()=>cancellation.resolve(),emit:items=>pending.resolve({value:new TextEncoder().encode('data: '+JSON.stringify(items)+'\n\n'),done:false}),end:()=>{request.close();pending.resolve({done:true})},fail:()=>{request.close();pending.reject(Error('outage'))}};
 }
 return {$,requests,timers,events,updates,respond,maximum:()=>maximum,active:()=>active,
  connect(token='test-token',enter=false){$('token').value=token;if(enter)$('token').listeners.keydown({key:'Enter'});else $('unlock').click()},
  retry(){assert.equal(timers.size,1);const callback=timers.values().next().value;callback()},
  async stop(){events.pagehide();await flush();assert.equal(active,0);assert.equal(timers.size,0)}
 };
}

test('repeated Connect and Enter replace the reader before opening a single authenticated request',async()=>{
 const s=setup();s.connect();await flush();
 const first=s.respond(s.requests[0],{holdCancellation:true});await flush();
 s.connect('replacement-token',true);s.connect('latest-token');await flush();
 assert.equal(s.requests.length,1);assert.equal(first.reader.cancelled,1);assert.equal(s.requests[0].options.signal.aborted,true);
 first.finishCancellation();await flush();
 assert.equal(first.reader.released,1);assert.equal(s.requests.length,2);assert.equal(s.maximum(),1);
 assert.equal(s.requests[1].path,'/api/v1/events/stream');assert.equal(s.requests[1].options.headers.Authorization,'Bearer latest-token');assert.equal(s.$('token').value,'');
 const latest=s.respond(s.requests[1]);await flush();latest.emit([{workerId:'current'}]);await flush();
 assert.equal(s.updates.length,1);assert.match(s.$('live').textContent,/updated/);await s.stop();assert.equal(latest.reader.released,1);
});

test('empty Connect disconnects during read and rapid reconnect cannot revive the obsolete reader',async()=>{
 const s=setup();s.connect();await flush();const first=s.respond(s.requests[0],{staleRead:true});await flush();
 s.connect('');s.connect('next-token');await flush();assert.equal(s.requests.length,1);
 first.emit([{workerId:'obsolete'}]);await flush();
 assert.equal(s.updates.length,0);assert.equal(s.requests.length,2);assert.equal(first.reader.released,1);assert.equal(s.timers.size,0);assert.equal(s.maximum(),1);
 await s.stop();
});

test('a stale fetch response releases its body without publishing status or starting a retry',async()=>{
 const s=setup();s.connect();await flush();s.requests[0].ignoreAbort=true;
 s.connect('replacement');await flush();assert.equal(s.requests.length,1);
 const stale=s.respond(s.requests[0]);await flush();
 assert.equal(stale.body.cancelled,1);assert.equal(stale.reader.cancelled,0);assert.equal(s.requests.length,2);assert.equal(s.updates.length,0);assert.equal(s.$('live').textContent,'Live · disconnected');assert.equal(s.timers.size,0);
 await s.stop();
});

test('failed requests use one cancellable retry wait and token replacement cancels that wait',async()=>{
 const s=setup();s.connect();await flush();const failed=s.respond(s.requests[0],{ok:false});await flush();
 assert.equal(failed.body.cancelled,1);assert.equal(s.active(),0);assert.equal(s.timers.size,1);
 const obsoleteTimer=s.timers.values().next().value;
 s.connect('next');await flush();assert.equal(s.timers.size,0);assert.equal(s.requests.length,2);
 obsoleteTimer();await flush();assert.equal(s.requests.length,2);
 s.requests[1].close();s.requests[1].response.reject(Error('unavailable'));await flush();assert.equal(s.timers.size,1);
 s.retry();await flush();assert.equal(s.requests.length,3);assert.equal(s.maximum(),1);
 s.requests[2].close();s.requests[2].response.reject(Error('unavailable'));await flush();
 await s.stop();obsoleteTimer();await flush();assert.equal(s.requests.length,3);
});

test('disconnect during retry cancels its timer and reconnect leaves obsolete callbacks inert',async()=>{
 const s=setup();s.connect();await flush();s.requests[0].close();s.requests[0].response.reject(Error('outage'));await flush();
 const obsoleteTimer=s.timers.values().next().value;
 s.connect('');await flush();assert.equal(s.timers.size,0);assert.equal(s.active(),0);assert.equal(s.$('live').textContent,'Live · disconnected');
 obsoleteTimer();await flush();assert.equal(s.requests.length,1);
 s.connect('replacement');await flush();obsoleteTimer();await flush();assert.equal(s.requests.length,2);assert.equal(s.maximum(),1);await s.stop();
});

test('Server outage and clean EOF release readers, then reconnect and resume Worker events',async()=>{
 const s=setup();s.connect();await flush();const first=s.respond(s.requests[0]);await flush();first.fail();await flush();
 assert.equal(first.reader.cancelled,1);assert.equal(first.reader.released,1);assert.equal(s.timers.size,1);
 s.retry();await flush();const restarted=s.respond(s.requests[1]);await flush();restarted.emit([{workerId:'after-restart'}]);await flush();
 assert.equal(s.updates[0][0].workerId,'after-restart');assert.match(s.$('live').textContent,/updated/);
 restarted.end();await flush();assert.equal(restarted.reader.released,1);assert.equal(s.timers.size,1);assert.equal(s.requests.length,2);
 s.retry();await flush();assert.equal(s.requests.length,3);assert.equal(s.maximum(),1);await s.stop();
});

test('page teardown cancels a read and back-forward cache restoration opens one current stream',async()=>{
 const s=setup();s.connect();await flush();const first=s.respond(s.requests[0]);await flush();await s.stop();assert.equal(first.reader.released,1);
 s.events.pageshow({persisted:false});await flush();assert.equal(s.requests.length,1);
 s.events.pageshow({persisted:true});await flush();assert.equal(s.requests.length,2);assert.equal(s.maximum(),1);await s.stop();
});

test('independent tabs retain independent stream owners',async()=>{
 const a=setup(),b=setup();a.connect();b.connect();await flush();assert.equal(a.active(),1);assert.equal(b.active(),1);
 await a.stop();assert.equal(b.active(),1);await b.stop();
});
