const {test}=require('node:test');
const assert=require('node:assert/strict');
const model=import('../../src/CodexServer/worker-poc/src/model.js');
test('Worker execution projection excludes other Workers and preserves reported state/stage',async()=>{
 const {workerExecutions}=await model;
 const items=[{id:'other',assignedWorkerId:'b',createdAtUtc:'2026-01-03'}, {id:'done',assignedWorkerId:'a',createdAtUtc:'2026-01-01',state:'Failed'}, {id:'live',assignedWorkerId:'a',createdAtUtc:'2026-01-02',state:'Running',currentStage:'Validation'}];
 assert.deepEqual(workerExecutions(items,'a').map(x=>x.id),['live','done']);
 assert.equal(workerExecutions(items,'a')[0].currentStage,'Validation');
 assert.deepEqual(workerExecutions(null,'a'),[]);
 assert.equal(items[0].id,'other');
});
test('Issue links use canonical references or safe repository identities',async()=>{
 const {issueLink}=await model;
 const work={type:'github-issue',id:'27',url:'https://github.com/original/repo/issues/27'};
 assert.equal(issueLink(work,'new/repo'),work.url);
 assert.equal(issueLink({...work,url:null},'owner/repo'),'https://github.com/owner/repo/issues/27');
 for(const url of ['javascript:alert(1)','https://evil.example/a/b/issues/27','https://github.com/a/b/issues/28','https://github.com/a/b/issues/27?token=secret','https://user:secret@github.com/a/b/issues/27'])assert.equal(issueLink({...work,url},null),null);
 for(const repository of ['../repo','owner/..','owner/repo/extra','owner/repo?query','https://github.com/owner/repo'])assert.equal(issueLink({...work,url:null},repository),null);
 assert.equal(issueLink({type:'task',id:'27'},'owner/repo'),null);
 assert.equal(issueLink({...work,id:'0'},'owner/repo'),null);
});
test('Timing uses reported duration or valid real timestamps without guessing terminal completion',async()=>{
 const {duration,timestamp}=await model;
 const now=Date.parse('2026-01-01T00:02:30Z');
 assert.equal(duration({state:'Running',startedAtUtc:'2026-01-01T00:00:00Z'},now),'2m 30s');
 assert.equal(duration({durationMilliseconds:61000},now),'1m 1s');
 assert.equal(duration({state:'Completed',startedAtUtc:'2026-01-01T00:00:00Z'},now),'Duration unavailable');
 assert.equal(duration({state:'Failed',startedAtUtc:'2026-01-01T00:00:00Z',completedAtUtc:'2026-01-01T00:01:00Z'},now),'1m 0s');
 assert.equal(duration({startedAtUtc:'2027-01-01'},now),'Duration unavailable');
 assert.equal(timestamp('invalid'),'Not reported');
});
test('Status colors preserve stale, unknown, unavailable and not applicable textual distinctions',async()=>{
 const {statusColor}=await model;
 for(const state of ['Unknown','Unavailable','Not applicable'])assert.equal(statusColor(state),'gray');
 assert.equal(statusColor('Stale'),'warning');assert.equal(statusColor('Failed'),'error');assert.equal(statusColor('ready'),'success');
 for(const state of ['update-failed','restart-failed','capability-regression','configuration-incompatible'])assert.equal(statusColor(state),'error');
 for(const state of ['starting','drain-requested','updating','restarting','reconnecting','not-synchronized'])assert.equal(statusColor(state),'warning');
 assert.equal(statusColor('active'),'success');
});
