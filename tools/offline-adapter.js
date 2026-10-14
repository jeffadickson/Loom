// Offline UI demonstration only. The real implementation is in src/Loom.Core and Loom.Sqlite.
(() => {
  let schemas=window.LOOM_MODELS;
  const key='loom-offline-demo-v02';
  const pending=new Map();
  const fingerprint=()=>JSON.stringify(schemas);
  const stamp=()=>new Date().toISOString();
  const clone=x=>JSON.parse(JSON.stringify(x));
  const id=()=>globalThis.crypto?.randomUUID?.()??Math.random().toString(36).slice(2)+Date.now();
  const record=(type,id,values)=>({id,type,version:1,state:'Draft',createdAt:stamp(),modifiedAt:stamp(),values});
  const seed=()=>({models:clone(window.LOOM_MODELS),migrations:[],docs:[record('Asset','asset-1',{name:'Bench oscilloscope',tag:'LAB-001',category:'Electronics',location:'Lab A / Bench 3',active:true}),record('Asset','asset-2',{name:'Vacuum pump',tag:'LAB-002',category:'Mechanical',location:'Lab B',active:true}),record('Asset','asset-3',{name:'Environmental chamber',tag:'LAB-003',category:'Facilities',location:'Test facility',active:true}),record('WorkOrder','work-1',{title:'Annual calibration',asset:'asset-1',priority:'High',due:'2026-11-02',hours:3,rate:125,estimate:375})],audit:[]});
  let state;try{state=JSON.parse(localStorage.getItem(key))||seed()}catch{state=seed()}
  schemas=state.models;
  const persist=()=>{try{localStorage.setItem(key,JSON.stringify(state))}catch{/* Sandboxed previews may forbid persistent browser storage. */}};
  const fail=(status,message)=>{throw {status,message}};
  window.fetch=async(url,options={})=>{
    const snapshot=clone(state);
    try{
      const path=new URL(url,'http://offline.local'),parts=path.pathname.split('/').filter(Boolean),q=path.searchParams;
      if(parts[0]!=='api')fail(404,'Offline adapter only supports the demonstration API.');
      const role=options.headers?.['X-Demo-Role'];if(!['Admin','Editor','Viewer'].includes(role))fail(403,'Invalid demo role.');
      const method=options.method||'GET',body=options.body?JSON.parse(options.body):null;
      const type=parts[2],schema=schemas.find(t=>t.name===type);
      let result;
      if(parts[1]==='meta')result={name:'Loom',version:'0.2',schemaHash:fingerprint(),designer:role==='Admin',actor:{id:'demo-'+role.toLowerCase(),role},provider:{name:'Browser preview',durable:false,capabilities:['UI demonstration']},types:schemas.map(t=>({...t,actions:t.permissions[role]||[]}))};
      else if(parts[1]==='designer'){
        if(role!=='Admin')fail(403,'Only administrators can edit models.');
        if(parts[2]==='history')result=state.migrations;
        else if(parts[2]==='export')return new Response(JSON.stringify(schemas,null,2),{headers:{'Content-Type':'application/json'}});
        else if(parts[2]==='plan'){
          if(body.sourceHash!==fingerprint())fail(409,'Model changed. Reopen the designer.');
          const old=schemas.find(s=>s.name===body.type);if(!old)fail(404,'Missing model.');
          if(body.fields.length!==old.fields.length)fail(400,'Preserve all field identities.');
          const next=clone(old),changes=[],sql=[];
          for(const f of next.fields){const edit=body.fields.find(e=>e.id===f.id);if(!edit)fail(400,'Missing permanent field identity.');
            if(f.name!==edit.name){if(old.name==='WorkOrder'&&['hours','rate','estimate'].includes(f.name))fail(422,f.name+' is used by a C# hook. Update its dependencies first.');if(old.fields.some(o=>o.id!==f.id&&o.name.toLowerCase()===edit.name.toLowerCase()))fail(422,'Choose a new unused field name.');changes.push({kind:'RenameField',type:old.name,fieldId:f.id,description:'Rename '+f.name+' → '+edit.name+'; preserve values.'});sql.push('ALTER TABLE "'+old.name+'" RENAME COLUMN "'+f.name+'" TO "'+edit.name+'";');if(next.titleField===f.name)next.titleField=edit.name;f.name=edit.name}
            if(f.label!==edit.label){changes.push({kind:'ChangeLabel',type:old.name,fieldId:f.id,description:'Caption: '+f.label+' → '+edit.label+'; no column change.'});f.label=edit.label}
          }
          for(const added of body.additions){if(!['Text','Integer','Decimal','Date','Boolean'].includes(added.type))fail(422,'Choose an optional scalar type.');const f={...added,id:added.id||id().replaceAll('-','')};next.fields.push(f);changes.push({kind:'AddField',type:old.name,fieldId:f.id,description:'Add optional '+f.name+'; existing values start empty.'});sql.push('ALTER TABLE "'+old.name+'" ADD COLUMN "'+f.name+'" '+(['Integer','Boolean'].includes(f.type)?'INTEGER':'TEXT')+';')}
          for(const f of next.fields)if(!/^[A-Za-z][A-Za-z0-9_]{0,62}$/.test(f.name)||['id','version','state','createdat','modifiedat'].includes(f.name.toLowerCase())||!f.label.trim())fail(422,'Use valid non-reserved names and nonempty captions.');
          if(new Set(next.fields.map(f=>f.name.toLowerCase())).size!==next.fields.length)fail(422,'Duplicate field name.');
          next.version++;changes.push({kind:'ModelVersion',type:old.name,description:'Model version '+old.version+' → '+next.version});
          result={id:id(),sourceHash:fingerprint(),changes,sql,rows:Object.fromEntries(schemas.map(t=>[t.name,state.docs.filter(d=>d.type===t.name).length])),target:schemas.map(t=>t.id===old.id?next:clone(t))};pending.set(result.id,clone(result));
        }else if(parts[2]==='apply'){
          const p=pending.get(body.planId);if(!p||p.sourceHash!==fingerprint())fail(409,'Preview the change again.');const before=clone(schemas);
          for(const type of p.target){const old=schemas.find(t=>t.id===type.id);for(const d of state.docs.filter(d=>d.type===type.name)){const values={};for(const f of type.fields){const previous=old.fields.find(o=>o.id===f.id);values[f.name]=previous?d.values[previous.name]??null:null}d.values=values}}
          schemas=clone(p.target);state.models=schemas;state.migrations.push({id:p.id,actor:'demo-admin',at:stamp(),changes:p.changes,before,after:clone(schemas)});pending.delete(body.planId);result={schemaHash:fingerprint(),message:'Offline model updated.'};
        }else fail(404,'Unknown designer action.');
      }
      else{
        if(!schema)fail(404,'Unknown document type.');
        const auth=action=>{if(!schema.permissions[role]?.includes(action))fail(403,'This role cannot '+action+' '+schema.label+'.')};
        const doc=state.docs.find(d=>d.type===type&&d.id===parts[3]);
        const requireDoc=()=>{if(!doc)fail(404,'Document not found.');return doc};
        const audit=(action,before,after)=>state.audit.push({id:id(),type,documentId:(after||before).id,actor:'demo-'+role.toLowerCase(),action,at:stamp(),before:clone(before),after:clone(after)});
        const checkVersion=version=>{requireDoc();if(doc.version!==version)fail(409,'Document changed. Reload before saving.')};
        const validate=values=>{
          for(const name of Object.keys(values))if(!schema.fields.some(f=>f.name===name&&!f.readOnly))fail(400,'Unknown or read-only field: '+name);
          const output={};for(const f of schema.fields){if(f.readOnly)continue;const raw=values[f.name],v=raw==null?'':String(raw).trim();if(!v){if(f.required)fail(422,f.label+' is required.');output[f.name]=null;continue}
            if(f.type==='Decimal'||f.type==='Integer'){const n=Number(v);if(!Number.isFinite(n)||(f.type==='Integer'&&!Number.isInteger(n)))fail(422,f.label+' must be numeric.');output[f.name]=n}
            else if(f.type==='Boolean'){if(!['true','false'].includes(v))fail(422,f.label+' must be true or false.');output[f.name]=v==='true'}
            else{if(f.type==='Select'&&!f.options.includes(v))fail(422,'Invalid '+f.label);if(f.type==='Link'&&!state.docs.some(d=>d.type===f.target&&d.id===v))fail(422,'Missing linked document.');if(f.type==='Date'&&!/^\d{4}-\d{2}-\d{2}$/.test(v))fail(422,'Invalid date.');output[f.name]=v}
            if(f.unique&&state.docs.some(d=>d.type===type&&d.id!==doc?.id&&d.values[f.name]===output[f.name]))fail(409,f.label+' must be unique.');
          }
          if(type==='WorkOrder'){if(!(output.hours>0&&output.hours<=10000))fail(422,'Estimated hours must be greater than zero and no more than 10,000.');if(!(output.rate>=0&&output.rate<=100000))fail(422,'Hourly rate must be between 0 and 100,000.');output.estimate=Math.round(output.hours*output.rate*100)/100}
          return output;
        };
        if(parts[4]==='audit'){auth('audit');result=state.audit.filter(e=>e.type===type&&e.documentId===parts[3])}
        else if(parts[1]==='reports'||(method==='GET'&&!parts[3])){
          auth('read');let docs=state.docs.filter(d=>d.type===type);const search=q.get('search')?.toLowerCase();if(search)docs=docs.filter(d=>Object.values(d.values).some(v=>String(v).toLowerCase().includes(search)));if(q.has('field'))docs=docs.filter(d=>String(d.values[q.get('field')])===q.get('equals'));
          const sort=q.get('sort')||schema.titleField,dir=q.get('descending')==='true'?-1:1;docs.sort((a,b)=>{const x=a.values[sort],y=b.values[sort];return dir*(typeof x==='number'?x-y:String(x??'').localeCompare(String(y??'')))});
          if(parts[3]==='csv'){const csv=v=>'"'+String(v??'').replace(/^[=+\-@\t\r]/,"'$&").replaceAll('"','""')+'"';return new Response([schema.fields.map(f=>csv(f.label)).join(','),...docs.map(d=>schema.fields.map(f=>csv(d.values[f.name])).join(','))].join('\n'),{headers:{'Content-Type':'text/csv'}})}
          const skip=Number(q.get('skip')||0),take=Number(q.get('take')||50);result={total:docs.length,items:docs.slice(skip,skip+take)};
        }else if(method==='GET'){auth('read');result=requireDoc()}
        else if(method==='POST'&&!parts[3]){auth('create');const d=record(type,id(),validate(body));state.docs.push(d);audit('create',null,d);result=d}
        else if(method==='PUT'){auth('write');checkVersion(body.version);if(doc.state!=='Draft')fail(409,'Only draft documents can be edited.');const before=clone(doc);doc.values=validate(body.values);doc.version++;doc.modifiedAt=stamp();audit('update',before,doc);result=doc}
        else if(method==='POST'&&parts[4]==='actions'){const action=parts[5];auth(action);checkVersion(body.version);if(!schema.submittable||doc.state!==(action==='submit'?'Draft':'Submitted'))fail(409,'Invalid transition.');const before=clone(doc);doc.state=action==='submit'?'Submitted':'Cancelled';doc.version++;doc.modifiedAt=stamp();audit(action,before,doc);result=doc}
        else if(method==='DELETE'){auth('delete');checkVersion(Number(q.get('version')));if(doc.state!=='Draft')fail(409,'Only drafts can be deleted.');for(const t of schemas)for(const f of t.fields.filter(f=>f.type==='Link'&&f.target===type))if(state.docs.some(d=>d.type===t.name&&d.values[f.name]===doc.id))fail(409,'Document is referenced; remove links first.');state.docs=state.docs.filter(d=>d!==doc);audit('delete',doc,null);persist();return new Response(null,{status:204})}
        else fail(400,'Unsupported preview operation.');
      }
      persist();return new Response(JSON.stringify(result),{headers:{'Content-Type':'application/json'}});
    }catch(e){state=snapshot;schemas=state.models;return new Response(JSON.stringify({error:e.message||'Preview error.'}),{status:e.status||500,headers:{'Content-Type':'application/json'}})}
  };
  document.querySelector('.banner').replaceChildren(document.createTextNode('Offline UI preview · JavaScript/browser storage, not the C# server. No data leaves this page. '));
  const reset=document.createElement('button');reset.textContent='Reset sample';reset.onclick=()=>{if(confirm('Reset the offline sample and discard its changes?')){state=seed();persist();location.reload()}};document.querySelector('.banner').append(reset);
  document.querySelector('.bottom-note').textContent='This preview demonstrates the generated interface. Run the included .NET project to exercise C# hooks, SQLite and transactional guarantees.';
})();
