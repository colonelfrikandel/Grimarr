import { chromium } from '../frontend/node_modules/@playwright/test/index.mjs';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import assert from 'node:assert/strict';

const root = process.cwd();
const config = await mkdtemp(path.join(os.tmpdir(), 'grimarr-ui-'));
await mkdir('test-results', { recursive: true });
const server = spawn(process.env.DOTNET_EXE || 'dotnet', [
  path.join(root, 'src/Grimarr/bin/Debug/net10.0/Grimarr.dll'),
  '--contentRoot', path.join(root, 'src/Grimarr'),
], {
  env: { ...process.env, ASPNETCORE_URLS: 'http://127.0.0.1:8787', GRIMARR_CONFIG: config, GRIMARR_PASSWORD: 'local-preview-password' },
  stdio: 'pipe', windowsHide: true,
});
let logs = '';
server.stdout.on('data', d => logs += d);
server.stderr.on('data', d => logs += d);
let browser, page;
try {
  let ready = false;
  for (let i = 0; i < 50; i++) {
    if (server.exitCode !== null) throw Error('Server exited: ' + logs);
    if (await fetch('http://127.0.0.1:8787/health', { signal: AbortSignal.timeout(1000) }).then(r => r.ok).catch(() => false)) { ready = true; break; }
    await new Promise(r => setTimeout(r, 200));
  }
  assert(ready, 'Server did not start');
  browser = await chromium.launch({ headless: true });
  page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.goto('http://127.0.0.1:8787');
  await page.getByLabel('Password', { exact: true }).fill('local-preview-password');
  await page.getByRole('button', { name: 'Open my library' }).click();
  await page.getByRole('heading', { name: 'Audiobooks', exact: true }).waitFor();
  await page.screenshot({ path: 'test-results/library-desktop.png', fullPage: true });
  await page.getByRole('navigation').getByRole('button', { name: 'Settings', exact: true }).click();
  await page.getByRole('heading', { name: 'Settings', exact: true }).waitFor();
  await page.screenshot({ path: 'test-results/settings-desktop.png', fullPage: true });
  await page.getByRole('button', { name: 'Library', exact: true }).click();
  await page.getByRole('button', { name: 'Add audiobook', exact: true }).first().click();
  await page.getByRole('button', { name: 'Add by title and author' }).click();
  await page.getByLabel('Book title', { exact: true }).fill('The Hobbit');
  await page.getByLabel('Author', { exact: true }).fill('J R R Tolkien');
  await page.getByRole('button', { name: 'Add & download' }).click();
  await page.getByRole('heading', { name: 'The Hobbit', exact: true }).waitFor();
  await page.getByRole('button', { name: /The Hobbit.*Monitored/ }).click();
  await page.getByRole('dialog').waitFor();
  await page.getByLabel('Narration', { exact: true }).selectOption('dramatized');
  await page.getByRole('button', { name: 'Save preferences' }).click();
  await page.getByRole('button', { name: 'Save preferences' }).waitFor({ state: 'visible' });
  await page.waitForFunction(async () => (await (await fetch('/api/books')).json())[0].preferences.narration === 'dramatized');
  await page.getByRole('button', { name: 'Close dialog' }).click();
  await page.screenshot({ path: 'test-results/library-desktop.png', fullPage: true });
  await page.getByRole('button', { name: 'List view', exact: true }).click();
  await page.locator('.book-list').waitFor();
  await page.getByText('Dramatized / full cast', { exact: true }).waitFor();
  await page.screenshot({ path: 'test-results/library-list.png', fullPage: true });
  await page.getByLabel('Sort audiobooks').selectOption('author');
  await page.getByLabel('Filter audiobooks').selectOption('available');
  await page.getByRole('heading', { name: 'No matching audiobooks' }).waitFor();
  await page.getByLabel('Filter audiobooks').selectOption('all');
  await page.getByRole('button', { name: 'Poster view', exact: true }).click();
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({ path: 'test-results/library-mobile.png', fullPage: true });
  assert(!(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)), 'Mobile layout overflows horizontally');
  await page.getByRole('button', { name: 'Activity', exact: true }).click();
  await page.getByText('Added and monitored for automatic download').waitFor();
  await page.getByRole('button', { name: 'Library', exact: true }).click();
  await page.getByRole('button', { name: /The Hobbit.*Monitored/ }).click();
  await page.getByRole('button', { name: 'Remove book', exact: true }).click();
  await page.getByRole('dialog').waitFor({ state: 'hidden' });
  await page.waitForFunction(async () => (await (await fetch('/api/books')).json()).length === 0);
  await page.reload();
  assert.equal(await page.getByRole('button', { name: /The Hobbit.*Monitored/ }).count(), 0);
  console.log('PASS: Remove book closes details and persists after page reload.');
  const stored = (await (await page.request.get('http://127.0.0.1:8787/api/settings')).json()).settings;
  const setup = await page.request.put('http://127.0.0.1:8787/api/settings', { headers: {'X-Grimarr':'1'}, data: {...stored,downloadRoot:path.join(config,'downloads'),libraryRoot:path.join(config,'library')} });
  assert(setup.ok());
  await page.getByRole('navigation').getByRole('button', { name: 'Settings', exact: true }).click();
  await page.getByRole('checkbox', { name: /Choose releases manually/ }).check();
  await page.getByRole('checkbox', { name: /Automatically replace failed downloads/ }).check();
  await page.getByLabel('Stall timeout (minutes)', { exact:true }).fill('60');
  await page.getByRole('button', { name: 'Save settings', exact: true }).click();
  await page.waitForFunction(async () => (await (await fetch('/api/settings')).json()).settings.manualSelection === true);
  assert.equal((await (await page.request.get('http://127.0.0.1:8787/api/settings')).json()).settings.stallTimeoutMinutes,60);
  assert.equal((await (await page.request.get('http://127.0.0.1:8787/api/settings')).json()).settings.automaticFailedDownloadRecovery,true);
  await page.getByRole('button', { name: 'Library', exact: true }).click();
  await page.getByRole('button', { name: 'Add audiobook', exact: true }).first().click();
  await page.getByRole('button', { name: 'Add by title and author' }).click();
  await page.getByLabel('Book title', { exact: true }).fill('Mage Tank: A LitRPG Adventure');
  await page.getByLabel('Author', { exact: true }).fill('Test Author');
  await page.getByRole('button', { name: 'Add & choose releases', exact: true }).click();
  await page.getByRole('heading', { name: 'Choose downloads', exact: true }).waitFor();
  assert.equal(await page.getByLabel('Release search', { exact: true }).inputValue(),'Mage Tank Test Author');
  await page.route('**/api/books/*/releases?*',route=>route.fulfill({json:Array.from({length:5},(_,i)=>({selectionId:`choice-${i}`,canSelect:true,release:{title:`Mage Tank Book ${i+1}`,indexer:'Fixture',seeders:0,seedersKnown:false,size:1000},score:100,eligible:false,reasons:['Title does not fully match']}))}));
  let selectedPayload;
  await page.route('**/api/books/*/select',route=>{selectedPayload=route.request().postDataJSON();return route.fulfill({json:[]});});
  await page.getByRole('button', { name: 'Search download options', exact: true }).click();
  await page.getByRole('checkbox', { name: 'Select Mage Tank Book 1', exact: true }).check();
  await page.getByRole('checkbox', { name: 'Select Mage Tank Book 5', exact: true }).check();
  await page.screenshot({ path:'test-results/manual-selection-mobile.png',fullPage:true });
  assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>window.innerWidth)), 'Manual selection overflows on mobile');
  await page.getByRole('button', { name: 'Queue selected (2)', exact: true }).click();
  await page.getByRole('dialog').waitFor({state:'hidden'});
  assert.deepEqual(selectedPayload.selectionIds,['choice-0','choice-4']);
  await page.getByRole('navigation').getByRole('button', { name: 'Settings', exact: true }).click();
  await page.getByRole('checkbox', { name: /Choose releases manually/ }).uncheck();
  await page.getByRole('button', { name: 'Save settings', exact: true }).click();
  await page.waitForFunction(async () => (await (await fetch('/api/settings')).json()).settings.manualSelection === false);
  console.log('PASS: Manual setting on/off, series query, five options, multi-selection, submission and mobile layout.');
  const quickInputs=[
    {title:'Quick Hash Fixture',source:'a'.repeat(40)},
    {title:'Quick Magnet Fixture',source:'magnet:?xt=urn:btih:'+'b'.repeat(40)+'&tr=https%3A%2F%2Ftracker.example%2Fprivate-ui-key'},
    {title:'Quick Torrent Fixture',torrent:Buffer.from('d4:infod6:lengthi4e4:name8:book.mp312:piece lengthi16384e6:pieces20:abcdefghijklmnopqrstee')},
  ];
  await page.getByRole('button',{name:'Library',exact:true}).click();
  for(const input of quickInputs) {
    await page.getByRole('button',{name:'Add audiobook',exact:true}).first().click();
    assert.equal(await page.getByRole('tab',{name:'Search with Prowlarr',exact:true}).getAttribute('aria-selected'),'true');
    await page.getByRole('tab',{name:'Quick Add',exact:true}).click();
    await page.getByLabel('Book title',{exact:true}).fill(input.title);
    await page.getByLabel('Author',{exact:true}).fill('UI Test Author');
    if(input.torrent) {
      await page.getByRole('radio',{name:'Upload a torrent file',exact:true}).check();
      await page.getByLabel('Torrent file',{exact:true}).setInputFiles({name:'fixture.torrent',mimeType:'application/x-bittorrent',buffer:input.torrent});
    } else await page.getByLabel('Info hash or magnet link',{exact:true}).fill(input.source);
    await page.screenshot({path:'test-results/quick-add-mobile.png',fullPage:true});
    assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth)),'Quick Add overflows on mobile');
    await page.getByRole('button',{name:'Queue download',exact:true}).click();
    await page.getByRole('dialog').waitFor({state:'hidden'});
    await page.getByRole('heading',{name:input.title,exact:true}).waitFor();
  }
  const quickBooks=(await (await page.request.get('http://127.0.0.1:8787/api/books')).json()).filter(b=>b.acquisitionMode==='quick-add');
  assert.equal(quickBooks.length,3);assert(quickBooks.every(b=>b.status==='queued'));assert(!JSON.stringify(quickBooks).includes('private-ui-key'));
  const beforePreviewCount=(await(await page.request.get('http://127.0.0.1:8787/api/books')).json()).length;
  await page.getByRole('button',{name:'Add audiobook',exact:true}).first().click();await page.getByRole('tab',{name:'Quick Add',exact:true}).click();
  await page.getByLabel('Info hash or magnet link',{exact:true}).fill('magnet:?xt=urn:btih:'+'c'.repeat(40)+'&dn='+encodeURIComponent('Jane Austen - Pride and Prejudice [M4B]'));
  const titleId=await page.getByLabel('Book title',{exact:true}).getAttribute('id');const authorId=await page.getByLabel('Author',{exact:true}).getAttribute('id');
  await page.waitForFunction(([id,value])=>document.getElementById(id)?.value===value,[titleId,'Pride and Prejudice']);
  assert.equal(await page.getByLabel('Author',{exact:true}).inputValue(),'Jane Austen');
  assert.equal((await(await page.request.get('http://127.0.0.1:8787/api/books')).json()).length,beforePreviewCount,'Preview created a download before review');
  await page.getByLabel('Book title',{exact:true}).fill('My Corrected Title');await page.getByLabel('Author',{exact:true}).fill('My Corrected Author');
  const previewResponse=page.waitForResponse(r=>r.url().endsWith('/books/quick-add/preview')&&r.request().postDataJSON()?.source?.includes('Cornman'));
  await page.getByLabel('Info hash or magnet link',{exact:true}).fill('magnet:?xt=urn:btih:'+'c'.repeat(40)+'&dn='+encodeURIComponent('Cornman - Mage Tank 03 [M4B]'));await previewResponse;
  await page.getByRole('button',{name:'Restore detected details',exact:true}).waitFor();
  assert.equal(await page.getByLabel('Book title',{exact:true}).inputValue(),'My Corrected Title');assert.equal(await page.getByLabel('Author',{exact:true}).inputValue(),'My Corrected Author');
  await page.getByRole('button',{name:'Queue download',exact:true}).click();await page.getByRole('dialog').waitFor({state:'hidden'});
  const corrected=(await(await page.request.get('http://127.0.0.1:8787/api/books')).json()).find(b=>b.title==='My Corrected Title');assert.equal(corrected.author,'My Corrected Author');
  await page.getByRole('button',{name:'Add audiobook',exact:true}).first().click();await page.getByRole('tab',{name:'Quick Add',exact:true}).click();
  await page.getByRole('radio',{name:'Upload a torrent file',exact:true}).check();
  const detectedName='Émilie - L’Étoile [M4B]';const detectedTorrent=Buffer.from('d4:infod6:lengthi4e4:name'+Buffer.byteLength(detectedName)+':'+detectedName+'12:piece lengthi16384e6:pieces20:abcdefghijklmnopqrstee');
  await page.getByLabel('Torrent file',{exact:true}).setInputFiles({name:'book.torrent',mimeType:'application/x-bittorrent',buffer:detectedTorrent});
  const fileTitleId=await page.getByLabel('Book title',{exact:true}).getAttribute('id');
  await page.waitForFunction(([id,value])=>document.getElementById(id)?.value===value,[fileTitleId,'L’Étoile']);assert.equal(await page.getByLabel('Author',{exact:true}).inputValue(),'Émilie');
  await page.getByLabel('Author',{exact:true}).fill('Edited Author');await page.getByRole('button',{name:'Restore detected details',exact:true}).click();assert.equal(await page.getByLabel('Author',{exact:true}).inputValue(),'Émilie');
  await page.screenshot({path:'test-results/quick-add-autofill-mobile.png',fullPage:true});
  await page.getByRole('button',{name:'Close dialog'}).click();
  const delayedPreview=async route=>{
    if(route.request().postDataJSON()?.source?.includes('Delayed')) {
      await new Promise(r=>setTimeout(r,900));
      try {await route.fulfill({json:{title:'Stale Title',author:'Stale Author',displayName:'Delayed',notice:'Old suggestion'}});}catch{/* The client intentionally aborted this preview. */}
    } else await route.continue();
  };
  await page.route('**/api/books/quick-add/preview',delayedPreview);
  await page.getByRole('button',{name:'Add audiobook',exact:true}).first().click();await page.getByRole('tab',{name:'Quick Add',exact:true}).click();
  const delayedRequest=page.waitForRequest(r=>r.url().endsWith('/books/quick-add/preview')&&r.postDataJSON()?.source?.includes('Delayed'));
  await page.getByLabel('Info hash or magnet link',{exact:true}).fill('magnet:?xt=urn:btih:'+'d'.repeat(40)+'&dn=Delayed');await delayedRequest;
  await page.getByLabel('Info hash or magnet link',{exact:true}).fill('magnet:?xt=urn:btih:'+'e'.repeat(40)+'&dn='+encodeURIComponent('Cornman - Mage Tank 03'));
  const freshTitleId=await page.getByLabel('Book title',{exact:true}).getAttribute('id');
  await page.waitForFunction(([id,value])=>document.getElementById(id)?.value===value,[freshTitleId,'Mage Tank 03']);await new Promise(r=>setTimeout(r,1000));
  assert.equal(await page.getByLabel('Book title',{exact:true}).inputValue(),'Mage Tank 03');assert.equal(await page.getByLabel('Author',{exact:true}).inputValue(),'Cornman');
  await page.getByRole('button',{name:'Close dialog'}).click();await page.unroute('**/api/books/quick-add/preview',delayedPreview);
  console.log('PASS: A canceled slow preview cannot replace details from the latest source.');
  console.log('PASS: Magnet and UTF-8 torrent names autofill before adding; previews create no downloads; corrections survive new suggestions, are submitted, and can be reset explicitly.');
  await page.getByRole('button',{name:'Add audiobook',exact:true}).first().click();
  await page.getByRole('tab',{name:'Quick Add',exact:true}).click();
  await page.getByLabel('Book title',{exact:true}).fill('Invalid Quick Add');await page.getByLabel('Author',{exact:true}).fill('UI Test Author');
  await page.getByLabel('Info hash or magnet link',{exact:true}).fill('invalid-hash');await page.getByRole('button',{name:'Queue download',exact:true}).click();
  await page.getByText('Enter a v1 info hash or a magnet link, or upload a .torrent file. Web download URLs are not supported here.',{exact:true}).waitFor();
  await page.getByRole('tab',{name:'Search with Prowlarr',exact:true}).click();await page.getByLabel('Search audiobook catalog',{exact:true}).waitFor();
  await page.getByRole('button',{name:'Close dialog'}).click();
  console.log('PASS: Quick Add hash, magnet and file forms queue real records, show validation errors, preserve Prowlarr choice, and fit mobile.');
  let fixture={id:'recovery-ui',title:'Recovery fixture',author:'Test Author',cover:'',status:'download-failed',suspended:false,canRemove:false,progress:0.2,
    message:'Torrent is missing from qBittorrent.',preferences:stored.defaults,selectedRelease:{title:'Failed release'},
    failedReleases:[{title:'Failed release',reason:'Torrent is missing from qBittorrent.',at:new Date().toISOString(),blocked:true}]};
  const actions=[];
  await page.route('**/api/books',route=>route.fulfill({json:[fixture]}));
  await page.route('**/api/books/recovery-ui/*',route=>{
    const action=route.request().url().split('/').at(-1);actions.push(action);
    if(action==='clear-blocklist') fixture={...fixture,failedReleases:fixture.failedReleases.map(f=>({...f,blocked:false}))};
    if(action==='replace') fixture={...fixture,status:'wanted'};
    if(action==='retry') fixture={...fixture,status:fixture.status==='import-failed'?'import-pending':'downloading'};
    return route.fulfill({json:{}});
  });
  await page.getByRole('button', { name:'Library',exact:true }).click();
  await page.reload();
  await page.getByRole('button',{name:/Recovery fixture.*Download failed/}).click();
  await page.getByRole('heading',{name:'Failed releases',exact:true}).waitFor();
  await page.getByRole('button',{name:'Search for replacement',exact:true}).waitFor();
  await page.screenshot({path:'test-results/recovery-mobile.png',fullPage:true});
  assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>window.innerWidth)),'Recovery details overflow on mobile');
  await page.getByRole('button',{name:'Clear failed-release blocklist',exact:true}).click();
  await page.getByRole('button',{name:'Clear failed-release blocklist',exact:true}).waitFor({state:'hidden'});
  await page.getByRole('button',{name:'Retry this release',exact:true}).click();
  await page.getByRole('button',{name:'Retry this release',exact:true}).waitFor({state:'hidden'});
  assert.deepEqual(actions,['clear-blocklist','retry']);
  await page.getByRole('button',{name:'Close dialog'}).click();
  fixture={...fixture,status:'download-failed'};await page.reload();
  await page.getByRole('button',{name:/Recovery fixture.*Download failed/}).click();
  await page.getByRole('button',{name:'Search for replacement',exact:true}).click();
  await page.getByRole('button',{name:'Search for replacement',exact:true}).waitFor({state:'hidden'});
  assert.equal(actions.at(-1),'replace');
  await page.getByRole('button',{name:'Close dialog'}).click();
  fixture={...fixture,status:'import-failed',message:'Storage access denied.'};await page.reload();
  await page.getByRole('button',{name:/Recovery fixture.*Import needs attention/}).click();
  assert.equal(await page.getByRole('button',{name:'Search for replacement',exact:true}).count(),0);
  await page.getByRole('button',{name:'Retry import',exact:true}).click();
  await page.getByRole('button',{name:'Retry import',exact:true}).waitFor({state:'hidden'});
  console.log('PASS: Recovery settings persist; failure history, clear blocklist, retry release, replace and retry import controls work on mobile.');
  assert.deepEqual(errors, [], 'Browser runtime errors');
  console.log('PASS: Browser login, navigation, settings, add book, saved edition preference, poster/list switching, sort/filter controls, activity, and mobile layout. No runtime errors.');
} catch (error) {
  if (page) await page.screenshot({ path: 'test-results/ui-failure.png', fullPage: true });
  throw error;
} finally {
  await browser?.close();
  if (server.exitCode === null) { server.kill(); await once(server, 'exit'); }
  await rm(config, { recursive: true, force: true });
}
