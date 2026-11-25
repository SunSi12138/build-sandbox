internal static class UploadPage
{
    public const string Html = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<title>文件助手</title>
<style>
:root{color-scheme:light dark;--bg:#f2f5f4;--surface:#fff;--ink:#172923;--muted:#6f8179;--line:#e3ebe7;--accent:#157b58;--soft:#e7f5ed;--error:#b44235;--track:#e0eae4}
@media(prefers-color-scheme:dark){:root{--bg:#101b17;--surface:#192822;--ink:#eef7f1;--muted:#a0b6a9;--line:#2c4035;--accent:#25986d;--soft:#213e30;--error:#ff9c8c;--track:#334c3e}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.5 -apple-system,BlinkMacSystemFont,"Microsoft YaHei",sans-serif}
main{max-width:540px;margin:auto;padding:34px 20px 48px;padding-bottom:calc(32px + env(safe-area-inset-bottom))}
header{display:flex;align-items:center;gap:13px;margin:0 0 24px}
.mark{width:44px;height:44px;background:var(--soft);color:var(--accent);display:grid;place-items:center;border-radius:14px;flex:none}
h1{font-size:24px;letter-spacing:-.6px;margin:0;font-weight:700}
.picker{position:relative;display:flex;align-items:center;justify-content:center;gap:10px;width:100%;min-height:70px;border:0;border-radius:18px;background:var(--accent);color:white;font:inherit;font-size:17px;font-weight:600;box-shadow:0 6px 20px #12674416;overflow:hidden}
.picker:focus-within{outline:3px solid #82bca4;outline-offset:3px}.picker:active{opacity:.85}
.picker input{position:absolute;inset:0;width:100%;height:100%;opacity:0;cursor:pointer;font-size:20px}
svg{width:23px;height:23px;stroke:currentColor;fill:none;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round}
#jobs{margin-top:18px;background:var(--surface);border:1px solid var(--line);border-radius:18px;overflow:hidden}
#jobs:empty{display:none}
.job{padding:16px 17px}.job+.job{border-top:1px solid var(--line)}.top{display:flex;align-items:flex-start;gap:10px}
.icon{flex:none;width:34px;height:38px;display:grid;place-items:center;border-radius:10px;background:var(--soft);color:var(--accent)}
.icon svg{width:20px;height:20px}.detail{min-width:0;flex:1}
.name{font-weight:600;overflow-wrap:anywhere;line-height:1.35}.status{font-size:12px;color:var(--muted);margin-top:4px;display:flex;align-items:center;gap:8px}
.track{height:5px;border-radius:6px;background:var(--track);overflow:hidden;margin:12px 0 0 44px}
.bar{height:100%;background:var(--accent);width:0;border-radius:6px;transition:width .12s linear}
.job.done .track,.job.failed .track{display:none}.job.done .status{color:var(--accent)}.job.failed .status{color:var(--error)}
.retry{display:none;border:1px solid var(--line);background:var(--soft);color:var(--ink);border-radius:9px;padding:5px 10px;font:inherit;flex:none}
.job.failed .retry{display:block}
@media(max-width:360px){main{padding:22px 14px}.job{padding:13px}}
</style>
</head>
<body>
<main>
<header><div class="mark"><svg viewBox="0 0 24 24"><path d="M4 7h6l2 2h8v11H4z"/><path d="M12 12v5m-2-2 2 2 2-2"/></svg></div><h1>文件助手</h1></header>
<label class="picker"><svg viewBox="0 0 24 24"><path d="M12 16V4m-5 5 5-5 5 5M4 16v4h16v-4"/></svg><span>选择文件</span><input id="files" type="file" multiple aria-label="选择文件"></label>
<section id="jobs" aria-label="本次上传" aria-live="polite"></section>
<noscript>请启用 JavaScript 以上传文件。</noscript>
</main>
<script>
(function(){
"use strict";
var picker=document.getElementById("files"), jobs=document.getElementById("jobs");
var chunk=2097152, max=10737418240, queue=[], running=false;
function uuid(){var a=new Uint8Array(16);window.crypto.getRandomValues(a);return Array.prototype.map.call(a,function(x){return ("0"+x.toString(16)).slice(-2);}).join("");}
function el(tag,cls,parent){var n=document.createElement(tag);n.className=cls;parent.appendChild(n);return n;}
function row(file){
 var wrap=el("article","job",jobs), top=el("div","top",wrap), icon=el("div","icon",top);
 icon.innerHTML='<svg viewBox="0 0 24 24"><path d="M6 3h8l4 4v14H6zM14 3v5h4M9 13h6m-6 4h6"/></svg>';
 var d=el("div","detail",top), name=el("div","name",d), status=el("div","status",d);
 name.textContent=file.name;status.textContent="等待上传";
 var retry=el("button","retry",top);retry.type="button";retry.textContent="重试";
 var track=el("div","track",wrap), bar=el("div","bar",track);
 var job={file:file,id:uuid(),wrap:wrap,status:status,bar:bar,retry:retry};
 retry.onclick=function(){wrap.className="job";status.textContent="等待上传";queue.push(job);pump();};
 return job;
}
function query(job,extra){return "?id="+job.id+(extra||"");}
function xhr(url,data,onprogress){
 return new Promise(function(resolve,reject){
  var x=new XMLHttpRequest();x.open("POST",url,true);x.setRequestHeader("X-File-Assistant","1");x.timeout=180000;
  if(data!==null)x.setRequestHeader("Content-Type","application/octet-stream");
  if(onprogress)x.upload.onprogress=function(e){onprogress(e.loaded);};
  x.onload=function(){if(x.status===200)resolve(x.responseText);else reject({code:x.status});};
  x.onerror=x.ontimeout=x.onabort=function(){reject({code:0});};x.send(data);
 });
}
function sleep(ms){return new Promise(function(r){setTimeout(r,ms);});}
async function request(job,url,data,progress){
 for(var n=0;;n++){
  try{return await xhr(url,data,progress);}
  catch(e){
   if(n>=6 || (e.code!==0&&e.code!==408&&e.code!==429&&e.code!==500&&e.code!==502&&e.code!==503&&e.code!==504))throw e;
   job.status.textContent="连接中断，正在重试…";
   await sleep(Math.min(8000,500*Math.pow(2,n)));
  }
 }
}
function percent(job,offset){
 var p=job.file.size===0?100:Math.min(100,Math.floor(offset/job.file.size*100));
 job.bar.style.width=p+"%";job.status.textContent=p===100?"正在保存…":p+"%";
}
function result(job,r){
 job.wrap.className="job done";job.bar.style.width="100%";job.status.textContent=r==="saved"?"上传成功":"文件已存在";
}
async function upload(job){
 var f=job.file;if(f.size>max){job.wrap.className="job failed";job.status.textContent="文件超过 10 GiB";job.retry.style.display="none";return;}
 var init="/api/init"+query(job,"&size="+f.size+"&name="+encodeURIComponent(f.name));
 try{
  var r=await request(job,init,null);
  if(r==="saved"||r==="duplicate"){result(job,r);return;}
  var off=Number(r);if(!Number.isSafeInteger(off)||off<0||off>f.size)throw {code:400};
  percent(job,off);
  while(off<f.size){
   var start=off,end=Math.min(off+chunk,f.size);
   r=await request(job,"/api/chunk"+query(job,"&offset="+start),f.slice(start,end),function(loaded){percent(job,start+loaded);});
   if(r==="saved"||r==="duplicate"){result(job,r);return;}
   var next=Number(r);if(!Number.isSafeInteger(next)||next<=off||next>f.size)throw {code:400};
   off=next;percent(job,off);
  }
  job.status.textContent="正在保存…";
  r=await request(job,"/api/finish"+query(job),null);
  if(r!=="saved"&&r!=="duplicate")throw {code:400};
  result(job,r);
 }catch(e){
  job.wrap.className="job failed";
  if(e.code===404){job.id=uuid();job.status.textContent="任务已失效，请重试";}
  else if(e.code===507)job.status.textContent="保存失败，请检查接收端磁盘";
  else if(e.code===413)job.status.textContent="文件或请求过大";
  else job.status.textContent="上传失败，请重试";
 }
}
async function pump(){
 if(running)return;running=true;
 try{while(queue.length)await upload(queue.shift());}finally{running=false;}
}
picker.addEventListener("change",function(){
 var selected=Array.prototype.slice.call(picker.files);picker.value="";
 selected.forEach(function(file){try{queue.push(row(file));}catch(e){alert("当前浏览器不支持文件上传，请更换浏览器。");}});
 pump();
});
})();
</script>
</body>
</html>
""";
}
