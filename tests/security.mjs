import http from 'node:http';
import {spawn} from 'node:child_process';
import {once} from 'node:events';
import {Readable} from 'node:stream';
import {mkdtemp,mkdir,writeFile,readFile,readdir,rm} from 'node:fs/promises';
import {DatabaseSync} from 'node:sqlite';
import {createHash} from 'node:crypto';
import path from 'node:path';
import os from 'node:os';
import assert from 'node:assert/strict';
const root=await mkdtemp(path.join(os.tmpdir(),'grimarr-security-')),config=path.join(root,'config'),library=path.join(root,'library'),downloads=path.join(root,'downloads');await Promise.all([config,library,downloads].map(p=>mkdir(p)));
const unsafe=Buffer.from('d4:infod6:lengthi4e4:name8:../x.mp312:piece lengthi16384e6:pieces20:abcdefghijklmnopqrstee');
const info=Buffer.from('d6:lengthi4e4:name8:book.mp312:piece lengthi16384e6:pieces20:abcdefghijklmnopqrste');const torrent=Buffer.concat([Buffer.from('d4:info'),info,Buffer.from('e')]);const hash=createHash('sha1').update(info).digest('hex');
let mode='newline',adds=0,attackerRequests=0,complete=false,server,cookie='',logs='',checks=0,password='security-fixture-password';
const attacker=http.createServer((req,res)=>{attackerRequests++;res.end('untrusted');});attacker.listen(0,'127.0.0.1');await once(attacker,'listening');const attackerUrl=`http://127.0.0.1:${attacker.address().port}`;
const mock=http.createServer(async(req,res)=>{
 let body='';for await(const c of req)body+=c;const u=new URL(req.url,'http://localhost');const json=x=>{res.setHeader('Content-Type','application/json');res.end(JSON.stringify(x));};
 if(u.pathname==='/api/libraries/fixture/items')return json({results:[],total:0});
 if(u.pathname==='/api/v2/auth/login'){res.setHeader('Set-Cookie','SID=fixture; Path=/');return res.end('Ok.');}
 if(u.pathname==='/api/v2/torrents/info')return json(adds?[{state:complete?'uploading':'downloading',progress:complete?1:0.1,amount_left:complete?0:1,content_path:path.join(downloads,'playlist.mp3')}]:[]);
 if(u.pathname==='/api/v2/torrents/add'){adds++;return res.end('Ok.');}
 if(u.pathname==='/api/v1/search') {
  const magnet=mode==='newline'?'magnet:?xt=urn:btih:'+'a'.repeat(40)+'\n'+attackerUrl:undefined;
  return json([{guid:mockUrl+'/identifier?passkey=private-identifier-sentinel',indexerId:1,indexer:'Fixture',protocol:'torrent',title:u.searchParams.get('query')+' English unabridged M4B',seeders:30,size:1000,magnetUrl:magnet,downloadUrl:mode==='external'?attackerUrl:mockUrl+'/download?apikey=private-download-sentinel'}]);
 }
 if(u.pathname==='/download'){if(mode==='redirect'){res.writeHead(302,{Location:attackerUrl});return res.end();}res.setHeader('Content-Type','application/x-bittorrent');return res.end(mode==='unsafe-file'?unsafe:torrent);}
 res.statusCode=404;json({error:u.pathname});
});mock.listen(0,'127.0.0.1');await once(mock,'listening');const mockUrl=`http://127.0.0.1:${mock.address().port}`;
const probe=http.createServer();probe.listen(0,'127.0.0.1');await once(probe,'listening');const port=probe.address().port;await new Promise(r=>probe.close(r));const base=`http://127.0.0.1:${port}`;
async function wait(fn,label){for(let i=0;i<150;i++){if(await fn())return;await new Promise(r=>setTimeout(r,100));}throw Error(label+'\n'+logs.slice(-2000));}
async function start(){server=spawn(process.env.DOTNET_EXE||'dotnet',[path.resolve('src/Grimarr/bin/Debug/net10.0/Grimarr.dll'),'--contentRoot',path.resolve('src/Grimarr')],{env:{...process.env,ASPNETCORE_URLS:base,GRIMARR_CONFIG:config,GRIMARR_PASSWORD:password,GRIMARR_POLL_SECONDS:'1',PATH:(process.env.FFPROBE_DIR?process.env.FFPROBE_DIR+path.delimiter:'')+process.env.PATH},stdio:['ignore','pipe','pipe'],windowsHide:true});server.stdout.on('data',d=>logs+=d);server.stderr.on('data',d=>logs+=d);await wait(()=>fetch(base+'/health').then(r=>r.ok).catch(()=>false),'startup');}
async function stop(){if(server?.exitCode===null){server.kill();await once(server,'exit');}}
async function login(){const r=await fetch(base+'/api/login',{method:'POST',headers:{'Content-Type':'application/json','X-Grimarr':'1'},body:JSON.stringify({password})});assert.equal(r.status,200);cookie=r.headers.get('set-cookie').split(';')[0];return r;}
async function api(route,method='GET',data,status=200){const r=await fetch(base+'/api'+route,{method,headers:{Cookie:cookie,'Content-Type':'application/json','X-Grimarr':'1'},body:data===undefined?undefined:JSON.stringify(data)});const text=await r.text();assert.equal(r.status,status,route+': '+text);return text?JSON.parse(text):null;}
async function check(name,fn){await fn();checks++;console.log('PASS:',name);}
try {
 await start();const auth=await login();
 await check('Session cookies are HttpOnly and SameSite Strict',async()=>assert(/httponly/i.test(auth.headers.get('set-cookie')) && /samesite=strict/i.test(auth.headers.get('set-cookie'))));
 await check('Sensitive API responses are non-cacheable and use security headers',async()=>{const r=await fetch(base+'/api/session?apikey=private-query-sentinel',{headers:{Cookie:cookie}});assert.equal(r.status,200);assert.equal(r.headers.get('cache-control'),'no-store');assert.equal(r.headers.get('x-content-type-options'),'nosniff');assert.equal(r.headers.get('referrer-policy'),'no-referrer');assert(r.headers.get('content-security-policy').includes("frame-ancestors 'none'"));assert.equal(r.headers.get('server'),null);});
 await check('Cross-site writes and CORS preflights cannot access owner APIs',async()=>{const r=await fetch(base+'/api/logout',{method:'POST',headers:{Cookie:cookie,Origin:attackerUrl}});assert.equal(r.status,403);const p=await fetch(base+'/api/settings',{method:'OPTIONS',headers:{Origin:attackerUrl,'Access-Control-Request-Method':'PUT','Access-Control-Request-Headers':'X-Grimarr,Content-Type'}});assert.equal(p.headers.get('access-control-allow-origin'),null);});
 await check('Ordinary API body size is capped before deserialization',()=>api('/settings','PUT',{padding:'x'.repeat(70*1024)},413));
 await check('Chunked requests obey the same body limit',async()=>{const r=await fetch(base+'/api/login',{method:'POST',headers:{'X-Grimarr':'1','Content-Type':'application/json'},duplex:'half',body:Readable.from([JSON.stringify({password:'x'.repeat(70*1024)})])});assert.equal(r.status,413);});
 await check('Quick Add preview retains its larger bounded upload allowance',async()=>assert.equal((await api('/books/quick-add/preview','POST',{source:'f'.repeat(40),padding:'x'.repeat(70*1024)})).title,''));
 await check('Oversized Quick Add requests fail before parsing or queuing',async()=>{let sent=false;const status=await new Promise((resolve,reject)=>{const r=http.request(base+'/api/books/quick-add',{method:'POST',headers:{Cookie:cookie,'X-Grimarr':'1','Content-Type':'application/json','Content-Length':24*1024*1024+1,Expect:'100-continue'}},response=>{response.resume();response.on('end',()=>resolve(response.statusCode));});r.on('continue',()=>{sent=true;r.end('x'.repeat(24*1024*1024+1));});r.on('error',reject);r.flushHeaders();});assert.equal(status,413);assert.equal(sent,false);assert.equal((await api('/books')).length,0);});
 await check('Anonymous requests cannot exhaust the authenticated catalog quota',async()=>{for(let n=0;n<17;n++)assert.equal((await fetch(base+'/api/catalog?q=x')).status,401);await api('/catalog?q=x','GET',undefined,400);});
 await check('Static routes cannot expose config databases or encryption keys',async()=>{for(const route of ['/config/grimarr.db','/config/keys/private.xml','/../config/grimarr.db']){const r=await fetch(base+route);const text=await r.text();assert(!text.startsWith('SQLite format 3'));assert(!text.includes('private-identifier-sentinel'));assert(!text.includes('<masterKey'));}});
 const originalCookie=cookie;await stop();await start();await check('Same-password restart retains valid owner sessions',()=>api('/session'));
 password='rotated-security-fixture-password';await stop();await start();cookie=originalCookie;
 await check('Password rotation rejects previously issued sessions',()=>api('/session','GET',undefined,401));await login();
 password='security-fixture-password';await stop();await start();cookie=originalCookie;
 await check('Returning to an old password does not resurrect its old session',()=>api('/session','GET',undefined,401));await login();
 await check('Tampered cookies are rejected',async()=>{const r=await fetch(base+'/api/session',{headers:{Cookie:cookie+'tampered'}});assert.equal(r.status,401);});
 let s=(await api('/settings')).settings;s={...s,libraryId:'fixture',libraryRoot:library,downloadRoot:downloads,audiobookshelf:{url:mockUrl,apiKey:'abs-fixture-secret'},qbittorrent:{url:mockUrl,username:'admin',password:'qbit-fixture-secret'},prowlarr:{url:mockUrl,apiKey:'prowlarr-fixture-secret'},automationEnabled:true};await api('/settings','PUT',s);
 const badBooks=[];
 for(const [n,kind] of ['newline','unsafe-file','redirect','external'].entries()) {
  mode=kind;const b=await api('/books','POST',{title:['Safety One','Safety Two','Safety Three','Safety Four'][n],author:'Test Author'});badBooks.push(b);
  await wait(async()=>{const x=(await api('/books')).find(x=>x.id===b.id);return x.status==='queued'&&x.nextSearch>'2000';},kind+' rejected');
  await check(kind+': untrusted release cannot submit a torrent or leak credentials',async()=>{assert.equal(adds,0);assert.equal(attackerRequests,0);assert(!JSON.stringify(await api('/books')).includes('private-'));});
 }
 mode='valid';const valid=await api('/books','POST',{title:'Safety Five',author:'Test Author'});await wait(async()=>(await api('/books')).find(b=>b.id===valid.id).status==='downloading','validated proxy download');
 await check('Validated Prowlarr torrent downloads still work',async()=>assert.equal(adds,1));
 const playlist='ffconcat version 1.0\nfile \''+attackerUrl+'/media-reference\'\n';await writeFile(path.join(downloads,'playlist.mp3'),playlist);complete=true;await wait(async()=>(await api('/books')).find(b=>b.id===valid.id).status==='import-failed','playlist blocked');
 await check('Audio probing cannot follow playlist network references',async()=>{assert.equal(attackerRequests,0);assert.equal(await readFile(path.join(downloads,'playlist.mp3'),'utf8'),playlist);assert.equal((await readdir(library)).length,0);});
 await stop();let db=new DatabaseSync(path.join(config,'grimarr.db'));let raw=db.prepare('SELECT json FROM books').all().map(r=>r.json).join('');
 await check('Private release identifiers are encrypted in the actual backend database',async()=>assert(!raw.includes('private-identifier-sentinel')&&!raw.includes('private-download-sentinel')));
 const legacy=JSON.parse(db.prepare('SELECT json FROM books WHERE id=?').get(badBooks[0].id).json);legacy.selectedRelease.guid=mockUrl+'/identifier?passkey=legacy-guid-sentinel';legacy.failedReleases=[{guid:mockUrl+'/failed?passkey=legacy-history-sentinel',indexerId:1,infoHash:null,title:'Legacy fixture',reason:'Fixture',at:new Date().toISOString(),blocked:true}];db.prepare('UPDATE books SET json=? WHERE id=?').run(JSON.stringify(legacy),legacy.id);db.exec('DELETE FROM security_migrations');db.close();await start();await login();
 db=new DatabaseSync(path.join(config,'grimarr.db'));raw=db.prepare('SELECT json FROM books').all().map(r=>r.json).join('');db.close();
 await check('Legacy selected and failed private identifiers are migrated securely',async()=>{assert(!raw.includes('legacy-guid-sentinel'));assert(!raw.includes('legacy-history-sentinel'));assert.equal((await api('/books')).length,5);});
 await check('Legacy plaintext is absent from current database and WAL bytes',async()=>{for(const name of ['grimarr.db','grimarr.db-wal']){const bytes=await readFile(path.join(config,name)).catch(e=>{if(e.code==='ENOENT')return Buffer.alloc(0);throw e;});assert(!bytes.includes(Buffer.from('legacy-guid-sentinel')));assert(!bytes.includes(Buffer.from('legacy-history-sentinel')));}});
 await check('Single-owner login throttling cannot be bypassed with forged forwarding headers',async()=>{const codes=[];for(let n=0;n<6;n++){const r=await fetch(base+'/api/login',{method:'POST',headers:{'Content-Type':'application/json','X-Grimarr':'1','X-Forwarded-For':`192.0.2.${n}`},body:JSON.stringify({password:'wrong-password'})});codes.push(r.status);}assert(codes.includes(429));assert(codes.every(c=>c===401||c===429));});
 await check('Logout clears the browser session cookie',async()=>{const r=await fetch(base+'/api/logout',{method:'POST',headers:{Cookie:cookie,'X-Grimarr':'1'}});assert.equal(r.status,200);assert(r.headers.get('set-cookie').includes('expires='));cookie='';await api('/session','GET',undefined,401);});
 await check('Request logs omit private query text, passwords and integration credentials',async()=>{for(const value of ['private-query-sentinel','prowlarr-fixture-secret','qbit-fixture-secret','abs-fixture-secret','legacy-guid-sentinel','private-download-sentinel',password])assert(!logs.includes(value),value+' leaked into logs');});
 console.log(`\n${checks} security integration checks passed.`);
} finally {await stop();await Promise.all([new Promise(r=>mock.close(r)),new Promise(r=>attacker.close(r))]);await rm(root,{recursive:true,force:true});}
