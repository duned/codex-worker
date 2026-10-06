// Exercise cookie-authenticated stream ownership with coordinated fetch/read/timers.
const {test}=require('node:test');
const assert=require('node:assert/strict');
const {setup,flush}=require('./server-stream-harness.cjs');

test('repeated session connections replace the reader before opening a single authenticated request',async()=>{
 const s=setup();s.connect();await flush();
 const first=s.respond(s.requests[0],{holdCancellation:true});await flush();
 s.connect('replacement-token',true);s.connect('latest-session');await flush();
 assert.equal(s.requests.length,1);assert.equal(first.reader.cancelled,1);assert.equal(s.requests[0].options.signal.aborted,true);
 first.finishCancellation();await flush();
 assert.equal(first.reader.released,1);assert.equal(s.requests.length,2);assert.equal(s.maximum(),1);
 assert.equal(s.requests[1].path,'/api/v1/events/stream');assert.equal(s.requests[1].options.credentials,'same-origin');assert.equal(s.requests[1].options.headers,undefined);assert.equal(s.$('token').value,'');
 const latest=s.respond(s.requests[1]);await flush();latest.emit([{workerId:'current'}]);await flush();
 assert.equal(s.updates.length,1);assert.match(s.$('live').textContent,/updated/);await s.stop();assert.equal(latest.reader.released,1);
});

test('clearing the session disconnects during read and rapid reconnect cannot revive the obsolete reader',async()=>{
 const s=setup();s.connect();await flush();const first=s.respond(s.requests[0],{staleRead:true});await flush();
 s.connect('');s.connect('next-token');await flush();assert.equal(s.requests.length,1);
 first.emit([{workerId:'obsolete'}]);await flush();
 assert.equal(s.updates.length,0);assert.equal(s.requests.length,2);assert.equal(first.reader.released,1);assert.equal(s.timers.size,0);assert.equal(s.maximum(),1);
 await s.stop();
});

test('a stale fetch response releases its body without publishing status or starting a retry',async()=>{
 const s=setup();s.connect();await flush();s.requests[0].ignoreAbort=true;
 s.connect('replacement');await flush();assert.equal(s.requests.length,1);
 const stale=s.respond(s.requests[0]);await flush();
 assert.equal(stale.body.cancelled,1);assert.equal(stale.reader.cancelled,0);assert.equal(s.requests.length,2);assert.equal(s.updates.length,0);assert.equal(s.$('live').textContent,'Live · disconnected');assert.equal(s.timers.size,0);
 await s.stop();
});

test('failed requests use one cancellable retry wait and session replacement cancels that wait',async()=>{
 const s=setup();s.connect();await flush();const failed=s.respond(s.requests[0],{ok:false});await flush();
 assert.equal(failed.body.cancelled,1);assert.equal(s.active(),0);assert.equal(s.timers.size,1);
 const obsoleteTimer=s.timers.values().next().value;
 s.connect('next');await flush();assert.equal(s.timers.size,0);assert.equal(s.requests.length,2);
 obsoleteTimer();await flush();assert.equal(s.requests.length,2);
 s.requests[1].close();s.requests[1].response.reject(Error('unavailable'));await flush();assert.equal(s.timers.size,1);
 s.retry();await flush();assert.equal(s.requests.length,3);assert.equal(s.maximum(),1);
 s.requests[2].close();s.requests[2].response.reject(Error('unavailable'));await flush();
 await s.stop();obsoleteTimer();await flush();assert.equal(s.requests.length,3);
});

test('disconnect during retry cancels its timer and reconnect leaves obsolete callbacks inert',async()=>{
 const s=setup();s.connect();await flush();s.requests[0].close();s.requests[0].response.reject(Error('outage'));await flush();
 const obsoleteTimer=s.timers.values().next().value;
 s.connect('');await flush();assert.equal(s.timers.size,0);assert.equal(s.active(),0);assert.equal(s.$('live').textContent,'Live · disconnected');
 obsoleteTimer();await flush();assert.equal(s.requests.length,1);
 s.connect('replacement');await flush();obsoleteTimer();await flush();assert.equal(s.requests.length,2);assert.equal(s.maximum(),1);await s.stop();
});

test('Server outage and clean EOF release readers, then reconnect and resume Worker events',async()=>{
 const s=setup();s.connect();await flush();const first=s.respond(s.requests[0]);await flush();first.fail();await flush();
 assert.equal(s.$('live').textContent,'Live · connection interrupted; retrying');
 assert.equal(first.reader.cancelled,1);assert.equal(first.reader.released,1);assert.equal(s.timers.size,1);
 s.retry();await flush();const restarted=s.respond(s.requests[1]);await flush();restarted.emit([{workerId:'after-restart'}]);await flush();
 assert.equal(s.updates[0][0].workerId,'after-restart');assert.match(s.$('live').textContent,/updated/);
 restarted.end();await flush();assert.equal(restarted.reader.released,1);assert.equal(s.timers.size,1);assert.equal(s.requests.length,2);
 assert.equal(s.$('live').textContent,'Live · connection interrupted; retrying');
 s.retry();await flush();assert.equal(s.requests.length,3);assert.equal(s.maximum(),1);await s.stop();
});

test('page teardown cancels a read and back-forward cache restoration opens one current stream',async()=>{
 const s=setup();s.connect();await flush();const first=s.respond(s.requests[0]);await flush();await s.stop();assert.equal(first.reader.released,1);
 s.events.pageshow({persisted:false});await flush();assert.equal(s.requests.length,1);
 s.events.pageshow({persisted:true});await flush();assert.equal(s.requests.length,2);assert.equal(s.maximum(),1);await s.stop();
});

test('independent tabs retain independent stream owners',async()=>{
 const a=setup(),b=setup();a.connect();b.connect();await flush();assert.equal(a.active(),1);assert.equal(b.active(),1);
 await a.stop();assert.equal(b.active(),1);await b.stop();
});

test('rendering and JSON failures stop retries and report safe processing diagnostics',async()=>{
 const incompatibleWorker={WorkerId:'private-body',DisplayName:'private-name',WorkerVersion:'1.0',SchedulingPolicy:'Enabled',Availability:'online',MaximumCapacity:4,ActiveExecutions:1,AvailableCapacity:3,LastSeenAtUtc:'2026-01-01T00:00:00Z'};
 for(const frame of ['event: workers\ndata: '+JSON.stringify([incompatibleWorker])+'\n\n','data: private-invalid-json\n\n']){
  const s=setup({realRenderer:true});s.connect('private-token');await flush();
  const connection=s.respond(s.requests[0]);await flush();connection.emitFrame(frame);await flush();
  assert.match(s.$('live').textContent,/Worker event processing failed/);
  assert.doesNotMatch(s.$('live').textContent,/private|toLowerCase/);
  assert.equal(connection.reader.released,1);assert.equal(s.timers.size,0);assert.equal(s.requests.length,1);assert.equal(s.active(),0);
  s.connect('corrected');await flush();assert.equal(s.requests.length,2);assert.equal(s.maximum(),1);await s.stop();
 }
});

test('authentication rejection stops retries and deliberate cancellation reports disconnection',async()=>{
 for(const status of [401,403]){
  const s=setup();s.connect();await flush();const rejected=s.respond(s.requests[0],{ok:false,status});await flush();
  assert.equal(s.$('live').textContent,'Live · signed out');
  assert.equal(rejected.body.cancelled,1);assert.equal(s.timers.size,0);assert.equal(s.active(),0);
  await s.stop();assert.equal(s.$('live').textContent,'Live · disconnected');
 }
});
