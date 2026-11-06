import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { build } from 'esbuild';
import { createRequire } from 'node:module';
const stubs = {
  react: `export const useState = value => globalThis.branchHooks.state(value); export const useRef = value => globalThis.branchHooks.ref(value); export const useEffect = effect => globalThis.branchHooks.effect(effect);`,
  'react/jsx-runtime': `export const Fragment = Symbol.for('react.fragment'); export const jsx = (type, props, key) => ({ type, props, key }); export const jsxs = jsx;`,
  '@untitledui/icons': `export const Trash01 = 'trash';`,
  '../../shared/api/session': `export const useRuntime = () => globalThis.branchHooks.runtime; export const useSession = () => ({generation: 1}); export const useApiRead = () => ({data: globalThis.branchHooks.inventory, hasError: !!globalThis.branchHooks.targetError, error: globalThis.branchHooks.targetError, loading: !!globalThis.branchHooks.loading, fetching: false, refetch: async () => {}});`,
  '../../shared/api/client': `export class ApiError extends Error { constructor(message, status) { super(message); this.status = status; } }`,
  '../../shared/api/validation': `export const workers = value => value;`,
  '../../shared/i18n': `export const useLanguage = () => {}; export const t = (key, values={}) => key + JSON.stringify(values);`,
  '../../shared/Dialogs': `export const ActionDialog = 'dialog';`,
  '../../shared/Presentation': `export const Notice = 'notice'; export const StatusBadge = 'badge';`,
  '../../untitled/components/base/buttons/button': `export const Button = 'button';`,
  '../../untitled/components/application/table/table': `export const TableCard = {Root:'card'};`,
  '../../model': `export const timestamp = value => value;`,
};
const bundle = await build({ stdin: { contents: `export * from './src/features/projects/ArchivedBranches';`, resolveDir: new URL('..', import.meta.url).pathname }, bundle: true, platform: 'node', format: 'cjs', write: false, plugins: [{name:'branch-hooks', setup(build) {
  build.onResolve({ filter: /.*/ }, args => args.path in stubs ? {path:args.path, namespace:'stub'} : undefined);
  build.onLoad({filter:/.*/, namespace:'stub'}, args => ({contents:stubs[args.path]}));
  build.onLoad({filter:/ArchivedBranches.tsx$/}, args => ({contents:readFileSync(args.path,'utf8')+'\nexport { BranchInventory };',loader:'tsx'}));
}}] });
const module = {exports:{}};
new Function('module','exports','require',bundle.outputFiles[0].text)(module,module.exports,createRequire(import.meta.url));
const { BranchInventory, ArchivedBranches } = module.exports;
const project = { id:'one', revision:1, name:'First project', repository:'owner/one', enabled:true };
const managedId = 'a'.repeat(32), standaloneId = 'obs_' + 'b'.repeat(32);
const target = { workerId: managedId, displayName: 'Managed one', mode: 'managed', eligible: true, reason: null, canApply: true, applyReason: null };
const row = { branch:'done/feature/task',tip:'a'.repeat(40),decision:'eligible',reason:'integrated',completedAtUtc:'2026-01-01' };
function harness() {
  const slots=[], effects=[], calls=[]; let cursor=0, current;
  const hooks = { inventory: {projectId: project.id, revision: project.revision, project: project.name, repository: project.repository, targets:[{...target}]},
    state(initial) { const i=cursor++; if (!(i in slots)) slots[i]=initial; return [slots[i],value=>{slots[i]=typeof value==='function'?value(slots[i]):value;}]; },
    ref(initial) {const i=cursor++; return slots[i] ??= {current:initial};}, effect(effect){ effects.push(effect); },
    runtime:{locked(){return false;}, async mutate(resource,path,method,request,validate,check){await check?.(new AbortController().signal);calls.push(request);current=request;return validate({request,status:'pending'});}, async read(path,signal,validate){ if(path.endsWith('/branch-maintenance-targets')) {if(hooks.targetError) throw Error(hooks.targetError);return validate(hooks.inventory);} if(hooks.failure) throw Error('403 unauthorized');return validate({operation:{request:current,status:'succeeded',report:{outcome:'succeeded',branches:current.apply?[{...row,decision:'review',reason:'remote-tip-changed'}]:hooks.rows??[row,{...row,branch:'complete/feature/retained',decision:'review',reason:'history-unknown'}]}}});},async reconcile(){}} };
  globalThis.branchHooks=hooks;
  return {hooks,calls,render(){cursor=0;effects.length=0;return BranchInventory({project});}};
}
function nodes(tree) { if(!tree || typeof tree!=='object') return [];return [tree,...[tree.props?.children].flat(Infinity).flatMap(nodes)]; }
function find(tree,type,match=()=>true) {return nodes(tree).find(node=>node.type===type && match(node));}
function button(tree,label) {return find(tree,'button',node=>String(node.props.children).startsWith(label));}
test('inventory renders reasons, selects eligible rows, confirms project and handles partial results',async()=>{
  const h=harness();let tree=h.render();
  find(tree,'select').props.onChange({target:{value:managedId}});tree=h.render();
  button(tree,'branches.load').props.onPress();await new Promise(resolve=>setImmediate(resolve));tree=h.render();
  assert.ok(JSON.stringify(tree).includes('history-unknown'));
  const inputs=nodes(tree).filter(n=>n.type==='input' && n.props.type==='checkbox');
  assert.equal(inputs[1].props.disabled,true);
  inputs[0].props.onChange({target:{checked:true}});tree=h.render();
  button(tree,'branches.deleteSelected').props.onPress();tree=h.render();
  const dialog=find(tree,'dialog');assert.ok(dialog.props.description.includes(project.name));assert.ok(JSON.stringify(dialog).includes(row.branch));
  await dialog.props.onSubmit();tree=h.render();
  assert.deepEqual(h.calls[1].branches,[{branch:row.branch,expectedTip:row.tip}]);
  assert.equal(h.calls[1].project,project.name);
  assert.equal(h.calls.length,3);assert.ok(JSON.stringify(tree).includes('remote-tip-changed'));
});
test('API errors clear inventory and retain operation for checking without another deletion',async()=>{
  const h=harness();let tree=h.render();find(tree,'select').props.onChange({target:{value:managedId}});tree=h.render();
  h.hooks.failure=true;button(tree,'branches.load').props.onPress();await new Promise(resolve=>setImmediate(resolve));tree=h.render();
  assert.ok(JSON.stringify(tree).includes('403 unauthorized'));
  assert.equal(find(tree,'select').props.disabled,true);
  assert.ok(button(tree,'branches.resume'));
});
test('project and session identity remount inventory, selections and confirmation',()=>{
  harness();const first=ArchivedBranches({project});const next=ArchivedBranches({project:{...project,id:'two'}});
  assert.notEqual(first.key,next.key);
});
test('single-row delete confirms exactly one branch',async()=>{
  const h=harness();let tree=h.render();find(tree,'select').props.onChange({target:{value:managedId}});tree=h.render();
  button(tree,'branches.load').props.onPress();await new Promise(resolve=>setImmediate(resolve));tree=h.render();
  const deleteButton=nodes(tree).find(node=>node.type==='button' && node.props.children==='branches.delete{}' && !node.props.isDisabled);
  deleteButton.props.onPress();tree=h.render();const dialog=find(tree,'dialog');
  assert.ok(JSON.stringify(dialog).includes(row.branch));
  assert.ok(!JSON.stringify(dialog).includes('complete/feature/retained'));
  await dialog.props.onSubmit();assert.equal(h.calls[1].branches.length,1);
});
test('age preview requests candidates before confirmation and submits only eligible observed refs',async()=>{
  const h=harness();let tree=h.render();find(tree,'select').props.onChange({target:{value:managedId}});tree=h.render();
  find(tree,'input',n=>n.props.type==='number').props.onChange({target:{value:'60'}});tree=h.render();
  assert.equal(h.calls.length,0);
  button(tree,'branches.previewAge').props.onPress();await new Promise(resolve=>setImmediate(resolve));tree=h.render();
  assert.equal(h.calls.length,1);assert.equal(h.calls[0].apply,false);assert.equal(h.calls[0].olderThanDays,60);
  const dialog=find(tree,'dialog');assert.ok(dialog.props.description.includes(project.name));assert.ok(dialog.props.description.includes('60'));
  assert.ok(JSON.stringify(dialog).includes('history-unknown'));
  await dialog.props.onSubmit();tree=h.render();
  assert.equal(h.calls[1].apply,true);assert.equal(h.calls[1].olderThanDays,60);
  assert.deepEqual(h.calls[1].branches,[{branch:row.branch,expectedTip:row.tip}]);
  assert.equal(h.calls.length,3);assert.ok(JSON.stringify(tree).includes('remote-tip-changed'));
});
test('invalid age thresholds cannot request preview; changing age clears previous candidates',async()=>{
  const h=harness();let tree=h.render();find(tree,'select').props.onChange({target:{value:managedId}});
  for(const value of ['', '0', '-1', '1.5', '36501']) {
    tree=h.render();find(tree,'input',n=>n.props.type==='number').props.onChange({target:{value}});tree=h.render();
    assert.equal(button(tree,'branches.previewAge').props.isDisabled,true);
    button(tree,'branches.previewAge').props.onPress();await new Promise(resolve=>setImmediate(resolve));assert.equal(h.calls.length,0);
    assert.equal(find(tree,'input',n=>n.props.type==='number').props['aria-invalid'],true);
  }
  find(tree,'input',n=>n.props.type==='number').props.onChange({target:{value:'1'}});tree=h.render();
  button(tree,'branches.previewAge').props.onPress();await new Promise(resolve=>setImmediate(resolve));tree=h.render();assert.ok(find(tree,'dialog'));
  find(tree,'dialog').props.onClose();tree=h.render();
  find(tree,'input',n=>n.props.type==='number').props.onChange({target:{value:'2'}});tree=h.render();
  assert.equal(find(tree,'dialog'),undefined);assert.equal(nodes(tree).filter(n=>n.type==='input'&&n.props.type==='checkbox').length,0);
});
test('preview with only retained branches cannot delete and explains why',async()=>{
  const h=harness();h.hooks.rows=[{...row,decision:'review',reason:'Unknown completion age',completedAtUtc:null}];
  let tree=h.render();find(tree,'select').props.onChange({target:{value:managedId}});tree=h.render();
  button(tree,'branches.previewAge').props.onPress();await new Promise(resolve=>setImmediate(resolve));tree=h.render();
  const dialog=find(tree,'dialog');assert.equal(dialog.props.disabled,true);
  assert.ok(JSON.stringify(dialog).includes('Unknown completion age'));assert.ok(JSON.stringify(dialog).includes('branches.noEligible'));
  await dialog.props.onSubmit();assert.equal(h.calls.length,1);
});

test('picker labels both ownership modes and disables read-only standalone observers with the Server reason', () => {
  const h = harness();
  h.hooks.inventory.targets.push({ ...target, workerId: standaloneId, displayName: 'Standalone one', mode: 'standalone-observer' },
    { ...target, workerId: 'obs_' + 'c'.repeat(32), displayName: 'Read only', mode: 'standalone-observer', eligible: false, reason: 'observer-maintenance-not-authorized', canApply: false, applyReason: 'observer-maintenance-not-authorized' });
  const options = nodes(h.render()).filter(node => node.type === 'option');
  assert.ok(options.some(node => JSON.stringify(node).includes('branches.managed')));
  const standalone = options.find(node => node.props.value === standaloneId);
  assert.equal(standalone.props.disabled, false);
  assert.match(JSON.stringify(standalone), /Standalone one.*branches.standalone/);
  assert.ok(options.some(node => node.props.disabled && JSON.stringify(node).includes('observer-maintenance-not-authorized')));
});
test('standalone selection and repository persist from preview through confirmation and partial results', async () => {
  const h = harness();
  h.hooks.inventory.targets.push({ ...target, workerId: standaloneId, displayName: 'Standalone one', mode: 'standalone-observer' });
  let tree = h.render(); find(tree, 'select').props.onChange({ target: { value: standaloneId } }); tree = h.render();
  button(tree, 'branches.previewAge').props.onPress(); await new Promise(resolve => setImmediate(resolve)); tree = h.render();
  const dialog = find(tree, 'dialog');
  assert.equal(find(tree, 'select').props.disabled, true);
  assert.match(JSON.stringify(dialog), /Standalone one/); assert.match(JSON.stringify(dialog), /owner\/one/);
  h.hooks.inventory.targets.reverse(); // A refresh must not select the first candidate.
  await dialog.props.onSubmit(); tree = h.render();
  assert.equal(find(tree, 'select').props.value, standaloneId);
  for (const request of h.calls) {
    assert.equal(request.workerId, standaloneId); assert.equal(request.repository, project.repository);
    assert.equal(request.projectId, project.id); assert.equal(request.projectRevision, project.revision);
  }
  assert.equal(h.calls[1].apply, true);
});
test('capability loss while confirmation is open disables deletion and retains the selected Worker', async () => {
  const h = harness(); let tree = h.render(); find(tree, 'select').props.onChange({ target: { value: managedId } }); tree = h.render();
  button(tree, 'branches.previewAge').props.onPress(); await new Promise(resolve => setImmediate(resolve));
  h.hooks.inventory.targets[0] = { ...target, eligible: false, canApply: false, reason: 'worker-branch-protocol-unavailable', applyReason: 'worker-branch-protocol-unavailable' };
  tree = h.render(); assert.equal(find(tree, 'dialog').props.disabled, true);
  assert.equal(find(tree, 'select').props.value, managedId);
  assert.match(JSON.stringify(tree), /worker-branch-protocol-unavailable/);
  await find(tree, 'dialog').props.onSubmit(); assert.equal(h.calls.length, 1);
});
test('fresh preflight rejects a stale binding without dispatch or automatic replacement', async () => {
  const h = harness(); let tree = h.render(); find(tree, 'select').props.onChange({ target: { value: managedId } }); tree = h.render();
  button(tree, 'branches.previewAge').props.onPress(); await new Promise(resolve => setImmediate(resolve)); tree = h.render();
  const dialog = find(tree, 'dialog');
  h.hooks.inventory = { ...h.hooks.inventory, repository: 'owner/changed' };
  await dialog.props.onSubmit(); tree = h.render();
  assert.equal(h.calls.length, 1); assert.equal(find(tree, 'select').props.value, managedId);
  assert.equal(find(tree, 'select').props.disabled, false); assert.match(JSON.stringify(tree), /Invalid branch maintenance targets/);
});
test('loading, empty eligible set, read failures and vanished selection are distinct and recoverable', () => {
  const h = harness(); h.hooks.loading = true; h.hooks.inventory = undefined;
  let tree = h.render(); assert.match(JSON.stringify(tree), /branches.loadingTargets/); assert.doesNotMatch(JSON.stringify(tree), /branches.noWorkers/);
  h.hooks.loading = false; h.hooks.inventory = { targets: [] };
  tree = h.render(); assert.match(JSON.stringify(tree), /branches.noWorkers/);
  h.hooks.targetError = 'server unavailable'; tree = h.render(); assert.match(JSON.stringify(tree), /server unavailable/);
  assert.equal(button(tree, 'branches.refreshTargets').props.isDisabled, false);
  h.hooks.targetError = undefined; h.hooks.inventory.targets = [{ ...target }];
  tree = h.render(); find(tree, 'select').props.onChange({ target: { value: managedId } });
  h.hooks.inventory.targets = []; tree = h.render();
  assert.equal(find(tree, 'select').props.value, managedId); assert.match(JSON.stringify(tree), /branches.staleTarget/);
  assert.equal(button(tree, 'branches.load').props.isDisabled, true);
});

test('an unavailable retained target can still be inspected without dispatching to another Worker', async () => {
  const h = harness(); let tree = h.render(); find(tree, 'select').props.onChange({ target: { value: managedId } }); tree = h.render();
  h.hooks.failure = true; button(tree, 'branches.load').props.onPress(); await new Promise(resolve => setImmediate(resolve));
  h.hooks.inventory.targets = [{ ...target, eligible: false, reason: 'worker-offline', canApply: false, applyReason: 'worker-offline' },
    { ...target, workerId: standaloneId, mode: 'standalone-observer' }];
  tree = h.render(); assert.equal(button(tree, 'branches.resume').props.isDisabled, false);
  h.hooks.failure = false; button(tree, 'branches.resume').props.onPress(); await new Promise(resolve => setImmediate(resolve)); tree = h.render();
  assert.equal(h.calls.length, 1); assert.equal(find(tree, 'select').props.value, managedId);
});
