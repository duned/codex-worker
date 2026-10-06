const vm=require('node:vm');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const html=fs.readFileSync(path.join(__dirname,'../../src/CodexServer/dashboard.html'),'utf8');
const streamStart=html.indexOf('// One owner per page.');
const streamEnd=html.indexOf('function editProject(',streamStart);
const connectStart=html.indexOf("$('unlock').onclick=");
const connectEnd=html.indexOf("$('new-project').onclick=",connectStart);
const source=html.slice(streamStart,streamEnd)+html.slice(connectStart,connectEnd);
const deferred=()=>{let resolve,reject;const promise=new Promise((yes,no)=>{resolve=yes;reject=no});return {promise,resolve,reject}};
const flush=async()=>{for(let i=0;i<30;i++)await Promise.resolve()};

function setup({realRenderer=false}={}){
 const elements=new Map(),$=id=>{
  if(!elements.has(id))elements.set(id,{value:'',textContent:'',listeners:{},addEventListener(type,handler){this.listeners[type]=handler},click(){this.onclick()}});
  return elements.get(id);
 };
 const requests=[],timers=new Map(),events={},updates=[];let timerId=0,active=0,maximum=0;
 const context=vm.createContext({$,managementToken:'',workers:[],AbortController,TextDecoder,Date,JSON,
  document:{querySelectorAll:()=>[]},
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
 const renderStart=html.indexOf('function renderWorkers(');
 const renderEnd=html.indexOf('\n',renderStart);
 const escapeStart=html.indexOf('const esc=');
 const escapeEnd=html.indexOf('\n',escapeStart);
 vm.runInContext((realRenderer?html.slice(escapeStart,escapeEnd)+html.slice(renderStart,renderEnd):'')+source,context);
 function respond(request,{ok=true,status=ok?200:503,holdCancellation=false,staleRead=false}={}){
  let pending=null;const cancellation=deferred();
  const reader={cancelled:0,released:0,read(){pending=deferred();return pending.promise},cancel(){this.cancelled++;request.close();if(!staleRead)pending?.resolve({done:true});return holdCancellation?cancellation.promise:Promise.resolve()},releaseLock(){this.released++}};
  const body={cancelled:0,getReader:()=>reader,cancel(){this.cancelled++;request.close();return Promise.resolve()}};
  request.response.resolve({ok,status,body});
  return {reader,body,finishCancellation:()=>cancellation.resolve(),emit:items=>pending.resolve({value:new TextEncoder().encode('data: '+JSON.stringify(items)+'\n\n'),done:false}),emitFrame:frame=>pending.resolve({value:new TextEncoder().encode(frame),done:false}),end:()=>{request.close();pending.resolve({done:true})},fail:()=>{request.close();pending.reject(Error('outage'))}};
 }
 return {$,requests,timers,events,updates,respond,maximum:()=>maximum,active:()=>active,
  connect(token='test-token',enter=false){$('token').value=token;if(enter)$('token').listeners.keydown({key:'Enter'});else $('unlock').click()},
  retry(){assert.equal(timers.size,1);const callback=timers.values().next().value;callback()},
  async stop(){events.pagehide();await flush();assert.equal(active,0);assert.equal(timers.size,0)}
 };
}
module.exports={setup,flush};
