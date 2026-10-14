const {chromium}=require('playwright');
const {pathToFileURL}=require('url');
const path=require('path');
(async()=>{
 const browser=await chromium.launch({headless:true,executablePath:process.env.CHROMIUM_EXECUTABLE||undefined,args:['--no-sandbox']});
 try{
 for(const [mode,url] of [['offline',pathToFileURL(path.resolve(__dirname,'../Loom_Introduction.html')).href],['live','http://127.0.0.1:5083/introduction.html']]){
  const page=await browser.newPage({viewport:{width:1380,height:1000}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(url);await page.locator('#summary').filter({hasText:'matching records'}).waitFor();
  const text=await page.locator('#mode').innerText();if(!text.includes(mode==='live'?'LIVE C#':'BROWSER SIMULATION'))throw Error('Wrong mode '+text);
  await page.locator('#input-Name').fill('Exact <b>result</b>');await page.locator('#input-Cost').fill('12345678901234567.89');await page.locator('#input-SampleCount').fill('9007199254740993');
  await page.locator('#save').click();await page.locator('#message').filter({hasText:'Saved.'}).waitFor();
  const row=page.locator('#rows tr').filter({hasText:'Exact <b>result</b>'});await row.waitFor();
  if(!(await row.innerText()).includes('12345678901234567.89')||!(await row.innerText()).includes('9007199254740993'))throw Error('Numeric precision lost');
  if(await row.locator('b').count())throw Error('Unescaped record markup');
  await row.getByRole('button',{name:'History'}).click();await page.locator('#audit').filter({hasText:'create'}).waitFor();
  await page.locator('#input-Cost').fill('1.005');await page.locator('#save').click();await page.locator('#message').filter({hasText:'scale'}).waitFor();
  await page.locator('#input-Cost').fill('12.34');await page.locator('#role').selectOption('Viewer');await page.locator('#save').click();await page.locator('#message').filter({hasText:'403'}).waitFor();
  await page.locator('#outcome').selectOption('Fail');await page.locator('#summary').filter({hasText:'0 matching'}).waitFor();
  await page.locator('#role').selectOption('Admin');await page.locator('#outcome').selectOption('');await page.locator('#summary').filter({hasText:'3 matching'}).waitFor();
  await page.screenshot({path:'/tmp/loom-intro-'+mode+'.png',fullPage:true});
  await page.setViewportSize({width:390,height:844});if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth))throw Error('Mobile page overflows');
  if(errors.length)throw Error(errors.join('\n'));await page.close();console.log('PASS introduction '+mode+': exact numbers, safe text, audit, scale refusal, authorization, filters and mobile layout');
 }
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exit(1)});
