// Quick Add uses the real backend with isolated loopback download/indexer/library fixtures.
import http from 'node:http';
import {spawn} from 'node:child_process';
import {once} from 'node:events';
import {mkdtemp,mkdir,writeFile,readFile,rm} from 'node:fs/promises';
import {DatabaseSync} from 'node:sqlite';
import {createHash} from 'node:crypto';
import path from 'node:path';
import os from 'node:os';
import assert from 'node:assert/strict';
const root=await mkdtemp(path.join(os.tmpdir(),'grimarr-quick-')),config=path.join(root,'config'),downloads=path.join(root,'downloads'),library=path.join(root,'library');
await Promise.all([config,downloads,library].map(p=>mkdir(p)));
const frame=Buffer.alloc(417);Buffer.from([0xff,0xfb,0x90,0x64]).copy(frame);const audio=Buffer.concat(Array.from({length:60},()=>frame));
await writeFile(path.join(downloads,'audio.mp3'),audio);
const info=Buffer.concat([Buffer.from(`d6:lengthi${audio.length}e4:name9:audio.mp312:piece lengthi262144e6:pieces20:`),createHash('sha1').update(audio).digest(),Buffer.from('e')]);
const tracker='https://tracker.example/private-file-key';
const torrent=Buffer.concat([Buffer.from(`d8:announce${tracker.length}:${tracker}4:info`),info,Buffer.from('e')]);
const fileHash=createHash('sha1').update(info).digest('hex'),hashes=['a'.repeat(40),'b'.repeat(40),fileHash],jobs=new Map(),adds=[];
let searches=0,scans=0,server,cookie='',logs='',checks=0;
const mock=http.createServer(async(req,res)=>{
 const chunks=[];for await(const c of req)chunks.push(c);const body=Buffer.concat(chunks),text=body.toString();const u=new URL(req.url,'http://localhost');
 const json=value=>{res.setHeader('Content-Type','application/json');res.end(JSON.stringify(value));};
 if(u.pathname==='/api/v2/auth/login'){res.setHeader('Set-Cookie','SID=fixture; Path=/');return res.end('Ok.');}
 if(u.pathname.startsWith('/api/v2/'))assert(req.headers.cookie?.includes('SID=fixture'));
 if(u.pathname==='/api/v2/torrents/add'){
  assert(text.includes('grimarr'));assert(text.includes('quick-add')===false);assert(text.includes('tags'));assert(text.includes(downloads));
  const hash=body.includes(torrent)?fileHash:text.match(/urn:btih:([a-f0-9]{40})/i)?.[1].toLowerCase();assert(hash);
  adds.push(hash);jobs.set(hash,{state:hash==='e'.repeat(40)?'error':'downloading',progress:0.2,amount_left:100,content_path:path.join(downloads,'audio.mp3')});return res.end('Ok.');
 }
 if(u.pathname==='/api/v2/torrents/info'){const j=jobs.get(u.searchParams.get('hashes'));return json(j?[j]:[]);}
 if(u.pathname==='/api/libraries/fixture/scan'){scans++;return json({success:true});}
 if(u.pathname==='/api/v1/search'){searches++;return json([{guid:'normal',title:'Catalog Fixture - Fixture Author English Unabridged MP3',indexer:'Fixture',indexerId:1,protocol:'torrent',seeders:20,size:audio.length,magnetUrl:'magnet:?xt=urn:btih:'+'d'.repeat(40)}]);}
 res.statusCode=404;json({error:u.pathname});
});
mock.listen(0,'127.0.0.1');await once(mock,'listening');
const probe=http.createServer();probe.listen(0,'127.0.0.1');await once(probe,'listening');const port=probe.address().port;await new Promise(r=>probe.close(r));
const base=`http://127.0.0.1:${port}`,mockUrl=`http://127.0.0.1:${mock.address().port}`;
async function api(route,method='GET',body,status=200){const r=await fetch(base+'/api'+route,{method,headers:{'X-Grimarr':'1','Content-Type':'application/json',Cookie:cookie},body:body===undefined?undefined:JSON.stringify(body)});const text=await r.text();assert.equal(r.status,status,route+': '+text);return text?JSON.parse(text):null;}
async function wait(fn,label){for(let i=0;i<100;i++){if(await fn())return;await new Promise(r=>setTimeout(r,100));}throw Error(label+'\n'+logs);}
async function check(label,fn){await fn();checks++;console.log('PASS:',label);}
async function start(){server=spawn(process.env.DOTNET_EXE||'dotnet',[path.resolve('src/Grimarr/bin/Debug/net10.0/Grimarr.dll'),'--contentRoot',path.resolve('src/Grimarr')],{env:{...process.env,ASPNETCORE_URLS:base,GRIMARR_CONFIG:config,GRIMARR_PASSWORD:'quick-add-fixture-password',GRIMARR_POLL_SECONDS:'1',PATH:(process.env.FFPROBE_DIR?process.env.FFPROBE_DIR+path.delimiter:'')+process.env.PATH},stdio:['ignore','pipe','pipe'],windowsHide:true});server.stdout.on('data',d=>logs+=d);server.stderr.on('data',d=>logs+=d);await wait(()=>fetch(base+'/health').then(r=>r.ok).catch(()=>false),'startup');const r=await fetch(base+'/api/login',{method:'POST',headers:{'Content-Type':'application/json','X-Grimarr':'1'},body:JSON.stringify({password:'quick-add-fixture-password'})});assert(r.ok);cookie=r.headers.get('set-cookie').split(';')[0];}
async function stop(){if(server?.exitCode===null){server.kill();await once(server,'exit');}}
try {
 await start();
 await check('Quick Add requires authentication',async()=>assert.equal((await fetch(base+'/api/books/quick-add',{method:'POST',headers:{'Content-Type':'application/json','X-Grimarr':'1'},body:'{}'})).status,401));
 await check('Quick Add blocks cross-site form requests',async()=>assert.equal((await fetch(base+'/api/books/quick-add',{method:'POST',headers:{Cookie:cookie},body:'{}'})).status,403));
 let settings=(await api('/settings')).settings;
 settings={...settings,prowlarr:{url:'',apiKey:''},qbittorrent:{url:mockUrl,username:'admin',password:'fixture'},audiobookshelf:{url:mockUrl,apiKey:'fixture'},libraryId:'fixture',downloadRoot:downloads,libraryRoot:library,qbitSavePath:downloads,automationEnabled:false,manualSelection:true};
 await api('/settings','PUT',settings);
 await check('Preview requires authentication',async()=>assert.equal((await fetch(base+'/api/books/quick-add/preview',{method:'POST',headers:{'Content-Type':'application/json','X-Grimarr':'1'},body:JSON.stringify({source:hashes[0]})})).status,401));
 await check('Preview requires the same anti-CSRF header',async()=>assert.equal((await fetch(base+'/api/books/quick-add/preview',{method:'POST',headers:{'Content-Type':'application/json',Cookie:cookie},body:'{}'})).status,403));
 await check('Magnet preview suggests details without queuing or contacting services',async()=>{
  const suggestion=await api('/books/quick-add/preview','POST',{source:'magnet:?xt=urn:btih:'+hashes[0]+'&dn='+encodeURIComponent('Cornman - Mage Tank 03 [M4B]')+'&tr=https%3A%2F%2Ftracker.example%2Fprivate-preview-key'});
  assert.equal(suggestion.title,'Mage Tank 03');assert.equal(suggestion.author,'Cornman');assert(!JSON.stringify(suggestion).includes('private-preview-key'));
  assert.equal((await api('/books')).length,0);assert.equal(adds.length,0);assert.equal(searches,0);
 });
 await check('Torrent preview reads internal name without starting a download',async()=>{const s=await api('/books/quick-add/preview','POST',{torrentBase64:torrent.toString('base64')});assert.equal(s.title,'audio');assert.equal(s.author,'');assert.equal(adds.length,0);assert.equal((await api('/books')).length,0);});
 await check('Bare hash preview leaves both fields for manual entry',async()=>{const s=await api('/books/quick-add/preview','POST',{source:hashes[0]});assert.equal(s.title,'');assert.equal(s.author,'');});
 await check('Invalid preview source is rejected without creating a record',async()=>{await api('/books/quick-add/preview','POST',{torrentBase64:Buffer.from('invalid').toString('base64')},400);assert.equal((await api('/books')).length,0);});
 await check('Invalid Quick Add sources create no records',async()=>{for(const input of [{source:'bad'},{source:hashes[0],torrentBase64:torrent.toString('base64')},{torrentBase64:Buffer.from('<html>wrong</html>').toString('base64')},{source:'http://127.0.0.1/private'}])await api('/books/quick-add','POST',{title:'Invalid',author:'Fixture Author',...input},400);assert.equal((await api('/books')).length,0);});
 await check('Unrecognized Apple choice cannot create a book or submit a torrent',async()=>{await api('/books/quick-add','POST',{title:'Invalid catalog choice',author:'Fixture',source:hashes[0],catalogSelectionId:'forged'},400);assert.equal((await api('/books')).length,0);assert.equal(adds.length,0);});
 const a=await api('/books/quick-add','POST',{title:'Hash Fixture',author:'Fixture Author',source:hashes[0].toUpperCase()});
 const b=await api('/books/quick-add','POST',{title:'Magnet Fixture',author:'Fixture Author',source:'magnet:?xt=urn:btih:'+hashes[1]+'&tr=https%3A%2F%2Ftracker.example%2Fprivate-magnet-key'});
 const c=await api('/books/quick-add','POST',{title:'Torrent Fixture',author:'Fixture Author',torrentBase64:torrent.toString('base64')});
 await check('Hash, magnet and file stay queued while automation is paused',async()=>{assert.equal(adds.length,0);assert([a,b,c].every(x=>x.status==='queued'&&x.acquisitionMode==='quick-add'&&x.canRemove));});
 await check('Quick Add rejects duplicate hashes across input types',async()=>{await api('/books/quick-add','POST',{title:'Duplicate',author:'Fixture Author',source:'magnet:?xt=urn:btih:'+hashes[0]},409);await api('/books/quick-add','POST',{title:'File duplicate',author:'Fixture Author',source:fileHash},409);});
 await check('Quick Add respects existing title/author uniqueness',async()=>{await api('/books/quick-add','POST',{title:'Hash Fixture',author:'Fixture Author',source:'f'.repeat(40)},409);});
 await check('Quick Add API omits magnet credentials and torrent payload',async()=>{const text=JSON.stringify(await api('/books'));assert(!text.includes('private-magnet-key'));assert(!text.includes('private-file-key'));assert(!text.includes(torrent.toString('base64')));assert(!text.includes('magnet:'));});
 await stop();
 const db=new DatabaseSync(path.join(config,'grimarr.db'));
 const stored=db.prepare('SELECT json FROM books').all().map(x=>JSON.parse(x.json));db.close();
 await check('Private magnets and uploaded metadata are encrypted at rest',async()=>{assert(stored.find(x=>x.id===b.id).selectedRelease.magnetUrl.startsWith('protected:'));assert(stored.find(x=>x.id===c.id).selectedRelease.torrentData.startsWith('protected:'));assert(!JSON.stringify(stored).includes('private-magnet-key'));});
 await start();await check('Quick Add queue survives restart',async()=>assert.equal((await api('/books')).filter(x=>x.status==='queued').length,3));
 settings={...settings,automationEnabled:true,automaticFailedDownloadRecovery:true};
 await check('Automation can run Quick Add without Prowlarr credentials',async()=>{await api('/settings','PUT',settings);await wait(()=>Promise.resolve(adds.length===3),'three submissions');});
 await check('All three input types submit once with configured category/tag/save path',async()=>{assert.deepEqual([...adds].sort(),[...hashes].sort());assert.equal(searches,0);});
 settings={...settings,manualSelection:false};await api('/settings','PUT',settings);
 jobs.get(fileHash).state='error';await wait(async()=> (await api('/books')).find(x=>x.id===c.id).status==='download-failed','file failure');await new Promise(r=>setTimeout(r,1200));
 await check('Failed Quick Add does not silently search Prowlarr even with automatic recovery on',async()=>{assert.equal(searches,0);assert.equal((await api('/books')).find(x=>x.id===c.id).status,'download-failed');});
 jobs.delete(fileHash);await api('/books/'+c.id+'/retry','POST');await wait(()=>Promise.resolve(adds.filter(h=>h===fileHash).length===2),'file resubmission');
 await check('Uploaded torrent can be resubmitted from encrypted persisted metadata',async()=>assert.equal(adds.filter(h=>h===fileHash).length,2));
 for(const j of jobs.values())Object.assign(j,{state:'uploading',progress:1,amount_left:0});
 await wait(async()=> (await api('/books')).every(x=>x.status==='available'),'three imports');
 await check('All Quick Add inputs complete validated imports and scan requests',async()=>{const books=await api('/books');assert.equal(scans,3);for(const x of books)assert.deepEqual(await readFile(path.join(x.importedPath,'audio.mp3')),audio);assert.equal(searches,0);});
 await api('/books','POST',{title:'Catalog Fixture',author:'Fixture Author'});
 await wait(async()=> (await api('/books')).some(x=>x.message.includes('Configure Prowlarr')),'missing indexer message');
 settings={...settings,prowlarr:{url:mockUrl,apiKey:'fixture'}};await api('/settings','PUT',settings);
 await wait(()=>Promise.resolve(jobs.has('d'.repeat(40))),'Prowlarr download');
 await check('Existing Prowlarr search remains available alongside Quick Add',async()=>{assert(searches>0);assert.equal((await api('/books')).find(x=>x.title==='Catalog Fixture').acquisitionMode,'prowlarr');});
 const e=await api('/books/quick-add','POST',{title:'Switch Fixture',author:'Fixture Author',source:'e'.repeat(40)});
 await wait(async()=> (await api('/books')).find(x=>x.id===e.id).status==='download-failed','switch failure');
 settings={...settings,manualSelection:true};await api('/settings','PUT',settings);await api('/books/'+e.id+'/replace','POST');
 await check('Explicit replacement switches a Quick Add failure to Prowlarr mode',async()=>{const switched=(await api('/books')).find(x=>x.id===e.id);assert.equal(switched.acquisitionMode,'prowlarr');assert.equal(switched.status,'wanted');assert.equal(switched.selectedRelease,null);});
 console.log(`\n${checks} Quick Add integration checks passed.`);
} finally {await stop();await new Promise(r=>mock.close(r));await rm(root,{recursive:true,force:true});}
