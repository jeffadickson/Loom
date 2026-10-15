const {chromium}=require('playwright');const assert=require('node:assert/strict');const fs=require('node:fs');
(async()=>{const browser=await chromium.launch({headless:true}),page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[];
page.on('pageerror',e=>errors.push(e.message));const origin=process.env.TENANT_JOBS_ORIGIN||'http://127.0.0.1:5094';
try{
 await page.goto(origin+'/jobs.html');await page.locator('[name=id]').fill('jobs-both');await page.locator('[name=password]').fill('Loom-jobs-demo!');await page.locator('#login-form button').click();
 await page.locator('#operations').waitFor({state:'visible'});assert.equal(await page.locator('#tenant option').count(),2);
 await page.locator('[data-view=Schedule]').click();await page.getByText('Protected schedule',{exact:true}).waitFor();
 await page.locator('#tenant').selectOption('/api/jobs/tenants/south');await page.locator('#content').getByText('No records match this view.').waitFor();assert.equal(await page.getByText('Protected schedule',{exact:true}).count(),0);
 await page.locator('#tenant').selectOption('/api/jobs/tenants/north');await page.getByText('Protected schedule',{exact:true}).waitFor();
 await page.locator('#content').getByRole('button',{name:'Pause',exact:true}).click();await page.locator('#confirm button.primary').click();await page.locator('#dialog').waitFor({state:'hidden'});await page.getByText('Paused',{exact:true}).waitFor();
 if(process.env.JOBS_SCREENSHOT_DIR){fs.mkdirSync(process.env.JOBS_SCREENSHOT_DIR,{recursive:true});await page.screenshot({path:process.env.JOBS_SCREENSHOT_DIR+'/tenant-jobs-desktop.png',fullPage:true});}
 await page.setViewportSize({width:390,height:844});assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));
 if(process.env.JOBS_SCREENSHOT_DIR)await page.screenshot({path:process.env.JOBS_SCREENSHOT_DIR+'/tenant-jobs-mobile.png',fullPage:true});
 await page.locator('#logout').click();await page.locator('#login').waitFor({state:'visible'});assert.equal(await page.locator('#tenant option').count(),0);assert.equal(await page.locator('#content').innerText(),'');assert.deepEqual(errors,[]);
 console.log('PASS tenant browser: scoped selector, data clearing, pause, logout and mobile');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exit(1);});
