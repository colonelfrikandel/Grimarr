import {readdir,readFile} from 'node:fs/promises';
import path from 'node:path';

// Represents a scanner indexing actual completed fixture directories, not a successful scan response.
export async function libraryItems(root) {
 const results=[];
 async function walk(dir) {
  let entries;try {entries=await readdir(dir,{withFileTypes:true});}catch(e){if(e.code==='ENOENT')return;throw e;}
  if(entries.some(e=>e.name==='.grimarr.json')) {
   const owner=JSON.parse(await readFile(path.join(dir,'.grimarr.json'),'utf8'));
   const metadata=JSON.parse(await readFile(path.join(dir,'metadata.json'),'utf8'));
   results.push({id:owner.id,mediaType:'book',relPath:path.relative(root,dir).replaceAll('\\','/'),path:dir,media:{numAudioFiles:1,metadata:{...metadata,authors:metadata.authors.map(name=>({name}))}}});
  }
  for(const entry of entries)if(entry.isDirectory())await walk(path.join(dir,entry.name));
 }
 await walk(root);return {results,total:results.length};
}
