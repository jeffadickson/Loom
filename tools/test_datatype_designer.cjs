const {chromium}=require('playwright');const assert=require('node:assert/strict');
(async()=>{
const browser=await chromium.launch({executablePath:process.env.CHROMIUM_EXECUTABLE,headless:true,args:['--no-sandbox']});const page=await browser.newPage();
await page.goto('http://127.0.0.1:5080');await page.locator('#design').click();await page.locator('#schemaEditor').waitFor({state:'visible'});
for(const [name,type,settings] of [['cost','Money',{currency:'USD',precision:19,scale:2}],['payload','Binary',{compression:'brotli',maxBytes:4096}],['series','Uri',{}]]){
 await page.locator('#addModelField').click();const row=page.locator('#newFields tr').last();await row.locator('.field-name').fill(name);await row.getByLabel('New field type',{exact:true}).selectOption(type);await row.locator('summary').click();await row.locator('textarea').fill(JSON.stringify(settings));
}
await page.locator('#previewMigration').click();await page.locator('#migrationPreview').waitFor({state:'visible'});await page.locator('#applyMigration').click();await page.locator('#schemaEditor').waitFor({state:'hidden'});
const headers={'X-Loom-Demo':'local','X-Demo-Role':'Admin'};const get=async p=>(await page.request.get('http://127.0.0.1:5080/api/'+p,{headers})).json();
const doc=(await get('documents/Asset')).items[0];const bytes=Buffer.alloc(2048,42);doc.values.cost='12345678901234567.89';doc.values.payload=bytes.toString('base64');doc.values.series='tsdb://example.test/runs/1';
assert.equal((await page.request.put('http://127.0.0.1:5080/api/documents/Asset/'+doc.id,{headers,data:{version:doc.version,values:doc.values}})).status(),200);
const saved=await get('documents/Asset/'+doc.id);assert.equal(saved.values.cost,'12345678901234567.89');assert.equal(saved.values.payload,bytes.toString('base64'));
const response=await page.request.get('http://127.0.0.1:5080/api/documents/Asset/'+doc.id+'/binary/payload',{headers});assert.deepEqual(await response.body(),bytes);
console.log('PASS type designer: Money/Binary/Uri additions, settings, SQLite save and transparent download');await browser.close();
})().catch(e=>{console.error(e);process.exit(1)});
