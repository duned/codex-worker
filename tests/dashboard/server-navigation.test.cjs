// Exercise the delivered bundle with deterministic DOM/HTTP/clock seams.
const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const {readDashboard}=require('./server-dashboard-source.cjs');
const flush=async()=>{for(let i=0;i<100;i++)await Promise.resolve()};
const deferred=()=>{let resolve;const promise=new Promise(r=>resolve=r);return {promise,resolve}};
function setup(path='/home'){
 const poc=path.split('?')[0].endsWith('/poc'),pocRenders=[],pocUpdates=[],pocSessions=[],assignments=[];
 const html=readDashboard({workerPoc:poc}),elements=new Map(),listeners=new Map(),timers=new Map(),calls=[];
 let timerId=0;
 class Element{
  constructor(id){this.id=id;this.value='';this.dataset={};this.hidden=false;this.open=false;this.children=[];this.attributes={};this.checked=false;this.markup='';this.textContent=''}
  set innerHTML(value){this.markup=value;this.children=[...value.matchAll(/<button([^>]*)>/g)].map(match=>{const button=new Element();for(const data of match[1].matchAll(/data-([a-z-]+)="([^"]*)"/g))button.dataset[data[1].replace(/-([a-z])/g,(_,letter)=>letter.toUpperCase())]=data[2];return button});for(const match of value.matchAll(/id="([^"]+)"/g))if(!elements.has(match[1]))elements.set(match[1],new Element(match[1]))}
  get innerHTML(){return this.markup}
  getAttribute(name){return this.attributes[name]}
  setAttribute(name,value){this.attributes[name]=value}
  removeAttribute(name){delete this.attributes[name]}
  addEventListener(){}
  querySelectorAll(selector){if(['a','button,select,a,input','button,select,a,input,summary'].includes(selector))return this.children;const key=selector.match(/^\[data-([a-z-]+)\]$/)?.[1]?.replace(/-([a-z])/g,(_,c)=>c.toUpperCase());return key?this.children.filter(c=>key in c.dataset):[]}
  contains(element){return this.children.includes(element)}
  querySelector(selector){return this.markup.includes(selector.slice(1,-1))?new Element():null}
  append(child){this.children.push(child)}
  focus(){this.focused=true}
  scrollIntoView(){this.scrolled=true}
  close(){this.open=false}
  showModal(){this.open=true}
 }
 for(const match of html.split('<script>')[0].matchAll(/id="([^"]+)"/g))elements.set(match[1],new Element(match[1]));
 const $=id=>elements.get(id)||null;
 const views=['home','projects','workers','executions','settings'].map(view=>{const el=$('view-'+view);el.dataset.view=view;return el});
 $('navigation').children=views.map(view=>{const el=new Element();el.attributes.href='/'+view.dataset.view;return el});
 const worker={workerId:'worker-a',displayName:'Worker A',workerVersion:'0.15.0',availability:'online',lifecycleState:'running',maximumCapacity:2,activeExecutions:0,availableCapacity:2,schedulingPolicy:'Enabled',activeAssignments:0,capabilities:[],activeProjects:['Project A'],managedDiagnostics:{source:'server',projects:[{projectId:'project-a',revision:1,state:'ready'}]}};
 const project={id:'project-a',name:'Project A',repository:'owner/repo',defaultBranch:'main',revision:1,enabled:true,requirements:[]};
 const github={definition:{id:'github-cli',displayName:'GitHub CLI'},state:{installation:'Installed',authentication:'Satisfied',health:'Healthy',operation:{state:'Idle'}},availableActions:[]};
 const server={id:'server',kind:'server',displayName:'Server',observationsStale:false,capabilities:[github],connectivity:'connected',health:'healthy'};
 const node={id:'worker-a',kind:'worker',displayName:'Worker A',connectivity:'connected',executionReadiness:'ready',observationsStale:false,capabilities:[]};
 const data={workers:[],projects:[],nodes:[{...server,capabilities:[{...github,state:{...github.state,authentication:'Required'}}]}],executions:[],commands:[],githubCommands:[]};
 const held=new Map();let rejectNodes=false,streamRead=null;
 const origin='https://server.example',entries=[],scrolls=[];let entry=-1;
 const window={location:{origin},scrollX:0,scrollY:0,scrollTo(x,y){this.scrollX=x;this.scrollY=y;scrolls.push([x,y])},addEventListener(name,handler){if(!listeners.has(name))listeners.set(name,[]);listeners.get(name).push(handler)}};
 window.location.assign=value=>assignments.push(value);
 if(poc)window.codexWorkerPoc={session:data=>pocSessions.push(data),render:(data,loading=false)=>pocRenders.push({data,loading}),update:data=>pocUpdates.push(data),clear:()=>pocUpdates.push({cleared:true})};
 function location(value){const url=new URL(value,origin);Object.assign(window.location,{pathname:url.pathname,search:url.search,hash:url.hash})}
 window.history={state:null,pushState(state,title,value){entries.splice(++entry);entries.push({state,path:value});this.state=state;location(value)},replaceState(state,title,value){const path=value||window.location.pathname+window.location.search;entries[entry]={state,path};this.state=state;location(path)}};
 window.history.pushState({},'',path);
 const documentListeners=new Map();
 const context=vm.createContext({window,document:{addEventListener(name,handler){documentListeners.set(name,handler)},getElementById:$,querySelectorAll:selector=>selector==='[data-view]'?views:selector==='dialog'?[...elements.values()].filter(el=>el.id?.endsWith('dialog')):[],createElement:()=>new Element()},
  Headers,AbortController,AbortSignal,TextDecoder,TextEncoder,Date,URL,URLSearchParams,Set,Math,JSON,encodeURIComponent,decodeURIComponent,confirm:()=>true,prompt:()=>null,
  setTimeout(fn,delay){const id=++timerId;timers.set(id,{fn,delay});return id},clearTimeout:id=>timers.delete(id),
  fetch:async(path,options)=>{
   calls.push({path,options});
   if(held.has(path))return held.get(path).promise;
   if(path==='/api/v1/events/stream'){
    options.signal.addEventListener('abort',()=>streamRead?.resolve({done:true}));
    return {ok:true,status:200,body:{getReader:()=>({read(){streamRead=deferred();return streamRead.promise},cancel:async()=>streamRead?.resolve({done:true}),releaseLock(){}})}};
   }
   if(path==='/api/v1/nodes'&&rejectNodes)throw Error('inventory unavailable');
   let body;
   if(path==='/api/v1/administration/session')body={csrfToken:'test-csrf',expiresAtUtc:new Date(Date.now()+3600000).toISOString()};
   else if(path==='/api/status')body={state:'running'};
   else if(path==='/api/version')body={version:'0.15.0'};
   else if(path==='/health')body={status:'healthy'};
   else if(path==='/api/v1/projects')body=data.projects;
   else if(path==='/api/v1/workers')body=data.workers;
   else if(path==='/api/v1/nodes')body=data.nodes;
   else if(path==='/api/v1/nodes/server/github-connection')body={commands:data.githubCommands,provisioningEnabled:true,elevationAllowed:false};
   else if(path==='/api/v1/provisioning/commands')body=options.method?{}:data.commands;
   else if(path.includes('/diagnostics'))body={projects:[],reasons:[],configurationSynchronization:'Current'};
   else if(path.includes('/credential-access'))body={status:'Authorized'};
   else if(path.startsWith('/api/v1/workers/'))body={...worker,workerId:path.split('/')[4]};
   else if(path.startsWith('/api/v1/executions?'))body=data.executions;
   else if(path.startsWith('/api/v1/executions/'))body={id:path.split('/')[4],state:'Completed',completionSummary:'Done'};
   else if(path.includes('/github/issues?'))body=[];
   else if(path.includes('/github/issues/'))body={number:7,url:'https://github.com/owner/repo/issues/7',title:'Issue',state:'open',labels:[],blockedBy:[],eligibilityReasons:[],body:'',isEligible:false};
   else if(path.startsWith('/api/v1/credentials/'))body={id:'credential-a',provider:'provider',type:'token',status:'Ready'};
   else body=[];
   return {ok:true,status:options.method==='DELETE'?204:200,json:async()=>body};
  }
 });
 vm.runInContext(html.match(/<script>([\s\S]*)<\/script>/)[1],context);
 const emit=async(name,event={})=>{for(const handler of listeners.get(name)||[])handler(event);await flush()};
 return {$,document:context.document,data,worker,project,node,server,github,calls,listeners,timers,held,pocRenders,pocUpdates,pocSessions,assignments,
  async route(path){window.history.pushState({},'',path);await emit('popstate')},
  window,scrolls,documentListeners,async back(){entry--;window.history.state=entries[entry].state;location(entries[entry].path);await emit('popstate')},async forward(){entry++;window.history.state=entries[entry].state;location(entries[entry].path);await emit('popstate')},
  async poll(){const entry=[...timers.entries()].find(([,timer])=>timer.delay===5000);assert.ok(entry);timers.delete(entry[0]);await entry[1].fn();await flush()},
  configured(){data.workers=[worker];data.projects=[project];data.nodes=[server,node]},
  rejectNodes(){rejectNodes=true},async workersEvent(items){streamRead.resolve({value:new TextEncoder().encode('data: '+JSON.stringify(items)+'\n\n'),done:false});await flush()},emit,flush
 };
}

test('first-use milestones support alternate order, configured state, stale facts and unavailable refresh',async()=>{
 const s=setup();await s.flush();
 assert.match(s.$('home-next').innerHTML,/Connect Server GitHub/);assert.match(s.$('home-next').innerHTML,/Create a project/);assert.match(s.$('home-next').innerHTML,/Prepare a Worker/);
 assert.doesNotMatch(s.$('home-next').innerHTML,/>Complete</);
 s.data.projects=[s.project];await s.poll();assert.match(s.$('home-next').innerHTML,/1 central project/);assert.equal((s.$('home-next').innerHTML.match(/>Complete</g)||[]).length,1);
 s.configured();await s.poll();assert.equal(s.$('home-heading').textContent,'Activity and next steps');assert.equal((s.$('home-next').innerHTML.match(/>Complete</g)||[]).length,3);
 s.github.state.operation={state:'Running',action:'login'};await s.poll();assert.equal((s.$('home-next').innerHTML.match(/>Complete</g)||[]).length,2);
 s.github.state.operation={state:'Failed',action:'login'};await s.poll();assert.equal((s.$('home-next').innerHTML.match(/>Complete</g)||[]).length,2);
 s.github.state.operation={state:'Idle'};
 s.data.nodes=[s.server,{...s.node,observationsStale:true}];await s.poll();assert.equal((s.$('home-next').innerHTML.match(/>Complete</g)||[]).length,2);
 s.rejectNodes();await s.poll();assert.match(s.$('home-next').innerHTML,/Unavailable/);assert.doesNotMatch(s.$('home-next').innerHTML,/Worker A has current/);
 await s.emit('pagehide');
});

test('one navigation owner preserves resource/filter URLs on reload and back without adding listeners or timers',async()=>{
 const s=setup('/executions/execution-a?project=project-a&state=Completed&issue=7&offset=50');await s.flush();
 assert.equal(s.$('view-title').textContent,'Executions');assert.equal(s.$('execution-project-filter').value,'project-a');assert.equal(s.$('execution-state-filter').value,'Completed');assert.match(s.$('execution-detail').innerHTML,/execution-a/);
 const listenerCount=[...s.listeners.values()].reduce((n,list)=>n+list.length,0),timerCount=s.timers.size;
 for(const route of ['/projects/project-a','/workers/worker-a','/settings','/home','/executions/execution-a?project=project-a&state=Completed&issue=7&offset=50']){await s.route(route);assert.equal([...s.listeners.values()].reduce((n,list)=>n+list.length,0),listenerCount);assert.equal(s.timers.size,timerCount)}
 assert.equal(s.$('view-projects').hidden,true);assert.equal(s.$('view-executions').hidden,false);assert.equal(s.$('view-title').focused,true);
 await s.emit('pagehide');assert.equal([...s.timers.values()].filter(timer=>timer.delay===5000).length,0);
 await s.emit('pageshow',{persisted:true});assert.equal([...s.timers.values()].filter(timer=>timer.delay===5000).length,1);
 await s.emit('pagehide');
});

test('resource context hydrates on session restore, and credential metadata has a reloadable Settings route',async()=>{
 const s=setup('/projects/project-a?issue=7');s.configured();await s.flush();
 assert.match(s.$('project-detail').innerHTML,/Project A/);assert.match(s.$('project-detail').innerHTML,/Worker A/);assert.match(s.$('github-issue-detail').innerHTML,/Issue/);
 await s.route('/settings/credential-a');assert.equal(s.$('credential-details-dialog').open,true);assert.match(s.$('credential-details').innerHTML,/credential-a/);
 await s.route('/workers/worker-a');assert.equal(s.$('credential-details-dialog').open,false);assert.match(s.$('worker-detail').innerHTML,/Worker A/);assert.match(s.$('worker-admin').innerHTML,/Drain/);
 await s.emit('pagehide');
});

test('a delayed detail response or failure cannot overwrite a new resource context',async()=>{
 const s=setup();await s.flush();const old=deferred();s.held.set('/api/v1/executions/old',old);
 await s.route('/executions/old');await s.route('/executions/new');assert.match(s.$('execution-detail').innerHTML,/new/);
 old.resolve({ok:true,status:200,json:async()=>({id:'old'})});await s.flush();assert.doesNotMatch(s.$('execution-detail').innerHTML,/old/);
 const bad=deferred();s.held.set('/api/v1/executions/bad',bad);await s.route('/executions/bad');await s.route('/executions/new');bad.resolve({ok:false,status:404,json:async()=>({error:'old failure'})});await s.flush();assert.doesNotMatch(s.$('execution-detail').innerHTML,/old failure/);
 await s.emit('pagehide');
});

test('selected Server and Worker provisioning stays contextual and navigation rejects malformed resource paths',async()=>{
 const s=setup();s.configured();await s.flush();
 await s.route('/settings?node=server');assert.equal(s.$('node-select').value,'server');assert.equal(s.$('worker-authentication-guidance').hidden,true);assert.equal(s.$('github-connection-panel').scrolled,undefined);assert.match(s.$('node-select').innerHTML,/Server/);assert.doesNotMatch(s.$('node-select').innerHTML,/Worker A/);
 await s.route('/workers/worker-a');assert.equal(s.$('node-select').value,'worker-a');assert.equal(s.$('worker-authentication-guidance').hidden,false);assert.doesNotMatch(s.$('node-select').innerHTML,/>Server/);
 await s.route('/workers/%invalid');assert.equal(s.$('view-title').textContent,'Home');
 await s.emit('pagehide');
});

test('an in-flight polling cycle cannot recreate its timer after page teardown',async()=>{
 const s=setup();await s.flush();const delayed=deferred();s.held.set('/api/v1/nodes',delayed);
 const polling=s.poll();await s.flush();await s.emit('pagehide');
 delayed.resolve({ok:true,status:200,json:async()=>[s.server,s.node]});await polling;
 assert.equal([...s.timers.values()].filter(timer=>timer.delay===5000).length,0);
 assert.doesNotMatch(s.$('home-next').innerHTML,/>Complete</);
});

test('Home shows actual pending and recovery evidence from the unfiltered bounded queue',async()=>{
 const s=setup('/executions?project=filtered-project');s.configured();s.data.executions=[{id:'blocked-request',state:'Queued',workReference:{type:'github-issue',id:'12'},pendingReason:'No eligible Worker',recoveryReason:'Check integration evidence'}];await s.flush();
 await s.route('/home');assert.match(s.$('home-next').innerHTML,/No eligible Worker/);assert.match(s.$('home-next').innerHTML,/blocked-request/);
 assert.ok(s.calls.some(c=>c.path==='/api/v1/executions?limit=50&offset=0'));
 await s.emit('pagehide');
});

test('Issue context preserves project and list filters through direct reload',async()=>{
 const s=setup('/projects/project-a?issue=7&issueState=closed&label=review&issues=1');s.configured();await s.flush();
 assert.equal(s.$('github-project').value,'project-a');assert.equal(s.$('github-state').value,'closed');assert.equal(s.$('github-label').value,'review');
 assert.ok(s.calls.some(c=>c.path.includes('/projects/project-a/github/issues?state=closed&limit=50&label=review')));
 await s.emit('pagehide');
});

test('project observation refresh keeps the focused resource action reachable',async()=>{
 const s=setup('/projects');s.configured();await s.flush();
 s.document.activeElement=s.$('projects').children.find(button=>button.dataset.edit==='project-a');assert.ok(s.document.activeElement);
 s.data.projects=[{...s.project,revision:2}];await s.poll();assert.ok(s.$('projects').children.find(button=>button.dataset.edit==='project-a').focused);
 await s.emit('pagehide');
});

test('delivered Home and route loaders share equivalent reads and keep one stream and poll owner',async()=>{
 const s=setup();await s.flush();
 const count=path=>s.calls.filter(c=>c.path===path).length;
 for(const path of ['/api/v1/projects','/api/v1/nodes','/api/v1/workers','/api/v1/provisioning/commands','/api/v1/executions?limit=50&offset=0'])assert.equal(count(path),1,path);
 for(const route of ['/projects/project-a','/workers/worker-a','/settings?node=server','/home']){
  s.configured();const start=s.calls.length;await s.route(route);
  const reads=s.calls.slice(start).filter(c=>(c.options.method||'GET')==='GET');
  const paths=reads.map(c=>c.path);assert.equal(new Set(paths).size,paths.length,route+': '+paths.join(', '));
  assert.equal(s.calls.filter(c=>c.path==='/api/v1/events/stream'&&!c.options.signal.aborted).length,1);
  assert.equal([...s.timers.values()].filter(t=>t.delay===5000).length,1);
 }
 const start=s.calls.length;await s.poll();assert.equal(s.calls.slice(start).filter(c=>c.path==='/api/v1/provisioning/commands').length,1);
 await s.emit('pagehide');assert.equal(s.calls.filter(c=>c.path==='/api/v1/events/stream'&&!c.options.signal.aborted).length,0);
});

test('route cancellation ignores stale shared inventory and command completions and loads the active view',async()=>{
 const s=setup();await s.flush();s.configured();
 const inventory=deferred(),commands=deferred();s.held.set('/api/v1/nodes',inventory);s.held.set('/api/v1/provisioning/commands',commands);
 await s.route('/settings?node=server');const obsolete=s.calls.filter(c=>s.held.has(c.path)).slice(-2);
 s.held.clear();await s.route('/workers/worker-a');
 assert.ok(obsolete.every(c=>c.options.signal.aborted));
 const markup=s.$('node-detail').innerHTML;
 inventory.resolve({ok:true,status:200,json:async()=>[{...s.server,displayName:'obsolete secret'}]});commands.resolve({ok:true,status:200,json:async()=>[]});await s.flush();
 assert.equal(s.$('node-detail').innerHTML,markup);assert.match(markup,/Worker A/);assert.doesNotMatch(s.$('provisioning').innerHTML,/obsolete/);
 await s.emit('pagehide');
});

test('mutation invalidates pending observations and refreshes provisioning progress without duplicate command reads',async()=>{
 const s=setup('/settings');await s.flush();
 s.data.commands=[{id:'command-a',request:{nodeId:'server',capabilityId:'github-cli',action:'Detect'},status:'Pending',createdAtUtc:'2026-01-01T00:00:00Z',diagnostic:'Queued'}];
 s.$('refresh-provisioning').onclick();await s.flush();
 const button=s.$('provisioning').children.find(c=>c.dataset.provisioningAction==='cancel');assert.ok(button);
 const pending=deferred();s.held.set('/api/v1/provisioning/commands',pending);
 s.$('refresh-nodes').onclick();s.$('refresh-provisioning').onclick();await s.flush();
 const old=s.calls.filter(c=>c.path==='/api/v1/provisioning/commands').at(-1);
 s.held.clear();s.data.commands=[];const start=s.calls.length;
 await button.onclick();await s.flush();assert.equal(old.options.signal.aborted,true);
 assert.equal(s.calls.slice(start).filter(c=>c.path==='/api/v1/provisioning/commands/command-a/cancel').length,1);
 pending.resolve({ok:true,status:200,json:async()=>[{...s.data.commands[0],id:'obsolete'}]});await s.flush();
 assert.doesNotMatch(s.$('provisioning').innerHTML,/obsolete/);assert.match(s.$('provisioning').innerHTML,/No provisioning/);
 await s.emit('pagehide');
});

test('a delayed overview snapshot cannot replace fresher Worker stream observations',async()=>{
 const s=setup();await s.flush();const pending=deferred();s.held.set('/api/v1/workers',pending);
 s.$('refresh-workers').onclick();await s.flush();
 await s.workersEvent([{...s.worker,displayName:'Fresh Worker observation'}]);
 pending.resolve({ok:true,status:200,json:async()=>[{...s.worker,displayName:'Old HTTP observation'}]});await s.flush();
 assert.match(s.$('workers').innerHTML,/Fresh Worker observation/);assert.doesNotMatch(s.$('workers').innerHTML,/Old HTTP/);
 await s.emit('pagehide');
});


test('Home and guided Server connection agree on pending, failed and recovered authentication',async()=>{
 const s=setup();s.configured();await s.flush();
 const command={id:'login',request:{nodeId:'server',capabilityId:'github-cli',action:'Login'},createdAtUtc:'2026-01-01T00:00:00Z',diagnostic:'Operation failed'};
 for(const status of ['Pending','Running','Failed','TimedOut']){
  s.data.githubCommands=[{...command,status,deadlineUtc:'2026-01-01T00:01:00Z'}];await s.poll();
  assert.equal((s.$('home-next').innerHTML.match(/>Complete</g)||[]).length,2,status);
  assert.ok(!s.$('github-connection').children.some(e=>e.textContent.startsWith('Connected')),status);
 }
 s.data.githubCommands=[{...command,status:'Succeeded'}];await s.poll();
 assert.equal((s.$('home-next').innerHTML.match(/>Complete</g)||[]).length,3);
 assert.ok(s.$('github-connection').children.some(e=>e.textContent.startsWith('Connected')));
 s.github.state.operation={state:'Running',action:'checkauthentication'};await s.poll();
 assert.equal((s.$('home-next').innerHTML.match(/>Complete</g)||[]).length,2);
 assert.ok(!s.$('github-connection').children.some(e=>e.textContent.startsWith('Connected')));
 await s.emit('pagehide');
});

test('leaving Settings clears Server connection consent before returning',async()=>{
 const s=setup('/settings');await s.flush();
 s.$('github-provisioning-consent').checked=true;s.$('github-elevation-consent').checked=true;
 await s.route('/workers');await s.route('/settings');
 assert.equal(s.$('github-provisioning-consent').checked,false);
 assert.equal(s.$('github-elevation-consent').checked,false);
 assert.ok(s.calls.every(c=>!c.options.method||c.options.method==='GET'));
 await s.emit('pagehide');
});

test('Worker-first onboarding remains resumable without a project or scheduling activation',async()=>{
 const s=setup();await s.flush();
 s.data.workers=[{...s.worker,schedulingPolicy:'Disabled'}];s.data.nodes.push(s.node);
 await s.workersEvent(s.data.workers);await s.poll();
 assert.equal((s.$('home-next').innerHTML.match(/>Complete</g)||[]).length,1);
 assert.match(s.$('home-next').innerHTML,/Create a project/);
 assert.match(s.$('home-next').innerHTML,/scheduling Disabled/);
 await s.route('/workers/worker-a?step=project');
 assert.match(s.$('worker-detail').innerHTML,/No central project exists/);
 assert.ok(s.calls.every(c=>!c.options.method||c.options.method==='GET'));
 await s.emit('pagehide');
});

 test('product clicks push canonical history once, polls preserve scroll and Back/Forward restores entries',async()=>{
 const s=setup('/executions?state=Completed&offset=50');await s.flush();
 s.window.scrollY=480;await s.emit('scroll');
 let prevented=false;
 const link={href:'https://server.example/workers/worker-a?step=project',hasAttribute:()=>false};
 s.documentListeners.get('click')({target:{closest:()=>link},button:0,preventDefault(){prevented=true}});await s.flush();
 assert.equal(prevented,true);assert.equal(s.window.location.pathname,'/workers/worker-a');assert.equal(s.window.location.hash,'');assert.equal(s.window.scrollY,0);
 s.window.scrollY=320;await s.emit('scroll');await s.poll();assert.equal(s.window.scrollY,320);
 await s.back();assert.equal(s.window.scrollY,480);assert.equal(s.$('execution-state-filter').value,'Completed');
 await s.forward();assert.equal(s.window.scrollY,320);assert.equal(s.window.location.search,'?step=project');
 await s.emit('pagehide');
 });
 test('legacy bookmarks normalize once and same-resource step changes preserve scroll and open operations',async()=>{
 const s=setup('/#/workers/worker-a?project=project-a&step=preparation');await s.flush();
 assert.equal(s.window.location.pathname,'/workers/worker-a');assert.equal(s.window.location.hash,'');
 s.window.scrollY=230;await s.emit('scroll');s.$('project-dialog').open=true;
 const link={href:'https://server.example/workers/worker-a?project=project-a&step=activation',hasAttribute:()=>false};
 s.documentListeners.get('click')({target:{closest:()=>link},button:0,preventDefault(){}});await s.flush();
 assert.equal(s.window.scrollY,230);assert.equal(s.$('project-dialog').open,true);assert.equal(s.$('worker-admin-panel').hidden,false);
 await s.emit('pagehide');
 });

test('observation rendering preserves expanded disclosures and summary focus',async()=>{
 const s=setup('/workers/worker-a');await s.flush();
 // Exercise the actual router primitive with deterministic layout effects.
 const source=require('node:fs').readFileSync(require('node:path').join(__dirname,'../../src/CodexServer/dashboard-navigation.js'),'utf8');
 const details={open:true},summary={dataset:{},id:'',getAttribute:()=>null,focus(options){this.focusOptions=options}};
 let currentDetails=details,currentSummary=summary;
 const element={contains:control=>control===currentSummary,querySelectorAll(selector){return selector==='details'?[currentDetails]:selector==='summary'?[currentSummary]:[currentSummary]}};
 const context=vm.createContext({URL,URLSearchParams,document:{...s.document,activeElement:summary},window:s.window});vm.runInContext(source,context);
 const router=context.createDashboardNavigation({document:context.document,window:s.window,onRoute(){},isServerGitHubConnected:()=>false});
 s.window.scrollY=420;
 router.preservePresentation(element,()=>{currentDetails={open:false};currentSummary={...summary};s.window.scrollY=100});
 assert.equal(currentDetails.open,true);assert.equal(currentSummary.focusOptions.preventScroll,true);assert.equal(s.window.scrollY,420);
 await s.emit('pagehide');
});

test('isolated Worker PoC restores the existing session and consumes shared snapshots and read-only diagnostics',async()=>{
 const s=setup('/workers/worker-a/poc');await s.flush();
 assert.equal(s.$('view-workers').hidden,false);
 assert.equal(s.$('worker-poc').hidden,false);
 assert.equal(s.$('worker-detail-panel').hidden,true);
 assert.equal(s.$('worker-admin-panel').hidden,true);
 assert.equal(s.$('view-title').hidden,true);
 assert.equal(s.$('view-context').hidden,true);
 assert.equal(s.$('worker-registration-guidance').hidden,true);
 assert.equal(s.calls.filter(c=>c.path==='/api/v1/administration/session').length,1);
 assert.equal(s.calls.filter(c=>c.path==='/api/v1/events/stream').length,1);
 await s.workersEvent([s.worker]);
 assert.equal(s.pocRenders.at(-1).data[0].workerId,'worker-a');
 await s.poll();
 assert.ok(s.calls.some(c=>c.path==='/api/v1/workers/worker-a/diagnostics'));
 assert.ok(s.calls.some(c=>c.path==='/api/v1/workers/worker-a'));
 assert.ok(!s.calls.some(c=>c.path.endsWith('/credential-access')));
 assert.ok(s.pocUpdates.some(update=>Array.isArray(update.executions)));
 assert.ok(s.pocUpdates.some(update=>update.diagnostics));
 assert.ok(s.pocUpdates.some(update=>Array.isArray(update.nodes)));
 assert.ok(s.calls.every(c=>!c.options.method||c.options.method==='GET'));
 await s.$('logout').onclick();await s.flush();
 assert.equal(s.$('administration-content').hidden,true);
 assert.equal(s.pocRenders.at(-1).data,null);
 assert.equal([...s.timers.values()].filter(t=>t.delay===5000).length,0);
 await s.emit('pagehide');
});

test('PoC query context cannot expose the ordinary node administration panel',async()=>{
 const s=setup('/workers/worker-a/poc?step=preparation');await s.flush();
 assert.equal(s.$('worker-poc').hidden,false);
 assert.equal(s.$('node-context').hidden,true);
 await s.emit('pagehide');
});

test('PoC failed reads clear individual projections and late diagnostics cannot repopulate logout',async()=>{
 const s=setup('/workers/worker-a/poc');await s.flush();s.configured();await s.workersEvent([s.worker]);
 const diagnosticsPath='/api/v1/workers/worker-a/diagnostics',executionsPath='/api/v1/executions?limit=50&offset=0';
 s.held.set(diagnosticsPath,{promise:Promise.resolve({ok:false,status:500})});
 s.held.set(executionsPath,{promise:Promise.resolve({ok:false,status:500})});
 s.rejectNodes();await s.poll();
 assert.ok(s.pocUpdates.some(update=>update.diagnostics===null));
 assert.ok(s.pocUpdates.some(update=>update.executions===null));
 assert.ok(s.pocUpdates.some(update=>update.nodes===null&&update.nodeCommands===null));
 const late=deferred();s.held.set(diagnosticsPath,late);
 const polling=s.poll();await s.flush();await s.$('logout').onclick();await s.flush();
 const updates=s.pocUpdates.length;
 late.resolve({ok:true,status:200,json:async()=>({workerId:'worker-a'})});await polling;
 assert.equal(s.pocUpdates.length,updates);
 assert.ok(s.pocUpdates.some(update=>update.cleared));
 await s.emit('pagehide');
});

test('PoC exit restores the ordinary page by same-origin navigation and normal pages have no PoC assets',async()=>{
 const s=setup('/workers/worker-a/poc');await s.flush();
 let prevented=false;
 s.documentListeners.get('click')({target:{closest:()=>({href:'https://server.example/workers/worker-a',hasAttribute:()=>false})},button:0,preventDefault(){prevented=true}});
 assert.equal(prevented,true);assert.deepEqual(s.assignments,['/workers/worker-a']);
 assert.doesNotMatch(readDashboard(),/src="\/dashboard-assets\/worker-poc|href="\/dashboard-assets\/worker-poc|id="worker-poc"/);
 await s.emit('pagehide');
});

test('PoC mutations share session CSRF and uncertain responses require explicit refresh without replay',async()=>{
 const s=setup('/workers/worker-a/poc');await s.flush();s.configured();await s.workersEvent([s.worker]);await s.poll();
 const control=()=>s.pocUpdates.filter(update=>update.administration).at(-1).administration;
 const path='/api/v1/workers/worker-a/scheduling-policy';
 s.held.set(path,{promise:Promise.resolve({ok:false,status:500})});
 await control().onAction('Draining');
 const writes=()=>s.calls.filter(call=>call.path===path&&call.options.method==='PUT');
 assert.equal(writes().length,1);
 assert.equal(writes()[0].options.headers.get('X-Codex-CSRF'),'test-csrf');
 assert.equal(writes()[0].options.credentials,'same-origin');
 assert.equal(JSON.parse(writes()[0].options.body).policy,'Draining');
 assert.equal(control().needsRefresh,true);
 await s.poll();await control().onAction('Draining');assert.equal(writes().length,1);
 await control().onRefresh();assert.equal(control().needsRefresh,false);assert.equal(writes().length,1);
 await s.$('logout').onclick();await s.emit('pagehide');
});


test('PoC React session callbacks use the existing login/logout owner and never publish credentials',async()=>{
 const s=setup('/workers/worker-a/poc');await s.flush();
 assert.equal(s.pocSessions.at(-1).authenticated,true);
 await s.pocSessions.at(-1).onSignOut();await s.flush();
 assert.equal(s.pocSessions.at(-1).authenticated,false);
 await s.pocSessions.at(-1).onSignIn('fixture-management-token');await s.flush();
 assert.equal(s.pocSessions.at(-1).authenticated,true);
 const login=s.calls.filter(call=>call.path==='/api/v1/administration/session'&&call.options.method==='POST');
 assert.equal(login.length,1);
 assert.equal(login[0].options.headers.get('Authorization'),'Bearer fixture-management-token');
 assert.equal(s.$('token').value,'');
 assert.ok(s.pocSessions.every(session=>!JSON.stringify(session).includes('fixture-management-token')&&!('csrfToken' in session)));
 await s.emit('pagehide');
});
