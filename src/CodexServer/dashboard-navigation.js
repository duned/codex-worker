// One navigation owner per page. Routes contain resource IDs and filters, never credentials.
function createDashboardNavigation({document,window,onRoute,isServerGitHubConnected}){
 const views=['home','projects','workers','executions','settings'];
 const $=id=>document.getElementById(id);
 const escape=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 let active=false,generation=0,route=null,lastHomeMarkup='';
 let state={projects:null,nodes:null,workers:null,executions:null};
 function readRoute(){
  const segments=(window.location.pathname||'/home').slice(1).split('/');
  if(segments[0]==='workers'&&segments[1]&&segments.length===3&&segments[2]==='poc')return {view:'workers',id:decodeURIComponent(segments[1]),poc:true,params:new URLSearchParams(window.location.search)};
  if(!views.includes(segments[0])||segments.length>2||(segments[0]==='home'&&segments.length!==1))return {view:'home',id:'',params:new URLSearchParams()};
  return {view:segments[0],id:segments[1]?decodeURIComponent(segments[1]):'',params:new URLSearchParams(window.location.search)};
 }
 function current(){try{return readRoute()}catch{return {view:'home',id:'',params:new URLSearchParams()}}}
 function canonical(view,id='',params={}){
  return '/'+view+(id?'/'+encodeURIComponent(id):'')+(new URLSearchParams(params).size?'?'+new URLSearchParams(params):'');
 }
 // Migrate old bookmarks once; all subsequent navigation uses this router.
 if(window.location.hash?.startsWith('#/')){
  const legacy=window.location.hash.slice(1);
  try{const [path,query='']=legacy.split('?'),parts=path.slice(1).split('/');
   window.history.replaceState({},'',views.includes(parts[0])&&parts.length<=2?canonical(parts[0],decodeURIComponent(parts[1]||''),new URLSearchParams(query)):'/home');
  }catch{window.history.replaceState({},'','/home')}
 }else if(window.location.pathname==='/')window.history.replaceState({},'','/home');
 window.history.scrollRestoration='manual';
 function saveScroll(){window.history.replaceState({...window.history.state,scroll:[window.scrollX||0,window.scrollY||0]},'')}
 function render({navigation=false,restore=null}={}){
  const previous=route;
  generation++;route=current();const version=generation;
  // Workers is React-owned, including old bookmarks and Back/Forward from
  // the retained shell. Never render a second Worker application here.
  if(route.view==='workers'){window.location.assign(window.location.pathname+window.location.search);return;}
  for(const view of document.querySelectorAll('[data-view]'))view.hidden=view.dataset.view!==route.view;
  for(const link of $('navigation').querySelectorAll('a')){
   if(link.getAttribute('href')==='/'+route.view)link.setAttribute('aria-current','page');else link.removeAttribute('aria-current');
  }
  $('view-title').textContent=route.view[0].toUpperCase()+route.view.slice(1);
  document.title='Codex Server · '+$('view-title').textContent+(route.id?' · '+route.id:'');
  $('view-context').textContent=route.id?'Selected '+({projects:'project',workers:'Worker',executions:'execution',settings:'credential'}[route.view]||'resource')+': '+route.id:'';
  // The isolated detail owns its friendly-name title and secondary identity.
  $('view-title').hidden=!!route.poc;$('view-context').hidden=!!route.poc;
  const step=route.params.get('step')||(route.params.has('project')?'project':route.params.has('prepare')?'preparation':'registration');
  $('node-context').hidden=!!route.poc||!['workers','settings'].includes(route.view)||(route.view==='workers'&&(!route.id||step!=='preparation'));
  if(route.view==='workers')$('node-context').open=true;else if(previous?.view!==route.view)$('node-context').open=false;
  $('worker-authentication-guidance').hidden=route.view!=='workers';
  $('project-context').hidden=route.view!=='projects'||!route.id;
  $('resource-back').hidden=!route.id;$('resource-back').href=canonical(route.view,'',route.view==='executions'?route.params:{});
  $('execution-list-toolbar').hidden=!!route.id;$('executions').hidden=!!route.id;$('execution-detail').hidden=!route.id;
  $('project-list-panel').hidden=route.view==='projects'&&!!route.id;
  $('worker-list-panel').hidden=route.view==='workers'&&!!route.id;
  $('worker-detail-panel').hidden=!route.id||!!route.poc;
  $('worker-admin-panel').hidden=!route.id||step!=='activation'||!!route.poc;
  $('worker-registration-guidance').hidden=!!route.poc;
  if($('worker-poc'))$('worker-poc').hidden=!route.poc;
  if(active){
   const loaded=onRoute(route,previous);
   if(navigation&&(!previous||previous.view!==route.view||previous.id!==route.id)){
    $('view-title').focus({preventScroll:true});
    if(!restore&&(!previous||previous.view!==route.view||previous.id!==route.id))window.scrollTo(0,0);
   }
   if(restore)Promise.resolve(loaded).then(()=>{if(active&&version===generation)window.scrollTo(...restore)});
  }
 }
 function navigate(view,id='',params={}){
  if(!views.includes(view))return;
  const path=canonical(view,id,params);
  // Leaving the isolated page restores the ordinary shell without React assets.
  if(view==='workers'||current().poc){window.location.assign(path);return;}
  if(window.location.pathname+window.location.search===path)return;
  saveScroll();window.history.pushState({},'',path);render({navigation:true});
 }
 document.addEventListener('click',event=>{
  const link=event.target.closest?.('a[href]');
  if(!link||event.defaultPrevented||event.button!==0||event.metaKey||event.ctrlKey||event.shiftKey||event.altKey||link.target||link.hasAttribute('download'))return;
  const url=new URL(link.href,window.location.origin),parts=url.pathname.slice(1).split('/');
  if(url.origin!==window.location.origin||url.hash||!views.includes(parts[0])||parts.length>2||(parts[0]==='home'&&parts.length!==1))return;
  try{const id=decodeURIComponent(parts[1]||'');event.preventDefault();navigate(parts[0],id,new URLSearchParams(url.search))}catch{/* Invalid links remain ordinary browser navigation. */}
 });
 window.addEventListener('scroll',saveScroll,{passive:true});
 window.addEventListener('popstate',()=>render({navigation:true,restore:window.history.state?.scroll||[0,0]}));
 // Keep expanded diagnostics, focused actions and viewport stable across observations.
 function preservePresentation(element,renderContent){
  const focused=document.activeElement,inside=focused&&element.contains(focused);
  const identity=inside?{dataset:{...focused.dataset},id:focused.id,href:focused.getAttribute('href'),summary:[...element.querySelectorAll('summary')].indexOf(focused)}:null;
  const disclosures=[...element.querySelectorAll('details')].map(details=>details.open);
  const scroll=[window.scrollX||0,window.scrollY||0];
  renderContent();
  [...element.querySelectorAll('details')].forEach((details,index)=>{if(index<disclosures.length)details.open=disclosures[index]});
  if(identity){
   const replacement=[...element.querySelectorAll('button,select,a,input,summary')].find(control=>
    identity.id?control.id===identity.id:identity.href?control.getAttribute('href')===identity.href:
    identity.summary>=0?[...element.querySelectorAll('summary')][identity.summary]===control:
    Object.keys(identity.dataset).length&&Object.entries(identity.dataset).every(([key,value])=>control.dataset[key]===value));
   if(replacement&&!replacement.disabled)replacement.focus({preventScroll:true});
  }
  if(window.scrollX!==scroll[0]||window.scrollY!==scroll[1])window.scrollTo(...scroll);
 }
 function updateRows(element,markup){
  if(element.innerHTML!==markup)preservePresentation(element,()=>{element.innerHTML=markup});
 }
 function renderHome(){
  const server=state.nodes?.find(n=>n.kind==='server');
  const github=server?.capabilities.find(c=>c.definition.id==='github-cli');
  const githubReady=!!server&&isServerGitHubConnected(server);
  const prepared=state.nodes?.find(n=>n.kind==='worker'&&!n.observationsStale&&n.connectivity==='connected'&&n.executionReadiness==='ready');
  const milestones=[
   {label:'Connect Server GitHub',complete:githubReady,known:state.nodes!==null,href:'/settings?node=server',action:'Connect Server GitHub',detail:server?.observationsStale?'Observations stale — check authentication':github?'Authentication: '+github.state.authentication:'Server capability state unavailable'},
   {label:'Create a project',complete:!!state.projects?.length,known:state.projects!==null,href:'/projects',action:'Manage projects',detail:state.projects===null?'Central definitions unavailable':state.projects.length+' central project(s)'},
   {label:'Prepare a Worker',complete:!!prepared,known:state.nodes!==null,href:prepared?'/workers/'+encodeURIComponent(prepared.id):'/workers?prepare=1',action:prepared?'Inspect Worker':'Prepare Worker',detail:prepared?prepared.displayName+' has current execution prerequisites':'Register a Worker and inspect tools, authentication and configuration'}
  ];
  const configured=milestones.every(m=>m.complete);
  $('home-heading').textContent=configured?'Activity and next steps':'System setup and next steps';
  const milestoneMarkup=milestones.map(m=>`<div class="row"><span><span class="primary">${escape(m.label)}</span><span class="sub">${escape(m.detail)}</span></span><span><span class="badge ${m.complete?'online':'stale'}">${m.complete?'Complete':m.known?'Needs attention':'Unavailable'}</span> <a class="button ${!m.complete?'solid':''}" href="${m.href}">${escape(m.action)}</a></span></div>`).join('');
  const setupMarkup=configured?'<details><summary>Setup milestones · complete</summary>'+milestoneMarkup+'</details>':milestoneMarkup;
  const blockers=state.workers?.filter(w=>w.availability!=='online'||w.schedulingPolicy!=='Enabled')||[];
  const pending=state.executions?.filter(e=>e.pendingReason||e.recoveryState||e.managedEligibilityState==='blocked')||[];
  const activity=state.executions?.slice(0,3).map(e=>`<div class="row"><span><a class="primary" href="/executions/${encodeURIComponent(e.id)}">${escape(e.workReference?.type)} ${escape(e.workReference?.id||e.id)}</a><span class="sub">${escape(e.pendingReason||e.recoveryReason||(e.managedEligibilityReasons||[]).join('; ')||e.completionSummary||e.currentStage||'')}</span></span><span class="badge">${escape(e.state)}</span></div>`).join('')||'';
  const workerBlockers=blockers.slice(0,3).map(w=>`<p class="sub"><a href="/workers/${encodeURIComponent(w.workerId)}">${escape(w.displayName)}</a> · ${escape(w.availability)} · scheduling ${escape(w.schedulingPolicy)}</p>`).join('');
  const markup=setupMarkup+activity+workerBlockers+`<p class="sub">${state.workers===null?'Worker connectivity unavailable':state.workers.length+' registered Worker(s) · '+blockers.length+' with connectivity or scheduling blockers'}. ${state.executions===null?'Execution activity unavailable':state.executions.length+' execution(s) in the latest bounded queue view · '+pending.length+' with pending, eligibility or recovery evidence'}.</p><p><a class="button" href="/executions">Review queue and outcomes</a> <a class="button" href="/workers">Review Worker availability</a></p>`;
  if(markup!==lastHomeMarkup){$('home-next').innerHTML=markup;lastHomeMarkup=markup}
 }
 render();renderHome();
 return {
  navigate,current,render,updateRows,preservePresentation,
  capture:()=>generation,isCurrent:value=>active&&value===generation,
  start(){active=true;render({restore:window.history.state?.scroll})},
  stop(){active=false;generation++;state={projects:null,nodes:null,workers:null,executions:null};renderHome()},
  observe(key,value){state[key]=value;renderHome()}
 };
}
