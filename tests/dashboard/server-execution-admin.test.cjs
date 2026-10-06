// Deterministic dashboard checks for bounded execution administration and safe operator actions.
const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const fs=require('node:fs');
const html=fs.readFileSync('src/CodexServer/dashboard.html','utf8');
const start=html.indexOf('let executionOffset=0;');
const end=html.indexOf('async function loadOverview()',start);
const source=html.slice(start,end);

function setup(){
 class Element{
  constructor(id){this.id=id;this.dataset={};this.value='';this.children=[];this.markup=''}
  set innerHTML(value){this.markup=value;this.children=[...value.matchAll(/<button[^>]*data-execution-(detail|cancel|reconcile)="([^"]+)"[^>]*>/g)].map(match=>({dataset:{[`execution${match[1][0].toUpperCase()}${match[1].slice(1)}`]:match[2]},onclick:null}))}
  get innerHTML(){return this.markup}
  querySelectorAll(selector){const action=selector.match(/data-execution-([a-z]+)/)?.[1];const key=action?'execution'+action[0].toUpperCase()+action.slice(1):null;return key?this.children.filter(button=>key in button.dataset):this.children}
 }
 const elements=new Map(),$=id=>{if(!elements.has(id))elements.set(id,new Element(id));return elements.get(id)};
 const calls=[];let promptAnswers=[];
 const items=[
  {id:'queued-id',projectId:'project-id',state:'Queued',workReference:{type:'github-issue',id:'7'},createdAtUtc:'2026-10-01T00:00:00Z',attemptNumber:1},
  {id:'uncertain-id',projectId:'project-id',state:'Failed',recoveryState:'LeaseExpiredUncertain',workReference:{type:'github-issue',id:'8'},createdAtUtc:'2026-10-01T00:00:00Z',attemptNumber:1}
 ];
 const detail={...items[0],lease:{generation:1,state:'Released'},retryOfExecutionId:null};
 const context=vm.createContext({$,authenticated:true,confirm:()=>true,prompt:()=>promptAnswers.shift(),Date,URLSearchParams,encodeURIComponent,JSON,
  esc:value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])),
  api:async(path,options)=>{calls.push({path,options});if(options)return {execution:detail,retry:null};if(path.startsWith('/api/v1/executions?'))return items;if(path.startsWith('/api/v1/executions/'))return detail;throw Error('unexpected endpoint '+path)}});
 vm.runInContext(source,context);
 return {$,calls,items,run:code=>vm.runInContext(code,context),answer:values=>{promptAnswers=values}};
}

test('execution list applies bounded filters and exposes details and only valid queue/recovery actions',async()=>{
 const s=setup();s.$('execution-project-filter').value='project-id';s.$('execution-state-filter').value='Queued';s.$('execution-issue-filter').value='7';
 await s.run('loadExecutions()');
 assert.match(s.calls[0].path,/limit=50/);assert.match(s.calls[0].path,/projectId=project-id/);assert.match(s.calls[0].path,/state=Queued/);assert.match(s.calls[0].path,/workId=7/);
 assert.match(s.$('executions').innerHTML,/Cancel queued/);assert.match(s.$('executions').innerHTML,/Reconcile uncertain/);
 assert.deepEqual(s.$('executions').children.map(button=>Object.keys(button.dataset)[0]),['executionDetail','executionCancel','executionDetail','executionReconcile']);
 await s.$('executions').children[0].onclick();assert.equal(s.calls[1].path,'/api/v1/executions/queued-id');assert.match(s.$('execution-detail').innerHTML,/lease/);
});

test('queued cancellation and uncertain reconciliation send explicit operator evidence',async()=>{
 const s=setup();await s.run('loadExecutions()');
 await s.$('executions').children[1].onclick();assert.equal(s.calls[1].path,'/api/v1/executions/queued-id/cancel');assert.equal(s.calls[1].options.method,'POST');
 s.answer(['NotIntegrated','Verified absent from authoritative base branch']);
 await s.$('executions').children[3].onclick();
 const reconciliation=s.calls.find(call=>call.path.endsWith('/uncertain-id/reconcile'));
 assert.equal(reconciliation.options.method,'POST');assert.deepEqual(JSON.parse(reconciliation.options.body),{disposition:'NotIntegrated',evidence:'Verified absent from authoritative base branch'});
});
