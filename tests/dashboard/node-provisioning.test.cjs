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
 const context=vm.createContext({...require('./server-dashboard-source.cjs').dashboardDependencies(),$,document:{createElement:()=>new Element()},authenticated:true,setInterval:()=>{},confirm:()=>confirmation,esc:value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])),api:async(path,options)=>{calls.push({path,options});if(options)return command;return path==='/api/v1/nodes'?[node]:[]}});
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
 const s=setup();let finish;let posts=0;s.context.api=(path,options)=>{if(options){posts++;return new Promise(resolve=>{finish=resolve})}return Promise.resolve(path==='/api/v1/nodes'?[s.node]:[s.command])};
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
