// Optional browser acceptance harness. Install Playwright separately; it is not
// a Server build/runtime dependency. All HTTP observations are deterministic.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {chromium}=require('playwright');
const {readDashboard}=require('./server-dashboard-source.cjs');
const {props}=require('./worker-poc-fixtures.cjs');
const frontend=path.resolve(__dirname,'../../src/CodexServer');
const output=path.resolve(process.argv[2]||'/tmp/codex-worker-poc-review');
const assets=path.join(frontend,'obj/worker-poc');

(async()=>{
 fs.mkdirSync(output,{recursive:true});
 const browser=await chromium.launch({headless:true});
 try{
  const page=await browser.newPage();
  const errors=[],requests=[],externalRequests=[];
  page.on('pageerror',error=>errors.push(error.message));
  page.on('request',request=>{if(new URL(request.url()).origin!=='https://worker.test')externalRequests.push(request.url())});
  // A fixed clock gives reproducible elapsed time without waiting for polling.
  await page.clock.setFixedTime(props.now);
  // Route fulfillment has a finite body. Keep the fixture SSE reader open like
  // the real endpoint; closing it correctly invalidates Worker observations.
  await page.addInitScript(()=>{
   const fetch=window.fetch.bind(window);
   window.fetch=async(input,options)=>{
    const response=await fetch(input,options);
    if(input!=='/api/v1/events/stream'||!response.ok)return response;
    const bytes=new Uint8Array(await response.arrayBuffer());
    return new Response(new ReadableStream({start(controller){
     controller.enqueue(bytes);
     options.signal.addEventListener('abort',()=>controller.close(),{once:true});
    }}),{headers:{'Content-Type':'text/event-stream'}});
   };
  });
  let signedIn=true,unavailable=false,stale=false;
  const worker=()=>({...props.observations[0],capabilities:[],activeProjects:[],authenticationCredentialStatus:'active',
   ...(stale?{availability:'stale',displayName:'Worker '+ 'long-name-'.repeat(15)}:{})});
  await page.route('https://worker.test/**',async route=>{
   const request=route.request(),url=new URL(request.url()),p=url.pathname;
   requests.push({path:p,method:request.method()});
   if(p.startsWith('/dashboard-assets/'))return route.fulfill({contentType:p.endsWith('.js')?'text/javascript':'text/css',body:fs.readFileSync(path.join(assets,p.endsWith('.js')?'poc.js':'poc.css'))});
   if(p==='/workers/worker-a/poc'||p==='/workers/worker-a')return route.fulfill({contentType:'text/html',body:readDashboard({workerPoc:p.endsWith('/poc')})});
   if(p==='/api/v1/administration/session'){
    if(request.method()==='DELETE'){signedIn=false;return route.fulfill({status:204})}
    if(request.method()==='POST'){assert.equal(request.headers().authorization,'Bearer fixture-management-token');signedIn=true;}
    return route.fulfill({status:signedIn?200:401,json:signedIn?{csrfToken:'fixture-csrf',expiresAtUtc:'2026-01-01T01:00:00Z'}:{}});
   }
   if(!signedIn)return route.fulfill({status:401,json:{}});
   if(unavailable&&['/api/v1/nodes','/api/v1/executions','/api/v1/workers/worker-a/diagnostics'].includes(p))return route.fulfill({status:503,json:{}});
   if(p==='/api/v1/events/stream')return route.fulfill({contentType:'text/event-stream',body:'data: '+JSON.stringify([worker()])+'\n\n'});
   const data={
    '/api/status':{state:'running'},'/api/version':{version:'fixture'},'/health':{status:'healthy'},
    '/api/v1/workers':[worker()],'/api/v1/workers/worker-a':worker(),
    '/api/v1/workers/worker-a/diagnostics':{...props.diagnostics,projects:[],reasons:[],canActivate:false,activationBlockingReasons:['Fixture readiness is informational.']},
    '/api/v1/workers/worker-a/credential-access':{status:'Authorized'},
    '/api/v1/projects':props.projects,'/api/v1/nodes':props.nodes.map(node=>({...node,observationsStale:stale})),
    '/api/v1/nodes/worker-a/commands':[],
    '/api/v1/nodes/server/github-connection':{commands:[],provisioningEnabled:false,elevationAllowed:false},
    '/api/v1/executions':props.executions,'/api/v1/provisioning':[],'/api/v1/provisioning/commands':[]
   };
   assert.ok(Object.hasOwn(data,p),'Unexpected request: '+p);
   assert.equal(request.method(),'GET','Fixture must not submit administration actions');
   return route.fulfill({json:data[p]});
  });
  for(const width of [1280,375]){
   await page.setViewportSize({width,height:900});
   await page.goto('https://worker.test/workers/worker-a/poc?step=preparation');
   await page.getByRole('heading',{name:'Build Worker North'}).waitFor();
   await page.getByRole('link',{name:'Issue #27'}).waitFor();
   const recentIssue=page.getByRole('link',{name:'Issue #26'});
   await recentIssue.waitFor();
   assert.equal(await recentIssue.getAttribute('href'),'https://github.com/owner/repo/issues/26');
   assert.equal(await page.locator('#view-title').isVisible(),false);
   assert.equal(await page.locator('#node-context').isVisible(),false);
   assert.equal(await page.locator('#worker-registration-guidance').isVisible(),false);
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
   const main=await page.locator('.poc-main').boundingBox(),rail=await page.locator('.poc-control-rail').boundingBox();
   assert.ok(width>1000?rail.x>main.x+main.width:rail.y>main.y+main.height);
   const issue=page.getByRole('link',{name:'Issue #27'});
   assert.equal(await issue.getAttribute('href'),'https://github.com/owner/repo/issues/27');
   await issue.focus();
   await page.evaluate(data=>window.codexWorkerPoc.update({executions:[...data.executions]}),props);
   await page.waitForFunction(()=>document.activeElement?.textContent==='Issue #27');
   const summary=page.getByText('Diagnostic context',{exact:true}).first();
   await summary.click();await summary.focus();
   await page.evaluate(data=>window.codexWorkerPoc.update({nodes:[...data.nodes]}),props);
   assert.equal(await summary.evaluate(el=>el===document.activeElement&&el.parentElement.open),true);
   assert.equal(await page.locator('#poc-legacy-owner').isVisible(),false);
   if(width===375){
    await page.getByRole('button',{name:'Expand navigation menu'}).focus();
    await page.keyboard.press('Enter');
    const drawer=page.getByRole('dialog',{name:'Main navigation'});
    await drawer.waitFor();
    assert.equal(await drawer.getByRole('link',{name:'Workers',exact:true}).getAttribute('aria-current'),'page');
    await page.keyboard.press('Escape');
    await drawer.waitFor({state:'hidden'});
    await page.waitForFunction(()=>document.activeElement?.getAttribute('aria-label')==='Expand navigation menu');
   }else{
    assert.equal(await page.getByRole('navigation',{name:'Main navigation'}).getByRole('link',{name:'Workers',exact:true}).getAttribute('aria-current'),'page');
   }
   await page.evaluate(()=>scrollTo(0,0));
   await page.screenshot({path:path.join(output,`worker-detail-${width}.png`),fullPage:true});
   await page.reload();await page.getByRole('link',{name:'Issue #27'}).waitFor();
  }
  await page.evaluate(data=>window.codexWorkerPoc.update({
   nodes:data.nodes.map(node=>({...node,capabilities:node.capabilities.map(capability=>({...capability,state:{...capability.state,operation:{state:'Failed',action:'checkauthentication',diagnosticCode:'authentication-required'}}}))})),
   nodeCommands:[{id:'fixture-pending',request:{nodeId:'worker-a',capabilityId:'codex-cli',action:'CheckAuthentication'},status:'Pending',createdAtUtc:'2026-01-01T00:01:00Z'}]
  }),props);
  await page.getByText('Pending',{exact:true}).waitFor();
  await page.locator('#worker-poc').getByText('Failed',{exact:true}).waitFor();
  stale=true;await page.reload();await page.getByText('Stale',{exact:true}).waitFor();
  assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
  unavailable=true;await page.reload();await page.getByText('Execution history unavailable. Refresh to try again.').waitFor();
  await page.getByRole('button',{name:'Expand navigation menu'}).click();
  await page.getByRole('button',{name:'Sign out',exact:true}).click();
  await page.getByRole('heading',{name:'Administration sign in'}).waitFor();
  assert.equal(await page.locator('#administration-content').isVisible(),false);
  stale=false;unavailable=false;
  await page.locator('#worker-poc').getByLabel('Server management token').fill('fixture-management-token');
  await page.getByRole('button',{name:'Sign in',exact:true}).click();
  await page.getByRole('link',{name:'Issue #27'}).waitFor();
  assert.equal(await page.locator('#token').inputValue(),'');
  assert.equal(requests.filter(request=>request.path==='/api/v1/administration/session'&&request.method==='POST').length,1);
  await page.evaluate(data=>window.codexWorkerPoc.update({observations:[{...data.observations[0],activeExecutions:0,activeAssignments:0}],executions:[]}),props);
  await page.getByText('No recent terminal executions for this Worker in this view.').waitFor();
  await page.evaluate(()=>window.codexWorkerPoc.update({observations:[]}));
  await page.getByText('Worker unavailable or deleted.',{exact:false}).waitFor();
  await page.reload();await page.getByRole('link',{name:'Issue #27'}).waitFor();
  await page.getByRole('link',{name:'Open Worker detail and administration'}).click();
  await page.waitForURL('https://worker.test/workers/worker-a');
  assert.equal(await page.locator('#worker-poc').count(),0);
  assert.equal(await page.locator('#worker-registration-guidance').isVisible(),true);
  assert.equal(await page.locator('script[src*="worker-poc"],link[href*="worker-poc"]').count(),0);
  assert.ok(requests.every(request=>request.method==='GET'||request.path==='/api/v1/administration/session'));
  assert.deepEqual(errors,[]);
  assert.deepEqual(externalRequests,[],'The PoC must load only local assets and shared API observations.');
  console.log('Desktop/narrow shell, rail, reload, stale/unavailable reads, focus, logout and ordinary route exit passed. Screenshots: '+output);
 }finally{await browser.close()}
})().catch(error=>{console.error(error);process.exitCode=1});
