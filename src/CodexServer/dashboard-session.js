// Administration requests share a generation so logout cannot publish late responses.
let csrfToken='',sessionGeneration=0,sessionTimer=null,pendingLogoutCsrf='';
const administrationRequests=new Set();
function signOut(message='Sign in to administer this Server.'){
 authenticated=false;csrfToken='';sessionGeneration++;clearTimeout(sessionTimer);stopDashboardPolling();stopWorkerStream();
 cancelDashboardReads();for(const controller of administrationRequests)controller.abort();administrationRequests.clear();
 $('unlock').disabled=false;$('administration-login').hidden=false;$('administration-content').hidden=true;$('logout').hidden=true;
 $('session-expiration').textContent='';$('session-message').textContent=message;$('live').textContent='Live · signed out';
 for(const dialog of document.querySelectorAll('dialog')){if(dialog.open)dialog.close()}
 $('credential-secret').value='';clearOnboardingAuthorization();resetResources();navigation.stop();
}
// Only pending GETs are shared; completed data is never cached or treated as authority.
const dashboardReads=new Map();
function cancelDashboardReads(){
 for(const entry of dashboardReads.values())entry.controller.abort();
 dashboardReads.clear();
}
function obsoleteDashboardRead(){return Object.assign(Error('Dashboard read cancelled.'),{name:'AbortError'})}
function api(path,options={},sessionRequest=false){
 if(!authenticated&&!sessionRequest)return Promise.reject(Error('Sign in to administer this Server.'));
 const method=(options.method||'GET').toUpperCase(),read=!sessionRequest&&method==='GET';
 // Headers and other caller options may change semantics; share only ordinary reads.
 const shared=read&&Object.keys(options).length===0;
 const key=path,generation=sessionGeneration,route=navigation.capture();
 if(shared&&dashboardReads.has(key))return dashboardReads.get(key).promise;
 if(shared&&dashboardReads.size>=64)return Promise.reject(Error('Dashboard read capacity reached. Refresh after pending reads finish.'));
 const controller=new AbortController();administrationRequests.add(controller);
 const entry={controller,promise:null};
 const current=()=>generation===sessionGeneration&&!controller.signal.aborted&&(!read||navigation.isCurrent(route));
 entry.promise=(async()=>{
  const headers=new Headers(options.headers||{});if(options.body)headers.set('Content-Type','application/json');
  if(!sessionRequest&&!['GET','HEAD'].includes(method))headers.set('X-Codex-CSRF',csrfToken);
  try{
   const response=await fetch(path,{...options,headers,credentials:'same-origin',cache:'no-store',signal:AbortSignal.any([controller.signal,AbortSignal.timeout(15000)])});
   if(!current())throw obsoleteDashboardRead();
   if(response.status===401||response.status===403){signOut('Your session expired or was rejected. Sign in again.');throw Error('Administration sign in required.');}
   // Never display untrusted error bodies, authentication challenges or query values.
   const resource=path.split('?')[0].slice(0,160);
   if(!response.ok)throw Error(`${method} ${resource} failed (HTTP ${response.status}). State may be stale; refresh before retrying.`);
   if(!sessionRequest&&!read&&method!=='HEAD')cancelDashboardReads();
   let result;
   try{result=response.status===204?null:await response.json()}
   catch{throw Error(`${method} ${resource}: invalid Server data. Refresh or check the Server deployment.`)}
   if(!current())throw obsoleteDashboardRead();
   return result;
  }catch(e){
   if(e.message==='Administration sign in required.')throw e;
   if(!current())throw obsoleteDashboardRead();
   if(e.name==='TypeError'||e.name==='TimeoutError')throw Error(`${method} ${path.split('?')[0].slice(0,160)}: connection unavailable. State may be stale; check the connection and refresh.`);
   throw e;
  }finally{
   administrationRequests.delete(controller);
   if(dashboardReads.get(key)===entry)dashboardReads.delete(key);
  }
 })();
 if(shared)dashboardReads.set(key,entry);
 return entry.promise;
}

function startSession(session){
 csrfToken=session.csrfToken;authenticated=true;pendingLogoutCsrf='';
 $('administration-login').hidden=true;$('administration-content').hidden=false;$('logout').hidden=false;
 $('session-expiration').textContent='Session expires '+new Date(session.expiresAtUtc).toLocaleString();
 const generation=sessionGeneration;clearTimeout(sessionTimer);sessionTimer=setTimeout(()=>{if(generation===sessionGeneration)signOut('Your session expired. Sign in again.')},Math.max(0,Date.parse(session.expiresAtUtc)-Date.now()));
 navigation.start();startDashboardPolling();streamWorkers();
}
async function restoreSession(){signOut('Checking administration session…');const generation=sessionGeneration;try{const session=await api('/api/v1/administration/session',{},true);if(generation===sessionGeneration)startSession(session)}catch(e){if(authenticated||e.message==='Administration session ended.')return;if(e.name!=='AbortError'&&e.message!=='Administration sign in required.')$('session-message').textContent='Server connection unavailable. Choose Check session to try again.'}}
async function signIn(){
 const token=$('token').value;$('token').value='';signOut('Signing in…');$('unlock').disabled=true;const generation=sessionGeneration;
 try{const session=await api('/api/v1/administration/session',{method:'POST',headers:{Authorization:'Bearer '+token}},true);if(generation===sessionGeneration)startSession(session)}
 catch(e){if(e.name!=='AbortError'&&e.message!=='Administration session ended.')$('session-message').textContent=e.message==='Administration sign in required.'?'Sign in was rejected. Check the management token and configured administration origin.':'Server connection unavailable. Try signing in again.'}
 finally{if(generation===sessionGeneration)$('unlock').disabled=false}
}
async function logout(){
 pendingLogoutCsrf=csrfToken||pendingLogoutCsrf;signOut();const generation=sessionGeneration,controller=new AbortController();administrationRequests.add(controller);
 try{const response=await fetch('/api/v1/administration/session',{method:'DELETE',headers:{'X-Codex-CSRF':pendingLogoutCsrf},credentials:'same-origin',cache:'no-store',signal:AbortSignal.any([controller.signal,AbortSignal.timeout(10000)])});if(!response.ok&&response.status!==401)throw Error();if(generation===sessionGeneration)pendingLogoutCsrf=''}
 catch{if(generation!==sessionGeneration)return;$('session-message').textContent='Sign out could not be confirmed by the Server. Retry sign out.';$('logout').hidden=false}
 finally{administrationRequests.delete(controller)}
}

// A single bounded polling owner per session. Rendering never creates timers.
let dashboardPollTimer=null,dashboardPollOwner=0;
function stopDashboardPolling(){dashboardPollOwner++;clearTimeout(dashboardPollTimer);dashboardPollTimer=null}
function startDashboardPolling(){
 stopDashboardPolling();const owner=dashboardPollOwner;
 const poll=async()=>{
  if(!authenticated||owner!==dashboardPollOwner)return;
  await Promise.allSettled([loadNodes(),loadProjects(),loadHomeActivity(),navigation.current().view==='executions'?loadExecutions():Promise.resolve(),['workers','settings'].includes(navigation.current().view)?loadProvisioning():Promise.resolve()]);
  const route=navigation.current();
  if(authenticated&&owner===dashboardPollOwner&&route.view==='workers'&&route.id)await Promise.allSettled([loadWorker(route.id),loadWorkerAdministration(route.id)]);
  if(authenticated&&owner===dashboardPollOwner)dashboardPollTimer=setTimeout(poll,5000);
 };
 dashboardPollTimer=setTimeout(poll,5000);
}
async function loadHomeActivity(){
 const context=navigation.capture();if(!authenticated)return;
 try{const items=await api('/api/v1/executions?limit=50&offset=0');if(navigation.isCurrent(context))navigation.observe('executions',items)}
 catch(e){if(navigation.isCurrent(context)&&e.name!=='AbortError'&&e.message!=='Administration session ended.')navigation.observe('executions',null)}
}
