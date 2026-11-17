import http from 'node:http';
import {spawn} from 'node:child_process';
import {once} from 'node:events';
import {mkdtemp,mkdir,writeFile,readFile,rm,realpath} from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import assert from 'node:assert/strict';
const root=await mkdtemp(path.join(os.tmpdir(),'grimarr-availability-')),config=path.join(root,'config'),library=path.join(root,'library'),downloads=path.join(root,'downloads');
await Promise.all([config,library,downloads].map(p=>mkdir(p)));
const frame=Buffer.alloc(417);Buffer.from([255,251,144,100]).copy(frame);const audio=Buffer.concat(Array.from({length:60},()=>frame));await writeFile(path.join(downloads,'book.mp3'),audio);
const item=(id,title,author,relPath='existing/'+id)=>({id,mediaType:'book',relPath,path:'/abs/'+relPath,media:{numAudioFiles:1,metadata:{title,authors:[{name:author}]}}});
const first=item('existing-one','Existing Book: A Tale','Jane Austen'),second=item('existing-two','Existing Book','Jane Austen');
let items=[item('unrelated','Unrelated','Other'),first,second],offline=false,brokenPagination=false,scans=0,searches=0,adds=[],jobs=new Map(),pages=[],server,cookie='',logs='',checks=0;
const mock=http.createServer(async(req,res)=>{
 let body='';for await(const c of req)body+=c;const u=new URL(req.url,'http://localhost');const json=x=>{res.setHeader('Content-Type','application/json');res.end(JSON.stringify(x));};
 if(u.pathname==='/api/libraries/fixture/items'){
  if(offline){res.statusCode=503;return json({error:'offline'});}
  const page=Number(u.searchParams.get('page'));pages.push(page);
  // Deliberately return short pages with a total so the client cannot assume the first page is complete.
  return json({results:brokenPagination?[]:items.slice(page,page+1),total:items.length});
 }
 if(u.pathname==='/api/libraries/fixture/scan'){scans++;return json({success:true});}
 if(u.pathname==='/api/v1/search'){searches++;return json([]);}
 if(u.pathname==='/api/v2/auth/login'){res.setHeader('Set-Cookie','SID=fixture; Path=/');return res.end('Ok.');}
 if(u.pathname==='/api/v2/torrents/add'){const hash=body.match(/urn:btih:([a-f0-9]{40})/)?.[1];assert(hash);adds.push(hash);jobs.set(hash,{state:'downloading',progress:0.2,amount_left:100,content_path:path.join(downloads,'book.mp3')});return res.end('Ok.');}
 if(u.pathname==='/api/v2/torrents/info'){const job=jobs.get(u.searchParams.get('hashes'));return json(job?[job]:[]);}
 res.statusCode=404;json({error:u.pathname});
});
mock.listen(0,'127.0.0.1');await once(mock,'listening');const probe=http.createServer();probe.listen(0,'127.0.0.1');await once(probe,'listening');const port=probe.address().port;await new Promise(r=>probe.close(r));const base=`http://127.0.0.1:${port}`,mockUrl=`http://127.0.0.1:${mock.address().port}`;
async function api(route,method='GET',data,status=200){const r=await fetch(base+'/api'+route,{method,headers:{'Content-Type':'application/json','X-Grimarr':'1',Cookie:cookie},body:data===undefined?undefined:JSON.stringify(data)});const text=await r.text();assert.equal(r.status,status,route+': '+text);return text?JSON.parse(text):null;}
async function wait(fn,label){for(let i=0;i<120;i++){if(await fn())return;await new Promise(r=>setTimeout(r,100));}throw Error(label+' '+JSON.stringify(await api('/books').catch(()=>[]))+' items='+JSON.stringify(items)+' scans='+scans+'\n'+logs.slice(-2000));}
async function start(){server=spawn(process.env.DOTNET_EXE||'dotnet',[path.resolve('src/Grimarr/bin/Debug/net10.0/Grimarr.dll'),'--contentRoot',path.resolve('src/Grimarr')],{env:{...process.env,ASPNETCORE_URLS:base,GRIMARR_CONFIG:config,GRIMARR_PASSWORD:'library-availability-fixture',GRIMARR_POLL_SECONDS:'1',PATH:(process.env.FFPROBE_DIR?process.env.FFPROBE_DIR+path.delimiter:'')+process.env.PATH},stdio:['ignore','pipe','pipe'],windowsHide:true});server.stdout.on('data',d=>logs+=d);server.stderr.on('data',d=>logs+=d);await wait(()=>fetch(base+'/health').then(r=>r.ok).catch(()=>false),'startup');const r=await fetch(base+'/api/login',{method:'POST',headers:{'Content-Type':'application/json','X-Grimarr':'1'},body:JSON.stringify({password:'library-availability-fixture'})});assert(r.ok);cookie=r.headers.get('set-cookie').split(';')[0];}
async function stop(){if(server?.exitCode===null){server.kill();await once(server,'exit');}}
const book=async id=>(await api('/books')).find(b=>b.id===id);
async function check(label,fn){await fn();checks++;console.log('PASS:',label);}
async function quick(title,author,letter){return api('/books/quick-add','POST',{title,author,source:letter.repeat(40)});}
try {
 await start();let settings=(await api('/settings')).settings;settings={...settings,downloadRoot:downloads,libraryRoot:library,qbitSavePath:downloads,libraryId:'fixture',audiobookshelf:{url:mockUrl,apiKey:'fixture'},qbittorrent:{url:mockUrl,username:'admin',password:'fixture'},prowlarr:{url:mockUrl,apiKey:'fixture'},automationEnabled:true};await api('/settings','PUT',settings);
 const a=await quick('Existing Book','Jane Austen','a');await wait(async()=>(await book(a.id)).status==='library-review','duplicate review');
 await check('Later-page and ambiguous Audiobookshelf matches hold Quick Add before torrent submission',async()=>{const b=await book(a.id);assert.equal(b.libraryMatches.length,2);assert(b.libraryMatches.every(m=>m.sameAuthor));assert(pages.includes(2));assert.equal(adds.length,0);assert(b.canRemove);});
 await check('Forged existing item cannot be accepted',()=>api(`/books/${a.id}/library-review`,'POST',{action:'use-existing',itemId:'forged'},400));
 await check('Retry cannot bypass an unresolved duplicate warning',async()=>{await api(`/books/${a.id}/retry`,'POST');assert.equal((await book(a.id)).status,'library-review');assert.equal(adds.length,0);});
 items=items.filter(i=>i.id!==first.id);
 await check('Chosen existing item is revalidated before confirmation',()=>api(`/books/${a.id}/library-review`,'POST',{action:'use-existing',itemId:first.id},400));
 await api(`/books/${a.id}/library-review`,'POST',{action:'use-existing',itemId:second.id});
 await check('Explicit existing selection confirms availability without importing or downloading',async()=>{const b=await book(a.id);assert.equal(b.status,'available');assert.equal(b.audiobookshelfItemId,second.id);assert.equal(b.verifiedLibraryId,'fixture');assert.equal(b.importedPath,null);assert.equal(adds.length,0);assert.equal(scans,0);});
 await stop();await start();await check('Existing-library confirmation survives restart',async()=>assert.equal((await book(a.id)).audiobookshelfItemId,second.id));
 const b=await quick('Existing Book','Different Author','b');await wait(async()=>(await book(b.id)).status==='library-review','uncertain author review');
 await check('A title match with another author requires review rather than automatic availability',async()=>{assert((await book(b.id)).libraryMatches.every(m=>!m.sameAuthor));assert.equal(adds.length,0);});
 await api(`/books/${b.id}/library-review`,'POST',{action:'download-anyway'});await wait(()=>adds.includes('b'.repeat(40)),'explicit override');
 await check('Only explicit download-another-copy approval bypasses the warning',async()=>{assert.equal((await book(b.id)).status,'downloading');assert.equal(adds.length,1);});
 const p=await api('/books','POST',{title:'Existing Book','author':'Prowlarr Author'});await wait(async()=>(await book(p.id)).status==='library-review','Prowlarr held');
 await check('Prowlarr acquisition is held before indexer search',async()=>assert.equal(searches,0));
 items=[];await api(`/books/${p.id}/library-review`,'POST',{action:'recheck'});await wait(()=>searches>0,'recheck release');
 await check('Recheck resumes acquisition when existing matches disappear',async()=>assert.equal((await book(p.id)).status,'wanted'));
 offline=true;const outage=await quick('Outage Fixture','Test','c');await wait(async()=>(await book(outage.id)).message.includes('503'),'library outage');
 await check('Library outages hold new downloads without release blocklisting',async()=>{const b=await book(outage.id);assert.equal(b.status,'queued');assert.equal(b.failedReleases.length,0);assert(!adds.includes('c'.repeat(40)));});
 offline=false;brokenPagination=true;items=[first];await api(`/books/${outage.id}/retry`,'POST');await wait(async()=>(await book(outage.id)).message.includes('incomplete library page'),'incomplete pagination');
 await check('Incomplete library pagination fails closed before downloading',async()=>assert(!adds.includes('c'.repeat(40))));
 brokenPagination=false;items=[];await api(`/books/${outage.id}/retry`,'POST');await wait(()=>adds.includes('c'.repeat(40)),'outage recovery');
 await check('Restored library checks resume the same queued download once',async()=>assert.equal(adds.filter(h=>h==='c'.repeat(40)).length,1));
 const index=await quick('Index Fixture','Test','d');await wait(async()=>(await book(index.id)).status==='downloading','index fixture started');Object.assign(jobs.get('d'.repeat(40)),{state:'uploading',progress:1,amount_left:0});await wait(async()=>(await book(index.id)).status==='indexing','scan requested');
 await check('Successful scan response alone never marks an import available',async()=>{const b=await book(index.id);assert.equal(b.status,'indexing');assert(b.importedPath);assert.equal(b.audiobookshelfItemId,null);assert.equal(scans,1);});
 const imported=await book(index.id),relative=path.relative(await realpath(library),await realpath(imported.importedPath)).replaceAll('\\','/');items=[item('wrong-directory',index.title,index.author,'different/'+relative)];await api(`/books/${index.id}/retry`,'POST');await wait(()=>scans===2,'rescan');await wait(async()=>(await book(index.id)).status==='indexing','unrelated path rejected');
 await check('Same title at another directory cannot confirm this import',async()=>assert.equal((await book(index.id)).audiobookshelfItemId,null));
 items=[{...item('missing-item',index.title,index.author,relative),isMissing:true}];await api(`/books/${index.id}/retry`,'POST');await wait(()=>scans===3,'missing rescan');await wait(async()=>(await book(index.id)).status==='indexing','missing item rejected');
 await check('Missing Audiobookshelf items do not count as confirmed audio',async()=>assert.equal((await book(index.id)).audiobookshelfItemId,null));
 items=[item('indexed-item',index.title,index.author,relative)];await api(`/books/${index.id}/retry`,'POST');await wait(async()=>(await book(index.id)).status==='available','actual indexed confirmation');
 await check('Actual indexed directory confirms availability and retains item identity',async()=>{const b=await book(index.id);assert.equal(b.audiobookshelfItemId,'indexed-item');assert.equal(b.verifiedLibraryId,'fixture');assert.deepEqual(await readFile(path.join(b.importedPath,'book.mp3')),audio);assert.equal(adds.filter(h=>h==='d'.repeat(40)).length,1);});
 console.log(`\n${checks} library-availability integration checks passed.`);
} finally {await stop();await new Promise(r=>mock.close(r));await rm(root,{recursive:true,force:true});}
