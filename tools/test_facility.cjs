// Real browser contract: cookie authentication, scoped actions, raw evidence, independent review and release.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs=require('node:fs');
(async()=>{
 const browser=await chromium.launch({headless:true}),page=await browser.newPage({viewport:{width:1440,height:1000}});
 const errors=[];page.on('pageerror',e=>errors.push(e.message));
 const origin=process.env.FACILITY_ORIGIN||'http://127.0.0.1:5091';
 try{
  await page.goto(origin+'/facility.html');
  async function login(user){
   await page.locator('#demo-user').selectOption(user);
   await page.locator('#login-form button').click();
   await page.locator('#app').waitFor({state:'visible'});
   await page.locator('#identity').filter({hasText:user}).waitFor({state:'visible'});
  }
  async function logout(){await page.locator('#signout').click();await page.locator('#login').waitFor({state:'visible'});}
  await login('north-manager');
  await page.locator('[data-request]').first().waitFor();
  const id=await page.locator('[data-request]').first().getAttribute('data-request');
  if(process.env.FACILITY_SCREENSHOT_DIR){fs.mkdirSync(process.env.FACILITY_SCREENSHOT_DIR,{recursive:true});await page.screenshot({path:process.env.FACILITY_SCREENSHOT_DIR+'/facility-desktop.png',fullPage:true});}
  // Spoofed roles cannot cross a laboratory boundary or mutate controlled workflow types.
  const denied=await page.request.get(origin+'/api/facility/south/overview',{headers:{'X-Loom-Demo':'local','X-Demo-Role':'Admin'}});
  assert.equal(denied.status(),403);
  const bypass=await page.request.post(origin+'/api/facility/north/register/Request',{headers:{'X-Loom-Demo':'local'},data:{}});
  assert.equal(bypass.status(),403);
  await page.getByRole('button',{name:'Facility register',exact:false}).click();
  await page.getByRole('button',{name:'Add instrument',exact:true}).click();
  await page.getByLabel('Instrument name',{exact:true}).fill('Browser-created reference meter');
  await page.getByLabel('Serial number',{exact:true}).fill('BROWSER-001');
  await page.getByLabel('Location',{exact:true}).fill('Metrology bench');
  await page.getByRole('button',{name:'Save instrument',exact:true}).click();
  await page.locator('#dialog').waitFor({state:'hidden'});
  await page.getByText('Browser-created reference meter',{exact:true}).waitFor();
  await page.getByRole('button',{name:'Test requests',exact:false}).click();
  await page.getByRole('button',{name:'New test request',exact:true}).click();
  await page.getByLabel('Request title',{exact:true}).fill('TR-002 · Follow-up stability');
  await page.getByLabel('Reservation start (UTC)',{exact:true}).fill(new Date(Date.now()+9*3600000).toISOString().slice(0,16));
  await page.getByLabel('Reservation end (UTC)',{exact:true}).fill(new Date(Date.now()+10*3600000).toISOString().slice(0,16));
  await page.getByLabel('Estimated cost (USD)',{exact:true}).fill('1234567890123.45');
  // The new meter sorts first; choose the calibrated chamber explicitly.
  await page.locator('[name=Instrument]').selectOption({label:'Thermal chamber TC-04'});
  await page.getByRole('button',{name:'Reserve and create request',exact:true}).click();
  await page.locator('#dialog').waitFor({state:'hidden'});
  await page.getByRole('button',{name:'TR-002 · Follow-up stability',exact:true}).waitFor();
  await logout();await login('north-operator');
  await page.getByRole('button',{name:'Test requests',exact:false}).click();
  await page.locator('[data-request="'+id+'"]').click();
  assert.equal(await page.locator('#dialog').getByText('Estimated cost (USD)',{exact:true}).count(),0);
  await page.getByRole('button',{name:'Start test',exact:true}).click();
  await page.getByRole('button',{name:'Record results',exact:true}).click();
  await page.getByLabel('Measured value (exact decimal)',{exact:true}).fill('20.125');
  await page.getByLabel('Acquisition notes',{exact:true}).fill('Stable reference after equilibration.');
  await page.locator('input[type=file]').setInputFiles({name:'waveform.csv',mimeType:'text/csv',buffer:Buffer.from('time,value\n0,20.125\n1,20.125\n')});
  await page.getByRole('button',{name:'Save evidence and submit for review',exact:true}).click();
  await page.locator('#dialog').getByText('Awaiting review',{exact:true}).waitFor();
  await page.locator('#close-dialog').click();await logout();await login('north-reviewer');
  await page.locator('[data-request="'+id+'"]').click();await page.getByRole('button',{name:'Review results',exact:true}).click();
  await page.getByLabel('Review findings / required changes',{exact:true}).fill('Evidence independently checked against procedure C.');
  await page.getByRole('button',{name:'Approve results',exact:true}).click();
  await page.locator('#dialog').getByText('Approved',{exact:true}).waitFor();
  await page.locator('#close-dialog').click();await logout();await login('north-manager');
  await page.locator('[data-request="'+id+'"]').click();await page.getByRole('button',{name:'Queue release report',exact:true}).click();
  await page.locator('#dialog').getByText('Report queued',{exact:true}).waitFor();
  await page.locator('#close-dialog').click();
  // The test host may pause the worker to verify process restart before report publication.
  if(process.env.FACILITY_PAUSED==='1'){assert.deepEqual(errors,[]);console.log('PASS browser request-to-queued-release workflow');return;}
  for(let i=0;i<40;i++){
   const overview=await page.request.get(origin+'/api/facility/north/overview',{headers:{'X-Loom-Demo':'local'}});
   if((await overview.json()).records.Release.length)break;
   await page.waitForTimeout(250);
  }
  await page.locator('#refresh').click();await page.getByRole('button',{name:'Reports & queue',exact:false}).click();
  const report=page.getByRole('button',{name:'Download report',exact:true});await report.waitFor();
  const downloadPromise=page.waitForEvent('download');await report.click();const download=await downloadPromise;
  const html=fs.readFileSync(await download.path(),'utf8');
  assert.match(html,/20.125/);assert.match(html,/north-reviewer/);assert.match(html,/SYNTHETIC-CAL/);
  await page.setViewportSize({width:390,height:844});await page.getByRole('button',{name:'Test requests',exact:false}).click();
  assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
  if(process.env.FACILITY_SCREENSHOT_DIR)await page.screenshot({path:process.env.FACILITY_SCREENSHOT_DIR+'/facility-mobile.png',fullPage:true});
  await logout();await login('south-manager');await page.locator('#content .badge.Scheduled').waitFor();
  assert.equal(await page.locator('[data-request="'+id+'"]').count(),0);
  assert.deepEqual(errors,[]);
  console.log('PASS facility browser: registers, execution, evidence, independent review, release, mobile layout and tenant isolation');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1)});
