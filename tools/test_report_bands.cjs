const {chromium}=require('playwright'),assert=require('node:assert/strict'),fs=require('node:fs');
(async()=>{
 const browser=await chromium.launch({headless:true,executablePath:process.env.CHROMIUM_EXECUTABLE||undefined,args:['--no-sandbox']});
 const page=await browser.newPage({viewport:{width:1440,height:1100}});const headers={'X-Loom-Demo':'local','X-Demo-Role':'Admin'};
 await page.goto('http://127.0.0.1:5080');await page.locator('#layoutReport').waitFor({state:'visible'});await page.locator('#layoutReport').click();await page.locator('#reportEditor').waitFor({state:'visible'});
 await page.locator('#reportPageHeader').fill('LABORATORY EQUIPMENT - CONTROLLED REGISTER');await page.locator('#reportPageFooter').fill('Engineering copy');await page.locator('#reportPageNumbers').check();
 await page.locator('#reportTextEditor summary').click();await page.locator('#reportAddText').click();await page.locator('#reportBlockText').fill('Calibration notes');
 const block=page.locator('.canvas-text');const box=await block.boundingBox();await page.mouse.move(box.x+10,box.y+10);await page.mouse.down();await page.mouse.move(box.x+140,box.y+40,{steps:8});await page.mouse.up();
 assert.ok(Number(await page.locator('#reportBlockX').inputValue())>0);assert.ok(Number(await page.locator('#reportBlockY').inputValue())>0);
 await page.locator('#reportBand').selectOption('footerBlocks');await page.locator('#reportAddText').click();await page.locator('#reportBlockText').fill('Approved by: __________________');
 await page.locator('#reportPreview').click();await page.frameLocator('#reportFrame').getByText('Approved by: __________________',{exact:true}).waitFor();
 await page.locator('#reportSave').click();await page.getByText('Saved layout v1',{exact:true}).waitFor();await page.locator('#reportClose').click();
 console.log('PASS bands: pointer placement, report-start/end notes and save');
 for(let i=0;i<95;i++){const response=await page.request.post('http://127.0.0.1:5080/api/documents/Asset',{headers,data:{name:'Instrument '+String(i).padStart(3,'0'),tag:'PRINT-'+i,category:'Electronics',location:'Measurement laboratory',active:true}});assert.equal(response.status(),201)}
 const download=await page.request.get('http://127.0.0.1:5080/api/reports/Asset/print',{headers});assert.equal(download.status(),200);const html=await download.text();fs.writeFileSync('/tmp/loom-multipage-report.html',html);
 await page.goto('file:///tmp/loom-multipage-report.html');await page.pdf({path:'/tmp/loom-multipage-report.pdf',preferCSSPageSize:true,printBackground:true,displayHeaderFooter:false});
 console.log('PASS bands: 98-record report printed to PDF from actual server HTML');
 await page.goto('http://127.0.0.1:5080');await page.locator('#layoutReport').waitFor({state:'visible'});await page.locator('#layoutReport').click();await page.locator('#reportEditor').waitFor({state:'visible'});
 assert.equal(await page.locator('#reportPageHeader').inputValue(),'LABORATORY EQUIPMENT - CONTROLLED REGISTER');assert.equal(await page.locator('#reportPageNumbers').isChecked(),true);
 await page.locator('#reportTextEditor summary').click();assert.match(await page.locator('#reportTextCanvas').innerText(),/Calibration notes/);
 console.log('PASS bands: page settings and positioned blocks survive reload');await browser.close();
})().catch(e=>{console.error(e);process.exit(1)});
