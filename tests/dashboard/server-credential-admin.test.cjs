// Deterministic checks for credential metadata projections and dashboard administration calls.
const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const fs=require('node:fs');
const html=fs.readFileSync('src/CodexServer/dashboard.html','utf8');
const start=html.indexOf('async function loadCredentials');
const end=html.indexOf('async function loadProjects',start);
const formStart=html.indexOf('async function submitCredential');
const formEnd=html.indexOf('async function deleteProject',formStart);
const source=html.slice(start,end)+html.slice(formStart,formEnd);

function setup(){
 class Element{
  constructor(){this.value='';this.hidden=false;this.textContent='';this.markup='';this.shown=false;this.closed=false}
  set innerHTML(value){this.markup=value}
  get innerHTML(){return this.markup}
  showModal(){this.shown=true}
  close(){this.closed=true}
 }
 const elements=new Map(),$=id=>{if(!elements.has(id))elements.set(id,new Element());return elements.get(id)};
 const calls=[];let confirmed=true;
 const credential={id:'credential-1',provider:'<provider>',type:'api-token',status:'Ready',version:2,
  assignedWorkerId:null,updatedAtUtc:'2026-10-01T00:00:00Z',secretReference:'credential:1',secret:'should-never-render'};
 const worker={workerId:'worker-1',displayName:'Worker <unsafe>'};
 const context=vm.createContext({$,managementToken:'management',workers:[],Date,encodeURIComponent,confirm:()=>confirmed,
  document:{querySelectorAll:()=>[]},
  esc:value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])),
  api:async(path,options)=>{calls.push({path,options});if(options)return path.includes('/revoke')?{status:'Revoked'}:credential;
   if(path==='/api/v1/credentials')return [credential];if(path==='/api/v1/workers')return [worker];
   if(path==='/api/v1/credentials/credential-1')return credential;throw Error('unexpected endpoint '+path)}});
 vm.runInContext(source,context);
 return {$,calls,credential,context,run:code=>vm.runInContext(code,context),setConfirmation:value=>confirmed=value};
}

test('credential list escapes metadata and never renders a secret payload',async()=>{
 const s=setup();await s.run('loadCredentials()');
 const markup=s.$('credentials').innerHTML;
 assert.match(markup,/&lt;provider&gt;/);
 assert.match(markup,/Worker &lt;unsafe&gt;/);
 assert.match(markup,/Replace secret/);
 assert.doesNotMatch(markup,/should-never-render/);
 assert.deepEqual(s.calls.map(call=>call.path),['/api/v1/credentials','/api/v1/workers']);
});

test('show metadata uses the show API but projects only the secret-free metadata contract',async()=>{
 const s=setup();await s.run("showCredential('credential-1')");
 assert.equal(s.calls[0].path,'/api/v1/credentials/credential-1');
 assert.match(s.$('credential-details').innerHTML,/Secret reference/);
 assert.doesNotMatch(s.$('credential-details').innerHTML,/should-never-render/);
 assert.equal(s.$('credential-details-dialog').shown,true);
});

test('create submits the transient secret to the existing encrypted-store API then clears the field',async()=>{
 const s=setup();s.$('credential-provider').value='github';s.$('credential-type').value='api-token';s.$('credential-secret').value='one-time-secret';
 await s.run("submitCredential({preventDefault(){}})");
 assert.equal(s.calls[0].path,'/api/v1/credentials');
 assert.equal(s.calls[0].options.method,'POST');
 assert.deepEqual(JSON.parse(s.calls[0].options.body),{provider:'github',type:'api-token',secret:{value:'one-time-secret'}});
 assert.equal(s.$('credential-secret').value,'');
 assert.doesNotMatch(s.$('credentials').innerHTML,/one-time-secret/);
});

test('assignment and revocation use the existing management APIs',async()=>{
 const s=setup();s.$('credential-worker-credential-1').value='worker-1';
 await s.run("assignCredential('credential-1')");
 assert.equal(s.calls[0].path,'/api/v1/credentials/credential-1/assignment');
 assert.equal(s.calls[0].options.method,'PUT');
 assert.deepEqual(JSON.parse(s.calls[0].options.body),{workerId:'worker-1'});
 const before=s.calls.length;await s.run("revokeCredential('credential-1')");
 assert.equal(s.calls[before].path,'/api/v1/credentials/credential-1/revoke');
 assert.equal(s.calls[before].options.method,'POST');
 s.setConfirmation(false);const after=s.calls.length;await s.run("revokeCredential('credential-1')");
 assert.equal(s.calls.length,after);
});
