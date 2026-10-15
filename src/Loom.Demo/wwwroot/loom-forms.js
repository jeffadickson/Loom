'use strict';
// Small shared renderer for Loom's role-filtered DocumentForm contract.
// It keeps exact numbers as strings. Server normalization/authorization remain authoritative.
window.LoomForms=(()=>{
 const escape=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 function options(rows){return rows.map(d=>'<option value="'+escape(d.id)+'">'+escape(d.values.Name||d.id)+'</option>').join('');}
 function html(definition,records={}){
  return definition.fields.filter(f=>!f.readOnly).map(f=>{
   const attrs=' name="'+escape(f.name)+'" data-field-id="'+escape(f.id)+'"'+(f.required&&f.type!=='Boolean'?' required':'')+
    (f.maxLength?' maxlength="'+f.maxLength+'"':'');
   let control;
   if(f.type==='Link')control='<input type="search" data-link-search="'+escape(f.name)+'" data-target="'+escape(f.target)+'" aria-label="Find '+escape(f.label)+'" placeholder="Search records…"><select'+attrs+'>'+options(records[f.target]||[])+'</select>';
   else if(f.type==='Select')control='<select'+attrs+'>'+f.options.map(v=>'<option'+(v===f.default?' selected':'')+'>'+escape(v)+'</option>').join('')+'</select>';
   else if(f.type==='Boolean')control='<input type="checkbox"'+attrs+(String(f.default).toLowerCase()==='true'?' checked':'')+'>';
   else if(f.type==='Binary')control='<input type="file"'+attrs+'>';
   else if(f.type==='Measurement')control='<textarea'+attrs+'></textarea>';
   else control='<input type="'+(f.type==='Date'?'date':'text')+'"'+attrs+
     (['Integer','Decimal','Money'].includes(f.type)?' inputmode="decimal"':'')+' value="'+escape(f.default||'')+'">';
   const bounds=[f.currency,f.minimum!=null?'Minimum '+f.minimum:null,f.maximum!=null?'Maximum '+f.maximum:null,
      f.scale!=null?'Up to '+f.scale+' decimal places':null].filter(Boolean).join(' · ');
   return '<label>'+escape(f.label)+control+(f.description?'<span class="help">'+escape(f.description)+'</span>':'')+
      (bounds?'<span class="help">'+escape(bounds)+'</span>':'')+'</label>';
  }).join('');
 }
 async function read(element,definition){
  const data=new FormData(element),values={};
  for(const f of definition.fields.filter(f=>!f.readOnly)){
   let value=data.get(f.name);
   if(f.type==='Boolean')value=element.elements.namedItem(f.name).checked;
   else if(f.type==='Binary'){
    if(!value.size)value=null;
    else {
     if(value.size>f.maxBytes)throw Error(f.label+' exceeds its byte limit.');
     const bytes=new Uint8Array(await value.arrayBuffer());let binary='';
     for(let i=0;i<bytes.length;i+=8192)binary+=String.fromCharCode(...bytes.subarray(i,i+8192));
     value=btoa(binary);
    }
   }else if(value===''&&!f.required)value=null;
   else if(f.type==='Measurement')value=JSON.parse(value);
   values[f.name]=value;
  }
  return values;
 }
 function bindLinks(element,lookup,onError){
  element.querySelectorAll('[data-link-search]').forEach(input=>{
   let timer,sequence=0;
   input.oninput=()=>{
    clearTimeout(timer);const current=++sequence;
    timer=setTimeout(async()=>{
     try {
      const rows=await lookup(input.dataset.target,input.value);
      if(current!==sequence||!input.isConnected)return;
      element.elements.namedItem(input.dataset.linkSearch).innerHTML=options(rows);
     }catch(e){if(current===sequence)onError(e);}
    },200);
   };
  });
 }
 return {html,read,bindLinks};
})();
