'use strict';
let reportTextSelection=null;
const reportBlocks=()=>reportDraft.layout[$('reportBand').value]??=[];
const blockCaption=b=>b.text+((b.source||'text')==='count'?'[Record count]':b.source==='sum'?'[Total: '+(reportModel().fields.find(f=>f.id===b.fieldId)?.label||'Choose field')+']':'');
const selectedReportBlock=()=>reportBlocks().find(b=>b.id===reportTextSelection);
function renderReportText(){
 const selected=selectedReportBlock();
 $('reportTextCanvas').replaceChildren(...reportBlocks().map(block=>{
  const item=element('button',blockCaption(block),'canvas-text');item.type='button';item.dataset.block=block.id;item.classList.toggle('selected',block.id===reportTextSelection);
  Object.assign(item.style,{left:block.x+'%',top:block.y+'px',width:block.width+'%',height:block.height+'px',fontSize:block.fontSize+'px'});
  item.onclick=()=>{reportTextSelection=block.id;renderReportText()};
  item.onpointerdown=e=>{if(e.button!==0)return;e.preventDefault();reportTextSelection=block.id;const startX=e.clientX,startY=e.clientY,x=block.x,y=block.y,rect=$('reportTextCanvas').getBoundingClientRect();item.setPointerCapture(e.pointerId);
   item.onpointermove=move=>{block.x=Math.max(0,Math.min(100-block.width,Math.round(x+(move.clientX-startX)*100/rect.width)));block.y=Math.max(0,Math.min(120-block.height,Math.round(y+move.clientY-startY)));item.style.left=block.x+'%';item.style.top=block.y+'px'};
   item.onpointerup=()=>{item.onpointermove=null;item.onpointerup=null;renderReportText();reportChanged()};
   item.onpointercancel=()=>{item.onpointermove=null;item.onpointerup=null;renderReportText();reportChanged()};
  };return item;
 }));
 for(const [id,key] of [['reportBlockText','text'],['reportBlockX','x'],['reportBlockY','y'],['reportBlockWidth','width'],['reportBlockHeight','height'],['reportBlockFont','fontSize']]){$(id).disabled=!selected;$(id).value=selected?.[key]??''}
 $('reportBlockSource').disabled=!selected;$('reportBlockSource').value=selected?.source||'text';
 const fields=reportModel().fields.filter(f=>['Decimal','Integer','Money'].includes(f.type)&&reportDraft.definition.columns.some(c=>c.fieldId===f.id));
 $('reportBlockField').replaceChildren(new Option('Choose field',''),...fields.map(fieldOption));
 $('reportBlockField').disabled=!selected||selected.source!=='sum';$('reportBlockField').value=selected?.fieldId||'';
 $('reportRemoveText').disabled=!selected;
}
$('reportBand').onchange=()=>{reportTextSelection=null;renderReportText()};
$('reportAddText').onclick=()=>{if(reportBlocks().length>=12){reportError(Error('Use up to 12 blocks per band.'));return}const block={id:crypto.randomUUID().replaceAll('-',''),text:'Report note',x:0,y:0,width:40,height:40,fontSize:16};reportBlocks().push(block);reportTextSelection=block.id;renderReportText();reportChanged()};
$('reportRemoveText').onclick=()=>{reportDraft.layout[$('reportBand').value]=reportBlocks().filter(b=>b.id!==reportTextSelection);reportTextSelection=null;renderReportText();reportChanged()};
for(const [id,key] of [['reportBlockText','text'],['reportBlockX','x'],['reportBlockY','y'],['reportBlockWidth','width'],['reportBlockHeight','height'],['reportBlockFont','fontSize']])$(id).oninput=()=>{const block=selectedReportBlock();if(!block)return;block[key]=key==='text'?$(id).value:Number($(id).value);const item=$('reportTextCanvas').querySelector('[data-block="'+block.id+'"]');if(item){item.textContent=blockCaption(block);Object.assign(item.style,{left:block.x+'%',top:block.y+'px',width:block.width+'%',height:block.height+'px',fontSize:block.fontSize+'px'})}reportChanged()};

$('reportBlockSource').onchange=()=>{const b=selectedReportBlock();if(!b)return;b.source=$('reportBlockSource').value;b.fieldId=null;renderReportText();reportChanged()};
$('reportBlockField').onchange=()=>{const b=selectedReportBlock();if(!b)return;b.fieldId=$('reportBlockField').value||null;renderReportText();reportChanged()};
