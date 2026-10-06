// Deterministic dashboard behavior checks; no browser or external services required.
const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const html=require('./server-dashboard-source.cjs').readDashboard();
const source=html.slice(html.indexOf('// Only wire actions'),html.indexOf('// One owner per page.'));
function setup(){
 class Element{
  constructor(){this.children=[];this.value='server';this.checked=false;this.disabled=false;this.textContent=''}
  set innerHTML(value){this.markup=value;this.children=[]}
  get innerHTML(){return this.markup||''}
  append(child){this.children.push(child)}
 }
 const elements=new Map();const $=id=>{if(!elements.has(id))elements.set(id,new Element());return elements.get(id)};
 const calls=[];let confirmation=true;
 const node={id:'server',displayName:'Server <unsafe>',kind:'server',connectivity:'connected',health:'healthy',executionReadiness:'not-applicable',provisioningReadiness:'ready',observationsStale:false,capabilities:[{definition:{id:'github-cli',displayName:'GitHub CLI',requiresAuthentication:true},availableActions:['refresh','install','uninstall','logout','checkauthentication'],state:{installation:'Missing',update:'Unknown',health:'Healthy',authentication:'Required',operation:{state:'Idle'}}}]};
 const command={id:'operation',request:{nodeId:'server',capabilityId:'github-cli',action:'Install'},status:'Pending',diagnostic:'Queued',createdAtUtc:'2026-01-01T00:00:00Z'};
 const context=vm.createContext({...require('./server-dashboard-source.cjs').dashboardDependencies(),$,document:{createElement:()=>new Element()},authenticated:true,setTimeout:()=>0,clearTimeout:()=>{},setInterval:()=>{},confirm:()=>confirmation,esc:value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])),api:async(path,options)=>{calls.push({path,options});if(options)return command;return path==='/api/v1/nodes'?[node]:path==='/api/v1/nodes/server/github-connection'?{commands:[],provisioningEnabled:true,elevationAllowed:true}:[]}});
 vm.runInContext(source,context);
 const run=code=>vm.runInContext(code,context);
 context.inventory=[node];context.commands=[];run('nodes=inventory;nodeCommands=commands;nodeSnapshotValid=true;renderNode()');
 const buttons=()=>$('node-detail').children.flatMap(s=>s.children).flatMap(e=>e.children);
 return {$,node,command,calls,run,context,buttons,setConfirmation:value=>confirmation=value};
}
test('renders independent readiness, escaped names and only advertised actions',()=>{
 const s=setup();assert.match(s.$('node-detail').innerHTML,/Server &lt;unsafe&gt;/);assert.match(s.$('node-detail').innerHTML,/Execution readiness/);assert.match(s.$('node-detail').innerHTML,/not-applicable/);
 assert.deepEqual(s.buttons().map(b=>b.textContent),['Refresh / Re-detect','Install','Uninstall','Logout / Remove authentication','Check authentication']);
 assert.ok(s.$('node-detail').children[0].children.some(e=>e.textContent.includes('Remote login is not supported')));
});
test('pending operations block every action, including duplicate submissions',async()=>{
 const s=setup();s.context.commands=[s.command];s.run('nodeCommands=commands;renderNode()');assert.ok(s.buttons().every(b=>b.disabled));await s.run("runNodeAction('server','github-cli','refresh')");assert.equal(s.calls.length,0);
});
test('expired Worker operations expose contextual reconciliation and policy denials explain node preparation',()=>{
 const s=setup();s.node.kind='worker';s.node.id='0123456789abcdef0123456789abcdef';s.$('node-select').value=s.node.id;
 s.context.commands=[{...s.command,request:{...s.command.request,nodeId:s.node.id},status:'Running',deadlineUtc:new Date(Date.now()-1000).toISOString()}];
 s.run('nodeCommands=commands;renderNode()');
 const section=s.$('node-detail').children[0];
 assert.ok(section.children.some(child=>child.textContent.includes('Deadline expired')));
 assert.ok(section.children.some(child=>child.textContent==='Reconcile after node quiescence'));
 assert.ok(s.buttons().every(button=>button.disabled));
 s.context.commands[0].status='Failed';s.context.commands[0].diagnostic='Denied';s.run('renderNode()');
 assert.ok(s.$('node-detail').children[0].children.some(child=>child.textContent.includes('permit the specific typed action')));
});
test('destructive actions require confirmation and installation requires elevation',async()=>{
 const s=setup();await s.run("runNodeAction('server','github-cli','install')");assert.equal(s.calls.length,0);assert.match(s.$('node-message').textContent,/Authorize elevation/);
 s.setConfirmation(false);await s.run("runNodeAction('server','github-cli','logout')");assert.equal(s.calls.length,0);
 s.$('node-elevation').checked=true;await s.run("runNodeAction('server','github-cli','install')");const payload=JSON.parse(s.calls[0].options.body);assert.deepEqual(payload,{nodeId:'server',capabilityId:'github-cli',action:'Install',timeoutSeconds:120,allowElevation:true});
});
test('retries failed commands only while their action remains advertised',()=>{
 const s=setup();s.context.commands=[{...s.command,status:'Failed',diagnostic:'Denied'}];s.run('nodeCommands=commands;renderNode()');assert.ok(s.buttons().some(b=>b.textContent==='Retry Install'));
 s.node.capabilities[0].availableActions=['refresh'];s.run('renderNode()');assert.deepEqual(s.buttons().map(b=>b.textContent),['Refresh / Re-detect']);
});
test('failed refresh disables stale actions and preserves diagnostics',async()=>{
 const s=setup();s.context.api=async()=>{throw Error('Server API returned 401')};await s.run('loadNodes()');assert.ok(s.buttons().every(b=>b.disabled));assert.match(s.$('node-message').textContent,/401/);await s.run("runNodeAction('server','github-cli','refresh')");assert.equal(s.calls.length,0);
});
test('in-flight submission prevents a second command',async()=>{
 const s=setup();let finish;let posts=0;s.context.api=(path,options)=>{if(options){posts++;return new Promise(resolve=>{finish=resolve})}return Promise.resolve(path==='/api/v1/nodes'?[s.node]:path==='/api/v1/nodes/server/github-connection'?{commands:[],provisioningEnabled:true,elevationAllowed:true}:[s.command])};
 const first=s.run("runNodeAction('server','github-cli','refresh')");await s.run("runNodeAction('server','github-cli','refresh')");assert.equal(posts,1);assert.ok(s.buttons().every(b=>b.disabled));finish(s.command);await first;
});

test('Worker selection uses Worker identity and stale observations stay visible',async()=>{
 const s=setup();s.node.id='0123456789abcdef0123456789abcdef';s.node.kind='worker';s.node.executionReadiness='not-ready';s.node.observationsStale=true;s.$('node-select').value=s.node.id;s.run('renderNode()');
 assert.match(s.$('node-detail').innerHTML,/Stale/);assert.match(s.$('node-detail').innerHTML,/not-ready/);
 await s.run(`runNodeAction('${s.node.id}','github-cli','refresh')`);assert.equal(JSON.parse(s.calls[0].options.body).nodeId,s.node.id);
});

test('Codex device login displays escaped instructions only during the active deadline',()=>{
 const s=setup();const cap=s.node.capabilities[0];cap.definition.id='codex-cli';cap.availableActions=['login','checkauthentication'];
 s.context.commands=[{...s.command,request:{nodeId:'server',capabilityId:'codex-cli',action:'Login'},status:'Running',deadlineUtc:new Date(Date.now()+60000).toISOString(),loginInstructions:{userCode:'ABCD-EFGH'}}];
 s.run('nodeCommands=commands;renderNode()');
 assert.ok(s.$('node-detail').children[0].children.some(e=>e.innerHTML.includes('ABCD-EFGH')&&e.innerHTML.includes('https://auth.openai.com/codex/device')));
 assert.ok(s.buttons().every(b=>b.disabled));
 s.context.commands[0].status='Succeeded';s.run('renderNode()');
 assert.ok(!s.$('node-detail').children[0].children.some(e=>e.innerHTML.includes('ABCD-EFGH')));
});

test('Codex node authentication projection exposes login, status and logout without claiming project authorization',()=>{
 const s=setup();const cap=s.node.capabilities[0];cap.definition.id='codex-cli';cap.definition.displayName='Codex CLI';cap.availableActions=['login','checkauthentication','logout'];
 s.run('renderNode()');
 const markup=s.$('node-detail').children.map(element=>element.innerHTML).join('');
 assert.match(markup,/Authentication/);assert.match(markup,/Required/);
 assert.deepEqual(s.buttons().map(button=>button.textContent),['Sign in with device code','Check authentication','Logout / Remove authentication']);
 assert.match(html,/service-account CLI authentication/);
 assert.match(html,/does not grant project scheduling or provider write permission/);
});

test('GitHub browser/device handoff remains node-local and exposes check and logout controls',()=>{
 const s=setup();const cap=s.node.capabilities[0];cap.availableActions=['prepareauthentication','checkauthentication','logout'];s.run('renderNode()');
 assert.deepEqual(s.buttons().map(button=>button.textContent),['Prepare GitHub login','Check authentication','Logout / Remove authentication']);
 assert.match(html,/gh auth login --hostname github.com/);
 assert.match(html,/node-local gh login only; provider-side scopes/);
});

test('Docker configuration is typed and requires explicit elevation',async()=>{
 const s=setup();s.node.capabilities=[{definition:{id:'docker',displayName:'Docker'},availableActions:['configure'],state:{installation:'Installed',health:'Healthy',configuration:'Required',operation:{state:'Idle'}}}];s.run('renderNode()');
 assert.deepEqual(s.buttons().map(b=>b.textContent),['Configure daemon access']);
 await s.run("runNodeAction('server','docker','configure')");assert.equal(s.calls.length,0);
 s.$('node-elevation').checked=true;await s.run("runNodeAction('server','docker','configure')");
 assert.deepEqual(JSON.parse(s.calls[0].options.body),{nodeId:'server',capabilityId:'docker',action:'Configure',timeoutSeconds:120,allowElevation:true});
});

function connectionSetup(){
 const s=setup();s.node.capabilities[0].state.installation='Installed';
 s.node.capabilities[0].availableActions=['prepareauthentication','login','checkauthentication','install'];
 s.connection={commands:[],provisioningEnabled:true,elevationAllowed:true};s.context.connection=s.connection;
 s.context.selectContextNode=()=>s.run('renderNode()');
 s.run('serverGitHub=connection;renderNode()');
 s.connectionText=()=>s.$('github-connection').children.map(e=>e.textContent+' '+e.innerHTML).join(' ');
 s.connectionButtons=()=>s.$('github-connection').children.filter(e=>e.onclick);
 s.login=(status='Running')=>({...s.command,request:{nodeId:'server',capabilityId:'github-cli',action:'Login'},status,deadlineUtc:'2030-01-01T00:00:00Z',loginInstructions:{verificationUri:'https://github.com/login/device',userCode:'ABCD-1234'}});
 s.clock={now:Date.parse('2026-01-01T00:00:00Z')};
 s.context.Date=class extends Date{constructor(...args){super(...(args.length?args:[s.clock.now]))}static now(){return s.clock.now}};
 s.context.setTimeout=(callback,delay)=>{s.timer={callback,delay};return 1};s.context.clearTimeout=()=>{s.timer=null};
 return s;
}
test('Server connection requires an explicit choice and honors local provisioning policy',async()=>{
 const s=connectionSetup();await s.run("runServerGitHub('prepareauthentication')");assert.equal(s.calls.length,0);assert.match(s.$('github-connection-message').textContent,/Authorize/);
 s.connection.provisioningEnabled=false;s.run('renderNode()');assert.match(s.connectionText(),/Provisioning is disabled/);assert.deepEqual(s.connectionButtons().map(b=>b.textContent),['Check Server authentication']);
 s.connection.provisioningEnabled=true;s.$('github-provisioning-consent').checked=true;
 await s.run("runServerGitHub('prepareauthentication')");assert.deepEqual(JSON.parse(s.calls[0].options.body),{nodeId:'server',capabilityId:'github-cli',action:'PrepareAuthentication',timeoutSeconds:120,allowElevation:false});
});
test('preparation, refusal and verified completion use real operation and capability states',()=>{
 const s=connectionSetup();s.connection.commands=[{...s.login('Succeeded'),request:{...s.login().request,action:'PrepareAuthentication'},loginInstructions:null}];s.run('renderNode()');assert.ok(s.connectionButtons().some(b=>b.textContent==='Start device login'));
 s.connection.commands[0].status='Failed';s.connection.commands[0].diagnostic='Denied';s.run('renderNode()');assert.match(s.connectionText(),/Existing operator authentication is preserved/);assert.ok(s.connectionButtons().some(b=>b.textContent==='Retry preparation'));
 s.connection.commands=[s.login('Succeeded')];s.run('renderNode()');assert.doesNotMatch(s.connectionText(),/Connected ·/);assert.doesNotMatch(s.connectionText(),/ABCD-1234/);
 s.node.capabilities[0].state.authentication='Satisfied';s.run('renderNode()');assert.match(s.connectionText(),/Connected ·/);assert.match(s.connectionText(),/Issue write authorization: not yet verified/);assert.deepEqual(s.connectionButtons().map(b=>b.textContent),['Check Server authentication']);
 s.connection.commands=[s.login('Failed')];s.run('renderNode()');assert.doesNotMatch(s.connectionText(),/Connected ·/);
});
test('reload recovers a live challenge without submitting, and expiry and unavailable snapshots hide it',async()=>{
 const s=connectionSetup();const login=s.login();s.context.api=async path=>{s.calls.push({path});return path==='/api/v1/nodes'?[s.node]:path==='/api/v1/nodes/server/github-connection'?{...s.connection,commands:[login]}:[]};
 await s.run('loadNodes();');assert.match(s.connectionText(),/ABCD-1234/);assert.match(s.connectionText(),/GitHub verification/);assert.ok(s.calls.every(c=>!c.options));assert.equal(s.connectionButtons().length,0);
 login.deadlineUtc='2025-01-01T00:00:00Z';s.run('renderNode()');assert.doesNotMatch(s.connectionText(),/ABCD-1234/);assert.match(s.connectionText(),/deadline expired/);
 login.deadlineUtc='2030-01-01T00:00:00Z';s.context.api=async()=>{throw Error('unavailable')};await s.run('loadNodes()');assert.doesNotMatch(s.connectionText(),/ABCD-1234/);assert.match(s.connectionText(),/Connection state unavailable/);
});
test('lost submission response is recovered before retry and never creates a duplicate login',async()=>{
 const s=connectionSetup();s.connection.commands=[{...s.login('Succeeded'),request:{...s.login().request,action:'PrepareAuthentication'}}];s.$('github-provisioning-consent').checked=true;
 let posts=0;const login=s.login();s.context.api=async(path,options)=>{if(options){posts++;throw Error('response lost')}return path==='/api/v1/nodes'?[s.node]:path==='/api/v1/nodes/server/github-connection'?{...s.connection,commands:[login]}:[]};
 await s.run("runServerGitHub('login')");assert.match(s.connectionText(),/ABCD-1234/);await s.run("runServerGitHub('login')");assert.equal(posts,1);assert.match(s.$('github-connection-message').textContent,/could not be confirmed/);
});
test('only queued operations can be cancelled and confirmed cancellation allows a safe retry',async()=>{
 const s=connectionSetup();const login=s.login('Pending');s.connection.commands=[login];s.run('renderNode()');assert.deepEqual(s.connectionButtons().map(b=>b.textContent),['Cancel queued operation']);
 const calls=[];s.context.api=async(path,options)=>{calls.push({path,options});if(options){login.status='Cancelled';login.loginInstructions=null;return login}return path==='/api/v1/nodes'?[s.node]:path==='/api/v1/nodes/server/github-connection'?s.connection:[]};
 await s.run("cancelServerGitHub('operation')");assert.equal(calls[0].path,'/api/v1/provisioning/commands/operation/cancel');assert.ok(s.connectionButtons().some(b=>b.textContent==='Retry device login'));assert.doesNotMatch(s.connectionText(),/ABCD-1234/);
 login.status='Running';await s.run("cancelServerGitHub('operation')");assert.equal(calls.filter(c=>c.options).length,1);
});
test('unsupported flow and missing tools offer bounded recovery and installation requires elevation',async()=>{
 const s=connectionSetup();s.node.capabilities[0].availableActions=['checkauthentication'];s.run('renderNode()');assert.match(s.connectionText(),/device flow is unavailable/);
 s.node.capabilities[0].state.installation='Missing';s.node.capabilities[0].availableActions=['install'];s.run('renderNode()');await s.run("runServerGitHub('install')");assert.equal(s.calls.length,0);assert.match(s.$('github-connection-message').textContent,/explicit elevation/);
 s.$('github-elevation-consent').checked=true;await s.run("runServerGitHub('install')");assert.equal(JSON.parse(s.calls[0].options.body).allowElevation,true);
});

test('challenge deadline clears active display without waiting for polling',()=>{
 const s=connectionSetup();s.connection.commands=[s.login()];s.run('renderNode()');assert.match(s.connectionText(),/ABCD-1234/);assert.ok(s.timer);
 const callback=s.timer.callback;s.clock.now=Date.parse('2030-01-01T00:00:00Z');callback();assert.doesNotMatch(s.connectionText(),/ABCD-1234/);assert.match(s.connectionText(),/deadline expired/);assert.equal(s.timer,null);
});
