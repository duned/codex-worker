// Administration requests share a generation so logout cannot publish late responses.
let csrfToken='',sessionGeneration=0,sessionTimer=null,pendingLogoutCsrf='';
const administrationRequests=new Set();
function signOut(message='Sign in to administer this Server.'){
 authenticated=false;csrfToken='';sessionGeneration++;clearTimeout(sessionTimer);stopDashboardPolling();stopWorkerStream();
 for(const controller of administrationRequests)controller.abort();administrationRequests.clear();
 $('administration-login').hidden=false;$('administration-content').hidden=true;$('logout').hidden=true;
 $('session-expiration').textContent='';$('session-message').textContent=message;$('live').textContent='Live · signed out';
 for(const dialog of document.querySelectorAll('dialog')){if(dialog.open)dialog.close()}
 $('credential-secret').value='';resetResources();navigation.stop();
}
async function api(path,options={},sessionRequest=false){
 if(!authenticated&&!sessionRequest)throw Error('Sign in to administer this Server.');
 const generation=sessionGeneration,controller=new AbortController();administrationRequests.add(controller);
 const headers=new Headers(options.headers||{});if(options.body)headers.set('Content-Type','application/json');
 if(!sessionRequest&&!['GET','HEAD'].includes(options.method||'GET'))headers.set('X-Codex-CSRF',csrfToken);
 try{
  const response=await fetch(path,{...options,headers,credentials:'same-origin',cache:'no-store',signal:controller.signal});
  if(generation!==sessionGeneration)throw Error('Administration session ended.');
  if(response.status===401||response.status===403){signOut('Your session expired or was rejected. Sign in again.');throw Error('Administration sign in required.');}
  if(!response.ok){let detail='';try{detail=(await response.json()).error||''}catch{}throw Error(detail||`Server API returned ${response.status}`)}
  const result=response.status===204?null:await response.json();
  if(generation!==sessionGeneration)throw Error('Administration session ended.');return result;
 }finally{administrationRequests.delete(controller)}
}
function startSession(session){
 csrfToken=session.csrfToken;authenticated=true;pendingLogoutCsrf='';
 $('administration-login').hidden=true;$('administration-content').hidden=false;$('logout').hidden=false;
 $('session-expiration').textContent='Session expires '+new Date(session.expiresAtUtc).toLocaleString();
 clearTimeout(sessionTimer);sessionTimer=setTimeout(()=>signOut('Your session expired. Sign in again.'),Math.max(0,Date.parse(session.expiresAtUtc)-Date.now()));
 navigation.start();startDashboardPolling();loadProjects();loadOverview();loadExecutions();loadNodes();loadProvisioning();loadCredentials();streamWorkers();
}
async function restoreSession(){signOut('Checking administration session…');try{startSession(await api('/api/v1/administration/session',{},true))}catch(e){if(authenticated||e.message==='Administration session ended.')return;if(e.name!=='AbortError'&&e.message!=='Administration sign in required.')$('session-message').textContent='Server connection unavailable. Choose Check session to try again.'}}
async function signIn(){
 const token=$('token').value;$('token').value='';signOut('Signing in…');$('unlock').disabled=true;
 try{startSession(await api('/api/v1/administration/session',{method:'POST',headers:{Authorization:'Bearer '+token}},true))}
 catch(e){if(e.name!=='AbortError'&&e.message!=='Administration session ended.')$('session-message').textContent=e.message==='Administration sign in required.'?'Sign in was rejected. Check the management token and configured administration origin.':'Server connection unavailable. Try signing in again.'}
 finally{$('unlock').disabled=false}
}
async function logout(){
 pendingLogoutCsrf=csrfToken||pendingLogoutCsrf;signOut();
 try{const response=await fetch('/api/v1/administration/session',{method:'DELETE',headers:{'X-Codex-CSRF':pendingLogoutCsrf},credentials:'same-origin',cache:'no-store',signal:AbortSignal.timeout(10000)});if(!response.ok&&response.status!==401)throw Error();pendingLogoutCsrf=''}
 catch{$('session-message').textContent='Sign out could not be confirmed by the Server. Retry sign out.';$('logout').hidden=false}
}

// A single bounded polling owner per session. Rendering never creates timers.
let dashboardPollTimer=null,dashboardPollOwner=0;
function stopDashboardPolling(){dashboardPollOwner++;clearTimeout(dashboardPollTimer);dashboardPollTimer=null}
function startDashboardPolling(){
 stopDashboardPolling();const owner=dashboardPollOwner;
 const poll=async()=>{
  if(!authenticated||owner!==dashboardPollOwner)return;
  await Promise.allSettled([loadNodes(),loadProjects(),loadHomeActivity(),navigation.current().view==='executions'?loadExecutions():Promise.resolve(),['workers','settings'].includes(navigation.current().view)?loadProvisioning():Promise.resolve()]);
  if(authenticated&&owner===dashboardPollOwner)dashboardPollTimer=setTimeout(poll,5000);
 };
 dashboardPollTimer=setTimeout(poll,5000);
}
async function loadHomeActivity(){
 if(!authenticated)return;
 try{navigation.observe('executions',await api('/api/v1/executions?limit=50&offset=0'))}
 catch(e){if(e.name!=='AbortError'&&e.message!=='Administration session ended.')navigation.observe('executions',null)}
}
