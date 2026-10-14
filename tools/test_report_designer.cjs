const {chromium}=require('playwright'),assert=require('node:assert/strict');
(async()=>{
 const browser=await chromium.launch({headless:true,executablePath:process.env.CHROMIUM_EXECUTABLE||undefined,args:['--no-sandbox']});
 const page=await browser.newPage({viewport:{width:1450,height:1050}});const errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto('http://127.0.0.1:5080');await page.locator('#layoutReport').waitFor({state:'visible'});await page.locator('#layoutReport').click();
 await page.locator('#reportTitle').fill('Laboratory equipment register');await page.locator('#reportFooter').fill('Engineering · controlled copy');
 await page.locator('#reportFont').selectOption('serif');await page.locator('#reportPage').selectOption('portrait');
 await page.getByRole('button',{name:'Remove active',exact:true}).click();await page.locator('#reportPalette').getByRole('button',{name:'In service',exact:true}).dragTo(page.locator('#reportColumns'));
 await page.getByRole('button',{name:'Move up tag',exact:true}).click();await page.getByLabel('Heading for tag',{exact:true}).fill('Serial / tag');await page.getByLabel('Alignment for tag',{exact:true}).selectOption('center');
 await page.getByLabel('Width for tag',{exact:true}).focus();await page.keyboard.press('Home');for(let n=0;n<34;n++)await page.keyboard.press('ArrowRight');
 await page.locator('#reportPreview').click();const frame=page.frameLocator('#reportFrame');await frame.getByRole('heading',{name:'Laboratory equipment register',exact:true}).waitFor();
 assert.equal(await frame.locator('th').first().innerText(),'Serial / tag');assert.match(await frame.locator('body').innerText(),/LAB-001/);
 // Check actual CSP permits preview typography; this browser context does NOT bypass CSP.
 assert.match(await frame.locator('body').evaluate(el=>getComputedStyle(el).fontFamily),/Georgia/);
 console.log('PASS visual layout: palette, reorder, headings, alignment and styled server preview');
 await page.screenshot({path:'/tmp/loom-layout-designer.png',fullPage:true});
 await page.locator('#reportSave').click();await page.getByText('Saved layout v1',{exact:true}).waitFor();await page.locator('#reportClose').click();await page.reload();await page.locator('#layoutReport').click();await page.locator('#reportEditor').waitFor({state:'visible'});
 assert.equal(await page.locator('#reportTitle').inputValue(),'Laboratory equipment register');assert.equal(await page.locator('#reportPage').inputValue(),'portrait');
 console.log('PASS visual layout: saved layout persists across reload');
 await page.locator('#reportClose').click();const response=await page.request.get('http://127.0.0.1:5080/api/reports/Asset/print',{headers:{'X-Loom-Demo':'local','X-Demo-Role':'Viewer'}});assert.match(await response.text(),/Laboratory equipment register/);
 const denied=await page.request.get('http://127.0.0.1:5080/api/report-designs/Asset',{headers:{'X-Loom-Demo':'local','X-Demo-Role':'Viewer'}});assert.equal(denied.status(),403);
 assert.deepEqual(errors,[]);console.log('PASS visual layout: saved output, viewer restriction and no runtime errors');await browser.close();
})().catch(e=>{console.error(e);process.exit(1)});
