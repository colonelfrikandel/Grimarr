// Run with Node 24 after building the server and UI. Uses loopback mock services only.
// DOTNET_EXE can point to a local SDK/runtime. FFPROBE_DIR optionally adds ffprobe to PATH.
import http from 'node:http';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, mkdir, writeFile, readFile, readdir, stat, rm } from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
const root=await mkdtemp(path.join(os.tmpdir(),'grimarr-integration-'));
const config=path.join(root,'config'),downloads=path.join(root,'downloads'),library=path.join(root,'audiobooks');
await Promise.all([config,downloads,library].map(p=>mkdir(p,{recursive:true})));
const frame=Buffer.alloc(417);Buffer.from([0xff,0xfb,0x90,0x64]).copy(frame);
const audio=Buffer.concat(Array.from({length:60},()=>frame));
await writeFile(path.join(downloads,'audiobook.mp3'),audio);
const info=Buffer.concat([Buffer.from(`d6:lengthi${audio.length}e4:name13:audiobook.mp312:piece lengthi262144e6:pieces20:`),createHash('sha1').update(audio).digest(),Buffer.from('e')]);
const torrent=Buffer.concat([Buffer.from('d4:info'),info,Buffer.from('e')]);
const hash=createHash('sha1').update(info).digest('hex');
let added=false,complete=false,adds=0,scans=0,searches=0,processHandle;
let logs='';
const mock=http.createServer(async(req,res)=>{
 const chunks=[];for await(const chunk of req)chunks.push(chunk);const body=Buffer.concat(chunks).toString();
 const url=new URL(req.url,'http://localhost');res.setHeader('Content-Type','application/json');
 const json=value=>res.end(JSON.stringify(value));
 if(url.pathname==='/api/v2/auth/login'){assert(body.includes('username=admin'));res.setHeader('Set-Cookie','SID=test; Path=/');return res.end('Ok.');}
 if(url.pathname.startsWith('/api/v2/'))assert(req.headers.cookie?.includes('SID=test'),'qBittorrent SID cookie missing');
 if(url.pathname==='/api/v1/system/status')return json({version:'fixture'});
 if(url.pathname==='/api/libraries')return json({libraries:[{id:'fixture-library',name:'Audiobooks',mediaType:'book'}]});
 if(url.pathname==='/api/libraries/fixture-library/scan'){scans++;if(scans===1){res.statusCode=503;return json({error:'temporary failure'});}return json({success:true});}
 if(url.pathname==='/api/v1/search'){searches++;assert.equal(req.headers['x-api-key'],'indexer-secret');return json([{guid:'fixture',title:'J R R Tolkien - The Hobbit English Unabridged MP3',indexer:'Test indexer',indexerId:1,protocol:'torrent',downloadUrl:`http://127.0.0.1:${mock.address().port}/1/download?apikey=indexer-secret`,seeders:20,size:audio.length}]);}
 if(url.pathname==='/1/download'){res.setHeader('Content-Type','application/x-bittorrent');return res.end(torrent);}
 if(url.pathname==='/api/v2/torrents/add'){assert(body.includes('grimarr'));adds++;added=true;return res.end('Ok.');}
 if(url.pathname==='/api/v2/torrents/info')return json(added?[{hash,state:complete?'uploading':'downloading',progress:complete?1:0.4,amount_left:complete?0:100,content_path:'/remote/downloads/audiobook.mp3'}]:[]);
 res.statusCode=404;json({error:'Unknown mock route '+url.pathname});
});
mock.listen(0,'127.0.0.1');await once(mock,'listening');
const portProbe=http.createServer();portProbe.listen(0,'127.0.0.1');await once(portProbe,'listening');const port=portProbe.address().port;await new Promise(r=>portProbe.close(r));
const base=`http://127.0.0.1:${port}`;const mockUrl=`http://127.0.0.1:${mock.address().port}`;
let cookie='';let checks=0;
async function check(name,fn){await fn();checks++;console.log('PASS:',name);}
async function waitFor(fn,label){for(let n=0;n<100;n++){if(await fn())return;await new Promise(r=>setTimeout(r,200));}throw Error('Timeout: '+label+'\n'+logs);}
async function request(route,method='GET',body){const response=await fetch(base+'/api'+route,{method,headers:{'Content-Type':'application/json','X-Grimarr':'1',Cookie:cookie},body:body===undefined?undefined:JSON.stringify(body)});const text=await response.text();assert(response.ok,`HTTP ${response.status} on ${route}: ${text}`);return text?JSON.parse(text):undefined;}
async function start(){
 processHandle=spawn(process.env.DOTNET_EXE||'dotnet',[path.resolve('src/Grimarr/bin/Debug/net10.0/Grimarr.dll'),'--contentRoot',path.resolve('src/Grimarr')],{env:{...process.env,ASPNETCORE_URLS:base,GRIMARR_CONFIG:config,GRIMARR_PASSWORD:'integration-password-only',GRIMARR_POLL_SECONDS:'1',PATH:(process.env.FFPROBE_DIR?process.env.FFPROBE_DIR+path.delimiter:'')+process.env.PATH},stdio:['ignore','pipe','pipe'],windowsHide:true});
 processHandle.stdout.on('data',d=>logs+=d);processHandle.stderr.on('data',d=>logs+=d);
 await waitFor(()=>{if(processHandle.exitCode!==null)throw Error('Server exited: '+logs);return fetch(base+'/health',{signal:AbortSignal.timeout(1000)}).then(r=>r.ok).catch(()=>false);},'server start');
 const login=await fetch(base+'/api/login',{method:'POST',headers:{'Content-Type':'application/json','X-Grimarr':'1'},body:JSON.stringify({password:'integration-password-only'})});assert.equal(login.status,200);cookie=login.headers.get('set-cookie').split(';')[0];
}
async function stop(){if(processHandle&&!processHandle.killed&&processHandle.exitCode===null){processHandle.kill();await once(processHandle,'exit');}}
try{
 await start();
 await check('Authentication required',async()=>assert.equal((await fetch(base+'/api/settings')).status,401));
 await check('Cross-site form requests blocked',async()=>assert.equal((await fetch(base+'/api/logout',{method:'POST',headers:{Cookie:cookie}})).status,403));
 let settings=(await request('/settings')).settings;
 settings={...settings,prowlarr:{url:mockUrl,apiKey:'indexer-secret',username:'',password:''},qbittorrent:{url:mockUrl,apiKey:'',username:'admin',password:'qbit-secret'},audiobookshelf:{url:mockUrl,apiKey:'abs-secret',username:'',password:''},downloadRoot:downloads,libraryRoot:library,libraryId:'fixture-library',pathMappings:[{remote:'/remote/downloads',local:downloads}],automationEnabled:true};
 await check('Connection test loads Audiobookshelf libraries',async()=>assert.equal((await request('/connections/audiobookshelf/test','POST',settings)).libraries[0].id,'fixture-library'));
 await check('Settings save masks credentials',async()=>{const r=await request('/settings','PUT',settings);assert.equal(r.settings.prowlarr.apiKey,'');assert(r.secrets.prowlarr);});
 const book=await request('/books','POST',{title:'The Hobbit',author:'J R R Tolkien'});
 await check('Duplicate book is rejected',async()=>{const r=await fetch(base+'/api/books',{method:'POST',headers:{'Content-Type':'application/json','X-Grimarr':'1',Cookie:cookie},body:JSON.stringify({title:'The Hobbit',author:'J R R Tolkien'})});assert.equal(r.status,409);});
 await waitFor(async()=>((await request('/books'))[0]?.status==='downloading'),'automatic download');
 await check('Best match is sent exactly once',async()=>{assert.equal(adds,1);assert(searches>=1);});
 await stop();await start();
 await waitFor(async()=>((await request('/books'))[0]?.progress===0.4),'resume tracking');
 await check('Restart resumes without duplicate torrent',async()=>assert.equal(adds,1));
 complete=true;
 await waitFor(async()=>((await request('/books'))[0]?.status==='scan-pending'),'import and failed scan');
 await check('Failed ABS scan preserves completed import',async()=>{const b=(await request('/books'))[0];assert.equal(b.status,'scan-pending');assert((await stat(b.importedPath)).isDirectory());assert.equal(scans,1);});
 await stop();await start();await request(`/books/${book.id}/retry`,'POST');
 await waitFor(async()=>((await request('/books'))[0]?.status==='available'),'ABS scan retry');
 await check('Scan retry completes without re-download',async()=>{assert.equal(adds,1);assert.equal(scans,2);});
 const imported=(await request('/books'))[0].importedPath;
 await check('Imported audio matches original and source remains',async()=>assert.deepEqual(await readFile(path.join(imported,'audiobook.mp3')),await readFile(path.join(downloads,'audiobook.mp3'))));
 await check('Only one library item is imported',async()=>assert.equal((await readdir(path.dirname(imported))).length,1));
 await check('History survives restart',async()=>assert((await request('/activity')).length>=4));
 await check('Secret download URLs are not returned to browser',async()=>assert(!(JSON.stringify(await request('/books'))).includes('indexer-secret')));
 console.log(`\n${checks} integration checks passed.`);
}finally{await stop();await new Promise(r=>mock.close(r));await rm(root,{recursive:true,force:true});}
