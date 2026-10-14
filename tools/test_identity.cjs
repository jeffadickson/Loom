const {chromium}=require('playwright');const assert=require('node:assert/strict');const fs=require('node:fs');const crypto=require('node:crypto');
const base='http://127.0.0.1:5087',password='Initial-test-password-42!',changed='Changed-test-password-43!';
function mail(email){return fs.readdirSync(process.env.LOOM_IDENTITY_MAIL).filter(f=>f.endsWith('.json')).map(f=>JSON.parse(fs.readFileSync(process.env.LOOM_IDENTITY_MAIL+'/'+f))).filter(m=>m.to===email)}
async function waitMail(email,predicate=()=>true){for(let i=0;i<100;i++){const found=fs.existsSync(process.env.LOOM_IDENTITY_MAIL)?mail(email).find(predicate):null;if(found)return found;await new Promise(r=>setTimeout(r,100));}throw Error('Queued email was not delivered');}
function link(message,part){const raw=[...message.html.matchAll(/href="([^"]+)"/g)].map(m=>m[1].replaceAll('&amp;','&')).find(url=>url.includes(part));assert.ok(raw,'Email must contain '+part);assert.equal(new URL(raw).origin,base);return raw}
function totp(key){const chars='ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';let bits='';for(const c of key.toUpperCase().replace(/\s/g,''))bits+=chars.indexOf(c).toString(2).padStart(5,'0');const bytes=Buffer.from(bits.match(/.{8}/g).map(b=>parseInt(b,2)));const counter=Buffer.alloc(8);counter.writeBigUInt64BE(BigInt(Math.floor(Date.now()/30000)));const hash=crypto.createHmac('sha1',bytes).update(counter).digest();const offset=hash[19]&15;return ((hash.readUInt32BE(offset)&0x7fffffff)%1000000).toString().padStart(6,'0')}
async function login(page,email,pw){await page.goto(base+'/Identity/Account/Login');await page.locator('#Input_Email').fill(email);await page.locator('#Input_Password').fill(pw);await page.locator('#login-submit').click();}
(async()=>{const browser=await chromium.launch({headless:true,executablePath:process.env.CHROMIUM_EXECUTABLE||undefined,args:['--no-sandbox']});try{const admin=await browser.newContext();const page=await admin.newPage();await login(page,'admin@example.test','Admin-test-password-42!');const authenticatedResponse=await admin.request.get(base+'/api/meta',{headers:{'X-Loom-Demo':'local','X-Demo-Role':'Viewer'}});assert.equal(authenticatedResponse.status(),200);assert.doesNotMatch(authenticatedResponse.headers()['set-cookie']||'',/Loom\.Identity=/,'Validation must not renew cookies and race credential rotation');
console.log("Identity login ready");const token=(await(await admin.request.get(base+'/identity-admin/api/token')).json()).token;const headers={'X-CSRF-TOKEN':token};
let users=(await(await admin.request.get(base+'/identity-admin/api/users')).json()).users;const adminId=users.find(u=>u.email==='admin@example.test').id;
if(process.argv[2]==='rollback'){
    const response=await admin.request.post(base+'/identity-admin/api/users',{headers,data:{email:'rollback-admin@example.test',role:'Viewer'}});
    assert.ok(response.status()>=400);
    const registration=await browser.newContext(),register=await registration.newPage();
    await register.goto(base+'/Identity/Account/Register');await register.locator('#Input_Email').fill('rollback-public@example.test');await register.locator('#Input_Password').fill(password);await register.locator('#Input_ConfirmPassword').fill(password);await register.locator('#registerSubmit').click();
    const accounts=(await(await admin.request.get(base+'/identity-admin/api/users')).json()).users;
    assert.ok(!accounts.some(u=>u.email.startsWith('rollback-')));
    console.log('PASS Identity: queue failure rolls back both admin-created and self-registered accounts');return;
}
if(process.argv[2]==='restart'){const member=await browser.newContext();assert.equal((await member.request.get(base+'/identity-admin/api/email-deliveries')).status(),401);
assert.equal((await admin.request.post(base+'/identity-admin/api/email-deliveries/cancel-fixture/cancel',{data:{}})).status(),400);
assert.equal((await admin.request.post(base+'/identity-admin/api/email-deliveries/cancel-fixture/cancel',{headers,data:{}})).status(),200);
assert.equal((await admin.request.post(base+'/identity-admin/api/email-deliveries/cancel-fixture/cancel',{headers,data:{}})).status(),409);
assert.ok(users.some(u=>u.email==='reader@example.test'));assert.equal((await admin.request.get(base+'/Identity/Account/Register')).status(),404);assert.ok((await(await admin.request.get(base+'/identity-admin/api/audit')).json()).length>=3);console.log('PASS Identity: persistent accounts/audit after restart and registration disabled');return;}
assert.equal((await admin.request.put(base+'/identity-admin/api/users/'+adminId,{headers,data:{role:'Viewer',disabled:false}})).status(),409);
assert.equal((await admin.request.post(base+'/identity-admin/api/users',{data:{email:'csrf@example.test',role:'Admin'}})).status(),400);
const member=await browser.newContext();const user=await member.newPage();
await user.goto(base+'/Identity/Account/Register');await user.locator('#Input_Email').fill('reader@example.test');await user.locator('#Input_Password').fill(password);await user.locator('#Input_ConfirmPassword').fill(password);await user.locator('#registerSubmit').click();await user.waitForURL(/RegisterConfirmation/);assert.ok(!(await user.content()).includes('Click here to confirm your account'));
await login(user,'reader@example.test',password);assert.equal((await member.request.get(base+'/api/meta',{headers:{'X-Loom-Demo':'local'}})).status(),401);
console.log("Identity registration ready");await user.goto(link(await waitMail('reader@example.test'),'ConfirmEmail'));await login(user,'reader@example.test',password);
let meta=await(await member.request.get(base+'/api/meta',{headers:{'X-Loom-Demo':'local','X-Demo-Role':'Admin'}})).json();assert.equal(meta.actor.role,'Viewer');const userId=meta.actor.id;
assert.equal((await member.request.get(base+'/identity-admin/api/users')).status(),403);
assert.equal((await member.request.get(base+'/identity-admin/api/email-deliveries')).status(),403);
await user.goto(base+'/Identity/Account/Manage/ChangePassword');await user.locator('#Input_OldPassword').fill(password);await user.locator('#Input_NewPassword').fill(changed);await user.locator('#Input_ConfirmPassword').fill(changed);await user.getByRole('button',{name:'Update password'}).click();await user.waitForLoadState('networkidle');assert.match(await user.innerText('body'),/password has been changed/i);
console.log("Identity password change ready");
// A separate session must be revoked by administrative disablement immediately.
assert.equal((await admin.request.put(base+'/identity-admin/api/users/'+userId,{headers,data:{role:'Viewer',disabled:true}})).status(),200);
assert.equal((await member.request.get(base+'/api/meta',{headers:{'X-Loom-Demo':'local'}})).status(),401);
await login(user,'reader@example.test',changed);assert.equal((await member.request.get(base+'/api/meta',{headers:{'X-Loom-Demo':'local'}})).status(),401);
assert.equal((await admin.request.put(base+'/identity-admin/api/users/'+userId,{headers,data:{role:'Editor',disabled:false}})).status(),200);
await login(user,'reader@example.test',changed);meta=await(await member.request.get(base+'/api/meta',{headers:{'X-Loom-Demo':'local'}})).json();assert.equal(meta.actor.id,userId);assert.equal(meta.actor.role,'Editor');
console.log("Identity revocation ready");
// Forgot-password UI and reset-token replay.
await user.goto(base+'/Identity/Account/ForgotPassword');await user.locator('#Input_Email').fill('reader@example.test');await user.getByRole('button',{name:'Reset Password',exact:true}).click();await user.waitForURL(/ForgotPasswordConfirmation/);
const resetMail=await waitMail('reader@example.test',m=>m.html.includes('ResetPassword'));const resetUrl=link(resetMail,'ResetPassword');const recovered='Recovered-test-password-44!';
await user.goto(resetUrl);await user.locator('#Input_Email').fill('reader@example.test');await user.locator('#Input_Password').fill(recovered);await user.locator('#Input_ConfirmPassword').fill(recovered);await user.getByRole('button',{name:'Reset',exact:true}).click();await user.waitForURL(/ResetPasswordConfirmation/);
assert.equal((await member.request.get(base+'/api/meta',{headers:{'X-Loom-Demo':'local'}})).status(),401);
await user.goto(resetUrl);await user.locator('#Input_Email').fill('reader@example.test');await user.locator('#Input_Password').fill(changed);await user.locator('#Input_ConfirmPassword').fill(changed);await user.getByRole('button',{name:'Reset',exact:true}).click();assert.match(await user.innerText('body'),/invalid token/i);
await login(user,'reader@example.test',recovered);
console.log('Identity password recovery ready');await user.goto(base+'/Identity/Account/Manage/EnableAuthenticator');const secret=await user.locator('kbd').innerText();await user.locator('#Input_Code').fill(totp(secret));await user.getByRole('button',{name:'Verify'}).click();await user.waitForLoadState('networkidle');assert.equal(new URL(user.url()).pathname,'/Identity/Account/Manage/ShowRecoveryCodes','MFA enrollment: '+(await user.locator('.validation-summary-errors').allTextContents()).join('; '));assert.match(await user.innerText('body'),/recovery codes/i);const recoveryCode=await user.locator('.recovery-code').first().innerText();
await member.clearCookies();await login(user,'reader@example.test',recovered);await user.waitForURL(/LoginWith2fa/);assert.equal((await member.request.get(base+'/api/meta',{headers:{'X-Loom-Demo':'local'}})).status(),401);await user.locator('#Input_TwoFactorCode').fill(totp(secret));await user.getByRole('button',{name:'Log in'}).click();await user.waitForLoadState('networkidle');assert.equal((await member.request.get(base+'/api/meta',{headers:{'X-Loom-Demo':'local'}})).status(),200);
// Recovery codes must work once and reject replay.
await member.clearCookies();await login(user,'reader@example.test',recovered);await user.waitForURL(/LoginWith2fa/);
await user.locator('a[href*=LoginWithRecoveryCode]').click();await user.locator('#Input_RecoveryCode').fill(recoveryCode);await user.getByRole('button',{name:'Log in'}).click();await user.waitForLoadState('networkidle');
assert.equal((await member.request.get(base+'/api/meta',{headers:{'X-Loom-Demo':'local'}})).status(),200);
await member.clearCookies();await login(user,'reader@example.test',recovered);await user.waitForURL(/LoginWith2fa/);
await user.locator('a[href*=LoginWithRecoveryCode]').click();await user.locator('#Input_RecoveryCode').fill(recoveryCode);await user.getByRole('button',{name:'Log in'}).click();await user.waitForLoadState('networkidle');
assert.equal((await member.request.get(base+'/api/meta',{headers:{'X-Loom-Demo':'local'}})).status(),401);
assert.equal((await admin.request.post(base+'/identity-admin/api/users',{headers,data:{email:'invited@example.test',role:'Viewer'}})).status(),200);await waitMail('invited@example.test',m=>m.html.includes('ConfirmEmail')&&m.html.includes('ResetPassword'));
assert.equal((await admin.request.post(base+'/identity-admin/api/users/'+userId+'/reset',{headers,data:{}})).status(),200);
const deliveryResponse=await admin.request.get(base+'/identity-admin/api/email-deliveries');assert.equal(deliveryResponse.status(),200);const deliveryText=await deliveryResponse.text();assert.ok(!deliveryText.includes('protectedPayload')&&!deliveryText.includes('example.test')&&!deliveryText.includes('ResetPassword?'));assert.ok(JSON.parse(deliveryText).some(e=>e.status==='Sent'));
await page.goto(base+'/accounts.html');await page.getByRole('heading',{name:'reader@example.test'}).waitFor();await page.screenshot({path:'artifacts/browser/identity-accounts.png',fullPage:true});
console.log('PASS Identity: registration, confirmation, stable roles, change/reset passwords, replay rejection, session revocation, disabled accounts, CSRF, TOTP MFA, recovery codes, admin creation/reset and last-admin protection');
}finally{await browser.close()}})().catch(e=>{console.error(e);process.exit(1)});
