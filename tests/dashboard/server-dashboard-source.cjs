const fs=require('node:fs');
const path=require('node:path');
const root=path.join(__dirname,'../../src/CodexServer');
function readDashboard({workerPoc=false}={}){
 let admin=fs.readFileSync(path.join(root,'dashboard-admin.js'),'utf8');
 for(const module of ['session','nodes','stream','executions','issues','onboarding','projects'])admin=admin.replace('/* dashboard-'+module+' */',fs.readFileSync(path.join(root,'dashboard-'+module+'.js'),'utf8'));
 let html=fs.readFileSync(path.join(root,'dashboard.html'),'utf8').replace('<!-- worker-poc -->','');
 if(workerPoc)return '<!DOCTYPE html><html lang="en" class="dark-mode"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Codex Server · Worker</title><link rel="stylesheet" href="/dashboard-assets/worker-poc.css"></head><body><div id="worker-poc"></div><script src="/dashboard-assets/worker-poc.js"></script></body></html>';
 return html.replace('<!-- dashboard-scripts -->','<script>'+fs.readFileSync(path.join(root,'dashboard-navigation.js'),'utf8')+admin+'</script>');
}
const navigationStub={preservePresentation(element,render){render()},updateRows(element,markup){element.innerHTML=markup},observe(){},capture:()=>0,isCurrent:()=>true,start(){},stop(){},current:()=>({view:'home',id:'',params:new URLSearchParams()}),navigate(){}};
function dashboardDependencies(){return {window:{},navigation:navigationStub,renderProjectContext(){},selectContextNode(){},stopDashboardPolling(){},cancelDashboardReads(){},resetResources(){},invalidateWorkers(){},administrationRequests:[],sessionGeneration:0,workerObservationGeneration:0}}
module.exports={readDashboard,navigationStub,dashboardDependencies};
