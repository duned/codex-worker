import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { build } from 'esbuild';
import { createRequire } from 'node:module';
const stubs = {
  react: `export const useState = value => globalThis.branchHooks.state(value); export const useRef = value => globalThis.branchHooks.ref(value); export const useEffect = effect => globalThis.branchHooks.effect(effect);`,
  'react/jsx-runtime': `export const Fragment = Symbol.for('react.fragment'); export const jsx = (type, props, key) => ({ type, props, key }); export const jsxs = jsx;`,
  '@untitledui/icons': `export const Trash01 = 'trash';`,
  '../../shared/api/session': `export const useRuntime = () => globalThis.branchHooks.runtime; export const useSession = () => ({generation: 1}); export const useApiRead = () => ({data: globalThis.branchHooks.workers});`,
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
const row = { branch:'done/feature/task',tip:'a'.repeat(40),decision:'eligible',reason:'integrated',completedAtUtc:'2026-01-01' };
function harness() {
  const slots=[], effects=[], calls=[]; let cursor=0, current;
  const hooks = { workers:[{workerId:'worker',availability:'online',schedulingPolicy:'Draining',activeAssignments:0,capabilities:[{type:'protocol',name:'archived-feature-maintenance-v1'}]}],
    state(initial) { const i=cursor++; if (!(i in slots)) slots[i]=initial; return [slots[i],value=>{slots[i]=typeof value==='function'?value(slots[i]):value;}]; },
    ref(initial) {const i=cursor++; return slots[i] ??= {current:initial};}, effect(effect){ effects.push(effect); },
    runtime:{async mutate(resource,path,method,request,validate){calls.push(request);current=request;return validate({request,status:'pending'});}, async read(path,signal,validate){ if(hooks.failure) throw Error('403 unauthorized');return validate({operation:{request:current,status:'succeeded',report:{outcome:'succeeded',branches:current.apply?[{...row,decision:'review',reason:'remote-tip-changed'}]:[row,{...row,branch:'complete/feature/retained',decision:'review',reason:'history-unknown'}]}}});},async reconcile(){}} };
  globalThis.branchHooks=hooks;
  return {hooks,calls,render(){cursor=0;effects.length=0;return BranchInventory({project});}};
}
function nodes(tree) { if(!tree || typeof tree!=='object') return [];return [tree,...[tree.props?.children].flat(Infinity).flatMap(nodes)]; }
function find(tree,type,match=()=>true) {return nodes(tree).find(node=>node.type===type && match(node));}
function button(tree,label) {return find(tree,'button',node=>String(node.props.children).startsWith(label));}
test('inventory renders reasons, selects eligible rows, confirms project and handles partial results',async()=>{
  const h=harness();let tree=h.render();
  find(tree,'select').props.onChange({target:{value:'worker'}});tree=h.render();
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
  const h=harness();let tree=h.render();find(tree,'select').props.onChange({target:{value:'worker'}});tree=h.render();
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
  const h=harness();let tree=h.render();find(tree,'select').props.onChange({target:{value:'worker'}});tree=h.render();
  button(tree,'branches.load').props.onPress();await new Promise(resolve=>setImmediate(resolve));tree=h.render();
  const deleteButton=nodes(tree).find(node=>node.type==='button' && node.props.children==='branches.delete{}' && !node.props.isDisabled);
  deleteButton.props.onPress();tree=h.render();const dialog=find(tree,'dialog');
  assert.ok(JSON.stringify(dialog).includes(row.branch));
  assert.ok(!JSON.stringify(dialog).includes('complete/feature/retained'));
  await dialog.props.onSubmit();assert.equal(h.calls[1].branches.length,1);
});
