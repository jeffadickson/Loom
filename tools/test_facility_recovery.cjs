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
  await page.setViewportSize({width:390,height:844});await page.getByRole('button',{name:'Test requests',exact:false}).click();
  assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
  if(process.env.FACILITY_SCREENSHOT_DIR)await page.screenshot({path:process.env.FACILITY_SCREENSHOT_DIR+'/facility-mobile.png',fullPage:true});
  await page.locator('#signout').click();await page.locator('#demo-user').selectOption('south-manager');await page.locator('#login-form button').click();
  await page.locator('#content .badge.Scheduled').waitFor();
  console.log('PASS browser recovered report download, mobile layout and second-lab isolation');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1)});
