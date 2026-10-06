let repositoryChoices=[],repositoryNextPage=null,projectDraft=null,projectReviewed=null,projectUncertain=null,projectBusy=false,projectDialogGeneration=0,projectConflict=null;
function projectError(message){$('form-error').textContent=message;$('form-error').hidden=false}
function invalidateProjectReview(){projectReviewed=null;$('project-review').hidden=true;$('save-project').disabled=!projectUncertain}
function openProject(p){
 if(projectUncertain){projectError('Resolve the unconfirmed save before starting another definition.');$('project-dialog').showModal();return}
 projectConflict=null;$('reload-project').hidden=true;projectDialogGeneration++;projectDraft=p||null;projectReviewed=null;repositoryChoices=[];repositoryNextPage=null;
 $('project-form').reset();$('project-id').value=p?.id||'';$('revision').value=p?.revision||'';
 for(const [id,value] of Object.entries({name:p?.name,repository:p?.repository,branch:p?.defaultBranch,description:p?.description,'issue-ready-label':p?.issueReadyLabel,'issue-blocked-label':p?.issueBlockedLabel}))$(id).value=value||'';
 $('dialog-title').textContent=p?'Edit project':'Create project';$('project-repository-selection').hidden=!!p;$('manual-repository').open=!!p;
 $('repository-selection').innerHTML='<option value="">Choose a repository</option>';$('more-repositories').hidden=true;$('repository-discovery-message').textContent='';
 $('requirements').innerHTML='';for(const requirement of p?.requirements||[])addProjectRequirement(requirement);
 $('form-error').hidden=true;$('save-project').textContent='Save reviewed project';invalidateProjectReview();$('project-dialog').showModal();
}
function editProject(id){const p=projects.find(p=>p.id===id);if(p)openProject(p)}
function newProject(){openProject(null)}
function addProjectRequirement(requirement={type:'runtime',name:'',version:null,scope:null}){
 const row=document.createElement('div');row.className='project-requirement';
 const types=[...new Set(['runtime','tool','service','authentication','agent-provider',requirement.type])];
 row.innerHTML=`<select class="field" data-field="type" aria-label="Requirement type">${types.map(t=>`<option ${t===requirement.type?'selected':''} value="${esc(t)}">${esc(t)}</option>`).join('')}</select><input class="field" data-field="name" aria-label="Requirement name" maxlength="100" required value="${esc(requirement.name)}"><input class="field" data-field="version" aria-label="Required version" placeholder="Version (optional, e.g. >=10.0)" value="${esc(requirement.version||'')}"><input class="field" data-field="scope" aria-label="Authentication repository scope" placeholder="Authentication scope (optional)" value="${esc(requirement.scope||'')}"><button class="button" type="button">Remove requirement</button>`;
 row.querySelector('button').onclick=()=>{row.remove();invalidateProjectReview()};$('requirements').appendChild(row);invalidateProjectReview();
}
function projectDefinition(){
 return {name:$('name').value.trim(),repository:$('repository').value.trim(),defaultBranch:$('branch').value.trim(),description:$('description').value.trim(),
 requirements:[...$('requirements').querySelectorAll('.project-requirement')].map(row=>Object.fromEntries(['type','name','version','scope'].map(field=>[field,row.querySelector(`[data-field="${field}"]`).value.trim()||null]))),
 issueReadyLabel:$('issue-ready-label').value.trim()||null,issueBlockedLabel:$('issue-blocked-label').value.trim()||null,automaticDiscovery:projectDraft?.automaticDiscovery||null};
}
async function discoverProjectRepositories(page=1){
 const generation=projectDialogGeneration;$('repository-discovery-message').textContent='Reading repositories using Server GitHub authentication…';
 try{const result=await api('/api/v1/github/repositories?page='+page);if(generation!==projectDialogGeneration||!$('project-dialog').open)return;
 repositoryChoices=page===1?result.repositories:[...repositoryChoices,...result.repositories];repositoryNextPage=result.nextPage;
 $('repository-selection').innerHTML='<option value="">Choose a repository</option>'+repositoryChoices.map(r=>`<option value="${esc(r.repository)}">${esc(r.repository)}</option>`).join('');
 $('more-repositories').hidden=!repositoryNextPage;$('repository-discovery-message').textContent=repositoryChoices.length+' accessible repositories loaded.';
 }catch(e){if(generation!==projectDialogGeneration)return;$('repository-discovery-message').textContent='Discovery unavailable: '+e.message+' Connect or check Server GitHub, then retry; manual repository input is available.'}
}
async function reviewProject(){
 if(projectBusy||projectUncertain)return;if(!$('project-form').reportValidity())return;
 const generation=projectDialogGeneration,definition=projectDefinition();projectBusy=true;invalidateProjectReview();$('form-error').hidden=true;
 try{const result=await api('/api/v1/projects/verify',{method:'POST',body:JSON.stringify(definition)});
 if(generation!==projectDialogGeneration||!$('project-dialog').open||JSON.stringify(definition)!==JSON.stringify(projectDefinition()))return;
 if(!result.repositoryReadable||!result.branchExists)throw Error(result.diagnostic||'Repository and branch are not verified.');
 projectReviewed=definition;$('project-review-definition').textContent=[
  'Name: '+definition.name,'Repository: '+definition.repository,'Base branch: '+definition.defaultBranch,
  'Description: '+(definition.description||'None'),
  'Worker requirements: '+(definition.requirements.length?definition.requirements.map(r=>r.type+' · '+r.name+(r.version?' '+r.version:'')+(r.scope?' for '+r.scope:'')).join('; '):'None added'),
  'Issue ready label: '+(definition.issueReadyLabel||'None'),'Issue blocked label: '+(definition.issueBlockedLabel||'None'),
  ...(definition.automaticDiscovery?['Existing discovery policy: every '+definition.automaticDiscovery.intervalSeconds+' seconds; page size '+definition.automaticDiscovery.pageSize+'; deadline '+definition.automaticDiscovery.deadlineSeconds+' seconds.']:[])
 ].join('\n');$('project-review-access').textContent=result.diagnostic+' Lifecycle policy: '+(projectDraft?.enabled===false?'disabled':'enabled')+'. Automatic discovery: '+(definition.automaticDiscovery?.enabled?'enabled under the existing policy':'disabled')+'.';$('project-review').hidden=false;$('save-project').disabled=false;
 }catch(e){if(generation===projectDialogGeneration)projectError(e.message)}finally{projectBusy=false}
}
function sameProjectDefinition(p,d){
 const normalizedVersion=value=>value?.trim().replace(/\d+/g,n=>String(Number(n)))||null;
 const normalize=d=>({...d,name:d.name.trim(),repository:d.repository.trim().toLowerCase(),defaultBranch:d.defaultBranch.trim(),description:d.description.trim(),requirements:(d.requirements||[]).map(r=>({type:r.type.trim().toLowerCase(),name:r.name.trim().toLowerCase(),version:normalizedVersion(r.version),scope:r.scope?.trim().toLowerCase()||null})),issueReadyLabel:d.issueReadyLabel?.trim()||null,issueBlockedLabel:d.issueBlockedLabel?.trim()||null,automaticDiscovery:d.automaticDiscovery||null});
 const existing=Object.fromEntries(Object.keys(d).map(key=>[key,p[key]]));return JSON.stringify(normalize(existing))===JSON.stringify(normalize(d));
}
async function finishProjectSave(project){
 projectUncertain=null;$('project-dialog').close();await loadProjects();
 let workerState=null;try{const current=await api('/api/v1/workers');if(Array.isArray(current))workerState=current}catch{/* Readiness remains unknown when this independent observation fails. */}
 const preparation=workerState===null?'Worker inventory is unavailable. Refresh Workers to inspect preparation.':workerState.length?'Inspect and associate a Worker, then verify its project configuration and authentication.':'No Worker exists yet. Add and prepare a Worker to continue.';
 $('project-result').textContent=`Project “${project.name}” saved. ${preparation} Server read verification does not establish Worker execution readiness. No Issues were marked ready or work enqueued.`;$('project-result').hidden=false;
 $('project-result').appendChild(Object.assign(document.createElement('a'),{href:'#/workers?prepare=1',textContent:' Add / associate Worker'}));
 $('project-result').appendChild(Object.assign(document.createElement('a'),{href:'#/projects/'+encodeURIComponent(project.id),textContent:' Inspect project preparation'}));
}
async function submitProject(event){
 event.preventDefault();if(projectBusy)return;projectBusy=true;$('save-project').disabled=true;
 try{
 if(projectUncertain){
  const all=await api('/api/v1/projects'),attempt=projectUncertain;
  const found=all.find(p=>attempt.id?p.id===attempt.id:p.name.toLowerCase()===attempt.definition.name.toLowerCase()||p.repository.toLowerCase()===attempt.definition.repository.toLowerCase());
  if(found&&sameProjectDefinition(found,attempt.definition)&&(!attempt.id||found.revision===attempt.revision+1)){await finishProjectSave(found);return}
  if(found&&(!attempt.id||found.revision!==attempt.revision)){projectConflict=found;$('reload-project').hidden=false;throw Error('Persisted definition differs or revision changed. Load the current definition to review it; this draft has not been overwritten.')}
  if(attempt.id&&!found){projectUncertain=null;invalidateProjectReview();throw Error('The project was deleted. Close this draft and refresh Projects.');}
  projectUncertain=null;projectError('Save was not applied. Verify and review this draft before retrying.');invalidateProjectReview();return;
 }
 if(!projectReviewed||JSON.stringify(projectReviewed)!==JSON.stringify(projectDefinition()))throw Error('Verify and review the current definition before saving.');
 const id=$('project-id').value,revision=Number($('revision').value),definition=projectReviewed;
 projectUncertain={id,revision,definition};
 const saved=await api(id?'/api/v1/projects/'+encodeURIComponent(id):'/api/v1/projects',{method:id?'PUT':'POST',body:JSON.stringify(id?{definition,expectedRevision:revision}:definition)});
 await finishProjectSave(saved);
 }catch(e){projectError(projectUncertain?'Save could not be confirmed. Choose Check saved definition to reconcile before retrying. '+e.message:e.message)}
 finally{projectBusy=false;$('save-project').textContent=projectUncertain?'Check saved definition':'Save reviewed project';$('save-project').disabled=!projectUncertain&&!projectReviewed}
}
$('discover-repositories').onclick=()=>discoverProjectRepositories();$('more-repositories').onclick=()=>{if(repositoryNextPage)discoverProjectRepositories(repositoryNextPage)};
$('repository-selection').onchange=()=>{const r=repositoryChoices.find(r=>r.repository===$('repository-selection').value);if(!r)return;$('repository').value=r.repository;$('name').value=r.name;$('branch').value=r.defaultBranch;$('description').value=r.description;invalidateProjectReview()};
$('add-requirement').onclick=()=>addProjectRequirement();$('review-project').onclick=reviewProject;
$('project-form').addEventListener('input',invalidateProjectReview);$('project-form').addEventListener('change',invalidateProjectReview);

$('reload-project').onclick=()=>{if(projectConflict){const current=projectConflict;projectUncertain=null;openProject(current)}};
