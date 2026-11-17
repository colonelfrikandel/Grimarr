import * as esbuild from 'esbuild';
import {cp,mkdir,readFile,rm,writeFile,lstat} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const root=fileURLToPath(new URL('.',import.meta.url));
const dist=path.resolve(root,'dist');
const watch=process.argv.includes('--watch');
if(path.dirname(dist)!==path.resolve(root))throw Error('Build output must stay inside the frontend directory.');
const existing=await lstat(dist).catch(e=>{if(e.code==='ENOENT')return null;throw e;});
if(existing?.isSymbolicLink())throw Error('Build output cannot be a symlink.');
await rm(dist,{recursive:true,force:true});await mkdir(dist,{recursive:true});
const publicUrl=file=>'/'+path.relative(dist,path.resolve(root,file)).split(path.sep).join('/');
const options={
 absWorkingDir:root,entryPoints:['src/main.tsx'],outdir:dist,entryNames:'assets/[name]-[hash]',assetNames:'assets/[name]-[hash]',
 bundle:true,format:'esm',platform:'browser',target:'es2022',jsx:'automatic',minify:!watch,metafile:true,sourcemap:false,
 define:{'process.env.NODE_ENV':JSON.stringify(watch?'development':'production')},logLevel:'info',legalComments:'linked',
 plugins:[{name:'static-html',setup(build){build.onEnd(async result=>{
  if(result.errors.length)return;
  const entry=Object.entries(result.metafile.outputs).find(([file,info])=>info.entryPoint && file.endsWith('.js'));
  if(!entry)throw Error('Frontend bundle was not produced.');
  let html=await readFile(path.join(root,'index.html'),'utf8');
  html=html.replace('src="/src/main.tsx"',`src="${publicUrl(entry[0])}"`);
  if(entry[1].cssBundle)html=html.replace('</head>',`<link rel="stylesheet" href="${publicUrl(entry[1].cssBundle)}"/></head>`);
  await cp(path.join(root,'public'),dist,{recursive:true});await writeFile(path.join(dist,'index.html'),html);
  if(watch){await cp(dist,path.resolve(root,'../src/Grimarr/wwwroot'),{recursive:true});console.log('Rebuilt frontend. Refresh http://127.0.0.1:8787 in the running backend.');}
 });}}],
};
if(watch){const context=await esbuild.context(options);await context.watch();console.log('Watching frontend sources; start the backend separately.');}
else await esbuild.build(options);
