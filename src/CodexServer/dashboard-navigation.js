// One navigation owner per page. Routes contain resource IDs and filters, never credentials.
function createDashboardNavigation({document,window,onRoute}){
 const views=['home','projects','workers','executions','settings'];
 const $=id=>document.getElementById(id);
 const escape=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 let active=false,generation=0,route=null,lastHomeMarkup='';
 let state={projects:null,nodes:null,workers:null,executions:null};
 function readRoute(){
  const [path,query='']=(window.location.hash||'#/home').slice(2).split('?');
  const segments=path.split('/');
  if(!views.includes(segments[0]))return {view:'home',id:'',params:new URLSearchParams()};
  return {view:segments[0],id:segments[1]?decodeURIComponent(segments[1]):'',params:new URLSearchParams(query)};
 }
 function current(){try{return readRoute()}catch{return {view:'home',id:'',params:new URLSearchParams()}}}
 function render(){
  generation++;route=current();
  for(const view of document.querySelectorAll('[data-view]'))view.hidden=view.dataset.view!==route.view;
  for(const link of $('navigation').querySelectorAll('a')){
   if(link.getAttribute('href')==='#/'+route.view)link.setAttribute('aria-current','page');else link.removeAttribute('aria-current');
  }
  $('view-title').textContent=route.view[0].toUpperCase()+route.view.slice(1);
  document.title='Codex Server · '+$('view-title').textContent+(route.id?' · '+route.id:'');
  $('view-context').textContent=route.id?'Selected '+({projects:'project',workers:'Worker',executions:'execution',settings:'credential'}[route.view]||'resource')+': '+route.id:'';
  $('node-context').hidden=!['workers','settings'].includes(route.view);
  $('project-context').hidden=route.view!=='projects'||!route.id;
  if(active){$('view-title').scrollIntoView({block:'start'});onRoute(route)}
 }
 function navigate(view,id='',params={}){
  const query=new URLSearchParams(params).toString();
  const hash='#/'+view+(id?'/'+encodeURIComponent(id):'')+(query?'?'+query:'');
  if(window.location.hash===hash)render();else window.location.hash=hash;
 }
 // Preserve a focused resource action when an observation refresh replaces its row.
 function updateRows(element,markup){
  const focused=document.activeElement;
  const identity=focused&&element.contains(focused)?{...focused.dataset}:null;
  element.innerHTML=markup;
  if(identity&&Object.keys(identity).length){
   const replacement=[...element.querySelectorAll('button,select,a,input')].find(control=>Object.entries(identity).every(([key,value])=>control.dataset[key]===value));
   if(replacement&&!replacement.disabled)replacement.focus({preventScroll:true});
  }
 }
 function renderHome(){
  const server=state.nodes?.find(n=>n.kind==='server');
  const github=server?.capabilities.find(c=>c.definition.id==='github-cli');
  const githubReady=!!server&&!server.observationsStale&&github?.state.installation==='Installed'&&github.state.authentication==='Satisfied'&&github.state.health==='Healthy'&&github.state.operation?.state!=='Running'&&!(github.state.operation?.state==='Failed'&&['login','checkauthentication'].includes(github.state.operation.action));
  const prepared=state.nodes?.find(n=>n.kind==='worker'&&!n.observationsStale&&n.connectivity==='connected'&&n.executionReadiness==='ready');
  const milestones=[
   {label:'Connect Server GitHub',complete:githubReady,known:state.nodes!==null,href:'#/settings?node=server',action:'Connect Server GitHub',detail:server?.observationsStale?'Observations stale — check authentication':github?'Authentication: '+github.state.authentication:'Server capability state unavailable'},
   {label:'Create a project',complete:!!state.projects?.length,known:state.projects!==null,href:'#/projects',action:'Manage projects',detail:state.projects===null?'Central definitions unavailable':state.projects.length+' central project(s)'},
   {label:'Prepare a Worker',complete:!!prepared,known:state.nodes!==null,href:prepared?'#/workers/'+encodeURIComponent(prepared.id):'#/workers?prepare=1',action:prepared?'Inspect Worker':'Prepare Worker',detail:prepared?prepared.displayName+' has current execution prerequisites':'Register a Worker and inspect tools, authentication and configuration'}
  ];
  const configured=milestones.every(m=>m.complete);
  $('home-heading').textContent=configured?'Activity and next steps':'System setup and next steps';
  const milestoneMarkup=milestones.map(m=>`<div class="row"><span><span class="primary">${escape(m.label)}</span><span class="sub">${escape(m.detail)}</span></span><span><span class="badge ${m.complete?'online':'stale'}">${m.complete?'Complete':m.known?'Needs attention':'Unavailable'}</span> <a class="button ${!m.complete?'solid':''}" href="${m.href}">${escape(m.action)}</a></span></div>`).join('');
  const setupMarkup=configured?'<details><summary>Setup milestones · complete</summary>'+milestoneMarkup+'</details>':milestoneMarkup;
  const blockers=state.workers?.filter(w=>w.availability!=='online'||w.schedulingPolicy!=='Enabled')||[];
  const pending=state.executions?.filter(e=>e.pendingReason||e.recoveryState||e.managedEligibilityState==='blocked')||[];
  const activity=state.executions?.slice(0,3).map(e=>`<div class="row"><span><a class="primary" href="#/executions/${encodeURIComponent(e.id)}">${escape(e.workReference?.type)} ${escape(e.workReference?.id||e.id)}</a><span class="sub">${escape(e.pendingReason||e.recoveryReason||(e.managedEligibilityReasons||[]).join('; ')||e.completionSummary||e.currentStage||'')}</span></span><span class="badge">${escape(e.state)}</span></div>`).join('')||'';
  const workerBlockers=blockers.slice(0,3).map(w=>`<p class="sub"><a href="#/workers/${encodeURIComponent(w.workerId)}">${escape(w.displayName)}</a> · ${escape(w.availability)} · scheduling ${escape(w.schedulingPolicy)}</p>`).join('');
  const markup=setupMarkup+activity+workerBlockers+`<p class="sub">${state.workers===null?'Worker connectivity unavailable':state.workers.length+' registered Worker(s) · '+blockers.length+' with connectivity or scheduling blockers'}. ${state.executions===null?'Execution activity unavailable':state.executions.length+' execution(s) in the latest bounded queue view · '+pending.length+' with pending, eligibility or recovery evidence'}.</p><p><a class="button" href="#/executions">Review queue and outcomes</a> <a class="button" href="#/workers">Review Worker availability</a></p>`;
  if(markup!==lastHomeMarkup){$('home-next').innerHTML=markup;lastHomeMarkup=markup}
 }
 window.addEventListener('hashchange',()=>{render();if(active)$('view-title').focus({preventScroll:true})});
 render();renderHome();
 return {
  navigate,current,render,updateRows,
  capture:()=>generation,isCurrent:value=>active&&value===generation,
  start(){active=true;render()},
  stop(){active=false;generation++;state={projects:null,nodes:null,workers:null,executions:null};renderHome()},
  observe(key,value){state[key]=value;renderHome()}
 };
}
