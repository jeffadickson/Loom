'use strict';
const $=id=>document.getElementById(id);
let data,tenant='',user='',view='requests',register='Instrument',operators=[];
const labels={AwaitingReview:'Awaiting review',ChangesRequested:'Changes requested',ReleaseQueued:'Report queued'};
const text=value=>String(value??'');
const escape=value=>text(value).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const badge=value=>'<span class="badge '+escape(value)+'">'+escape(labels[value]||value)+'</span>';
const name=(type,id)=>data?.records[type]?.find(d=>d.id===id)?.values.Name||id||'—';
const date=value=>value?new Date(value).toLocaleString():'—';
async function api(path,body,method){
  const response=await fetch(path,{method:method||(body?'POST':'GET'),headers:{'Content-Type':'application/json','X-Loom-Demo':'local'},body:body?JSON.stringify(body):undefined});
  if(!response.ok){let message='Request failed ('+response.status+').';try{message=(await response.json()).error||message;}catch{}throw Error(message);}
  return response.status===204?null:response.json();
}
const route=path=>'/api/facility/'+encodeURIComponent(tenant)+path;
async function download(path,filename){
  const response=await fetch(route(path),{headers:{'X-Loom-Demo':'local'}});
  if(!response.ok)throw Error('Download denied or unavailable.');
  const url=URL.createObjectURL(await response.blob()),a=document.createElement('a');a.href=url;a.download=filename;a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
}
function notify(e){$('notice').textContent=e.message||e;}
async function load(){
  if(!tenant)return;
  [data,operators]=await Promise.all([api(route('/overview')),api(route('/operators'))]);
  $('identity').textContent=user+' / '+data.role;
  $('notice').textContent='';render();
}
async function session(){
  const state=await api('/auth/session');
  if(!state.authenticated){$('app').hidden=true;$('login').hidden=false;$('signout').hidden=true;$('tenant').innerHTML='';return;}
  user=state.name;
  const memberships=await api('/api/facility/memberships');
  $('tenant').innerHTML=memberships.map(m=>'<option value="'+escape(m.tenantId)+'">'+escape(m.tenantId==='north'?'North laboratory':m.tenantId==='south'?'South laboratory':m.tenantId)+'</option>').join('');
  tenant=memberships.some(m=>m.tenantId===tenant)?tenant:memberships[0]?.tenantId||'';
  $('tenant').value=tenant;$('login').hidden=true;$('app').hidden=false;$('signout').hidden=false;
  if(!tenant){$('content').innerHTML='<div class="panel empty">No laboratory membership. Ask your administrator to grant access.</div>';return;}
  await load();
}
function render(){
  $('content').dataset.view=view;
  document.querySelectorAll('nav button').forEach(b=>b.classList.toggle('selected',b.dataset.view===view));
  const requests=data.records.Request;
  $('metrics').innerHTML=[['Open requests',requests.filter(d=>!['Released','Cancelled'].includes(d.values.Status)).length],['In progress',requests.filter(d=>d.values.Status==='Running').length],['Awaiting review',requests.filter(d=>d.values.Status==='AwaitingReview').length],['Released',requests.filter(d=>d.values.Status==='Released').length]].map(([label,value])=>'<div class="metric"><strong>'+value+'</strong><span>'+label+'</span></div>').join('');
  $('create-request').hidden=data.role!=='Manager'||view!=='requests';
  $('filter-label').hidden=view!=='requests';
  $('page-title').textContent={requests:'Test requests',register:'Facility register',releases:'Reports & queue'}[view];
  $('page-description').textContent={requests:'Follow each test from its reservation to released evidence.',register:'Immutable equipment, calibration, specimen and procedure records.',releases:'Inspect report generation and download approved release snapshots.'}[view];
  if(view==='requests')renderRequests();else if(view==='register')renderRegister();else renderReleases();
}
function matches(d){const query=$('search').value.toLowerCase();return !query||JSON.stringify(d.values).toLowerCase().includes(query)||Object.entries(d.values).some(([key,value])=>data.records[key]&&name(key,value).toLowerCase().includes(query));}
function table(head,rows){return '<div class="table-wrap"><table><thead><tr>'+head.map(h=>'<th>'+h+'</th>').join('')+'</tr></thead><tbody>'+rows.join('')+'</tbody></table>'+(!rows.length?'<p class="empty">No matching records.</p>':'')+'</div>';}
function renderRequests(){
  const rows=data.records.Request.filter(matches).filter(d=>!$('filter').value||d.values.Status===$('filter').value);
  $('content').innerHTML=table(['Test / specimen','Instrument','Reservation','Status'],rows.map(d=>'<tr><td><button class="row-button" data-request="'+d.id+'">'+escape(d.values.Name)+'</button><small>'+escape(name('Specimen',d.values.Specimen))+'</small></td><td>'+escape(name('Instrument',d.values.Instrument))+'<small>'+escape(d.values.Operator)+'</small></td><td>'+escape(date(d.values.Starts))+'<small>Until '+escape(date(d.values.Ends))+'</small></td><td>'+badge(d.values.Status)+'</td></tr>'));
  document.querySelectorAll('[data-request]').forEach(b=>b.onclick=()=>detail(b.dataset.request));
}
const fields={
Campaign:[['Name','Campaign name'],['Project','Project'],['Budget','Budget (USD)']],
Instrument:[['Name','Instrument name'],['Serial','Serial number'],['Location','Location']],
Calibration:[['Name','Calibration revision'],['Instrument','Instrument','link'],['ValidFrom','Valid from','date'],['ValidUntil','Valid until','date'],['Certificate','Certificate reference']],
Specimen:[['Name','Specimen name'],['Serial','Serial number'],['Configuration','Configuration revision'],['Material','Material / assembly']],
Procedure:[['Name','Procedure name'],['Revision','Revision'],['Instructions','Instructions','textarea'],['Unit','Measurement unit'],['Lower','Lower acceptance limit'],['Upper','Upper acceptance limit']]
};
function renderRegister(){
  $('content').innerHTML='<div class="register-tabs">'+Object.keys(fields).map(type=>'<button data-register="'+type+'" class="'+(type===register?'selected':'')+'">'+type+'</button>').join('')+'</div><div class="section-head"><h2>'+register+' records</h2>'+(data.role==='Manager'?'<button id="add-register">Add '+register.toLowerCase()+'</button>':'')+'</div>'+
    table(['Record','Details','Created'],data.records[register].filter(matches).map(d=>'<tr><td><strong>'+escape(d.values.Name)+'</strong><small>'+escape(d.id.slice(0,12))+'</small></td><td>'+Object.entries(d.values).filter(([k])=>k!=='Name').map(([k,v])=>'<small>'+escape(k)+': '+escape(data.records[k]?name(k,v):v)+'</small>').join('')+'</td><td>'+escape(date(d.createdAt))+'</td></tr>'));
  document.querySelectorAll('[data-register]').forEach(b=>b.onclick=()=>{register=b.dataset.register;renderRegister();});
  if($('add-register'))$('add-register').onclick=()=>registerForm();
}
function renderReleases(){
  $('content').innerHTML='<div class="section-head"><h2>Released reports</h2></div>'+(data.role==='Operator'?'<div class="panel empty">Released reports include project costs. Managers and reviewers can download them.</div>':table(['Report','Released','Evidence'],data.records.Release.filter(matches).map(d=>'<tr><td><strong>'+escape(d.values.Name)+'</strong><small class="hash">'+escape(d.values.Sha256)+'</small></td><td>'+escape(date(d.values.ReleasedAt))+'</td><td><button data-report="'+d.id+'">Download report</button></td></tr>')))+
    '<div class="section-head"><h2>Report queue</h2><span class="help">Refresh to see worker progress</span></div>'+table(['Request','Status','Attempts / action'],data.records.ReleaseJob.filter(matches).map(d=>'<tr><td>'+escape(d.values.Name)+(d.values.Error?'<small>'+escape(d.values.Error)+'</small>':'')+'</td><td>'+badge(d.values.Status)+'</td><td>'+escape(d.values.Attempts)+(data.role==='Manager'&&d.values.Status==='Failed'?' <button data-retry="'+d.id+'">Retry</button>':'')+'</td></tr>'));
  document.querySelectorAll('[data-report]').forEach(b=>b.onclick=()=>download('/releases/'+b.dataset.report,'Test_Release.html').catch(notify));
  document.querySelectorAll('[data-retry]').forEach(b=>b.onclick=async()=>{try{const d=data.records.ReleaseJob.find(x=>x.id===b.dataset.retry);await api(route('/jobs/'+d.id+'/retry'),{version:d.version});await load();}catch(e){notify(e);}});
}
function dialog(title,body){$('dialog-title').textContent=title;$('dialog-body').innerHTML=body;$('dialog-error').textContent='';if(!$('dialog').open)$('dialog').showModal();}
function detail(id){
  const d=data.records.Request.find(x=>x.id===id),v=d.values;
  const results=data.records.Result.filter(r=>r.values.Request===id).sort((a,b)=>Number(b.values.Attempt)-Number(a.values.Attempt));
  const reviews=data.records.Review.filter(r=>r.values.Request===id);
  let action='';
  if(data.role==='Operator'&&v.Operator===user&&['Scheduled','ChangesRequested'].includes(v.Status))action='<button id="start" class="primary">Start test</button>';
  if(data.role==='Operator'&&v.Operator===user&&v.Status==='Running')action='<button id="record" class="primary">Record results</button>';
  if(data.role==='Reviewer'&&v.Status==='AwaitingReview')action='<button id="review" class="primary">Review results</button>';
  if(data.role==='Manager'&&v.Status==='Approved')action='<button id="release" class="primary">Queue release report</button>';
  if(data.role==='Manager'&&v.Status==='Released')action='<button id="amend" class="primary">Create amendment</button>';
  if(data.role==='Manager'&&['Scheduled','Running','ChangesRequested'].includes(v.Status))action+='<button id="cancel-request">Cancel request</button>';
  const facts=[['Specimen',name('Specimen',v.Specimen)],['Instrument',name('Instrument',v.Instrument)],['Procedure',name('Procedure',v.Procedure)],['Calibration',name('Calibration',v.Calibration)],['Operator',v.Operator],['Reservation',date(v.Starts)+' — '+date(v.Ends)],['Attempt',v.Attempt]];
  if(v.Cost!==undefined)facts.push(['Estimated cost (USD)',v.Cost]);
  if(v.CancellationReason)facts.push(['Cancellation reason',v.CancellationReason]);
  if(v.Amends)facts.push(['Amends',name('Request',v.Amends)]);
  dialog(v.Name,badge(v.Status)+'<div class="details-grid">'+facts.map(([k,value])=>'<p><span>'+escape(k)+'</span>'+escape(value)+'</p>').join('')+'</div><div class="actions">'+action+'<button id="history">View audit history</button></div>'+
    results.map(r=>'<section class="evidence-card"><h3>Attempt '+escape(r.values.Attempt)+' · '+escape(r.values.Value)+' '+escape(r.values.Unit)+' '+badge(r.values.Outcome)+'</h3><p class="help">'+escape(r.values.Notes)+'</p><p class="help">Recorded by '+escape(r.values.RecordedBy)+' · '+escape(date(r.values.RecordedAt))+'</p><p class="hash">SHA-256 '+escape(r.values.Sha256)+'</p><button data-evidence="'+r.id+'">Download evidence</button>'+reviews.filter(x=>x.values.Result===r.id).map(x=>'<p class="help">'+escape(x.values.Decision)+' by '+escape(x.values.ReviewedBy)+' — '+escape(x.values.Disposition)+'</p>').join('')+'</section>').join(''));
  if($('start'))$('start').onclick=()=>act('/requests/'+id+'/start',{version:d.version},()=>detail(id));
  if($('record'))$('record').onclick=()=>resultForm(d);
  if($('review'))$('review').onclick=()=>reviewForm(d,results[0]);
  if($('release'))$('release').onclick=()=>act('/requests/'+id+'/release',{version:d.version},()=>detail(id));
  if($('cancel-request'))$('cancel-request').onclick=()=>{
    dialog('Cancel test request','<p class="help">Release the reservation and retain the request and its evidence.</p><form id="cancel-form">'+input('Reason','Cancellation reason','textarea')+'<button class="primary">Confirm cancellation</button></form>');
    $('cancel-form').onsubmit=e=>{e.preventDefault();act('/requests/'+id+'/cancel',{version:d.version,reason:new FormData(e.target).get('Reason')},()=>detail(id));};
  };
  if($('amend'))$('amend').onclick=()=>requestForm(d);
  $('history').onclick=async()=>{try{const history=await api(route('/history/Request/'+id));dialog('Request audit history','<ol class="audit">'+history.map(a=>'<li>'+escape(a.action)+' · '+escape(a.actor)+'<time>'+escape(date(a.at))+'</time><span>'+escape(a.before?.values.Status||'New')+' → '+escape(a.after?.values.Status||'Removed')+'</span></li>').join('')+'</ol>');}catch(e){$('dialog-error').textContent=e.message;}};
  document.querySelectorAll('[data-evidence]').forEach(b=>b.onclick=()=>download('/evidence/'+b.dataset.evidence,'Evidence.bin').catch(e=>$('dialog-error').textContent=e.message));
}
async function act(path,body,after){
  const buttons=$('dialog').querySelectorAll('button');buttons.forEach(b=>b.disabled=true);
  try{await api(route(path),body);await load();after?after():$('dialog').close();}
  catch(e){$('dialog-error').textContent=e.message;}
  finally{buttons.forEach(b=>b.disabled=false);}
}
function input(key,label,type='text',value=''){
  const options=type==='link'?data.records[key]||[]:[];
  return '<label'+(type==='textarea'?' class="wide"':'')+'>'+escape(label)+(type==='link'?'<select name="'+key+'" required>'+options.map(d=>'<option value="'+d.id+'"'+(value===d.id?' selected':'')+'>'+escape(d.values.Name)+'</option>').join('')+'</select>':type==='textarea'?'<textarea name="'+key+'" required>'+escape(value)+'</textarea>':'<input name="'+key+'" type="'+type+'" value="'+escape(value)+'" required>')+'</label>';
}
function registerForm(){
  dialog('Add '+register.toLowerCase(),'<p class="help">Saved records are immutable. Add a new revision when details change.</p><form id="edit-form"><div class="form-grid">'+fields[register].map(([key,label,type])=>input(key,label,type)).join('')+'</div><button class="primary">Save '+register.toLowerCase()+'</button></form>');
  $('edit-form').onsubmit=e=>{e.preventDefault();act('/register/'+register,Object.fromEntries(new FormData(e.target)));};
}
function requestForm(previous){
  const v=previous?.values||{};
  const defaultTime=new Date(Date.now()+60000).toISOString().slice(0,16);
  dialog(previous?'Amend released request':'New test request','<p class="help">Reserve an instrument and operator. Times below are UTC.</p><form id="edit-form"><div class="form-grid">'+
    input('Name','Request title','text',previous?v.Name+' / amendment':'')+
    ['Campaign','Instrument','Calibration','Specimen','Procedure'].map(key=>input(key,key,'link',v[key])).join('')+
    '<label>Assigned operator<select name="Operator" required>'+operators.map(m=>'<option>'+escape(m.userId)+'</option>').join('')+'</select></label>'+
    input('Starts','Reservation start (UTC)','datetime-local',defaultTime)+input('Ends','Reservation end (UTC)','datetime-local',new Date(Date.now()+3600000).toISOString().slice(0,16))+
    input('Cost','Estimated cost (USD)','text',v.Cost||'0.00')+'</div><button class="primary">Reserve and create request</button></form>');
  $('edit-form').onsubmit=e=>{e.preventDefault();const body=Object.fromEntries(new FormData(e.target));body.Starts+=':00Z';body.Ends+=':00Z';if(previous)body.Amends=previous.id;act('/requests',body);};
}
function resultForm(d){
  const procedure=data.records.Procedure.find(p=>p.id===d.values.Procedure).values;
  dialog('Record test results','<p class="help">'+escape(procedure.Instructions)+'</p><p>Acceptance: '+escape(procedure.Lower)+' to '+escape(procedure.Upper)+' '+escape(procedure.Unit)+'</p><form id="result-form"><div class="form-grid">'+input('Value','Measured value (exact decimal)')+input('Unit','Measurement unit','text',procedure.Unit)+input('Notes','Acquisition notes','textarea')+
    '<label>Raw evidence file (1 MiB maximum)<input name="file" type="file" required></label><label>External dataset URI (optional)<input name="Dataset" type="text" placeholder="https://… or tsdb://…"></label></div><button class="primary">Save evidence and submit for review</button></form>');
  $('result-form').onsubmit=async e=>{
    e.preventDefault();const values=new FormData(e.target),file=values.get('file');
    try{
      if(!file.size||file.size>1048576)throw Error('Choose a file between 1 byte and 1 MiB.');
      const bytes=new Uint8Array(await file.arrayBuffer());let binary='';for(let i=0;i<bytes.length;i+=8192)binary+=String.fromCharCode(...bytes.subarray(i,i+8192));
      await act('/requests/'+d.id+'/results',{version:d.version,value:values.get('Value'),unit:values.get('Unit'),notes:values.get('Notes'),dataset:values.get('Dataset'),waveform:btoa(binary)},()=>detail(d.id));
    }catch(error){$('dialog-error').textContent=error.message;}
  };
}
function reviewForm(d,result){
  const v=result.values;
  dialog('Review test results','<p>Attempt '+escape(v.Attempt)+' · '+escape(v.Value)+' '+escape(v.Unit)+' '+badge(v.Outcome)+'</p><p class="help">Recorded by '+escape(v.RecordedBy)+'. Confirm the evidence before deciding.</p><p class="hash">SHA-256 '+escape(v.Sha256)+'</p><button id="review-evidence">Download evidence</button><form id="review-form">'+input('Disposition','Review findings / required changes','textarea')+'<div class="actions"><button class="primary" name="decision" value="approve" '+(v.Outcome==='Pass'?'':'disabled')+'>Approve results</button><button name="decision" value="reject">Request changes</button></div></form>');
  $('review-evidence').onclick=()=>download('/evidence/'+result.id,'Evidence.bin').catch(e=>$('dialog-error').textContent=e.message);
  $('review-form').onsubmit=e=>{e.preventDefault();act('/requests/'+d.id+'/review',{version:d.version,approve:e.submitter.value==='approve',disposition:new FormData(e.target).get('Disposition')},()=>detail(d.id));};
}
$('login-form').onsubmit=async e=>{e.preventDefault();try{await api('/auth/login',Object.fromEntries(new FormData(e.target)));await session();}catch(error){notify(error);}};
$('signout').onclick=async()=>{try{await api('/auth/logout',{});tenant='';data=null;await session();}catch(e){notify(e);}};
$('tenant').onchange=async()=>{tenant=$('tenant').value;$('dialog').close();try{await load();}catch(e){notify(e);}};
$('refresh').onclick=()=>load().catch(notify);$('search').oninput=()=>data&&render();$('filter').onchange=()=>render();
$('create-request').onclick=()=>requestForm();$('close-dialog').onclick=()=>$('dialog').close();
document.querySelectorAll('nav button').forEach(b=>b.onclick=()=>{view=b.dataset.view;$('search').value='';if(data)render();});
(async()=>{
  const config=await api('/facility-config');
  if(!config.enabled){notify('Facility Operations is not enabled. Start the host with --facility-demo, or configure Facility:Enabled and memberships.');return;}
  if(config.demo){
    $('demo-accounts').hidden=false;$('demo-user').innerHTML=config.accounts.map(m=>'<option value="'+escape(m.userId)+'">'+escape(m.userId)+' / '+escape(m.role)+'</option>').join('');
    function select(){document.querySelector('#login-form [name=id]').value=$('demo-user').value;document.querySelector('#login-form [name=password]').value=config.password;}
    $('demo-user').onchange=select;select();
  }else if(config.identity){$('identity-login').hidden=false;$('login-form').hidden=true;}
  await session();
})().catch(notify);
