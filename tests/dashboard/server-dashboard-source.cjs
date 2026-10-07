const fs=require('node:fs');
const path=require('node:path');
const root=path.join(__dirname,'../../src/CodexServer');
function readDashboard(){
 let admin=fs.readFileSync(path.join(root,'dashboard-admin.js'),'utf8');
 for(const module of ['session','nodes','stream','executions','issues','onboarding','projects'])admin=admin.replace('/* dashboard-'+module+' */',fs.readFileSync(path.join(root,'dashboard-'+module+'.js'),'utf8'));
 let html=fs.readFileSync(path.join(root,'dashboard.html'),'utf8').replace('<!-- worker-poc -->','');
 return html.replace('<!-- dashboard-scripts -->','<script>'+fs.readFileSync(path.join(root,'dashboard-navigation.js'),'utf8')+admin+'</script>');
}
const navigationStub={preservePresentation(element,render){render()},updateRows(element,markup){element.innerHTML=markup},observe(){},capture:()=>0,isCurrent:()=>true,start(){},stop(){},current:()=>({view:'home',id:'',params:new URLSearchParams()}),navigate(){}};
function dashboardDependencies(){return {window:{},navigation:navigationStub,renderProjectContext(){},selectContextNode(){},stopDashboardPolling(){},cancelDashboardReads(){},resetResources(){},invalidateWorkers(){},administrationRequests:[],sessionGeneration:0,workerObservationGeneration:0}}
module.exports={readDashboard,navigationStub,dashboardDependencies};
