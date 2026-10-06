// Only wire actions registered by the API are offered. Labels do not decide availability.
const nodeActions={configure:{command:'Configure',label:'Configure daemon access',elevation:true},login:{command:'Login',label:'Sign in with device code'},refresh:{command:'Detect',label:'Refresh / Re-detect'},install:{command:'Install',label:'Install',elevation:true},update:{command:'Update',label:'Update',elevation:true},uninstall:{command:'Uninstall',label:'Uninstall',elevation:true,destructive:true},checkauthentication:{command:'CheckAuthentication',label:'Check authentication'},logout:{command:'Logout',label:'Logout / Remove authentication',destructive:true},checkconfiguration:{command:'CheckConfiguration',label:'Check configuration'},prepareauthentication:{command:'PrepareAuthentication',label:'Prepare GitHub login'},generatesshkey:{command:'GenerateSshKey',label:'Generate SSH key'},inspectsshkey:{command:'InspectSshKey',label:'Inspect public key'},removesshkey:{command:'RemoveSshKey',label:'Remove SSH key',destructive:true},verifyrepositoryaccess:{command:'VerifyRepositoryAccess',label:'Verify repository access'}};
let nodes=[],nodeCommands=[],nodesLoading=false,nodeSubmitting=false,nodeSnapshotValid=false;
let serverGitHub=null,githubChallengeTimer=null,nodeLoadGeneration=0;
function selectedNode(){return nodes.find(n=>n.id===$('node-select').value)}
function commandActive(c){return c.status==='Pending'||c.status==='Running'}
function renderNode(){navigation.preservePresentation($('node-detail'),renderNodeContent)}
function renderNodeContent(){
 renderServerGitHub();
 const node=selectedNode();if(!node){$('node-detail').innerHTML='<div class="empty">No node selected.</div>';return}
 const busy=nodeSubmitting||!nodeSnapshotValid||node.provisioningReadiness==='busy'||nodeCommands.some(c=>c.request.nodeId===node.id&&commandActive(c));
 $('node-detail').innerHTML=`<h3>${esc(node.displayName)} · ${esc(node.kind)}</h3><dl><dt>Connectivity</dt><dd>${esc(node.connectivity)}</dd><dt>Service health</dt><dd>${esc(node.health)}</dd><dt>Execution readiness</dt><dd>${esc(node.executionReadiness)}</dd><dt>Provisioning readiness</dt><dd>${esc(node.provisioningReadiness)}</dd><dt>Observations</dt><dd>${node.observationsStale?'Stale — re-detect before relying on these facts':'Current'}</dd></dl>${busy?'<p class="sub">Actions are disabled while an operation is pending/running or current state is unavailable.</p>':''}`;
 for(const capability of node.capabilities){
  const state=capability.state,actions=capability.availableActions.filter(a=>nodeActions[a]);
  const capabilityCommands=nodeCommands.filter(c=>c.request.nodeId===node.id&&c.request.capabilityId===capability.definition.id);
  const latest=capabilityCommands.find(commandActive)||capabilityCommands.at(-1);
  const section=document.createElement('section');
  section.innerHTML=`<h3>${capability.definition.id==='codex-cli'?'Codex authentication / tools':capability.definition.id==='github-cli'?'Worker / node GitHub authentication':capability.definition.id==='git'?'Repository access / Git':esc(capability.definition.displayName)}</h3><dl><dt>Installation</dt><dd>${esc(state.installation)} · version ${esc(state.detectedVersion||'unknown')}</dd><dt>Update</dt><dd>${esc(state.update)}</dd><dt>Health</dt><dd>${esc(state.health)}</dd>${state.authentication!=null?`<dt>Authentication</dt><dd>${esc(state.authentication)}</dd>`:''}${state.configuration!=null?`<dt>Configuration</dt><dd>${esc(state.configuration)}</dd>`:''}<dt>Detected</dt><dd>${esc(state.detectedAtUtc?new Date(state.detectedAtUtc).toLocaleString():'Not detected')}</dd><dt>Operation</dt><dd>${esc(state.operation.state)} ${esc(state.operation.action)} ${esc(state.operation.diagnosticCode)}</dd>${state.diagnosticCode?`<dt>Diagnostic</dt><dd>${esc(state.diagnosticCode)}</dd>`:''}</dl>${latest?`<details><summary>Advanced · last provisioning command</summary><p class="operation">Last command: ${esc(latest.request.action)} · ${esc(latest.status)} · ${esc(latest.diagnostic)}${latest.failureDetail?' · '+esc(latest.failureDetail.description):''}<br>ID ${esc(latest.id)} · queued ${esc(new Date(latest.createdAtUtc).toLocaleString())}${latest.startedAtUtc?' · started '+esc(new Date(latest.startedAtUtc).toLocaleString()):''}${latest.deadlineUtc?' · deadline '+esc(new Date(latest.deadlineUtc).toLocaleString()):''}${latest.completedAtUtc?' · completed '+esc(new Date(latest.completedAtUtc).toLocaleString()):''}</p></details>`:''}`;
  if(latest){
   const progress=document.createElement('p');progress.className='operation';
   const expired=latest.status==='Running'&&Date.parse(latest.deadlineUtc)<=Date.now();
   progress.textContent=`${latest.request.action}: ${latest.status}. ${expired?'Deadline expired; verify the node is quiescent and reconcile before retrying.':commandActive(latest)?'You may leave and return; this operation is retained.':''} ${latest.failureDetail?.description||latest.diagnostic}`;section.append(progress);
   if(latest.status==='Pending'||expired){const control=document.createElement('button');control.id='node-command-'+latest.id;control.className='button';control.textContent=expired?'Reconcile after node quiescence':'Cancel queued operation';control.disabled=nodeSubmitting||!nodeSnapshotValid;control.onclick=()=>runProvisioningCommandAction(latest.id,expired?'reconcile':'cancel');section.append(control)}
  }
  for(const command of nodeCommands.filter(c=>c.request.nodeId===node.id&&c.request.capabilityId===capability.definition.id&&c.publicIdentity).slice(-30)){const identity=document.createElement('div');identity.innerHTML=`<p class="sub">Public SSH identity</p><pre>${esc(command.publicIdentity.publicKey)}\n${esc(command.publicIdentity.fingerprint)}</pre>`;section.append(identity)}
  if(nodeSnapshotValid&&latest?.loginInstructions&&latest.status==='Running'&&new Date(latest.deadlineUtc)>new Date()){
   const guide=document.createElement('p');guide.className='operation';
   const isGitHub=latest.loginInstructions.verificationUri==='https://github.com/login/device';const loginUrl=isGitHub?'https://github.com/login/device':'https://auth.openai.com/codex/device';
   guide.innerHTML=`Sign in at <a href="${loginUrl}" target="_blank" rel="noopener noreferrer">${isGitHub?'GitHub':'OpenAI'} device login</a> and enter <strong>${esc(latest.loginInstructions.userCode)}</strong>. Only approve the login you started for this node. ${isGitHub?'Authentication is stored in this node’s product-managed GitHub CLI configuration.':'Enable device code login in ChatGPT security settings or workspace permissions if required.'} Waiting for verification; expires by ${esc(new Date(latest.deadlineUtc).toLocaleString())}.`;section.append(guide)
  }
  if(capability.definition.requiresAuthentication&&!capability.definition.supportedActions?.includes('prepareauthentication')&&!actions.includes('prepareauthentication')&&!actions.some(a=>a==='login'||a==='authenticate')){const note=document.createElement('p');note.className='sub';note.textContent='Remote login is not supported by this node API. Complete authentication in the node service account environment, then re-detect. No credentials are collected or displayed here.';section.append(note)}
  const policy=document.createElement('p');policy.className='sub';policy.textContent=latest?.diagnostic==='Denied'?'The node rejected this action under local authorization policy. Ask its administrator to permit the specific typed action and service-account access, or complete that preparation locally, then refresh before retrying.':'These are registered supported actions. The node checks local authorization when executing; Server authorization cannot override that policy.';section.append(policy);
  const buttons=document.createElement('div');buttons.className='capability-actions';
  function addAction(action,label){const button=document.createElement('button');button.id='node-action-'+encodeURIComponent(node.id)+'-'+encodeURIComponent(capability.definition.id)+'-'+encodeURIComponent(label);button.className='button'+(nodeActions[action].destructive?' danger':'');button.textContent=label;button.disabled=busy;button.onclick=()=>runNodeAction(node.id,capability.definition.id,action);buttons.append(button)}
  if(!actions.length){const note=document.createElement('p');note.className='sub';note.textContent='No remote actions are currently available. Ask the node administrator to install/configure this supported tool in the Worker service account, or permit its typed action in node-local provisioning policy, then refresh observations. Project runtimes outside the supported catalog must be prepared on the node.';section.append(note)}
  for(const action of actions)addAction(action,nodeActions[action].label);
  const retry=latest&&Object.keys(nodeActions).find(a=>nodeActions[a].command===latest.request.action);
  if(latest&&['Failed','TimedOut','Cancelled'].includes(latest.status)&&actions.includes(retry))addAction(retry,'Retry '+nodeActions[retry].label);
  section.append(buttons);$('node-detail').append(section);
 }
}
async function loadNodes(){
 if(!authenticated||nodeSubmitting)return;const generation=++nodeLoadGeneration,context=navigation.capture();nodesLoading=true;nodeSnapshotValid=false;renderNode();
 try{const [inventory,commands,connection]=await Promise.all([api('/api/v1/nodes'),api(navigation.current().view==='workers'&&navigation.current().id?'/api/v1/nodes/'+encodeURIComponent(navigation.current().id)+'/commands':'/api/v1/provisioning/commands'),api('/api/v1/nodes/server/github-connection')]);if(generation!==nodeLoadGeneration||!navigation.isCurrent(context))return;serverGitHub=connection;const selected=$('node-select').value;nodes=inventory;nodeCommands=[...commands.filter(c=>!connection.commands.some(g=>g.id===c.id)),...connection.commands].sort((a,b)=>Date.parse(a.createdAtUtc)-Date.parse(b.createdAtUtc));nodeSnapshotValid=true;window.codexWorkerPoc?.update?.({nodes,nodeCommands});navigation.observe('nodes',nodes);$('node-select').innerHTML=nodes.map(n=>`<option value="${esc(n.id)}">${esc(n.displayName)} · ${esc(n.kind)} · ${esc(n.connectivity)}</option>`).join('');$('node-select').disabled=false;$('node-select').value=selected;selectContextNode()}
 catch(e){if(generation!==nodeLoadGeneration||!navigation.isCurrent(context)||e.name==='AbortError'||e.message==='Administration session ended.')return;navigation.observe('nodes',null);window.codexWorkerPoc?.update?.({nodes:null,nodeCommands:null});nodeSnapshotValid=false;renderNode();$('node-message').textContent='Cannot refresh provisioning state: '+e.message}
 finally{if(generation===nodeLoadGeneration)nodesLoading=false}
}
async function runNodeAction(nodeId,capabilityId,action){
 const context=navigation.capture();
 const node=selectedNode(),spec=nodeActions[action],capability=node?.capabilities.find(c=>c.definition.id===capabilityId);
 if(nodeSubmitting||!nodeSnapshotValid||nodesLoading||!spec||node?.id!==nodeId||!capability?.availableActions.includes(action)||node.provisioningReadiness==='busy'||nodeCommands.some(c=>c.request.nodeId===nodeId&&commandActive(c)))return;
 if(spec.elevation&&!$('node-elevation').checked){$('node-message').textContent='Authorize elevation before launching this tool action. Node-local policy must also permit it.';return}
 let repository=null;if(action==='verifyrepositoryaccess'){repository=projects.find(p=>p.id===navigation.current().params.get('project'))?.repository||prompt('GitHub repository to verify (owner/repository):');if(!repository)return;}
 if(spec.destructive&&!confirm(action==='removesshkey'?'Remove the product SSH keypair? GitHub registrations must be removed separately.':action==='logout'&&capabilityId==='github-cli'?'Remove product-managed GitHub authentication on this node?':`${spec.label} ${capability.definition.displayName} on ${node.displayName}? This may prevent task execution.`))return;
 nodeSubmitting=true;renderNode();$('node-message').textContent='Submitting '+spec.label+'…';
 try{const command=await api('/api/v1/provisioning/commands',{method:'POST',body:JSON.stringify({nodeId,capabilityId,action:spec.command,timeoutSeconds:action==='login'?600:120,allowElevation:!!spec.elevation&&$('node-elevation').checked,...(repository?{repository}:{})})});nodeCommands.push(command);if(nodeId==='server'&&capabilityId==='github-cli'&&serverGitHub)serverGitHub.commands.unshift(command);if(!navigation.isCurrent(context))return;$('node-message').textContent='Operation queued. Progress is refreshed automatically; command details are in advanced history.'}
 catch(e){nodeSnapshotValid=false;if(!navigation.isCurrent(context))return;$('node-message').textContent='Action submission could not be confirmed: '+e.message+'. Refresh state before trying again.'}
 finally{nodeSubmitting=false;renderNode();await loadNodes()}
}
$('node-select').onchange=()=>{const route=navigation.current();navigation.navigate(route.view,route.view==='workers'?$('node-select').value:'',{node:$('node-select').value})};$('refresh-nodes').onclick=loadNodes;
// The connection view reads durable operations; rendering never starts authentication.
// Home and guided connection use the same current capability and retained-operation evidence.
function serverGitHubConnected(node,commands){
 const capability=node?.capabilities.find(c=>c.definition.id==='github-cli');
 if(!capability||!commands||node.observationsStale)return false;
 const latest=commands.find(commandActive)||commands[0],operation=capability.state.operation;
 const failed=latest&&['Login','CheckAuthentication'].includes(latest.request.action)&&['Failed','TimedOut'].includes(latest.status);
 const observationFailed=operation&&['login','checkauthentication'].includes(operation.action)&&['Failed','TimedOut'].includes(operation.state);
 return !commands.some(commandActive)&&!failed&&operation?.state!=='Running'&&!observationFailed&&capability.state.installation==='Installed'&&capability.state.health==='Healthy'&&capability.state.authentication==='Satisfied';
}
function serverGitHubState(){
 const node=nodes.find(n=>n.id==='server'&&n.kind==='server'),capability=node?.capabilities.find(c=>c.definition.id==='github-cli');
 const commands=serverGitHub?.commands||[];
 const latest=commands.find(commandActive)||commands[0];
 const busy=nodeSubmitting||!nodeSnapshotValid||!serverGitHub||node?.provisioningReadiness==='busy'||nodeCommands.some(c=>c.request.nodeId==='server'&&commandActive(c))||commands.some(commandActive);
 return {node,capability,latest,busy};
}
function renderServerGitHub(){navigation.preservePresentation($('github-connection'),renderServerGitHubContent)}
function renderServerGitHubContent(){
 clearTimeout(githubChallengeTimer);githubChallengeTimer=null;
 const panel=$('github-connection');panel.innerHTML='';
 const {node,capability,latest,busy}=serverGitHubState();
 function paragraph(text){const p=document.createElement('p');p.textContent=text;panel.append(p)}
 function button(label,action,disabled=busy){const b=document.createElement('button');b.id='server-github-'+encodeURIComponent(label);b.className='button';b.textContent=label;b.disabled=disabled;b.onclick=action;panel.append(b)}
 if(!nodeSnapshotValid||!serverGitHub||!capability){paragraph('Connection state unavailable. Refresh connection before starting or retrying. An unconfirmed submission may still be running.');return}
 const active=latest&&commandActive(latest);
 const connected=serverGitHubConnected(node,serverGitHub.commands);
 paragraph(connected?'Connected · GitHub authentication checked in the Server service account.':'Connect GitHub for the Server service account.');
 paragraph('Repository read access: not yet verified for a selected project. Issue write authorization: not yet verified. Worker authentication: checked separately on each Worker. Repository push access: checked separately by the Worker. Server login does not grant these permissions.');
 if(active){
  if(latest.status==='Pending'){paragraph('Waiting to start. You can cancel this queued operation.');button('Cancel queued operation',()=>cancelServerGitHub(latest.id),nodeSubmitting)}
  else if(Date.parse(latest.deadlineUtc)<=Date.now())paragraph('Device login deadline expired. The code is no longer available. Wait for the Server to confirm the operation has stopped before retrying; if it remains interrupted, ask the Server administrator to reconcile it after verifying it is quiescent.');
  else {
   paragraph(latest.request.action==='Login'?'Device login in progress. Waiting for GitHub verification.':'Preparing the Server connection…');
   const instructions=latest.loginInstructions;
   if(latest.request.action==='Login'&&instructions?.verificationUri==='https://github.com/login/device'){
    githubChallengeTimer=setTimeout(()=>renderNode(),Math.max(1,Date.parse(latest.deadlineUtc)-Date.now()));
    const challenge=document.createElement('p');challenge.innerHTML=`Open <a href="https://github.com/login/device" target="_blank" rel="noopener noreferrer">GitHub verification</a> and enter <strong>${esc(instructions.userCode)}</strong>. Only approve the Server login you started. Expires by ${esc(new Date(latest.deadlineUtc).toLocaleString())}.`;panel.append(challenge);
   }
   paragraph('Running operations cannot be cancelled here; they stop at their deadline. You can leave this page and return to recover progress.');
  }
  return;
 }
 if(latest&&['Failed','TimedOut','Cancelled'].includes(latest.status))paragraph(`Previous operation ${latest.status.toLowerCase()}: ${latest.failureDetail?.description||latest.diagnostic}. Refresh state before a safe retry. Preparation refuses unrelated operator authentication; ask the Server administrator to check the dedicated service account and local policy. Existing operator authentication is preserved.`);
 const actions=capability.availableActions;
 function offer(action,label){if(actions.includes(action))button(label,()=>runServerGitHub(action))}
 offer('checkauthentication','Check Server authentication');
 if(connected)return;
 if(!serverGitHub.provisioningEnabled){paragraph('Provisioning is disabled on this Server. Ask the Server administrator to enable local provisioning in the dedicated service account, then refresh. Authentication checks remain available.');return}
 if(capability.state.installation!=='Installed'){
  paragraph('GitHub CLI must be installed before authentication. Installation requires your elevation choice and permission in Server-local policy.');
  if(serverGitHub.elevationAllowed)offer('install','Install GitHub CLI');else paragraph('Installation elevation is unavailable. Ask the Server administrator to install the supported GitHub CLI, then refresh.');return;
 }
 const prepared=latest?.status==='Succeeded'&&latest.request.action==='PrepareAuthentication'||latest?.request.action==='Login'&&latest.diagnostic!=='Denied';
 if(!prepared){paragraph('Prepare a private, product-managed GitHub CLI configuration. Explicit authorization is required; unrelated operator authentication will be refused.');offer('prepareauthentication',latest?.status==='Failed'?'Retry preparation':'Connect · prepare authentication')}
 else {paragraph('Authentication prepared. Start a device login, then approve the temporary code at GitHub. Queuing the operation does not mean you are connected.');offer('login',latest?.request.action==='Login'?'Retry device login':'Start device login')}
 if(!actions.includes('login')||!actions.includes('prepareauthentication'))paragraph('The supported device flow is unavailable. Refresh capabilities; if still unavailable, ask the Server administrator to inspect supported provisioning and service-account authentication.');
}
async function runServerGitHub(action){
 const {node,capability,busy}=serverGitHubState(),spec=nodeActions[action];
 if(busy||nodesLoading||!spec||!capability?.availableActions.includes(action))return;
 if(['prepareauthentication','login','install'].includes(action)&&!serverGitHub.provisioningEnabled)return;
 if(['prepareauthentication','login'].includes(action)&&!$('github-provisioning-consent').checked){$('github-connection-message').textContent='Authorize preparation and device login before continuing.';return}
 if(spec.elevation&&(!$('github-elevation-consent').checked||!serverGitHub.elevationAllowed)){$('github-connection-message').textContent='Installation requires explicit elevation authorization and Server-local permission.';return}
 nodeSubmitting=true;renderNode();$('github-connection-message').textContent='Submitting operation…';
 try{const command=await api('/api/v1/provisioning/commands',{method:'POST',body:JSON.stringify({nodeId:node.id,capabilityId:'github-cli',action:spec.command,timeoutSeconds:action==='login'?600:120,allowElevation:!!spec.elevation})});serverGitHub.commands.unshift(command);nodeCommands.push(command);$('github-connection-message').textContent='Operation queued. Waiting for completion; progress refreshes automatically.'}
 catch(e){nodeSnapshotValid=false;$('github-connection-message').textContent='Submission could not be confirmed. Refresh connection to recover the actual operation before trying again.'}
 finally{nodeSubmitting=false;renderNode();await loadNodes()}
}
async function cancelServerGitHub(id){
 const {latest}=serverGitHubState();if(nodeSubmitting||!nodeSnapshotValid||latest?.id!==id||latest.status!=='Pending')return;
 nodeSubmitting=true;renderNode();
 try{await api('/api/v1/provisioning/commands/'+encodeURIComponent(id)+'/cancel',{method:'POST'});$('github-connection-message').textContent='Queued operation cancelled.'}
 catch(e){nodeSnapshotValid=false;$('github-connection-message').textContent='Cancellation could not be confirmed. Refresh connection; a dispatched operation must finish or reach its deadline.'}
 finally{nodeSubmitting=false;await loadNodes();renderNode()}
}
$('refresh-github-connection').onclick=loadNodes;
