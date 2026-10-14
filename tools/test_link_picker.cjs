const {chromium}=require('playwright');
(async()=>{
 const browser=await chromium.launch({executablePath:process.env.CHROMIUM_EXECUTABLE,headless:true,args:['--no-sandbox']});
 const page=await browser.newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));
 const headers={'X-Loom-Demo':'local','X-Demo-Role':'Admin'};
 let last;
 for(let i=0;i<205;i++){
  const response=await page.request.post('http://127.0.0.1:5080/api/documents/Asset',{headers,data:{name:'Paged asset '+String(i).padStart(3,'0'),tag:'PAGED-'+i,category:'Electronics',location:'Lab',active:true}});
  if(!response.ok())throw Error(await response.text());last=await response.json();
 }
 const catalogPage=await (await page.request.get('http://127.0.0.1:5080/api/documents/Asset?take=1',{headers})).json();
 await page.goto('http://127.0.0.1:5080');
 await page.getByRole('button',{name:'Work orders',exact:true}).click();
 await page.locator('#create').click();await page.locator('#editor').waitFor({state:'visible'});
 const choices=page.locator('[name="asset"]');
 if(await choices.locator('option').count()>51)throw Error('Unbounded initial link page');
 await page.getByRole('button',{name:'Next Equipment choices',exact:true}).click();
 await page.getByText('51–100 of '+catalogPage.total,{exact:true}).waitFor();
 await page.getByRole('searchbox',{name:'Search Equipment links',exact:true}).fill('Paged asset 204');
 await page.getByText('1–1 of 1',{exact:true}).waitFor();
 await choices.selectOption(last.id);
 await page.locator('[name="title"]').fill('Linked beyond first 200');
 await page.locator('[name="priority"]').selectOption('Routine');
 await page.locator('[name="due"]').fill('2026-11-02');await page.locator('[name="hours"]').fill('1');await page.locator('[name="rate"]').fill('100');
 await page.locator('#save').click();await page.locator('#editor').waitFor({state:'hidden'});
 await page.getByRole('button',{name:'Linked beyond first 200',exact:true}).click();await page.locator('#editor').waitFor({state:'visible'});
 if(await choices.inputValue()!==last.id)throw Error('Off-page selection lost on reopen');
 await page.getByRole('searchbox',{name:'Search Equipment links',exact:true}).fill('no such asset');
 await page.getByText('No matching records',{exact:true}).waitFor();
 if(await choices.inputValue()!==last.id)throw Error('Search discarded selection');
 if(errors.length)throw Error(errors.join('\n'));
 console.log('PASS paged links: bounded pages, search beyond 200, off-page selection retained on reopen and empty search');
 await browser.close();
})().catch(e=>{console.error(e);process.exit(1)});
