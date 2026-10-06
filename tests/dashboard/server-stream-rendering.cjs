// Input consists exclusively of events produced by ServerApplication in the .NET regression test.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const {setup,flush}=require('./server-stream-harness.cjs');
(async()=>{
 const frames=JSON.parse(fs.readFileSync(0,'utf8'));
 assert.equal(frames.length,2);
 const s=setup({realRenderer:true});s.connect();await flush();
 const connection=s.respond(s.requests[0]);await flush();
 for(const frame of frames){
  connection.emitFrame(frame);await flush();
  assert.match(s.$('live').textContent,/Live · updated/);
  assert.match(s.$('workers').innerHTML,/populated worker/);
  assert.match(s.$('workers').innerHTML,/policy enabled/);
  assert.equal(s.$('capacity-total').textContent,4);
  assert.equal(s.$('capacity-used').textContent,1);
  assert.equal(s.$('capacity-free').textContent,'Available 3');
  assert.equal(s.$('worker-health').textContent,'1 online · 0 stale · 0 offline');
  assert.equal(s.active(),1);assert.equal(s.requests.length,1);assert.equal(s.timers.size,0);
 }
 assert.equal(connection.reader.released,0);await s.stop();assert.equal(connection.reader.released,1);
})().catch(()=>{console.error('Server event failed dashboard rendering or stream lifecycle assertions.');process.exitCode=1});
