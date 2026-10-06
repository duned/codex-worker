// Deterministic dashboard checks for combined legacy/typed history and typed lifecycle controls.
const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const fs=require('node:fs');
const html=fs.readFileSync('src/CodexServer/dashboard.html','utf8');
const start=html.indexOf('async function loadProvisioning');
const actionStart=html.indexOf('async function runProvisioningCommandAction',start);
const end=html.indexOf('\n',actionStart);
const source=html.slice(start,end);

function setup(){
 class Element{
  constructor(){this.dataset={};this.children=[];this.textContent='';this.markup=''}
  set innerHTML(value){this.markup=value;this.children=[...value.matchAll(/<button[^>]*data-provisioning-action="([^"]+)"[^>]*data-command-id="([^"]+)"[^>]*>/g)].map(match=>({dataset:{provisioningAction:match[1],commandId:match[2]},onclick:null}))}
  get innerHTML(){return this.markup}
  querySelectorAll(){return this.children}
 }
 const elements=new Map(),$=id=>{if(!elements.has(id))elements.set(id,new Element());return elements.get(id)};
 const calls=[];let confirmation=true;
 const plan={id:'legacy-plan',workerId:'worker',state:'Running',createdAtUtc:'2026-10-01T00:00:00Z',actions:[],currentActionId:'install'};
 const commands=[
  {id:'pending-command',request:{nodeId:'worker',capabilityId:'git',action:'Install'},status:'Pending',diagnostic:'Queued',createdAtUtc:'2026-10-01T00:01:00Z'},
  {id:'expired-command',request:{nodeId:'server',capabilityId:'git',action:'Install'},status:'Running',diagnostic:'Executing',createdAtUtc:'2026-10-01T00:02:00Z',deadlineUtc:'2000-01-01T00:00:00Z'},
  {id:'failed-command',request:{nodeId:'server',capabilityId:'github-cli',action:'Install'},status:'Failed',diagnostic:'ProcessFailed',failureDetail:{code:'ElevationDenied',description:'Non-interactive sudo authorization was denied.'},createdAtUtc:'2026-10-01T00:03:00Z'}
 ];
 const context=vm.createContext({$,authenticated:true,provisioningActionPending:false,confirm:()=>confirmation,Date,encodeURIComponent,
  esc:value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])),
  loadNodes:async()=>{},api:async(path,options)=>{calls.push({path,options});if(options)return {};if(path==='/api/v1/provisioning')return [plan];if(path==='/api/v1/provisioning/commands')return commands;throw Error('unexpected endpoint')}});
 vm.runInContext(source,context);
 return {$,calls,plan,commands,run:code=>vm.runInContext(code,context),setConfirmation:value=>confirmation=value};
}

test('history combines legacy plans and typed commands while offering only applicable lifecycle actions',async()=>{
 const s=setup();await s.run('loadProvisioning()');
 const markup=s.$('provisioning').innerHTML;
 assert.match(markup,/Legacy plan legacy-plan/);
 assert.match(markup,/Typed command pending-command/);
 assert.match(markup,/Cancel queued command/);
 assert.match(markup,/Non-interactive sudo authorization was denied/);
 assert.match(markup,/expired-command/);
 assert.match(markup,/Reconcile after node quiescence/);
 const actions=s.$('provisioning').children;
 assert.deepEqual(actions.map(button=>button.dataset.provisioningAction),['reconcile','cancel']);
});

test('queued cancellation uses the API and reconciliation requires explicit quiescence confirmation',async()=>{
 const s=setup();await s.run('loadProvisioning()');
 await s.$('provisioning').children[1].onclick();
 assert.equal(s.calls[2].path,'/api/v1/provisioning/commands/pending-command/cancel');
 assert.equal(s.calls[2].options.method,'POST');
 s.setConfirmation(false);await s.$('provisioning').children[0].onclick();
 assert.equal(s.calls.length,5); // two history reads, the cancel, and its two refresh reads
 s.setConfirmation(true);await s.$('provisioning').children[0].onclick();
 assert.equal(s.calls.at(-3).path,'/api/v1/provisioning/commands/expired-command/reconcile?nodeQuiescent=true');
 assert.equal(s.calls.at(-3).options.method,'POST');
});
