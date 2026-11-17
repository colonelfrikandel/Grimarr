// Isolated loopback services, synthetic audio, and persisted crash states. No real services or torrents.
import http from 'node:http';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, mkdir, writeFile, readFile, rm, readdir } from 'node:fs/promises';
import { DatabaseSync } from 'node:sqlite';
import {libraryItems} from './library-fixture.mjs';
import path from 'node:path';
import os from 'node:os';
import assert from 'node:assert/strict';

const root=await mkdtemp(path.join(os.tmpdir(),'grimarr-recovery-'));
const downloads=path.join(root,'downloads'), library=path.join(root,'library'), config=path.join(root,'config');
await Promise.all([downloads,library,config].map(p=>mkdir(p,{recursive:true})));
const frame=Buffer.alloc(417); Buffer.from([0xff,0xfb,0x90,0x64]).copy(frame);
const audio=Buffer.concat(Array.from({length:60},()=>frame));
const source=path.join(downloads,'book.mp3'); await writeFile(source,audio);
const hashes=['a','b','c','d'].map(c=>c.repeat(40));
const torrents=new Map(); let adds=0, scans=0, unavailable=false, server, cookie='', logs='', checks=0;
const releases=hashes.map((hash,i)=>({guid:`fixture-${i}?private=do-not-expose`,indexerId:1,indexer:'Fixture',protocol:'torrent',
  title:`The Hobbit - J R R Tolkien English unabridged M4B edition ${i}`,seeders:40-i,size:1000,magnetUrl:`magnet:?xt=urn:btih:${hash}`}));
let results=releases;
const mock=http.createServer(async(req,res)=>{
  let body=''; for await(const chunk of req) body+=chunk;
  const url=new URL(req.url,'http://localhost');
  const json=value=>{res.setHeader('Content-Type','application/json');res.end(JSON.stringify(value));};
  if(url.pathname==='/api/v1/search') return json(results);
  if(url.pathname==='/alias') {res.statusCode=302;res.setHeader('Location',`magnet:?xt=urn:btih:${hashes[0]}`);return res.end();}
  if(url.pathname.startsWith('/api/v2/') && unavailable) {res.statusCode=503;return json({error:'offline'});}
  if(url.pathname==='/api/v2/auth/login') {res.setHeader('Set-Cookie','SID=fixture; Path=/');return res.end('Ok.');}
  if(url.pathname==='/api/v2/torrents/add') {
    const hash=body.match(/urn:btih:([a-f0-9]{40})/)?.[1]; assert(hash); adds++;
    torrents.set(hash,{hash,state:'downloading',progress:0.2,amount_left:800,content_path:source}); return res.end('Ok.');
  }
  if(url.pathname==='/api/v2/torrents/info') {const t=torrents.get(url.searchParams.get('hashes'));return json(t?[t]:[]);}
  if(url.pathname==='/api/libraries/fixture/scan') {scans++;return json({success:true});}
  if(url.pathname==='/api/libraries/fixture/items')return json(scans>0 ? await libraryItems(library) : {results:[],total:0});
  // Any deletion API would be a test failure.
  res.statusCode=404;json({error:'Unexpected mock route '+url.pathname});
});
mock.listen(0,'127.0.0.1');await once(mock,'listening');
const probe=http.createServer();probe.listen(0,'127.0.0.1');await once(probe,'listening');
const port=probe.address().port;await new Promise(r=>probe.close(r));
const base=`http://127.0.0.1:${port}`, mockUrl=`http://127.0.0.1:${mock.address().port}`;
async function api(route,method='GET',body,status=200) {
  const r=await fetch(base+'/api'+route,{method,headers:{'X-Grimarr':'1','Content-Type':'application/json',Cookie:cookie},body:body===undefined?undefined:JSON.stringify(body)});
  const text=await r.text();assert.equal(r.status,status,`${route}: ${text}`);return text?JSON.parse(text):null;
}
async function waitFor(fn,label) {for(let i=0;i<100;i++){if(await fn())return;await new Promise(r=>setTimeout(r,100));}throw Error('Timeout: '+label+'\n'+logs);}
async function start() {
  server=spawn(process.env.DOTNET_EXE||'dotnet',[path.resolve('src/Grimarr/bin/Debug/net10.0/Grimarr.dll'),'--contentRoot',path.resolve('src/Grimarr')],
    {env:{...process.env,ASPNETCORE_URLS:base,GRIMARR_CONFIG:config,GRIMARR_PASSWORD:'recovery-fixture-password',GRIMARR_POLL_SECONDS:'1',PATH:(process.env.FFPROBE_DIR?process.env.FFPROBE_DIR+path.delimiter:'')+process.env.PATH},stdio:['ignore','pipe','pipe'],windowsHide:true});
  server.stdout.on('data',d=>logs+=d);server.stderr.on('data',d=>logs+=d);
  await waitFor(()=>{if(server.exitCode!==null)throw Error(logs);return fetch(base+'/health').then(r=>r.ok).catch(()=>false);},'startup');
  const login=await fetch(base+'/api/login',{method:'POST',headers:{'Content-Type':'application/json','X-Grimarr':'1'},body:JSON.stringify({password:'recovery-fixture-password'})});
  assert(login.ok);cookie=login.headers.get('set-cookie').split(';')[0];
}
async function stop() {if(server?.exitCode===null){server.kill();await once(server,'exit');}}
let id;
const book=async()=> (await api('/books')).find(b=>b.id===id);
async function check(name,fn) {await fn();checks++;console.log('PASS:',name);}
// Only edit the isolated fixture database while the process is stopped, to simulate elapsed time/crash boundaries.
async function restartWith(edit) {
  await stop();const db=new DatabaseSync(path.join(config,'grimarr.db'));
  try {const b=JSON.parse(db.prepare('SELECT json FROM books WHERE id=?').get(id).json);edit(b);db.prepare('UPDATE books SET json=? WHERE id=?').run(JSON.stringify(b),id);}
  finally {db.close();}await start();
}
try {
  await start();let settings=(await api('/settings')).settings;
  await check('Automatic replacement is opt-in and stall timeout defaults to one day',async()=>{assert.equal(settings.automaticFailedDownloadRecovery,false);assert.equal(settings.stallTimeoutMinutes,1440);});
  settings={...settings,downloadRoot:downloads,libraryRoot:library,libraryId:'fixture',automationEnabled:true,stallTimeoutMinutes:15,
    prowlarr:{url:mockUrl,apiKey:'fixture'},qbittorrent:{url:mockUrl,username:'admin',password:'fixture'},audiobookshelf:{url:mockUrl,apiKey:'fixture'}};
  await check('Invalid stall timeout rejected',async()=>{await api('/settings','PUT',{...settings,stallTimeoutMinutes:1},400);});
  await api('/settings','PUT',settings); id=(await api('/books','POST',{title:'The Hobbit',author:'J R R Tolkien'})).id;
  await waitFor(async()=> (await book()).progress===0.2,'initial download');
  torrents.delete(hashes[0]);
  await waitFor(async()=> (await book()).message.includes('up to two minutes'),'missing grace');
  await check('Briefly absent torrent does not fail or resubmit',async()=>{assert.equal((await book()).status,'downloading');assert.equal(adds,1);});
  await restartWith(b=>{b.missingSince=new Date(Date.now()-180000).toISOString();});
  await waitFor(async()=> (await book()).status==='download-failed','missing failure');
  await check('Missing torrent becomes an explicit failure with a persistent block',async()=>{assert((await book()).failureReason.includes('missing'));assert((await book()).failedReleases[0].blocked);assert.equal(adds,1);});
  await stop();await start();
  await check('Failure survives restart and API history does not expose release URLs',async()=>{const b=await book();assert.equal(b.status,'download-failed');assert(!JSON.stringify(b).includes('do-not-expose'));});
  await api(`/books/${id}/retry`,'POST');
  await waitFor(async()=> (await book()).status==='downloading','same release retry');
  await check('Retry missing release adds it once and retains unblocked history',async()=>{assert.equal(adds,2);assert.equal((await book()).torrentHash,hashes[0]);assert.equal((await book()).failedReleases[0].blocked,false);});
  torrents.get(hashes[0]).state='error';
  await waitFor(async()=> (await book()).status==='download-failed','error state');
  torrents.get(hashes[0]).state='downloading';await api(`/books/${id}/retry`,'POST');
  await waitFor(async()=> (await book()).status==='downloading','existing retry');
  await check('Retry reconnects to existing torrent without duplicate submission',async()=>assert.equal(adds,2));
  torrents.get(hashes[0]).state='error';await waitFor(async()=> (await book()).status==='download-failed','second error');
  const alias={...releases[0],guid:'proxy-alias',indexerId:2,seeders:100,magnetUrl:undefined,downloadUrl:mockUrl+'/alias'};
  results=[alias,...releases];
  await api(`/books/${id}/replace`,'POST');
  await waitFor(async()=> (await book()).message.includes('resolves to a blocked torrent'),'proxy alias rejected');
  await check('Proxy alias of blocked hash is rejected before torrent submission',async()=>{assert.equal(adds,2);assert((await book()).failedReleases.some(f=>f.reason.includes('previously blocked')));});
  await api(`/books/${id}/retry`,'POST');
  await waitFor(async()=> {const b=await book();return b.torrentHash===hashes[1]&&b.status==='downloading';},'replacement submitted');
  await check('Replacement skips failed hash and retains old torrent and source',async()=>{assert.equal(adds,3);assert(torrents.has(hashes[0]));assert.deepEqual(await readFile(source),audio);});
  const rows=await api(`/books/${id}/releases`);
  await check('Release search explains blocked candidates',async()=>assert(rows.some(r=>!r.eligible&&r.reasons.some(reason=>reason.includes('Blocked')))));
  results=releases;
  settings={...settings,automaticFailedDownloadRecovery:true};await api('/settings','PUT',settings);
  torrents.get(hashes[1]).state='missingFiles';
  await waitFor(async()=> {const b=await book();return b.torrentHash===hashes[2]&&b.status==='downloading';},'automatic replacement submitted');
  await check('Opt-in recovery replaces missing-files torrent without deleting it',async()=>{assert(torrents.has(hashes[1]));assert.equal(adds,4);});
  unavailable=true;await api(`/books/${id}/retry`,'POST');
  await waitFor(async()=> (await book()).message.includes('HTTP 503'),'service outage');
  await check('Service outage is retryable and never blocks the release',async()=>{const b=await book();assert.equal(b.status,'downloading');assert(!b.failedReleases.some(f=>f.infoHash===hashes[2]));assert.equal(adds,4);});
  unavailable=false;await api(`/books/${id}/retry`,'POST');
  torrents.get(hashes[2]).state='stoppedDL';
  await restartWith(b=>{b.lastProgressAt=new Date(Date.now()-1200000).toISOString();b.lastDownloadCheck=new Date().toISOString();});
  await waitFor(async()=> (await book()).message.includes('stoppedDL'),'paused state');
  await check('Paused download does not trigger stall recovery',async()=>{assert.equal((await book()).status,'downloading');assert.equal(adds,4);});
  settings={...settings,automaticFailedDownloadRecovery:false};await api('/settings','PUT',settings);
  torrents.get(hashes[2]).state='stalledDL';
  await restartWith(b=>{b.lastProgressAt=new Date(Date.now()-1200000).toISOString();b.lastDownloadCheck=new Date().toISOString();});
  await waitFor(async()=> (await book()).status==='download-failed','stall timeout');
  await check('Persisted no-progress timer produces explicit stalled failure',async()=>assert((await book()).failureReason.includes('No download progress')));
  settings={...settings,manualSelection:true,automaticFailedDownloadRecovery:true};await api('/settings','PUT',settings);
  await new Promise(r=>setTimeout(r,1200));
  await check('Manual mode suppresses automatic replacement',async()=>assert.equal((await book()).status,'download-failed'));
  await api(`/books/${id}/replace`,'POST');
  const manual=await api(`/books/${id}/releases?q=The%20Hobbit`);
  const blocked=manual.find(r=>r.release.title===releases[2].title);
  await check('Manual replacement cannot choose a blocked release',async()=>{assert.equal(blocked.canSelect,false);await api(`/books/${id}/select`,'POST',{selectionIds:[blocked.selectionId]},400);});
  await api(`/books/${id}/clear-blocklist`,'POST');
  await check('Clearing blocklist preserves history and permits selection again',async()=>{assert((await book()).failedReleases.every(f=>!f.blocked));assert((await api(`/books/${id}/releases?q=The%20Hobbit`)).find(r=>r.release.title===releases[2].title).canSelect);});
  // Reuse the completed third torrent; avoid a new transfer while testing the import boundary.
  results=[releases[2]];settings={...settings,manualSelection:false,automaticFailedDownloadRecovery:false};await api('/settings','PUT',settings);
  torrents.get(hashes[2]).state='uploading';torrents.get(hashes[2]).progress=1;torrents.get(hashes[2]).amount_left=0;
  await writeFile(source,'invalid audio');
  await waitFor(async()=> (await book()).status==='import-failed','invalid audio');
  await check('Invalid audio becomes an import failure without blocking or replacing torrent',async()=>{assert.equal(adds,4);assert.equal(scans,0);assert(!(await book()).failedReleases.some(f=>f.blocked));await api(`/books/${id}/replace`,'POST',undefined,400);});
  torrents.delete(hashes[2]);await writeFile(source,audio);
  await api(`/books/${id}/retry`,'POST');
  await waitFor(async()=> (await book()).status==='available','import retry without torrent');
  const imported=(await book()).importedPath;
  await check('Import retry works after torrent removal without re-download',async()=>{assert.equal(adds,4);assert.equal(scans,1);assert.deepEqual(await readFile(path.join(imported,'book.mp3')),audio);});
  await restartWith(b=>{b.status='import-pending';b.importedPath=null;});
  await waitFor(async()=> (await book()).status==='available','atomic move recovery');
  await check('Crash after atomic import resumes without duplicate library entry',async()=>{assert.equal((await book()).importedPath,imported);assert.equal((await readdir(path.dirname(imported))).length,1);assert.equal(adds,4);});
  await check('Recovery activity persists with original files intact',async()=>{assert((await api('/activity')).some(a=>a.message.includes('replacement')));assert.deepEqual(await readFile(source),audio);});
  console.log(`\n${checks} recovery integration checks passed.`);
} finally {await stop();await new Promise(r=>mock.close(r));await rm(root,{recursive:true,force:true});}
