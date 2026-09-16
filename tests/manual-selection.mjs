// Local mock services only; never downloads or imports real content.
import http from 'node:http';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
const config = await mkdtemp(path.join(os.tmpdir(), 'grimarr-manual-'));
const hashes = Array.from({length:5},(_,i)=>(i+1).toString().repeat(40));
let queries = [], added = [], server, logs = '', cookie = '', checks = 0;
const releases = hashes.map((hash,i)=>({guid:`fixture-${i}`,indexerId:1,indexer:'Fixture',protocol:'torrent',title:`Mage Tank Book ${i+1} - Test Author English unabridged`,seeders:10,size:1000,magnetUrl:`magnet:?xt=urn:btih:${hash}`}));
const mock = http.createServer(async (req,res)=>{
  const url = new URL(req.url,'http://localhost'); let body=''; for await(const chunk of req) body+=chunk;
  const json = value => {res.setHeader('Content-Type','application/json');res.end(JSON.stringify(value));};
  if(url.pathname==='/api/v1/search') {
    queries.push(url.searchParams.get('query'));
    if(queries.at(-1).startsWith('Automatic')) return json([1,20].map((seeders,i)=>({...releases[0],guid:`auto-${i}`,title:'Automatic Test - Test Author English unabridged',seeders,magnetUrl:`magnet:?xt=urn:btih:${(i?'b':'a').repeat(40)}`})));
    return json(releases);
  }
  if(url.pathname==='/api/v2/auth/login') {res.setHeader('Set-Cookie','SID=fixture; Path=/');return res.end('Ok.');}
  if(url.pathname==='/api/v2/torrents/add') {const hash=body.match(/urn:btih:([a-f0-9]{40})/)?.[1];assert(hash);added.push(hash);return res.end('Ok.');}
  if(url.pathname==='/api/v2/torrents/info') return json(added.includes(url.searchParams.get('hashes')) ? [{state:'downloading',progress:0.2,amount_left:800,content_path:'/unused'}] : []);
  res.statusCode=404;res.end();
});
mock.listen(0,'127.0.0.1');await once(mock,'listening');
const probe=http.createServer();probe.listen(0,'127.0.0.1');await once(probe,'listening');const port=probe.address().port;await new Promise(r=>probe.close(r));
const base=`http://127.0.0.1:${port}`,mockUrl=`http://127.0.0.1:${mock.address().port}`;
async function waitFor(fn,label){for(let n=0;n<80;n++){if(await fn())return;await new Promise(r=>setTimeout(r,100));}throw Error(label+'\n'+logs);}
async function api(route,method='GET',body,status=200){const r=await fetch(base+'/api'+route,{method,headers:{'X-Grimarr':'1','Content-Type':'application/json',Cookie:cookie},body:body===undefined?undefined:JSON.stringify(body)});const text=await r.text();assert.equal(r.status,status,`${route}: ${text}`);return text?JSON.parse(text):null;}
async function check(name,fn){await fn();checks++;console.log('PASS:',name);}
async function start(){
  server=spawn(process.env.DOTNET_EXE||'dotnet',[path.resolve('src/Grimarr/bin/Debug/net10.0/Grimarr.dll'),'--contentRoot',path.resolve('src/Grimarr')],{env:{...process.env,ASPNETCORE_URLS:base,GRIMARR_CONFIG:config,GRIMARR_PASSWORD:'manual-selection-fixture',GRIMARR_POLL_SECONDS:'1'},stdio:['ignore','pipe','pipe'],windowsHide:true});
  server.stdout.on('data',d=>logs+=d);server.stderr.on('data',d=>logs+=d);
  await waitFor(()=>fetch(base+'/health').then(r=>r.ok).catch(()=>false),'server startup');
  const r=await fetch(base+'/api/login',{method:'POST',headers:{'Content-Type':'application/json','X-Grimarr':'1'},body:JSON.stringify({password:'manual-selection-fixture'})});assert(r.ok);cookie=r.headers.get('set-cookie').split(';')[0];
}
async function stop(){if(server?.exitCode===null){server.kill();await once(server,'exit');}}
try {
  await start();
  let settings=(await api('/settings')).settings;
  assert.equal(settings.manualSelection,false);
  settings={...settings,downloadRoot:path.join(config,'downloads'),libraryRoot:path.join(config,'library'),automationEnabled:true,manualSelection:true,prowlarr:{url:mockUrl,apiKey:'fixture'},qbittorrent:{url:mockUrl,username:'admin',password:'fixture'},audiobookshelf:{url:mockUrl,apiKey:'fixture'},libraryId:'fixture'};
  await api('/settings','PUT',settings);
  const book=await api('/books','POST',{title:'Mage Tank: A LitRPG Adventure',author:'Test Author'});
  await new Promise(r=>setTimeout(r,1200));
  await check('Manual mode never automatically chooses a release',async()=>{assert.equal(added.length,0);assert.equal(queries.length,0);assert.equal((await api('/books'))[0].status,'wanted');});
  const rows=await api(`/books/${book.id}/releases?q=Mage%20Tank`);
  await check('Broad manual query returns five selectable volumes without secrets',async()=>{assert.equal(queries.at(-1),'Mage Tank');assert.equal(rows.length,5);assert(rows.every(r=>r.canSelect&&r.selectionId));assert(!JSON.stringify(rows).includes('magnet:'));assert(rows.some(r=>!r.eligible));});
  await check('Forged selection cannot submit an arbitrary download',async()=>{await api(`/books/${book.id}/select`,'POST',{selectionIds:['forged'],downloadUrl:'http://127.0.0.1/private'},400);assert.equal((await api('/books')).length,1);});
  const other=await api('/books','POST',{title:'Another search',author:'Test Author'});
  await check('Choices are bound to their original book',async()=>{await api(`/books/${other.id}/select`,'POST',{selectionIds:[rows[0].selectionId]},400);});
  await check('Invalid batch is rejected without partially queuing valid choices',async()=>{await api(`/books/${book.id}/select`,'POST',{selectionIds:[rows[0].selectionId,'expired']},400);assert.equal((await api('/books')).find(b=>b.id===book.id).status,'wanted');});
  const chosen=[rows[0].selectionId,rows[4].selectionId];
  const queued=await api(`/books/${book.id}/select`,'POST',{selectionIds:chosen});
  await check('Multiple selected volumes get distinct tracked entries',async()=>{assert.equal(queued.length,2);assert.equal(new Set(queued.map(b=>b.id)).size,2);assert(queued.some(b=>b.id===book.id));assert(queued.every(b=>b.status==='queued'));});
  await check('Repeated submission cannot create duplicate books',async()=>{await api(`/books/${book.id}/select`,'POST',{selectionIds:chosen},400);assert.equal((await api('/books')).length,3);});
  await waitFor(()=>Promise.resolve(added.length===2),'selected downloads');
  await check('Only the two chosen torrents were submitted',async()=>assert.deepEqual([...added].sort(),[hashes[0],hashes[4]].sort()));
  const dup=await api('/books','POST',{title:'Duplicate search',author:'Test Author'});
  const dupRows=await api(`/books/${dup.id}/releases?q=Mage%20Tank`);
  await check('Already tracked options are marked and not selectable',async()=>{assert.equal(dupRows[0].alreadyTracked,true);assert.equal(dupRows[0].canSelect,false);});
  await check('Already tracked release is rejected from a different search',async()=>{await api(`/books/${dup.id}/select`,'POST',{selectionIds:[dupRows[0].selectionId]},400);});
  await stop();await start();
  await check('Selected entries and setting survive restart',async()=>{assert((await api('/settings')).settings.manualSelection);assert.equal((await api('/books')).filter(b=>b.status==='downloading').length,2);assert.equal(added.length,2);});
  await check('Search choices expire across a restart',async()=>{await api(`/books/${dup.id}/select`,'POST',{selectionIds:[dupRows[1].selectionId]},400);});
  // Simulate a completed import while the isolated fixture server is stopped.
  await stop();
  const db=new DatabaseSync(path.join(config,'grimarr.db'));
  const completed=JSON.parse(db.prepare('SELECT json FROM books WHERE id=?').get(book.id).json);
  completed.status='available';completed.importedPath=path.join(config,'library','existing');
  db.prepare('UPDATE books SET json=? WHERE id=?').run(JSON.stringify(completed),book.id);db.close();
  await start();
  const more=await api(`/books/${book.id}/releases?q=Mage%20Tank`);
  await check('Completed entry cannot queue its existing release again',async()=>{await api(`/books/${book.id}/select`,'POST',{selectionIds:[more[0].selectionId]},400);});
  const extra=await api(`/books/${book.id}/select`,'POST',{selectionIds:[more[2].selectionId]});
  await check('Choosing another volume from a completed entry preserves the original',async()=>{assert.equal(extra.length,1);assert.notEqual(extra[0].id,book.id);const original=(await api('/books')).find(b=>b.id===book.id);assert.equal(original.status,'available');assert.equal(original.importedPath,completed.importedPath);});
  await waitFor(()=>Promise.resolve(added.length===3),'additional volume');
  const fresh=await api('/books','POST',{title:'Automatic Test',author:'Test Author'});
  await api('/settings','PUT',{...settings,manualSelection:false});
  await check('Manual submission is disabled when setting is off',async()=>{await api(`/books/${fresh.id}/select`,'POST',{selectionIds:chosen},409);});
  await waitFor(()=>Promise.resolve(added.includes('b'.repeat(40))),'automatic best match');
  await check('Turning manual mode off restores best-match automatic download',async()=>{assert(!added.includes('a'.repeat(40)));assert.equal(added.length,4);});
  console.log(`\n${checks} manual-selection integration checks passed.`);
} finally {
  await stop();await new Promise(r=>mock.close(r));
  assert(path.resolve(config).startsWith(path.join(path.resolve(os.tmpdir()),'grimarr-manual-')));
  await rm(config,{recursive:true,force:true});
}
