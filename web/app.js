const $ = s => document.querySelector(s);
const names = {codex:'OpenAI Codex',claude:'Anthropic Claude',antigravity:'Google Antigravity',copilot:'GitHub Copilot'};
const errors = {sign_in_required:'Session expired or rejected. Reconnect through the official client.',local_session_unavailable:'Local session unavailable.',independent_sign_in_needed:'Official sign-in needed.',rate_limited:'Provider rate limit. Waiting to retry.',identity_changed:'Account changed. Waiting for discovery.',quota_not_reported:'No quota reported.',copilot_setup_required:'Run Setup-Copilot.ps1 to verify the existing GitHub CLI account.',copilot_auth_source_unsupported:'This reader requires the existing github.com GitHub CLI sign-in.',copilot_read_timeout:'Copilot quota read timed out.'};
let latest=null, editing=false, draft=[], removed=[], busy=false, dragged=null, alerts=false, notice='';
const expanded=new Set(), previous=new Map(), lastNotification=new Map();
try{$('#mode').value=localStorage.getItem('quotaDisplayMode')==='used'?'used':'remaining'}catch{}
function el(tag,cls,text){const n=document.createElement(tag);if(cls)n.className=cls;if(text!==undefined)n.textContent=text;return n}
function button(text,label,action,cls=''){const b=el('button',cls,text);b.type='button';b.setAttribute('aria-label',label);b.onclick=action;return b}
function date(value){return value?new Date(value).toLocaleString([], {month:'short',day:'numeric',hour:'2-digit',minute:'2-digit'}):'Not reported'}
function age(seconds){if(seconds===null)return 'Never read';if(seconds<60)return 'Just now';const m=Math.floor(seconds/60);return m<60?m+'m ago':Math.floor(m/60)+'h '+m%60+'m ago'}
function resetText(value){if(!value)return 'Reset not reported';const seconds=(new Date(value)-Date.now())/1000;if(seconds<=0)return 'Reset due · awaiting provider';const minutes=Math.ceil(seconds/60), hours=Math.floor(minutes/60), days=Math.floor(hours/24);const relative=days?`${days}d ${hours%24}h`:hours?`${hours}h ${minutes%60}m`:`${minutes}m`;return relative+' · '+date(value)}
function message(text,error=false){notice=text;$('#message').textContent=text;$('#message').className=error?'error':''}
// PACE_CALC_START
function quotaPace(b,live=true,now=Date.now()){
 const fixed=b?.windowSeconds===18000||b?.windowSeconds===604800;
 if(!fixed)return null;
 if(!live)return {available:false,reason:'quota data is not live'};
 if(b.available===false)return {available:false,reason:'quota is currently unavailable'};
 if(!Number.isFinite(b.remaining)||b.remaining<0||b.remaining>100)return {available:false,reason:'quota remaining is not reported'};
 if(!b.resetsAt)return {available:false,reason:'reset time is not reported'};
 const reset=new Date(b.resetsAt).getTime(),duration=b.windowSeconds*1000,untilReset=reset-now;
 if(!Number.isFinite(reset)||untilReset<=0)return {available:false,reason:'reset time has passed'};
 if(untilReset>duration)return {available:false,reason:'reset time is outside this window'};
 const timeRemaining=Math.max(0,Math.min(100,untilReset/duration*100));
 const difference=b.remaining-timeRemaining;
 return {available:true,timeRemaining,difference,direction:difference>=0?'under':'over'};
}
// PACE_CALC_END
function metric(b,live=true){
 if(!b)return el('div','unknown','Not reported');
 const box=el('div','metric');
 if(b.unlimited||b.remaining==null){
  box.append(el('span','unknown',b.unlimited?'Unlimited':'Unknown'));
 }else{
  const used=$('#mode').value==='used',value=used?100-b.remaining:b.remaining;
  const p=el('span','percent',Math.round(value)+'%');p.title=value.toFixed(1)+'% '+(used?'used':'remaining');
  const meter=el('div','meter'),bar=el('div','bar'+(b.remaining<=10?' low':''));bar.style.width=value+'%';
  meter.setAttribute('role','meter');meter.setAttribute('aria-label',b.label+' '+(used?'used':'remaining'));
  meter.setAttribute('aria-valuenow',value);meter.setAttribute('aria-valuemin',0);meter.setAttribute('aria-valuemax',100);
  meter.append(bar);box.append(p,meter);
  const pace=quotaPace(b,live);
  if(pace){
   if(pace.available){
    const timeMeter=el('div','time-meter'),timeBar=el('div','time-bar');timeBar.style.width=pace.timeRemaining+'%';timeMeter.append(timeBar);
    const points=Math.abs(pace.difference),rounded=points<1?points.toFixed(1):Math.round(points).toString();
    const paceText=`${rounded} percentage points ${pace.direction} pace`;
    timeMeter.title=`${pace.timeRemaining.toFixed(1)}% of the quota window remains. ${b.remaining.toFixed(1)}% of quota remains. ${paceText}.`;
    timeMeter.setAttribute('role','img');timeMeter.setAttribute('aria-label',timeMeter.title);
    box.append(timeMeter,el('span','pace '+pace.direction,paceText));
   }else{
    const unavailable=el('span','pace unavailable','Pace unavailable · '+pace.reason);unavailable.setAttribute('aria-label','Pace unavailable because '+pace.reason+'.');box.append(unavailable);
   }
  }
 }
 const reset=el('span','reset',resetText(b.resetsAt));
 reset.title=b.resetsAt?new Date(b.resetsAt).toString():'Provider did not report a reset time';box.append(reset);
 if(b.amountRemaining!=null){
  box.append(el('span','reset',new Intl.NumberFormat(undefined,{maximumFractionDigits:2}).format(b.amountRemaining)+' / '+(b.entitlement??'?')+' '+b.unit+' remaining'));
 }
 if(!b.unlimited&&b.available===false)box.append(el('span','reset','Currently unavailable'));
 return box;
}
function groupsFor(a){const groups=[...a.groups];if(a.provider==='antigravity')for(const [label,re] of [['Gemini',/gemini/i],['Claude / GPT',/claude|gpt/i]])if(!groups.some(g=>re.test(g.label)))groups.push({label,buckets:[]});if(!groups.length)groups.push({label:'Subscription',buckets:[]});return groups}
function focusMove(id,direction){const card=[...document.querySelectorAll('.account')].find(c=>c.dataset.id===id);const wanted=card?.querySelector(`[data-move="${direction}"]`);(wanted&&!wanted.disabled?wanted:card?.querySelector('[data-move]:not(:disabled)'))?.focus()}
function move(id,direction){if(busy)return;const i=draft.findIndex(a=>a.id===id), j=i+direction;if(j<0||j>=draft.length)return;[draft[i],draft[j]]=[draft[j],draft[i]];renderAccounts();focusMove(id,direction)}
function renderAccounts(){const area=$('#accounts');area.classList.toggle('editing',editing);area.replaceChildren();const accounts=editing?draft:latest.accounts;const display=$('#mode').value==='used'?'used':'remaining';
accounts.forEach((a,index)=>{const card=el('article','account');card.dataset.id=a.id;card.setAttribute('aria-label',names[a.provider]+' '+a.label);const head=el('div','account-head');
if(editing){const handle=el('span','drag-handle','↕');handle.title='Drag to reorder';handle.setAttribute('aria-hidden','true');head.append(handle);setupDrag(handle,card,a.id)}
head.append(el('span','provider',names[a.provider]),el('span','email',a.label),el('span','state '+a.status,a.status),el('span','age',age(a.ageSeconds)));
if(editing){const controls=el('div','edit-controls');for(const [symbol,direction,label] of [['↑',-1,'Move up'],['↓',1,'Move down']]){const b=button(symbol,label+' '+names[a.provider]+' '+a.label,()=>move(a.id,direction));b.dataset.move=direction;b.disabled=busy||(direction<0?index===0:index===accounts.length-1);controls.append(b)}controls.append(button('Remove','Remove '+names[a.provider]+' account '+a.label,()=>{if(busy)return;removed.push(a.id);draft=draft.filter(x=>x.id!==a.id);renderAccounts()},'remove'));controls.lastChild.disabled=busy;head.append(controls)}else{const b=button('Details','Details for '+names[a.provider]+' '+a.label,()=>{expanded.has(a.id)?expanded.delete(a.id):expanded.add(a.id);renderAccounts()},'detail-toggle');b.setAttribute('aria-expanded',expanded.has(a.id));head.append(b);if(a.error&&['sign_in_required','local_session_unavailable','independent_sign_in_needed'].includes(a.error)){const action=button(a.provider==='antigravity'?'Open app':'Reconnect','Reconnect '+names[a.provider]+' '+a.label,()=>connect(a.provider,a.id),'reconnect');action.disabled=connectionActive()||!latest.connections?.clients[a.provider];action.title='Uses the official client session';head.append(action)}}card.append(head);
const groups=groupsFor(a),monthly=a.provider==='copilot';
const columns=monthly?[{label:'Monthly '+display,test:b=>b.windowKind==='monthly'}]:[{label:'5-hour '+display,test:b=>b.windowSeconds===18000},{label:'Weekly '+display,test:b=>b.windowSeconds===604800}];
const other=groups.some(g=>g.buckets.some(b=>!columns.some(c=>c.test(b))));
if(other){const known=[...columns];columns.push({label:'Other limits',test:b=>!known.some(c=>c.test(b))})}
const table=el('table','quota-table');table.setAttribute('aria-label',names[a.provider]+' quota '+display);const tr=el('tr');for(const label of ['Quota group',...columns.map(c=>c.label)]){const th=el('th','',label);th.scope='col';tr.append(th)}const thead=el('thead');thead.append(tr);table.append(thead);const tbody=el('tbody');for(const g of groups){const row=el('tr');row.append(el('td','group-label',g.label));for(const column of columns){const cell=el('td'),matches=g.buckets.filter(column.test);if(!matches.length)cell.append(metric(null));for(const b of matches){if(matches.length>1)cell.append(el('div','unknown',b.label));cell.append(metric(b,a.status==='live'))}row.append(cell)}tbody.append(row)}table.append(tbody);card.append(table);
if(monthly&&groups[0]?.plan)card.append(el('div','details',groups[0].plan+' · Models '+(groups[0].models?.join(', ')||'not reported')));
if(a.error)card.append(el('div','warning',(errors[a.error]||'Reader unavailable.')+(a.nextAttempt>Date.now()/1000?' Retry eligible '+date(a.nextAttempt*1000):'')));
if(expanded.has(a.id)&&!editing){const details=el('div','details');details.append(el('div','',a.source),el('div','',a.identityStatus),el('div','','Last successful read '+(a.lastSuccess?new Date(a.lastSuccess*1000).toLocaleString():'Never')),el('div','','Next eligible read '+date(a.nextAttempt? a.nextAttempt*1000:null)));card.append(details)}area.append(card)});
if(!accounts.length)area.append(el('div','empty',editing?'All accounts marked for removal. Save to apply, or Cancel to keep them.':'No accounts displayed. Restore removed accounts below.'));
$('#edit-count').textContent=editing?`${draft.length} accounts · ${removed.length} marked for removal · Changes not saved`:'';
}
function render(){if(!latest)return;$('#summary').textContent=`${latest.accounts.filter(a=>a.status==='live').length}/${latest.accounts.length} live`;$('#updated').textContent='Display '+new Date(latest.now*1000).toLocaleTimeString();$('#restore').hidden=!latest.hasRemovedAccounts||editing;$('#edit').hidden=editing;$('#save').hidden=!editing;$('#cancel').hidden=!editing;$('#edit-help').hidden=!editing;$('#refresh').disabled=editing||busy;$('#save').disabled=busy;$('#cancel').disabled=busy;$('#mode').disabled=busy;$('#edit').disabled=busy;$('#message').textContent=notice||(latest.storageError?'Encrypted cache unavailable.':latest.refreshing?'Reading quotas…':'Auto-check 5 min. Refresh respects provider backoff.');renderAccounts();renderConnections()}
function notify(data){for(const a of data.accounts){if(a.status!=='live')continue;for(const g of a.groups)for(const b of g.buckets){if(b.remaining===null)continue;const key=a.id+'/'+g.id+'/'+b.id,old=previous.get(key);let text='';if(b.remaining<=10&&(!old||old.remaining>10))text='10% or less remaining.';if(old&&old.resetsAt&&new Date(old.resetsAt)<Date.now()&&b.remaining>old.remaining&&b.resetsAt!==old.resetsAt)text='Provider reports replenished quota.';previous.set(key,b);if(alerts&&text&&Date.now()-(lastNotification.get(key)||0)>3600000){new Notification(names[a.provider],{body:g.label+' · '+b.label+'\n'+text});lastNotification.set(key,Date.now())}}}}
async function update(){try{const r=await fetch('/api/status');if(!r.ok)throw Error();latest=await r.json();if(!editing&&!busy)render();notify(latest)}catch{message('Disconnected. Displayed values may be stale.',true);document.querySelectorAll('.state').forEach(n=>{n.textContent='Disconnected';n.className='state stale'})}}
async function post(path,payload={}){const r=await fetch(path,{method:'POST',headers:{'X-Quota-Request':'refresh','Content-Type':'application/json'},body:JSON.stringify(payload)});if(!r.ok){const data=await r.json().catch(()=>({}));throw Error(data.error||'Request failed. Please try again.')}return r.json()}
$('#edit').onclick=()=>{if(!latest)return;editing=true;draft=structuredClone(latest.accounts);removed=[];notice='';render()};
$('#cancel').onclick=()=>{editing=false;draft=[];removed=[];notice='Changes discarded.';render()};
$('#save').onclick=async()=>{busy=true;render();try{await post('/api/accounts/layout',{order:draft.map(a=>a.id),removed});editing=false;notice='Account layout saved.';await update()}catch(e){message(e.message,true)}finally{busy=false;render()}};
$('#refresh').onclick=async()=>{busy=true;render();try{await post('/api/refresh');notice='Refresh requested. Accounts in cooldown wait until their next eligible read.';await update()}catch(e){message(e.message,true)}finally{busy=false;render()}};
$('#restore').onclick=async()=>{busy=true;$('#restore').disabled=true;try{await post('/api/accounts/restore');notice='Removed accounts restored.';await update()}catch(e){message(e.message,true)}finally{busy=false;$('#restore').disabled=false;render()}};
$('#mode').onchange=()=>{try{localStorage.setItem('quotaDisplayMode',$('#mode').value)}catch{}renderAccounts()};
$('#notify').onclick=async()=>{if(alerts){alerts=false;$('#notify').textContent='Alerts off';return}if(!('Notification'in window)){message('Notifications unavailable in this browser.',true);return}alerts=await Notification.requestPermission()==='granted';$('#notify').textContent=alerts?'Alerts on':'Alerts off';message(alerts?'Alerts enabled while this page is open.':'Notification permission was not granted.')};
window.addEventListener('beforeunload',e=>{if(editing){e.preventDefault();e.returnValue=''}});
update();setInterval(update,5000);

function setupDrag(handle,card,id){
 let start=null;
 function clear(){document.querySelectorAll('.drag-over,.dragging').forEach(n=>n.classList.remove('drag-over','dragging'));start=null;dragged=null}
 handle.onpointerdown=e=>{if(busy||e.button!==0)return;e.preventDefault();start={x:e.clientX,y:e.clientY};handle.setPointerCapture(e.pointerId)};
 handle.onpointermove=e=>{if(!start||Math.hypot(e.clientX-start.x,e.clientY-start.y)<5)return;dragged=id;card.classList.add('dragging');document.querySelectorAll('.drag-over').forEach(n=>n.classList.remove('drag-over'));const target=document.elementFromPoint(e.clientX,e.clientY)?.closest('.account');if(target&&target.dataset.id!==id)target.classList.add('drag-over')};
 handle.onpointerup=e=>{if(!start)return;const moved=Math.hypot(e.clientX-start.x,e.clientY-start.y)>=5;const target=document.elementFromPoint(e.clientX,e.clientY)?.closest('.account');const destination=target?.dataset.id;clear();if(!busy&&moved&&destination&&destination!==id){const from=draft.findIndex(a=>a.id===id),to=draft.findIndex(a=>a.id===destination);if(from>=0&&to>=0){draft.splice(to,0,draft.splice(from,1)[0]);renderAccounts()}}};
 handle.onpointercancel=clear;
}

function connectionActive(){return ['starting','waiting','verifying'].includes(latest?.connections?.job?.state)}
async function connect(provider,accountId){
 if(busy||connectionActive())return;
 busy=true;render();
 try{await post('/api/connections/start',{provider,accountId});notice='Connection started. Follow the official client’s sign-in flow.'}
 catch(e){notice=e.message}
 finally{busy=false;await update()}
}
let connectionMarkup='';
function renderConnections(){
 const info=latest.connections;if(!info)return;
 const signature=JSON.stringify([info,latest.enabledProviders,latest.supportedProviders,editing,busy]);if(signature===connectionMarkup)return;connectionMarkup=signature;
 const actions=$('#connection-actions');actions.replaceChildren();
 for(const provider of Object.keys(info.clients)){
  const row=el('div','connection-option');row.append(el('strong','',names[provider]));
  row.append(el('span','',info.clients[provider]?'Official client available':'Client not found · install and restart dashboard'));
  const b=button(provider==='antigravity'?'Open Antigravity':'Sign in','Sign in with '+names[provider],()=>connect(provider));
  if(provider==='antigravity')b.setAttribute('aria-label','Open Antigravity app');b.disabled=!info.clients[provider]||connectionActive()||editing||busy;row.append(b);actions.append(row);
 }
 const copilotHelp=el('div','connection-option');copilotHelp.append(el('strong','','GitHub Copilot'),el('span','','Uses your existing GitHub CLI sign-in. Run Setup-Copilot.ps1 for initial verification.'));const settingsLink=el('a','','Copilot settings');settingsLink.href='https://github.com/settings/copilot';settingsLink.target='_blank';settingsLink.rel='noopener noreferrer';copilotHelp.append(settingsLink);actions.append(copilotHelp);
 const providerSettings=$('#provider-settings');
 if(providerSettings){
  providerSettings.className='provider-settings';providerSettings.replaceChildren();
  const supported=latest.supportedProviders||info.supportedProviders||['codex','claude','antigravity','copilot'];
  const enabled=new Set(latest.enabledProviders||info.enabledProviders||[]),fieldset=el('fieldset');
  fieldset.append(el('legend','','Monitored providers'));
  for(const provider of supported){const label=el('label'),input=el('input');input.type='checkbox';input.value=provider;input.checked=enabled.has(provider);input.disabled=busy||editing;label.append(input,document.createTextNode(names[provider]||provider));fieldset.append(label)}
  const saveProviders=button('Save providers','Save monitored providers',async()=>{const selected=[...providerSettings.querySelectorAll('input:checked')].map(input=>input.value);busy=true;render();try{await post('/api/providers',{enabled:selected});notice='Monitored providers saved.';await update()}catch(e){message(e.message,true)}finally{busy=false;render()}});
  saveProviders.disabled=busy||editing;providerSettings.append(fieldset,saveProviders);
 }
 const box=$('#connection-job'),job=info.job;box.hidden=!job;box.replaceChildren();if(!job)return;
 box.className='connection-job '+job.state;
 box.append(el('strong','',names[job.provider]+' · '+job.state.replaceAll('_',' ')),el('span','',job.message));
 if(job.expectedLabel)box.append(el('span','connection-target','Requested account '+job.expectedLabel));
 if(connectionActive()){
  const cancel=button('Cancel','Cancel connection',async()=>{try{await post('/api/connections/cancel',{jobId:job.id});notice='Connection cancelled.';await update()}catch(e){message(e.message,true)}});
  box.append(cancel);
 }else{
  const dismiss=button('Dismiss','Dismiss connection status',()=>{box.hidden=true;dismissedJob=job.id;notice='';message('Auto-check 5 min. Refresh respects provider backoff.')});box.append(dismiss);
 }
 if(dismissedJob===job.id&&!connectionActive())box.hidden=true;
}
let dismissedJob=null;
$('#connections-toggle').onclick=()=>{const panel=$('#connections-panel');panel.hidden=!panel.hidden;$('#connections-toggle').setAttribute('aria-expanded',!panel.hidden)};
