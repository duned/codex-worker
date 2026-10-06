const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const fs=require('node:fs');
const path=require('node:path');
const source=fs.readFileSync(path.join(__dirname,'../../src/CodexServer/dashboard-projects.js'),'utf8');
function setup(handler,projects=[],workers=[]){
 const elements=new Map();
 function element(id){return {id,value:'',hidden:false,disabled:false,open:false,children:[],textContent:'',innerHTML:'',addEventListener(){},appendChild(child){this.children.push(child)},querySelectorAll(){return this.children},querySelector(selector){
  if(selector==='button')return this.removeButton||(this.removeButton={});
  const field=selector.match(/data-field="(.*?)"/)?.[1];this.controls||={};
  if(!this.controls[field]){const input=this.innerHTML.match(new RegExp('data-field="'+field+'".*?value="([^"]*)"'));
   this.controls[field]={value:field==='type'?this.innerHTML.match(/<option selected value="([^"]*)"/)?.[1]||'runtime':input?.[1]||''}}
  return this.controls[field];
 },remove(){},reset(){},reportValidity(){return true},showModal(){this.open=true},close(){this.open=false}}}
 const $=id=>{if(!elements.has(id))elements.set(id,element(id));return elements.get(id)};
 const calls=[];
 const context=vm.createContext({$,projects,workers,document:{createElement:tag=>element(tag)},esc:String,encodeURIComponent,
 api:async(url,options)=>{calls.push({url,options});return url==='/api/v1/workers'?workers:handler(url,options,calls)},loadProjects:async()=>{}});
 vm.runInContext(source,context);
 return {$,calls,run:code=>vm.runInContext(code,context),definition:()=>JSON.parse(vm.runInContext('JSON.stringify(projectDefinition())',context))};
}
const definition={name:'Project',repository:'team/project',defaultBranch:'trunk',description:'',requirements:[],issueReadyLabel:null,issueBlockedLabel:null,automaticDiscovery:null};
const stored={...definition,id:'project',revision:1,enabled:true};
function fill(s){for(const [id,key] of [['name','name'],['repository','repository'],['branch','defaultBranch'],['description','description']])s.$(id).value=definition[key]}
const checked={repositoryReadable:true,branchExists:true,diagnostic:'Read verified; Worker access is separate.'};
test('repository selection derives essentials from actual discovery and loads bounded next page',async()=>{
 const repo={repository:'team/example',name:'example',defaultBranch:'release',description:'Description'};
 const s=setup(async url=>({repositories:[repo],nextPage:url.endsWith('=1')?2:null}));
 s.run('newProject()');await s.run('discoverProjectRepositories()');s.$('repository-selection').value=repo.repository;s.$('repository-selection').onchange();
 assert.equal(s.$('name').value,'example');assert.equal(s.$('branch').value,'release');assert.equal(s.$('repository').value,'team/example');
 await s.$('more-repositories').onclick();assert.equal(s.calls[1].url,'/api/v1/github/repositories?page=2');
});
test('failed or unknown verification keeps save unavailable and displays actionable failure',async()=>{
 for(const response of [null,{repositoryReadable:true,branchExists:false,diagnostic:'Base branch is missing.'}]){
  const s=setup(async()=>{if(!response)throw Error('Repository unreadable.');return response});s.run('newProject()');fill(s);await s.run('reviewProject()');
  assert.equal(s.$('save-project').disabled,true);assert.equal(s.$('project-review').hidden,true);assert.match(s.$('form-error').textContent,/unreadable|missing/);
 }
});
test('create without a Worker requires review and shows preparation without enqueueing',async()=>{
 const s=setup(async(url,options)=>url.endsWith('/verify')?checked:{...JSON.parse(options.body),id:'project',revision:1});
 s.run('newProject()');fill(s);await s.run('submitProject({preventDefault(){}})');assert.equal(s.calls.length,0);
 await s.run('reviewProject()');await s.run('submitProject({preventDefault(){}})');
 assert.deepEqual(s.calls.map(c=>c.url),['/api/v1/projects/verify','/api/v1/projects','/api/v1/workers']);assert.match(s.$('project-result').textContent,/No Worker exists yet/);assert.match(s.$('project-result').textContent,/No Issues were marked ready/);
});
test('edit preserves advanced policies and lifecycle labels and submits the expected revision',async()=>{
 const project={...stored,revision:8,issueReadyLabel:'approved',issueBlockedLabel:'blocked',automaticDiscovery:{enabled:true,intervalSeconds:120}};
 const s=setup(async(url,options)=>url.endsWith('/verify')?checked:{...project,...JSON.parse(options.body).definition,revision:9},[project]);
 s.run('editProject("project")');s.$('description').value='Updated';await s.run('reviewProject()');await s.run('submitProject({preventDefault(){}})');
 const request=JSON.parse(s.calls[1].options.body);assert.equal(request.expectedRevision,8);assert.equal(request.definition.issueReadyLabel,'approved');assert.deepEqual(request.definition.automaticDiscovery,project.automaticDiscovery);
});
test('typed requirements retain versions and authentication scope',()=>{
 const s=setup(async()=>{});s.run('newProject()');fill(s);
 const values={type:'authentication',name:'github-api',version:'>=2.0',scope:'team/project'};
 s.$('requirements').children=[{querySelector:selector=>({value:values[selector.match(/"(.*?)"/)[1]]})}];
 assert.deepEqual(s.definition().requirements,[values]);
});
test('lost create response reconciles durable identity without a second POST',async()=>{
 let persisted;
 const s=setup(async(url,options)=>{if(url.endsWith('/verify'))return checked;if(options?.method==='POST'){persisted={...JSON.parse(options.body),id:'project',revision:1};throw Error('Response lost.')}return [persisted]});
 s.run('newProject()');fill(s);await s.run('reviewProject()');await s.run('submitProject({preventDefault(){}})');
 assert.equal(s.$('save-project').textContent,'Check saved definition');await s.run('submitProject({preventDefault(){}})');
 assert.equal(s.calls.filter(c=>c.url==='/api/v1/projects'&&c.options?.method==='POST').length,1);assert.equal(s.$('project-dialog').open,false);
});
test('unapplied save retains draft and requires review before retry; unavailable reconciliation does not repeat mutation',async()=>{
 let unavailable=true;
 const s=setup(async(url,options)=>{if(url.endsWith('/verify'))return checked;if(options)throw Error('Unavailable');if(unavailable)throw Error('Still unavailable');return []});
 s.run('newProject()');fill(s);await s.run('reviewProject()');await s.run('submitProject({preventDefault(){}})');await s.run('submitProject({preventDefault(){}})');
 assert.equal(s.calls.filter(c=>c.options?.method==='POST'&&c.url==='/api/v1/projects').length,1);
 unavailable=false;await s.run('submitProject({preventDefault(){}})');assert.equal(s.$('name').value,'Project');assert.equal(s.$('save-project').disabled,true);assert.match(s.$('form-error').textContent,/not applied/);
});
test('stale revision preserves draft and requires explicit loading of current definition',async()=>{
 const current={...stored,description:'Other change',revision:4};
 const s=setup(async(url,options)=>url.endsWith('/verify')?checked:options?Promise.reject(Error('Revision stale')):[current],[stored]);
 s.run('editProject("project")');s.$('description').value='My draft';await s.run('reviewProject()');await s.run('submitProject({preventDefault(){}})');await s.run('submitProject({preventDefault(){}})');
 assert.equal(s.$('description').value,'My draft');assert.equal(s.$('reload-project').hidden,false);s.$('reload-project').onclick();assert.equal(s.$('description').value,'Other change');assert.equal(s.$('revision').value,4);
});
test('verification completed after user edits cannot approve a different draft',async()=>{
 let complete;const s=setup(()=>new Promise(resolve=>{complete=resolve}));s.run('newProject()');fill(s);
 const pending=s.run('reviewProject()');s.$('branch').value='other';complete(checked);await pending;assert.equal(s.$('save-project').disabled,true);
});

test('editing configured typed requirements preserves custom descriptors and normalizes saved versions for reconciliation',async()=>{
 const requirement={type:'custom-tool',name:'builder',version:'>=010.00',scope:null};
 const project={...stored,requirements:[requirement]};
 const s=setup(async(url,options)=>url.endsWith('/verify')?checked:{...project,revision:2,requirements:[{...requirement,version:'>=10.0'}]},[project]);
 s.run('editProject("project")');assert.deepEqual(s.definition().requirements,[requirement]);
 await s.run('reviewProject()');assert.match(s.$('project-review-definition').textContent,/custom-tool · builder >=010.00/);
 assert.equal(s.run('sameProjectDefinition({...projectDraft,requirements:[{type:"custom-tool",name:"builder",version:">=10.0"}]},projectDefinition())'),true);
});
test('lost edit response recovers only the expected next revision without repeating PUT',async()=>{
 let persisted;
 const s=setup(async(url,options)=>{if(url.endsWith('/verify'))return checked;if(options?.method==='PUT'){persisted={...stored,...JSON.parse(options.body).definition,revision:2};throw Error('Response lost')}return [persisted]},[stored]);
 s.run('editProject("project")');s.$('description').value='Changed';await s.run('reviewProject()');await s.run('submitProject({preventDefault(){}})');await s.run('submitProject({preventDefault(){}})');
 assert.equal(s.calls.filter(c=>c.options?.method==='PUT').length,1);assert.equal(s.$('project-dialog').open,false);
});
test('successful save with unavailable Worker inventory reports unknown readiness',async()=>{
 const s=setup(async(url,options)=>url.endsWith('/verify')?checked:{...stored,...JSON.parse(options.body)},[],null);
 s.run('newProject()');fill(s);await s.run('reviewProject()');await s.run('submitProject({preventDefault(){}})');
 assert.match(s.$('project-result').textContent,/Worker inventory is unavailable/);assert.doesNotMatch(s.$('project-result').textContent,/No Worker exists/);
});
