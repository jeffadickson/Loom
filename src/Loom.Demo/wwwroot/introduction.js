'use strict';
(async()=>{
 const $=id=>document.getElementById(id);
 const offline=window.LOOM_INTRO_STANDALONE===true;
 const names=['Name','Outcome','SampleCount','Cost'];
 let schema,hash='',skip=0,generation=0,busy=false;
 const fields=[{name:'Name',label:'Name',type:'Text',required:true,id:'cc000000000000000000000000000002'},
 {name:'Outcome',label:'Outcome',type:'Select',required:true,options:['Pass','Review','Fail'],id:'cc000000000000000000000000000003'},
 {name:'SampleCount',label:'Sample count',type:'Integer',id:'cc000000000000000000000000000004'},
 {name:'Cost',label:'Cost (USD)',type:'Money',currency:'USD',precision:19,scale:2,id:'cc000000000000000000000000000005'}];
 const records=[{id:'preview-1',version:1,values:{Name:'Thermal cycle • channel 1',Outcome:'Pass',SampleCount:'9007199254740993',Cost:'1234.56'}},
 {id:'preview-2',version:1,values:{Name:'Vibration sweep • channel 2',Outcome:'Review',SampleCount:'64000',Cost:'87.40'}}];
 const history=new Map(records.map(r=>[r.id,[{action:'create',actor:'preview-seed',after:r}]]));
 function cents(s){const m=String(s??'0').match(/^([+-]?)(\d+)(?:\.(\d{1,2}))?$/);if(!m)throw Error('Cost exceeds scale 2 or is not a plain decimal. Enter, for example, 12.34.');return (m[1]==='-'?-1n:1n)*(BigInt(m[2])*100n+BigInt((m[3]||'').padEnd(2,'0')));}
 function money(n){const sign=n<0n?'-':'';n=n<0n?-n:n;return sign+(n/100n).toString()+'.'+(n%100n).toString().padStart(2,'0');}
 function message(text,error=false){$('message').textContent=text;$('message').className=error?'error':'';}
 function trace(text){$('trace').textContent=text;}
 async function api(path,method='GET',body){
  const response=await fetch('/api/'+path,{method,headers:{'X-Loom-Demo':'local','X-Demo-Role':$('role').value,'X-Loom-Schema':hash,'Content-Type':'application/json'},body:body===undefined?undefined:JSON.stringify(body)});
  const result=await response.json();if(!response.ok)throw Error(`HTTP ${response.status}: ${result.error||response.statusText}`);return result;
 }
 function form(){
  $('metadata').textContent=JSON.stringify(schema,null,2);$('fields').replaceChildren();
  for(const f of schema.fields.filter(f=>names.includes(f.name))){
   const label=document.createElement('label');label.textContent=f.label+(f.required?' *':'');
   const input=document.createElement(f.type==='Select'?'select':'input');input.name=f.name;input.id='input-'+f.name;
   if(f.type==='Select')for(const option of f.options){const o=document.createElement('option');o.value=o.textContent=option;input.append(o);}
   else {input.type='text';if(f.type==='Money'||f.type==='Integer')input.inputMode=f.type==='Money'?'decimal':'numeric';}
   input.required=f.required||false;label.append(input);const hint=document.createElement('small');hint.textContent=f.type==='Money'?'Exact decimal · USD · 2 places':f.type==='Integer'?'64-bit integer · no JavaScript rounding':f.type;label.append(hint);$('fields').append(label);
  }
  $('input-Name').value='Calibration run 003';$('input-Cost').value='12.34';$('input-SampleCount').value='10000';
 }
 async function refresh(){
  const mine=++generation,role=$('role').value;
  try{
   let page;
   if(offline){let selected=records.filter(r=>!$('outcome').value||r.values.Outcome===$('outcome').value);
    selected.sort((a,b)=>{let n;if($('sort').value==='Cost'){const av=a.values.Cost,bv=b.values.Cost;n=av==null?(bv==null?0:-1):bv==null?1:cents(av)<cents(bv)?-1:cents(av)>cents(bv)?1:0;}else n=a.values.Name<b.values.Name?-1:a.values.Name>b.values.Name?1:0;return n||(a.id<b.id?-1:1);});page={total:selected.length,items:selected.slice(skip,skip+5)};
   }else{const q=new URLSearchParams({take:'5',skip:String(skip),sort:$('sort').value});if($('outcome').value){q.set('field','Outcome');q.set('equals',$('outcome').value);}page=await api('documents/TestResult?'+q);}
   if(mine!==generation)return;
   $('rows').replaceChildren();let sum=0n;
   for(const record of page.items){const tr=document.createElement('tr');
    for(const name of names){const td=document.createElement('td');td.textContent=record.values[name]??'—';tr.append(td);}sum+=cents(record.values.Cost);
    const td=document.createElement('td'),button=document.createElement('button');button.textContent='History';button.disabled=role==='Viewer';button.title=role==='Viewer'?'Viewer has no audit permission':'Inspect this record';
    button.addEventListener('click',async()=>{try{$('audit').textContent=JSON.stringify(offline?history.get(record.id):await api('documents/TestResult/'+encodeURIComponent(record.id)+'/audit'),null,2);$('auditPanel').open=true;}catch(e){message(e.message,true);}});td.append(button);tr.append(td);$('rows').append(tr);
   }
   if(!page.items.length){const tr=document.createElement('tr'),td=document.createElement('td');td.colSpan=5;td.textContent='No matching results.';tr.append(td);$('rows').append(tr);}
   $('summary').textContent=`${page.total} matching records · Page ${Math.floor(skip/5)+1} · Visible cost total: $${money(sum)}`;
   $('previous').disabled=skip===0;$('next').disabled=skip+5>=page.total;
  }catch(e){if(mine===generation)message(e.message,true);}
 }
 $('entry').addEventListener('submit',async event=>{
  event.preventDefault();if(busy)return;busy=true;$('save').disabled=true;
  const values=Object.fromEntries(names.map(n=>[n,$('input-'+n).value.trim()||null]));
  trace((offline?'SIMULATED ':'')+'POST /api/documents/TestResult\n'+JSON.stringify(values,null,2));
  try{
   let saved;
   if(offline){
    if($('role').value==='Viewer')throw Error('Simulated HTTP 403: Viewer cannot create records.');
    if(!values.Name||values.Name.length>2000)throw Error('Name is required and must be at most 2,000 characters.');
    if(!['Pass','Review','Fail'].includes(values.Outcome))throw Error('Choose a valid outcome.');
    if(values.Cost!=null){const n=cents(values.Cost);if(n<=-10000000000000000000n||n>=10000000000000000000n)throw Error('Cost exceeds precision 19.');values.Cost=money(n);}
    if(values.SampleCount!=null){if(!/^[+-]?\d+$/.test(values.SampleCount))throw Error('Sample count must be an integer.');const n=BigInt(values.SampleCount);if(n< -9223372036854775808n||n>9223372036854775807n)throw Error('Sample count exceeds 64-bit range.');values.SampleCount=n.toString();}
    saved={id:'preview-'+(records.length+1),version:1,values};records.push(saved);history.set(saved.id,[{action:'create',actor:'preview-admin',at:new Date().toISOString(),after:saved}]);
   }else saved=await api('documents/TestResult','POST',values);
   trace((offline?'SIMULATED SUCCESS':'HTTP 201 — C# accepted the record')+'\n1. Authorize actor\n2. Validate fields\n3. Insert record + audit in one transaction\n\n'+JSON.stringify(saved,null,2));
   message('Saved. Record and audit entry created.');skip=0;$('outcome').value='';await refresh();
  }catch(e){message(e.message,true);trace($('trace').textContent+'\n\nREJECTED: '+e.message+'\nThe save did not complete.');}
  finally{busy=false;$('save').disabled=false;}
 });
 for(const id of ['outcome','sort','role'])$(id).addEventListener('change',()=>{skip=0;$('audit').textContent='Choose History on a record.';$('auditPanel').open=false;refresh();});
 $('previous').addEventListener('click',()=>{skip=Math.max(0,skip-5);refresh();});$('next').addEventListener('click',()=>{skip+=5;refresh();});
 try{
  if(offline){schema={name:'TestResult',fields};$('mode').textContent='BROWSER SIMULATION';$('modeNote').textContent='Standalone edition: JavaScript simulates the four-field example in browser memory. No C# or database runs here; refresh resets all changes. Use the command below for the real framework.';}
  else{const meta=await api('meta');hash=meta.schemaHash;schema=meta.types.find(t=>t.name==='TestResult');if(!schema)throw Error('Start this host with --memory-demo or --science-demo to load the TestResult model.');$('mode').textContent='LIVE C# / '+meta.provider.name.toUpperCase();$('modeNote').textContent='Connected to the real Loom C# service. Saves modify this host’s '+meta.provider.name+' database. Demo roles simulate users; they are not authentication.';}
  form();await refresh();
 }catch(e){$('mode').textContent='NOT CONNECTED';$('modeNote').textContent=e.message;$('save').disabled=true;message('The live service could not load. No simulation was substituted.',true);}
})();
