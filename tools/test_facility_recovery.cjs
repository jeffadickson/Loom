const {chromium}=require('playwright'),assert=require('node:assert/strict'),fs=require('node:fs');
(async()=>{
 const browser=await chromium.launch({headless:true}),page=await browser.newPage({viewport:{width:1440,height:1000}});
 try {
  await page.goto((process.env.FACILITY_ORIGIN||'http://127.0.0.1:5091')+'/facility.html');
  await page.locator('#demo-user').selectOption('north-manager');await page.locator('#login-form button').click();
  await page.getByText('Released',{exact:true}).last().waitFor();
  if(process.env.FACILITY_SCREENSHOT_DIR)await page.screenshot({path:process.env.FACILITY_SCREENSHOT_DIR+'/facility-released.png',fullPage:true});
  await page.getByRole('button',{name:'Reports & queue',exact:false}).click();
  const promise=page.waitForEvent('download');await page.getByRole('button',{name:'Download report',exact:true}).click();
  const download=await promise;assert.match(fs.readFileSync(await download.path(),'utf8'),/20.125/);
  const packagePromise=page.waitForEvent('download');await page.getByRole('button',{name:'Download evidence package',exact:true}).click();
  const packageDownload=await packagePromise;
  assert.equal(packageDownload.suggestedFilename(),'Test_Evidence.zip');
  assert.equal(fs.readFileSync(await packageDownload.path()).subarray(0,2).toString(),'PK');
  await page.setViewportSize({width:390,height:844});await page.getByRole('button',{name:'Test requests',exact:false}).click();
  assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
  if(process.env.FACILITY_SCREENSHOT_DIR)await page.screenshot({path:process.env.FACILITY_SCREENSHOT_DIR+'/facility-mobile.png',fullPage:true});
  let releaseLate,markSeen;
  const gate=new Promise(resolve=>releaseLate=resolve),seen=new Promise(resolve=>markSeen=resolve);
  await page.route('**/api/facility/north/overview',async route=>{
   const response=await route.fetch();markSeen();await gate;await route.fulfill({response});
  });
  try {
   await page.locator('#refresh').click();await seen;
   await page.locator('#signout').click();await page.locator('#login').waitFor({state:'visible'});
   assert.equal(await page.locator('#metrics').textContent(),'');
   await page.locator('#demo-user').selectOption('south-manager');await page.locator('#login-form button').click();
   await page.locator('#identity').filter({hasText:'south-manager'}).waitFor();
   await page.locator('#content .badge.Scheduled').waitFor();
   const lateResponse=page.waitForResponse(r=>r.url().endsWith('/api/facility/north/overview'));
   releaseLate();await lateResponse;
   await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
   assert.match(await page.locator('#identity').textContent(),/south-manager/);
   assert.equal(await page.locator('#content .badge.Released').count(),0);
   const denied=await page.request.get((process.env.FACILITY_ORIGIN||'http://127.0.0.1:5091')+'/api/facility/north/releases/unknown/package');
   assert.equal(denied.status(),403);
  }finally{releaseLate();await page.unroute('**/api/facility/north/overview');}
  console.log('PASS recovered report/package downloads, mobile layout, delayed response isolation and denied export');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1)});
