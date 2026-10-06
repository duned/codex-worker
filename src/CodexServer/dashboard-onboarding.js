// Only the public request survives dialog close. Authorization stays in volatile DOM state.
let onboardingGeneration=0,onboardingBusy=false,onboardingExpiry=null;
function clearOnboardingAuthorization(){
 onboardingGeneration++;clearTimeout(onboardingExpiry);
 $('onboarding-authorization').value='';$('onboarding-authorization').type='password';
}
async function openOnboarding(){
 clearOnboardingAuthorization();$('onboarding-dialog').showModal();
 $('onboarding-destination').textContent='Destination Server: '+window.location.origin;
 $('onboarding-message').textContent='';$('onboarding-prepare').hidden=true;
 await updateOnboardingInstruction();
}
async function updateOnboardingInstruction(){
 clearOnboardingAuthorization();const generation=onboardingGeneration;
 $('onboarding-request').value='';$('onboarding-prepare').hidden=true;
 const operation=$('onboarding-kind').value,server=window.location.origin;
 $('onboarding-local-note').textContent=operation==='enroll'?
  'Supported installer: Ubuntu 24.04 x86_64. Uses the matching published release, verifies its checksum, and pairs as the service account. A source build needs a locally built matching Worker; do not substitute an older release without pairing support.':
  'Use the existing installed Worker. Drain and reconcile active leases with the previous Server, then stop the service before association. Identity, configuration, history and uncertain resources are retained; previous Server credentials are not revoked. A denied local policy requires the node administrator.';
 try{
  if(server.startsWith('https://')===false)throw Error('Guided onboarding requires the final HTTPS Server origin and a trusted certificate.');
  const version=(await api('/api/version')).version;
  if(!/^[0-9]+\.[0-9]+\.[0-9]+(?:[-.][A-Za-z0-9.-]+)?$/.test(version))throw Error('No supported pinned release version is available.');
  if(generation!==onboardingGeneration)return;
  $('onboarding-instruction').textContent=operation==='enroll'?
   `curl -fsSL https://raw.githubusercontent.com/duned/codex-worker/v${version}/packaging/linux/install-worker.sh | sudo bash -s -- --version ${version} --server ${server} --pair --start`:
   `sudo -u codex-worker env HOME=/var/lib/codex-worker codex-worker register --config /etc/codex-worker/worker.yml --server ${server} --operation associate --pair\n# After acknowledged registration:\nsudo systemctl start codex-worker`;
 }catch(e){if(generation===onboardingGeneration){$('onboarding-instruction').textContent='';$('onboarding-message').textContent=e.message}}
}
function readOnboardingRequest(){
 let request;try{request=JSON.parse($('onboarding-request').value)}catch{throw Error('Paste the complete public request printed by the node.');}
 if(!request||typeof request!=='object'||Array.isArray(request)||request.contractVersion!==1||!/^[0-9a-f]{32}$/i.test(request.workerId)||request.operation!==$('onboarding-kind').value||request.server!==window.location.origin||Object.keys(request).sort().join(',')!=='contractVersion,operation,server,workerId')
  throw Error('Request does not match this Server and selected operation. Return to the node; do not edit its request.');
 return request;
}
async function authorizeOnboarding(){
 if(onboardingBusy)return;
 clearOnboardingAuthorization();const generation=onboardingGeneration;onboardingBusy=true;
 try{
  const request=readOnboardingRequest();
  $('onboarding-message').textContent='Authorizing '+request.operation+' at '+request.server+'…';
  const result=await api('/api/v1/workers/onboarding/authorize',{method:'POST',body:JSON.stringify(request)});
  if(generation!==onboardingGeneration)return;
  $('onboarding-authorization').value=result.authorization;
  onboardingExpiry=setTimeout(()=>clearOnboardingAuthorization(),result.lifetimeSeconds*1000);
  $('onboarding-message').textContent='Authorization issued. Paste it into the hidden node prompt. Wait for the node acknowledgement, start the service, then Check progress. If this response was lost, resume on the node with empty input first; request a fresh authorization only if the retained credential is rejected.';
 }catch(e){if(generation===onboardingGeneration)$('onboarding-message').textContent=e.message+' Retain local pending state; check progress before requesting another authorization.'}
 finally{onboardingBusy=false}
}
async function checkOnboarding(){
 clearOnboardingAuthorization();const generation=onboardingGeneration;$('onboarding-prepare').hidden=true;
 try{
  const request=readOnboardingRequest();
  const list=await api('/api/v1/workers');if(generation!==onboardingGeneration)return;
  const worker=list.find(w=>w.workerId===request.workerId);
  if(!worker){$('onboarding-message').textContent='Not registered here yet. Resume the same node operation. Invalid, expired or used authorization needs a fresh authorization only after retained credential reconciliation fails.';return;}
  // An authenticated heartbeat is separate from bootstrap and credential acknowledgement.
  const connected=worker.authenticationCredentialStatus==='active'&&worker.lastHeartbeatAtUtc&&worker.availability!=='stale'&&worker.availability!=='offline';
  $('onboarding-message').textContent=connected?
   'Registration acknowledged and communication observed. Execution readiness is not established. Scheduling remains a separate administration choice. Continue with contextual preparation.':
   'Registration recorded; waiting for observed communication. Resume on the node with empty input to verify acknowledgement, then start the service. Inspect local status if startup is denied or unavailable.';
  if(connected){$('onboarding-prepare').hidden=false;$('onboarding-prepare').href='#/workers/'+encodeURIComponent(worker.workerId)+'?prepare=1';}
 }catch(e){if(generation===onboardingGeneration)$('onboarding-message').textContent=e.message+' Preserve node state and check the Server connection before retrying.'}
}
$('add-worker').onclick=openOnboarding;$('onboarding-kind').onchange=updateOnboardingInstruction;
$('onboarding-authorize').onclick=authorizeOnboarding;$('onboarding-check').onclick=checkOnboarding;
$('onboarding-reveal').onclick=()=>{$('onboarding-authorization').type=$('onboarding-authorization').type==='password'?'text':'password'};
$('onboarding-close').onclick=()=>$('onboarding-dialog').close();
$('onboarding-dialog').onclose=clearOnboardingAuthorization;
