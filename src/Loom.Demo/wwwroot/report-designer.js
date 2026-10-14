'use strict';
let reportDraft, reportType, reportTimer, reportRequest=0;
const reportModel=()=>meta.types.find(t=>t.name===reportType);
function reportError(error){$('reportError').textContent=error?.message||''}
function fieldOption(field){const option=element('option',field.label);option.value=field.id;return option}
function reportChanged(){ $('reportStatus').textContent='Unsaved changes';clearTimeout(reportTimer);reportTimer=setTimeout(previewReport,400); }
function reportControls(){
 const m=reportModel(),d=reportDraft;
 $('reportPalette').replaceChildren(...m.fields.map(f=>{const b=element('button',f.label);b.draggable=true;b.disabled=d.definition.columns.some(c=>c.fieldId===f.id);b.onclick=()=>addReportField(f.id);b.ondragstart=e=>e.dataTransfer.setData('text/plain',f.id);return b}));
 $('reportGroup').replaceChildren(new Option('No grouping',''),...m.fields.filter(f=>d.definition.columns.some(c=>c.fieldId===f.id)).map(fieldOption));$('reportGroup').value=d.layout.groupFieldId||'';
 $('reportSort').replaceChildren(new Option('Default order',''),...m.fields.map(fieldOption));$('reportSort').value=d.definition.sortFieldId||'';
 $('reportSums').replaceChildren(...m.fields.filter(f=>['Decimal','Integer','Money'].includes(f.type)&&d.definition.columns.some(c=>c.fieldId===f.id)).map(f=>{const label=element('label',f.label),check=element('input');check.type='checkbox';check.checked=(d.layout.sumFieldIds||[]).includes(f.id);check.onchange=()=>{d.layout.sumFieldIds=check.checked?[...(d.layout.sumFieldIds||[]),f.id]:(d.layout.sumFieldIds||[]).filter(id=>id!==f.id);reportChanged()};label.prepend(check);return label}));
 $('reportColumns').replaceChildren(...d.definition.columns.map((col,i)=>{
  const field=m.fields.find(f=>f.id===col.fieldId),row=element('div',undefined,'report-column');row.draggable=true;row.dataset.fieldId=col.fieldId;
  row.ondragstart=e=>e.dataTransfer.setData('text/plain',col.fieldId);row.ondragover=e=>e.preventDefault();row.ondrop=e=>{e.preventDefault();e.stopPropagation();moveReportField(e.dataTransfer.getData('text/plain'),i)};
  row.append(element('strong',field?.label||'Missing field'));
  const heading=element('input');heading.value=col.heading??'';heading.placeholder=field?.label||'';heading.setAttribute('aria-label','Heading for '+field?.name);heading.oninput=()=>{col.heading=heading.value||null;reportChanged()};row.append(heading);
  const width=element('input');width.type='range';width.min=1;width.max=100;width.value=col.width; width.setAttribute('aria-label','Width for '+field?.name);width.oninput=()=>{col.width=Number(width.value);reportChanged()};row.append(width);
  const align=element('select');for(const value of ['auto','left','center','right'])align.add(new Option(value,value));align.value=col.alignment;align.setAttribute('aria-label','Alignment for '+field?.name);align.onchange=()=>{col.alignment=align.value;reportChanged()};row.append(align);
  for(const [text,delta] of [['↑',-1],['↓',1]]){const b=element('button',text);b.setAttribute('aria-label',(delta<0?'Move up ':'Move down ')+field?.name);b.disabled=i+delta<0||i+delta>=d.definition.columns.length;b.onclick=()=>{const list=d.definition.columns;[list[i],list[i+delta]]=[list[i+delta],list[i]];reportControls();reportChanged()};row.append(b)}
  const remove=element('button','×');remove.setAttribute('aria-label','Remove '+field?.name);remove.onclick=()=>{d.definition.columns.splice(i,1);if(d.layout.groupFieldId===col.fieldId)d.layout.groupFieldId=null;d.layout.sumFieldIds=(d.layout.sumFieldIds||[]).filter(id=>id!==col.fieldId);reportControls();reportChanged()};row.append(remove);return row;
 }));
 renderReportText();
}
function addReportField(id){if(reportDraft.definition.columns.some(c=>c.fieldId===id)||!reportModel().fields.some(f=>f.id===id))return;reportDraft.definition.columns.push({fieldId:id,heading:null,width:20,alignment:'auto'});reportControls();reportChanged()}
function moveReportField(id,index){const list=reportDraft.definition.columns,old=list.findIndex(c=>c.fieldId===id);if(old<0){addReportField(id);const fresh=list.findIndex(c=>c.fieldId===id);if(fresh<0)return;list.splice(index,0,list.splice(fresh,1)[0])}else list.splice(index,0,list.splice(old,1)[0]);reportControls();reportChanged()}
$('reportColumns').ondragover=e=>e.preventDefault();$('reportColumns').ondrop=e=>{e.preventDefault();addReportField(e.dataTransfer.getData('text/plain'))};
async function previewReport(){const request=++reportRequest;try{const result=await api('report-designs/'+reportType+'/preview','POST',reportDraft);if(request!==reportRequest)return;$('reportFrame').srcdoc=result.html;reportError(null)}catch(e){if(request===reportRequest)reportError(e)}}
$('layoutReport').onclick=async()=>{try{reportType=type;reportDraft=await api('report-designs/'+type);for(const [id,key] of [['reportTitle','title'],['reportSubtitle','subtitle'],['reportFooter','footer'],['reportFont','font'],['reportSize','fontSize'],['reportPage','orientation'],['reportPageHeader','pageHeader'],['reportPageFooter','pageFooter']])$(id).value=reportDraft.layout[key]??'';reportControls();$('reportPageNumbers').checked=!!reportDraft.layout.pageNumbers;reportTextSelection=null;renderReportText();$('reportStatus').textContent=reportDraft.version?'Saved layout v'+reportDraft.version:'Default layout';$('reportEditor').showModal();await previewReport()}catch(e){showError(e)}};
for(const [id,key] of [['reportTitle','title'],['reportSubtitle','subtitle'],['reportFooter','footer'],['reportFont','font'],['reportSize','fontSize'],['reportPage','orientation'],['reportPageHeader','pageHeader'],['reportPageFooter','pageFooter']])$(id).oninput=()=>{reportDraft.layout[key]=key==='fontSize'?Number($(id).value):$(id).value;reportChanged()};
$('reportGroup').onchange=()=>{reportDraft.layout.groupFieldId=$('reportGroup').value||null;reportChanged()};$('reportSort').onchange=()=>{reportDraft.definition.sortFieldId=$('reportSort').value||null;reportChanged()};
$('reportPreview').onclick=previewReport;
$('reportSave').onclick=async()=>{try{const sent=JSON.stringify(reportDraft),saved=await api('report-designs/'+reportType,'PUT',reportDraft),unchanged=JSON.stringify(reportDraft)===sent;reportDraft.version=saved.version;$('reportStatus').textContent=unchanged?'Saved layout v'+saved.version:'Unsaved changes';reportError(null)}catch(e){reportError(e)}};
$('reportClose').onclick=()=>{clearTimeout(reportTimer);reportRequest++;$('reportEditor').close()};

$('reportPageNumbers').onchange=()=>{reportDraft.layout.pageNumbers=$('reportPageNumbers').checked;reportChanged()};
