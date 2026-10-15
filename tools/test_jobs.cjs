const {chromium}=require('playwright');const assert=require('node:assert/strict');const fs=require('node:fs');
(async()=>{const browser=await chromium.launch({headless:true}),page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[];page.on('pageerror',e=>errors.push(e.message));const origin=process.env.JOBS_ORIGIN||'http://127.0.0.1:5093';
try{
 await page.goto(origin+'/jobs.html');
 async function login(id){await page.locator('#login-form input[name=id]').fill(id);await page.locator('#login-form input[name=password]').fill('Loom-jobs-demo!');await page.locator('#login-form button').click();}
 await login('jobs-viewer');await page.locator('#notice').filter({hasText:'Administrator access required'}).waitFor();
 const headers={'X-Loom-Demo':'local','X-Demo-Role':'Admin'};
 assert.equal((await page.request.get(origin+'/api/jobs/records/Delivery',{headers})).status(),403);
 assert.equal((await page.request.post(origin+'/api/jobs/schedules',{headers,data:{name:'unauthorized',intervalSeconds:60,firstDue:'2026-01-01T00:00:00Z',missedRuns:'Skip',handler:'demo.receipt',message:'bad'}})).status(),403);
 await page.locator('#logout').click();await login('jobs-admin');await page.locator('#operations').waitFor({state:'visible'});
 assert.equal((await page.request.get(origin+'/api/jobs/records/Delivery')).status(),403);
 const records=await (await page.request.get(origin+'/api/jobs/records/Delivery?status=Failed',{headers})).json();const failed=records.items[0];assert.ok(failed);
 assert.ok(!('Payload' in failed.values));assert.ok(!('LeaseToken' in failed.values));
 await page.locator('#status').selectOption('Failed');await page.locator(`[data-id="${failed.id}"][data-action=retry]`).click();await page.locator('#confirm button.primary').click();await page.locator('#dialog').waitFor({state:'hidden'});await page.locator('#content').filter({hasText:'No records'}).waitFor();
 assert.equal((await page.request.post(origin+`/api/jobs/deliveries/${failed.id}/retry`,{headers,data:{versionNumber:failed.version}})).status(),409);
 await page.locator('#status').selectOption('Pending');await page.locator('#range').filter({hasText:'1–25'}).waitFor();await page.locator('#next').click();await page.locator('#range').filter({hasText:'26–'}).waitFor();await page.locator('#previous').click();
 await page.locator('#create').click();await page.locator('#edit [name=message]').fill('Browser queued message');await page.locator('#edit button.primary').click();await page.locator('#dialog').waitFor({state:'hidden'});
 const pending=await (await page.request.get(origin+'/api/jobs/records/Delivery?status=Pending',{headers})).json();const target=pending.items[0];
 await page.locator('#refresh').click();await page.locator(`[data-id="${target.id}"][data-action=cancel]`).click();await page.locator('#confirm button.primary').click();await page.locator('#dialog').waitFor({state:'hidden'});
 await page.locator('[data-view=Schedule]').click();await page.locator('#heading').filter({hasText:'Schedules'}).waitFor();await page.locator('#create').click();await page.locator('#edit [name=name]').fill('Browser recurring check');await page.locator('#edit [name=interval]').fill('3600');await page.locator('#edit [name=due]').fill('2026-01-01T00:00');await page.locator('#edit button.primary').click();await page.locator('#dialog').waitFor({state:'hidden'});
 const row=page.locator('tr').filter({hasText:'Browser recurring check'});await row.getByRole('button',{name:'Pause',exact:true}).click();await page.locator('#confirm button.primary').click();await page.locator('#dialog').waitFor({state:'hidden'});await row.getByText('Paused',{exact:true}).waitFor();await row.getByRole('button',{name:'Resume',exact:true}).click();await page.locator('#confirm button.primary').click();await page.locator('#dialog').waitFor({state:'hidden'});await row.getByText('Active',{exact:true}).waitFor();
 if(process.env.JOBS_SCREENSHOT_DIR){fs.mkdirSync(process.env.JOBS_SCREENSHOT_DIR,{recursive:true});await page.screenshot({path:process.env.JOBS_SCREENSHOT_DIR+'/jobs-desktop.png',fullPage:true});}
 await page.setViewportSize({width:390,height:844});assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));if(process.env.JOBS_SCREENSHOT_DIR)await page.screenshot({path:process.env.JOBS_SCREENSHOT_DIR+'/jobs-mobile.png',fullPage:true});
 await page.locator('#logout').click();await page.locator('#login').waitFor({state:'visible'});assert.equal(await page.locator('#content').innerText(),'');assert.deepEqual(errors,[]);console.log('PASS jobs browser: permissions, header protection, paging, retry, cancel, schedule pause/resume, mobile');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exit(1);});
