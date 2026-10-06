// One owner per page. Replacements wait for cancelled readers/requests to release.
let workerStreamOwner=null;
function cancelWorkerReader(owner){
 if(owner.reader&&!owner.readerCancellation){
  // Abort can already have errored the stream; cancellation still releases ownership.
  owner.readerCancellation=owner.reader.cancel().catch(()=>{});
 }
 return owner.readerCancellation;
}
function stopWorkerStream(){
 const owner=workerStreamOwner;
 if(owner){owner.controller.abort();cancelWorkerReader(owner)}
 $('live').textContent='Live · disconnected';
}
function waitForWorkerRetry(signal){
 return new Promise(resolve=>{
  if(signal.aborted){resolve();return}
  const finish=()=>{clearTimeout(timer);signal.removeEventListener('abort',finish);resolve()};
  const timer=setTimeout(finish,3000);
  signal.addEventListener('abort',finish,{once:true});
 });
}
function streamWorkers(){
 const previous=workerStreamOwner;
 stopWorkerStream();
 if(!authenticated)return previous?.done;
 const owner={controller:new AbortController(),reader:null,readerCancellation:null,done:null};
 workerStreamOwner=owner;
 const current=()=>workerStreamOwner===owner&&!owner.controller.signal.aborted;
 owner.done=(async()=>{
  if(previous)await previous.done;
  while(current()){
   let processing=false;
   try{
    const response=await fetch('/api/v1/events/stream',{credentials:'same-origin',cache:'no-store',signal:owner.controller.signal});
    // A response can arrive after cancellation. Release its body without publishing it.
    if(!current()){if(response.body)await response.body.cancel();break}
    if(response.status===401||response.status===403){
     if(response.body)await response.body.cancel();
     signOut('Your session expired or was rejected. Sign in again.');
     break;
    }
    if(!response.ok||!response.body){if(response.body)await response.body.cancel();throw Error('Stream unavailable')}
    owner.reader=response.body.getReader();
    $('live').textContent='Live · connected';
    const decoder=new TextDecoder();let buffer='';
    while(current()){
     const {value,done}=await owner.reader.read();
     if(!current()||done)break;
     buffer+=decoder.decode(value,{stream:true});
     const events=buffer.split('\n\n');buffer=events.pop()||'';
     for(const item of events){
      const data=item.split('\n').find(line=>line.startsWith('data: '));
      if(data){processing=true;renderWorkers(JSON.parse(data.slice(6)));processing=false;$('live').textContent='Live · updated '+new Date().toLocaleTimeString()}
     }
    }
   }catch{
    // Fixed diagnostics never expose exception text, event bodies or credentials.
    if(current()&&processing){
     invalidateWorkers();$('live').textContent='Live · Worker event processing failed. Refresh the dashboard or reconnect after correcting the Server data.';
     break;
    }
   }finally{
    if(owner.reader){await cancelWorkerReader(owner);owner.reader.releaseLock();owner.reader=null;owner.readerCancellation=null}
   }
   if(current()){invalidateWorkers();$('live').textContent='Live · connection interrupted; retrying';await waitForWorkerRetry(owner.controller.signal)}
  }
 })();
 return owner.done;
}
window.addEventListener('pagehide',()=>{stopWorkerStream();stopDashboardPolling();for(const controller of administrationRequests)controller.abort();sessionGeneration++;navigation.stop()});
window.addEventListener('pageshow',event=>{if(event.persisted)restoreSession()});
