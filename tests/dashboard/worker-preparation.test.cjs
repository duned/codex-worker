const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const {readDashboard,dashboardDependencies}=require('./server-dashboard-source.cjs');
const html=readDashboard();
const source=html.slice(html.indexOf('function workerPreparation'),html.indexOf('async function loadWorkerAdministration'));
function setup(){
 const elements=new Map(),$=id=>{if(!elements.has(id))elements.set(id,{innerHTML:'',textContent:''});return elements.get(id)};
 const project={id:'project',name:'Project',repository:'owner/repo',revision:1,enabled:true,requirements:[{type:'runtime',name:'custom',version:'2'}]};
 const worker={workerId:'worker',displayName:'Worker',availability:'online',lifecycleState:'running',capabilities:[{type:'authentication',name:'github-api',scope:'owner/repo'},{type:'authentication',name:'git-repository',scope:'owner/repo'}],capabilityInventory:['git','github-cli','codex-cli'].map(id=>({id,detectedAtUtc:new Date().toISOString()}))};
 const diagnostics={capabilityObservationsCurrent:true,configurationSynchronization:'synchronized',aiAgentReady:false,gitHubReady:true,canActivate:false,activationBlockingReasons:['Codex execution preflight required'],reasons:[],projects:[{projectId:'project',observationStatus:'worker-reported-current-revision',workerReportedRevision:1,materializationState:'not-materialized',missingRequirements:['requires custom 2'],isEligible:false}]};
 const calls=[];
 const context=vm.createContext({URLSearchParams,encodeURIComponent,...dashboardDependencies(),$,projects:[project],esc:value=>String(value??'').replaceAll('<','&lt;'),api:async path=>{calls.push(path);return path.endsWith('/diagnostics')?diagnostics:worker}});
 vm.runInContext(source,context);
 let step='project';context.navigation={...context.navigation,current:()=>({view:'workers',id:'worker',params:new URLSearchParams({step})})};
 return {$,context,worker,project,diagnostics,calls,render:(value=step)=>{step=value;return context.workerPreparation(worker,diagnostics)}};
}
test('separates preparation evidence, requirements and explicit activation without submitting operations',async()=>{
 const s=setup();const markup=['registration','preparation','project','activation'].map(step=>s.render(step)).join('');
 for(const label of ['Registration and connectivity','Tools / configuration','Codex authentication / execution preflight','Worker GitHub authentication','Repository access','requires custom 2','Activation blocked','No separate binding','no test push'])assert.ok(markup.includes(label),label);
 assert.match(markup,/current managed revision reported/);
 assert.match(markup,/Checkout: not-materialized/);
 assert.match(markup,/\/workers\/worker\?step=preparation&project=project/);
 await s.context.loadWorker('worker');await s.context.loadWorker('worker');
 assert.ok(s.calls.every(path=>!path.includes('provisioning')));
});
test('stale and unavailable evidence never reports project preparation complete',()=>{
 const s=setup();s.diagnostics.projects[0].isEligible=true;
 s.diagnostics.capabilityObservationsCurrent=false;
 assert.doesNotMatch(s.render(),/Capabilities match this project|Worker-reported evidence/);
 s.diagnostics.capabilityObservationsCurrent=true;s.diagnostics.projects[0].observationStatus='stale-revision';
 assert.match(s.render(),/awaiting current Worker-managed snapshot evidence/);
 assert.doesNotMatch(s.render(),/Capabilities match this project/);
 s.worker.availability='stale';assert.doesNotMatch(s.render(),/Worker-reported evidence/);
});
test('a registered Worker with no project offers the central project journey',()=>{
 const s=setup();s.context.projects=[];
 assert.match(s.render(),/No central project exists/);assert.match(s.render(),/href="\/projects"/);
});
test('unavailable detail is actionable',async()=>{
 const s=setup();s.context.api=async()=>{throw Error('Unavailable')};await s.context.loadWorker('worker');
 assert.match(s.$('worker-detail').innerHTML,/Preparation evidence unavailable/);
});
