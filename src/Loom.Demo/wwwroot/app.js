'use strict';
const $ = id => document.getElementById(id);
let meta, type=new URLSearchParams(location.search).get('type')||'Asset', tab='records', rows=[], skip=0, total=0, descending=false, current=null;
const labels=new Map();
const schema=()=>meta.types.find(t=>t.name===type);
const allowed=action=>schema().actions.includes(action);
async function api(path, method='GET', body){
  const response=await fetch('/api/'+path,{method,headers:{'Content-Type':'application/json','X-Loom-Demo':'local','X-Demo-Role':$('role').value,'X-Loom-Schema':meta?.schemaHash||''},body:body===undefined?undefined:JSON.stringify(body)});
  if(response.status===401){location.href='/login.html';throw Error('Sign in required')}if(!response.ok){let error;try{error=(await response.json()).error}catch{error='Request failed ('+response.status+')'}throw Error(error)}
  return response.status===204?null:response.json();
}
function showError(e, form=false){const el=$(form?'formError':'error');el.textContent=e.message;el.hidden=false}
function element(tag,text,cls){const el=document.createElement(tag);if(text!==undefined)el.textContent=text;if(cls)el.className=cls;return el}
async function boot(){
  try{meta=await api('meta');if(!meta.types.some(t=>t.name===type))type=meta.types[0].name;if(meta.mode!=='Local demonstration'){$('role').closest('label').hidden=true;document.querySelector('.banner').textContent=meta.mode;}$('provider').textContent=meta.provider.name+' connected';$('design').hidden=!meta.designer;$('layoutReport').hidden=!meta.reportDesigner;$('storage').textContent=meta.provider.name;$('durable').textContent=meta.provider.durable?'Saved across restarts':'Temporary • resets on restart';
    $('types').replaceChildren(...meta.types.map(t=>{const b=element('button',t.label);b.classList.toggle('active',t.name===type);b.onclick=()=>choose(t.name);return b}));
    $('sort').replaceChildren(...schema().fields.filter(f=>!['Binary','Measurement'].includes(f.type)).map(f=>{const o=element('option',f.label);o.value=f.name;return o}));$('sort').value=schema().titleField;
    await load();
  }catch(e){showError(e)}
}
async function choose(name){type=name;skip=0;$('search').value='';await boot()}
async function linkLabels(){
  labels.clear();
  for(const f of schema().fields.filter(f=>f.type==='Link')){
    const target=meta.types.find(t=>t.name===f.target);if(!target)continue;
    const ids=[...new Set(rows.map(d=>d.values[f.name]).filter(Boolean))];for(const id of ids){const doc=await api('documents/'+f.target+'/'+encodeURIComponent(id));labels.set(id,doc.values[target.titleField])}
  }
}
async function load(){
 try{$('error').hidden=true;$('title').textContent=schema().label;$('schemaVersion').textContent='v'+schema().version;$('create').hidden=!allowed('create');
   for(const name of ['records','report','model'])$(name==='model'?'modelTab':name+'Tab').classList.toggle('selected',tab===name);
   $('search').disabled=tab==='model';$('sort').disabled=tab==='model';$('direction').disabled=tab==='model';$('export').hidden=tab==='model';
   const q=new URLSearchParams({search:$('search').value,sort:$('sort').value,descending,skip,take:20});
   const page=await api('documents/'+type+'?'+q);rows=page.items;total=page.total;await linkLabels();
   $('count').textContent=total;$('summary').textContent=total?`${skip+1}–${skip+rows.length} of ${total} records`:'No records';$('previous').disabled=skip===0;$('next').disabled=skip+20>=total;
   if(tab==='model'){$('content').replaceChildren(element('pre',JSON.stringify(schema(),null,2)));return}
   const fields=tab==='report'?schema().fields:schema().fields.slice(0,5);
   const table=element('table'),thead=element('thead'),tr=element('tr');fields.forEach(f=>tr.append(element('th',f.label)));tr.append(element('th','State'));thead.append(tr);table.append(thead);
   const tbody=element('tbody');for(const d of rows){const row=element('tr');fields.forEach((f,i)=>{const td=element('td'),value=d.values[f.name];const text=f.type==='Link'?(labels.get(value)||value):f.type==='Boolean'?(value?'Yes':'No'):f.type==='Binary'?(value==null?'—':binarySize(value)+' bytes'):f.type==='Measurement'?(value==null?'—':value.value+(value.standardUncertainty==null?'':' ± '+value.standardUncertainty)+' '+value.unit):f.type==='Money'?(value==null?'—':f.currency+' '+value):value??'—';
     if(i===0){const b=element('button',text,'record-link');b.onclick=()=>edit(d);td.append(b)}else if(f.type==='Select'){td.append(element('span',text,'pill '+String(text).toLowerCase()))}else td.textContent=text;row.append(td)});row.append(element('td'));row.lastChild.append(element('span',d.state,'pill '+d.state.toLowerCase()));tbody.append(row)}table.append(tbody);
   $('content').replaceChildren(rows.length?table:element('div','No records match. Create a record or clear your search.','empty'));
 }catch(e){showError(e)}
}
async function edit(doc=null){
 try{current=doc?await api('documents/'+type+'/'+doc.id):null;$('formError').hidden=true;$('editorTitle').textContent=current?current.values[schema().titleField]:'New '+schema().label;$('editorKicker').textContent=current?'DOCUMENT / VERSION '+current.version:'NEW DOCUMENT';$('fields').replaceChildren();$('history').replaceChildren();$('actions').replaceChildren();
   const editable=!current||allowed('write')&&current.state==='Draft';
   for(const f of schema().fields){const label=element('label',f.label+(f.required?' *':''));let control;
     if(f.type==='Select'||f.type==='Link'||f.type==='Boolean'){control=element('select');let options=f.type==='Select'?f.options.map(x=>[x,x]):f.type==='Boolean'?[["true","Yes"],["false","No"]]:[];
       control.append(element('option','Select…'));control.firstChild.value='';for(const [value,text]of options){const o=element('option',text);o.value=value;control.append(o)}
     }else{control=element('input');control.type=f.type==='Binary'?'file':f.type==='Date'?'date':'text';if(['Integer','Decimal','Money'].includes(f.type))control.inputMode=f.type==='Integer'?'numeric':'decimal'}
     control.name=f.name;control.required=f.required&&!f.readOnly&&!(f.type==='Binary'&&current?.values[f.name]!=null);control.disabled=f.readOnly||!editable;if(f.type!=='Binary')control.value=f.type==='Measurement'?(current?.values[f.name]?.value??''):(current?.values[f.name]??'');label.append(control);
     if(f.type==='Link')await configureLinkPicker(label,control,f,!editable||f.readOnly,current?.values[f.name]);
     if(f.type==='Measurement'){const unit=element('select');unit.name=f.name+'__unit';for(const u of f.allowedUnits)unit.add(new Option(u,u));unit.value=current?.values[f.name]?.unit??f.allowedUnits[0];unit.disabled=!editable||f.readOnly;unit.setAttribute('aria-label',f.label+' unit');const uncertainty=element('input');uncertainty.name=f.name+'__uncertainty';uncertainty.placeholder='Standard uncertainty (optional)';uncertainty.setAttribute('aria-label',f.label+' standard uncertainty');uncertainty.value=current?.values[f.name]?.standardUncertainty??'';uncertainty.disabled=unit.disabled;label.append(unit,uncertainty)}
     if(f.type==='Money')label.append(element('span',f.currency+' · '+(f.scale??2)+' decimal places · rounding: '+f.rounding,'hint'));
     if(f.description)label.append(element('span',f.description,'hint'));if(f.maxLength)control.maxLength=f.maxLength;
     if(f.type==='Uri')label.append(element('span','External reference; data is not fetched','hint'));
     if(f.type==='Binary'){label.append(element('span','Maximum '+f.maxBytes+' bytes · compression: '+f.compression,'hint'));if(current?.values[f.name]!=null){const download=element('button','Download '+binarySize(current.values[f.name])+' bytes');download.type='button';download.onclick=()=>downloadBinary(f);label.append(download);if(editable&&!f.required){const clear=element('input');clear.type='checkbox';clear.name=f.name+'__clear';const cap=element('label','Clear stored data');cap.prepend(clear);label.append(cap)}}}
     if(f.readOnly)label.append(element('span','Calculated by a C# hook on save','hint'));$('fields').append(label);
   }
   $('save').hidden=!editable;$('recordState').textContent=current?current.state+' · v'+current.version:'New draft';
   if(current){for(const action of ['submit','cancel','delete']){if(!allowed(action))continue;if(action==='submit'&&(!schema().submittable||current.state!=='Draft'))continue;if(action==='cancel'&&current.state!=='Submitted')continue;if(action==='delete'&&current.state!=='Draft')continue;const b=element('button',action[0].toUpperCase()+action.slice(1));b.type='button';b.onclick=()=>act(action);$('actions').append(b)}
     if(allowed('audit')){const entries=await api('documents/'+type+'/'+current.id+'/audit');$('history').append(element('h3','Change history'));for(const entry of entries.slice().reverse()){const details=element('details');details.append(element('summary',entry.action+' · '+entry.actor+' · '+new Date(entry.at).toLocaleString()));details.append(element('pre',JSON.stringify({before:entry.before?.values,after:entry.after?.values},(key,value)=>schema().fields.some(f=>f.name===key&&f.type==='Binary')&&value!=null?'[Binary: '+binarySize(value)+' bytes]':value,2)));$('history').append(details)}}
   }
   if(!$('editor').open)$('editor').showModal();
 }catch(e){showError(e,$('editor').open)}
}
$('form').onsubmit=async event=>{event.preventDefault();$('save').disabled=true;try{const values={};for(const f of schema().fields.filter(f=>!f.readOnly)){const input=$('form').elements.namedItem(f.name);if(f.type==='Binary'){const file=input.files[0];if(file){if(file.size>f.maxBytes)throw Error(f.label+' exceeds its byte limit');values[f.name]=await new Promise((resolve,reject)=>{const reader=new FileReader();reader.onload=()=>resolve(reader.result.split(',')[1]);reader.onerror=reject;reader.readAsDataURL(file)})}else values[f.name]=$('form').elements.namedItem(f.name+'__clear')?.checked?null:current?.values[f.name]??null;continue}if(f.type==='Measurement'){const u=$('form').elements.namedItem(f.name+'__uncertainty').value;values[f.name]=input.value===''?null:{value:input.value,unit:$('form').elements.namedItem(f.name+'__unit').value,standardUncertainty:u===''?null:u};continue}const value=input.value;values[f.name]=value===''?null:f.type==='Boolean'?value==='true':value}if(current)await api('documents/'+type+'/'+current.id,'PUT',{version:current.version,values});else await api('documents/'+type,'POST',values);$('editor').close();await load()}catch(e){showError(e,true)}finally{$('save').disabled=false}};
async function act(action){if(!confirm(action+' this document?'))return;try{if(action==='delete')await api('documents/'+type+'/'+current.id+'?version='+current.version,'DELETE');else await api('documents/'+type+'/'+current.id+'/actions/'+action,'POST',{version:current.version});$('editor').close();await load()}catch(e){showError(e,true)}}
$('close').onclick=()=>$('editor').close();$('create').onclick=()=>edit();$('role').onchange=()=>{$('editor').close();$('schemaEditor').close();boot()};
for(const name of ['records','report','model'])$(name==='model'?'modelTab':name+'Tab').onclick=()=>{tab=name;load()};
$('schemaButton').onclick=()=>{tab='model';load()};
let timer;$('search').oninput=()=>{clearTimeout(timer);timer=setTimeout(()=>{skip=0;load()},250)};$('sort').onchange=()=>{skip=0;load()};$('direction').onclick=()=>{descending=!descending;$('direction').textContent=descending?'↓':'↑';load()};$('previous').onclick=()=>{skip-=20;load()};$('next').onclick=()=>{skip+=20;load()};
$('export').onclick=async()=>{try{const r=await fetch('/api/reports/'+type+'/csv?'+new URLSearchParams({search:$('search').value,sort:$('sort').value,descending}),{headers:{'X-Loom-Demo':'local','X-Demo-Role':$('role').value}});if(!r.ok)throw Error((await r.json()).error);const u=URL.createObjectURL(await r.blob()),a=element('a');a.href=u;a.download=type+'.csv';a.click();setTimeout(()=>URL.revokeObjectURL(u),1000)}catch(e){showError(e)}};
let modelDraft, migrationPlan;
function modelError(error){$('designerError').textContent=error.message;$('designerError').hidden=false}
function invalidatePlan(){migrationPlan=null;$('migrationPreview').hidden=true}
function fieldRow(field, added=false){
 const row=element('tr');row.dataset.field=field.name;
 const name=element('input');name.value=field.name;name.className='field-name';name.setAttribute('aria-label','Field name '+field.name);name.oninput=()=>{field.name=name.value.trim();invalidatePlan()};
 const label=element('input');label.value=field.label;label.className='field-label';label.setAttribute('aria-label','Caption '+field.name);label.oninput=()=>{field.label=label.value;invalidatePlan()};
 const a=element('td'),b=element('td'),c=element('td');a.append(name);b.append(label);
 if(added){const select=element('select');for(const t of ['Text','Integer','Decimal','Money','Date','Boolean','Select','Binary','Uri','Measurement']){const o=element('option',t);select.append(o)}select.value=field.type;select.setAttribute('aria-label','New field type');select.onchange=()=>{field.type=select.value;invalidatePlan()};c.append(select);
 const required=element('input');required.type='checkbox';required.checked=!!field.required;required.setAttribute('aria-label','Required new field');const caption=element('label','Required');caption.prepend(required);c.append(caption);
 const fill=element('input');fill.placeholder='Initial value for existing records';fill.setAttribute('aria-label','Backfill value');fill.disabled=!field.required;fill.value=field.backfill??'';fill.oninput=()=>{field.backfill=fill.value;invalidatePlan()};required.onchange=()=>{field.required=required.checked;fill.disabled=!required.checked;field.backfill=required.checked?fill.value:null;invalidatePlan()};c.append(fill);
 const remove=element('button','Remove');remove.onclick=()=>{modelDraft.additions=modelDraft.additions.filter(f=>f!==field);row.remove();invalidatePlan()};c.append(remove)}
 else{c.textContent=field.type;name.title='Permanent field ID: '+field.id;
 if(['Text','Select','Uri','Integer','Decimal','Money'].includes(field.type)&&!field.unique&&!field.readOnly){c.textContent='';const select=element('select');for(const t of ({Text:['Text','Integer','Decimal','Money','Date','Select','Uri'],Select:['Select','Text'],Uri:['Uri','Text'],Integer:['Integer','Decimal','Money'],Decimal:['Decimal','Integer','Money'],Money:['Money']}[field.type]))select.add(new Option(t,t));select.value=field.type;select.setAttribute('aria-label','Type for '+field.name);select.onchange=()=>{field.type=select.value;invalidatePlan()};c.append(select)}
 if(!field.unique&&!field.readOnly&&field.type!=='Link'){
 const originallyRequired=field.required;
 const required=element('input');required.type='checkbox';required.checked=!!field.required;required.setAttribute('aria-label','Make '+field.name+' required');const caption=element('label','Required');caption.prepend(required);c.append(caption);
 const fill=element('input');fill.placeholder='Fill missing values (optional if none)';fill.setAttribute('aria-label','Backfill for '+field.name);fill.disabled=true;fill.oninput=()=>{field.backfill=fill.value===''?null:fill.value;invalidatePlan()};required.onchange=()=>{field.required=required.checked;fill.disabled=originallyRequired||!required.checked;field.backfill=!originallyRequired&&required.checked&&fill.value!==''?fill.value:null;invalidatePlan()};c.append(fill);
 }}
 const details=element('details');details.append(element('summary','Type settings'));
 const settings=element('textarea');settings.setAttribute('aria-label','Type settings for '+field.name);
 const keys=['precision','scale','currency','rounding','dateFormat','options','compression','maxBytes','mediaType','quantityKind','allowedUnits','description','maxLength','minimum','maximum','default'];
 settings.value=JSON.stringify(Object.fromEntries(keys.filter(k=>field[k]!=null).map(k=>[k,field[k]])),null,2);
 settings.placeholder='{"currency":"USD","precision":19,"scale":2}';
 settings.oninput=()=>{invalidatePlan();try{const parsed=JSON.parse(settings.value||'{}');if(!parsed||Array.isArray(parsed)||typeof parsed!=='object')throw Error();field.settings=parsed;settings.setCustomValidity('')}catch{settings.setCustomValidity('Enter a JSON object');}};
 details.append(element('span','Money: currency, precision, scale, rounding (reject/even/away). Select: options array. Date conversion: dateFormat. Binary: compression (auto/none/brotli), maxBytes, mediaType.','hint'),settings);c.append(details);
 row.append(a,b,c);return row;
}
$('design').onclick=async()=>{
 try{const fresh=await api('meta');if(fresh.schemaHash!==meta.schemaHash){await boot()}
 const s=schema();modelDraft={sourceHash:meta.schemaHash,type:s.name,fields:s.fields.map(f=>({...f})),additions:[]};invalidatePlan();$('designerError').hidden=true;$('designerTitle').textContent='Edit '+s.label.toLowerCase();$('modelFields').replaceChildren(...modelDraft.fields.map(f=>fieldRow(f)));$('newFields').replaceChildren();$('migrationEntries').replaceChildren();
 const entries=await api('designer/history');for(const e of entries.slice().reverse()){const d=element('details');d.append(element('summary',new Date(e.at).toLocaleString()+' · '+e.actor+' · '+e.changes.length+' changes'));d.append(element('pre',e.changes.map(c=>c.description).join('\n')));$('migrationEntries').append(d)}
 if(!entries.length)$('migrationEntries').textContent='No migrations applied yet.';$('schemaEditor').showModal();
 }catch(e){showError(e)}
};
$('designerClose').onclick=()=>$('schemaEditor').close();
$('addModelField').onclick=()=>{const f={id:crypto.randomUUID().replaceAll('-',''),name:'newField'+(modelDraft.additions.length+1),label:'New field',type:'Text'};modelDraft.additions.push(f);let table=$('newFields').querySelector('table');if(!table){table=element('table');$('newFields').append(table)}table.append(fieldRow(f,true));invalidatePlan()};
$('previewMigration').onclick=async()=>{try{$('designerError').hidden=true;for(const input of $('schemaEditor').querySelectorAll('textarea'))if(!input.reportValidity())return;migrationPlan=await api('designer/plan','POST',modelDraft);$('migrationChanges').replaceChildren(...migrationPlan.changes.map(c=>element('li',c.description)));$('migrationRows').textContent=Object.entries(migrationPlan.rows).map(([t,n])=>t+': '+n+' existing records').join(' · ');$('migrationSql').textContent=migrationPlan.sql.join('\n')||'Metadata only; no column changes.';$('migrationPreview').hidden=false}catch(e){invalidatePlan();modelError(e)}};
$('applyMigration').onclick=async()=>{if(!migrationPlan)return;$('applyMigration').disabled=true;try{await api('designer/apply','POST',{planId:migrationPlan.id});await boot();$('schemaEditor').close()}catch(e){modelError(e)}finally{$('applyMigration').disabled=false}};
$('exportModel').onclick=async()=>{try{const r=await fetch('/api/designer/export',{headers:{'X-Loom-Demo':'local','X-Demo-Role':$('role').value}});if(!r.ok)throw Error((await r.json()).error);const u=URL.createObjectURL(await r.blob()),a=element('a');a.href=u;a.download=meta.provider.name==='Browser preview'?'Loom_Model_Preview.json':'Loom_Model.zip';a.click();setTimeout(()=>URL.revokeObjectURL(u),1000)}catch(e){modelError(e)}};
function binarySize(value){return value.length*3/4-(value.endsWith('==')?2:value.endsWith('=')?1:0)}
async function downloadBinary(field){try{const response=await fetch('/api/documents/'+type+'/'+current.id+'/binary/'+field.name,{headers:{'X-Loom-Demo':'local','X-Demo-Role':$('role').value}});if(!response.ok)throw Error((await response.json()).error);const url=URL.createObjectURL(await response.blob()),a=element('a');a.href=url;a.download=field.name+'.bin';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000)}catch(e){showError(e,true)}}
boot();

$('printReport').onclick=async()=>{try{
 const r=await fetch('/api/reports/'+type+'/print',{headers:{'X-Loom-Demo':'local','X-Demo-Role':$('role').value}});
 if(!r.ok)throw Error((await r.json()).error);
 const url=URL.createObjectURL(await r.blob()),a=element('a');a.href=url;a.download=type+'_Report.html';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
}catch(e){showError(e)}};

// Bounded pages preserve an existing selection even when it is outside the result page.
async function configureLinkPicker(label,control,field,disabled,selected){
 const target=meta.types.find(t=>t.name===field.target);let offset=0,generation=0;
 const search=element('input');search.type='search';search.placeholder='Search '+target.label;search.setAttribute('aria-label','Search '+field.label+' links');search.disabled=disabled;
 const previous=element('button','Previous choices'),next=element('button','Next choices'),status=element('span','','hint');
 previous.type=next.type='button';previous.setAttribute('aria-label','Previous '+field.label+' choices');next.setAttribute('aria-label','Next '+field.label+' choices');status.setAttribute('aria-live','polite');
 label.append(search,previous,next,status);
 let selectedLabel='';if(selected){const doc=await api('documents/'+field.target+'/'+encodeURIComponent(selected));selectedLabel=doc.values[target.titleField];control.add(new Option(selectedLabel,selected));control.value=selected}
 async function refresh(){
  const request=++generation;
  previous.disabled=next.disabled=true;
  try{
   const page=await api('documents/'+field.target+'?'+new URLSearchParams({search:search.value,skip:offset,take:50}));if(request!==generation)return;
   const value=control.value,caption=control.selectedOptions[0]?.textContent;
   control.replaceChildren(new Option('Select…',''));for(const doc of page.items)control.add(new Option(doc.values[target.titleField],doc.id));
   if(value&&!page.items.some(d=>d.id===value))control.add(new Option(caption||selectedLabel||value,value));control.value=value;
   status.textContent=page.total?`${offset+1}–${Math.min(offset+50,page.total)} of ${page.total}`:'No matching records';
   previous.disabled=disabled||offset===0;next.disabled=disabled||offset+50>=page.total;
  }catch(error){if(request===generation){status.textContent=error.message;showError(error,true)}}
 }
 search.oninput=()=>{offset=0;void refresh()};previous.onclick=()=>{offset=Math.max(0,offset-50);void refresh()};next.onclick=()=>{offset+=50;void refresh()};
 await refresh();
}
