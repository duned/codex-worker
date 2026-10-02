// Deterministic dashboard checks for project-scoped GitHub Issue administration.
const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const fs=require('node:fs');
const html=fs.readFileSync('src/CodexServer/dashboard.html','utf8');
const start=html.indexOf('function populateGithubProjects()');
const end=html.indexOf('async function loadOverview()',start);
const source=html.slice(start,end);

function datasetFromTag(tag){
 const dataset={};
 for(const [,name,value] of tag.matchAll(/data-([\w-]+)="([^"]*)"/g))
  dataset[name.replace(/-([a-z])/g,(_,letter)=>letter.toUpperCase())]=value;
 return dataset;
}

function setup(apiHandler){
 class Element{
  constructor(id){this.id=id;this.dataset={};this.value='';this.children=[];this.hidden=false;this.textContent='';this.markup=''}
  set innerHTML(value){this.markup=value;this.children=[...value.matchAll(/<button\b[^>]*>/g)].map(([tag])=>({dataset:datasetFromTag(tag),onclick:null}))}
  get innerHTML(){return this.markup}
  querySelectorAll(selector){const name=selector.match(/\[data-([\w-]+)\]/)?.[1];if(!name)return this.children;const key=name.replace(/-([a-z])/g,(_,letter)=>letter.toUpperCase());return this.children.filter(button=>key in button.dataset)}
  querySelector(selector){return this.querySelectorAll(selector)[0]||null}
 }
 const elements=new Map(),$=id=>{if(!elements.has(id))elements.set(id,new Element(id));return elements.get(id)};
 const calls=[],confirmMessages=[],answers=[];
 const projects=[{id:'project-id',name:'Project',repository:'team/project',issueReadyLabel:'ready',issueBlockedLabel:'blocked'}];
 const context=vm.createContext({$,projects,managementToken:'management',confirm:message=>{confirmMessages.push(message);return true},prompt:()=>answers.shift(),
  URLSearchParams,encodeURIComponent,Number,JSON,console,
  esc:value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])),
  api:async(path,options)=>{calls.push({path,options});return apiHandler(path,options,calls)}});
 vm.runInContext(source,context);
 $('github-project').value='project-id';
 return {$,calls,confirmMessages,answers,run:code=>vm.runInContext(code,context)};
}

const issue={number:55,title:'Current title',body:'Current body',state:'OPEN',url:'https://github.com/team/project/issues/55',
 labels:['ready'],blockedBy:[],isEligible:true,eligibilityReasons:[]};

test('dashboard creates an Issue only after showing the validated project-scoped preview',async()=>{
 const s=setup(async(path,options)=>{
  if(path.endsWith('/github/issues')&&options?.method==='POST'){
   const request=JSON.parse(options.body);
   return request.previewOnly?{operation:'create',previewOnly:true,changed:true,repository:'team/project',title:request.title,body:request.body}:
    {operation:'create',previewOnly:false,changed:true,issueNumber:77,url:'https://github.com/team/project/issues/77'};
  }
  if(path.includes('/github/issues?'))return [];
  if(path.endsWith('/github/issues/77'))return {...issue,number:77,title:'New Issue',body:'Description',url:'https://github.com/team/project/issues/77'};
  throw Error('unexpected endpoint '+path);
 });
 s.answers.push('New Issue','Description');
 await s.run('createGithubIssue()');
 const writes=s.calls.filter(call=>call.options?.method==='POST');
 assert.equal(writes.length,2);
 assert.equal(JSON.parse(writes[0].options.body).previewOnly,true);
 assert.equal(JSON.parse(writes[1].options.body).previewOnly,false);
 assert.match(s.confirmMessages[0],/team\/project/);
 assert.match(s.confirmMessages[0],/Description/);
 assert.equal(s.$('github-message').textContent,'Created Issue #77 in team/project.');
});

test('title/body, configured labels, and blocked-by changes each preview before write',async()=>{
 const s=setup(async(path,options)=>{
  if(path.endsWith('/github/issues/55')&&!options)return issue;
  if(path.endsWith('/github/issues/55')&&options?.method==='PATCH'){const request=JSON.parse(options.body);return {changed:true,previewOnly:request.previewOnly,title:request.title??issue.title,body:request.body??issue.body}}
  if(path.endsWith('/labels/configured')&&options?.method==='PUT')return {changed:true,previewOnly:JSON.parse(options.body).previewOnly};
  if(path.endsWith('/dependencies/blocked-by')&&options?.method==='PUT')return {changed:true,previewOnly:JSON.parse(options.body).previewOnly};
  throw Error('unexpected endpoint '+path);
 });
 await s.run('showGithubIssue(55)');
 s.answers.push('Edited title','Edited body');
 await s.run('editGithubIssue(55,"Current title","Current body")');
 await s.run('setGithubLabel(55,"ready",false)');
 await s.run('setGithubDependency(55,56,true)');
 const mutations=s.calls.filter(call=>['PATCH','PUT'].includes(call.options?.method));
 assert.deepEqual(mutations.map(call=>call.options.method),['PATCH','PATCH','PUT','PUT','PUT','PUT']);
 assert.deepEqual(mutations.map(call=>JSON.parse(call.options.body).previewOnly),[true,false,true,false,true,false]);
 assert.match(s.confirmMessages[0],/Edited body/);
 assert.match(s.confirmMessages[1],/remove configured label 'ready'/);
 assert.match(s.confirmMessages[2],/Issue #55 blocked by Issue #56/);
});

test('dashboard uses project-configured eligibility labels and exposes no lifecycle label controls',()=>{
 assert.match(html,/id="github-create"/);
 assert.match(source,/project\?\.issueReadyLabel/);
 assert.match(source,/project\?\.issueBlockedLabel/);
 assert.doesNotMatch(source,/data-issue-label="(?:working|done|failed|blocked-worker)/i);
});
