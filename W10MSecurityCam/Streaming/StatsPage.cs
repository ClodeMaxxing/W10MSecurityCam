namespace W10MSecurityCam.Streaming
{
    /// <summary>
    /// The stats page (/stats on the USB port 8020 and on http://PHONE-IP:5001): the last 12 hours of status samples
    /// as graphs, in the style of the PC's soak monitor (tools\Soak-Monitor.ps1). Ported from W10MDashcam with webcam
    /// columns. /*ROWS*/ is replaced by the samples (MainPage.Network.cs).
    /// </summary>
    internal static class StatsPage
    {
        public const string Html = @"<!doctype html><html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>W10MSecurityCam stats</title>
<style>
body{font-family:'Segoe UI',system-ui,sans-serif;background:#000;color:#d6e8d9;margin:0;padding:18px 22px}
h1{font-size:20px;margin:0;color:#e4f5e7}h1 small{font-size:12px;color:#39ff14;margin-left:10px}
.sub{color:#7f9a86;font-size:12px;margin:4px 0 14px}
.sum{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:8px;margin-bottom:16px}
.k{background:#0b1a10;border:1px solid #16301e;border-radius:6px;padding:8px 10px}
.k b{display:block;font-size:10px;letter-spacing:1px;text-transform:uppercase;color:#6f8f77}
.k span{font-family:Consolas,monospace;font-size:16px;color:#e4f5e7}.k.bad span{color:#ff6b6b}.k.ok span{color:#39ff14}
.charts{display:grid;grid-template-columns:repeat(auto-fill,minmax(520px,1fr));gap:12px}
.c{background:linear-gradient(180deg,#07140b,#030805);border:1px solid #1f3a26;border-radius:8px;padding:8px 10px}
.c h3{margin:0 0 4px;font-size:12px;letter-spacing:1px;text-transform:uppercase;color:#9cf0b0;display:flex;justify-content:space-between}
.c h3 i{font-style:normal;color:#7f9a86;font-weight:normal;letter-spacing:0;text-transform:none}
canvas{width:100%;height:170px;display:block}
.legend{font-size:11px;color:#7f9a86;margin-top:2px}.legend span{margin-right:12px}.legend span::before{content:'';display:inline-block;width:10px;height:3px;margin-right:4px;vertical-align:middle;background:var(--c)}
#tip{position:fixed;pointer-events:none;background:#0b1a10;border:1px solid #2e7d46;border-radius:6px;padding:6px 8px;font-size:12px;display:none;white-space:pre;font-family:Consolas,monospace;z-index:9}
</style></head><body>
<h1>W10MSecurityCam stats<small id=""ver""></small></h1><div class=""sub"" id=""span""></div>
<div class=""sum"" id=""sum""></div><div class=""charts"" id=""charts""></div><div id=""tip""></div>
<script>
var COLS=""time,ok,version,streaming,fps,mbps,viewers,format,bitrate,latency_ms,battery_pct,charging,net_w,draw_w,heat_warn,protect,cpu_phone,cpu_app,memory_mb,wifi_bars,uptime_s,safe"".split(','),LIVE=true,ROWS=[/*ROWS*/].map(function(r){var a=r.split(','),o={};COLS.forEach(function(c,i){o[c]=a[i]===undefined?'':a[i]});o.t=new Date(o.time).getTime();return o});
if(LIVE)setTimeout(function(){location.reload()},60000);
function n(v){return v===''||v==null?null:+v}
var R=ROWS,ok=R.filter(function(r){return r.ok==='1'});
function fmtDur(ms){var m=Math.round(ms/60000),h=Math.floor(m/60);return h?h+' h '+(m%60)+' min':m+' min'}
function avg(a){a=a.filter(function(v){return v!=null});return a.length?a.reduce(function(x,y){return x+y},0)/a.length:null}
function dt(t){var d=new Date(t);return d.toLocaleDateString()+' '+d.toLocaleTimeString()}
// ---- summary
(function(){
 if(!R.length){document.getElementById('span').textContent='No samples yet.';return}
 var t0=R[0].t,t1=R[R.length-1].t,dur=t1-t0;
 document.getElementById('span').textContent=dt(t0)+'  to  '+dt(t1)+'   ('+fmtDur(dur)+', '+R.length+' samples'+(LIVE?', since the app started, updates every minute':'')+')';
 var last=ok[ok.length-1];if(last)document.getElementById('ver').textContent='v'+last.version+'  '+last.format+'  '+last.bitrate;
 var str=ok.filter(function(r){return r.streaming==='1'}),strPct=str.length/Math.max(1,ok.length)*100;
 var prot=0,heat=0,short=0,restarts=0,stops=0;
 for(var i=1;i<ok.length;i++){var a=ok[i-1],b=ok[i];if(b.protect==='1')prot+=b.t-a.t;if(b.heat_warn==='1')heat+=b.t-a.t;if(b.charging==='1'&&n(b.net_w)!=null&&n(b.net_w)<0)short+=b.t-a.t;
  if(n(b.uptime_s)!=null&&n(a.uptime_s)!=null&&n(b.uptime_s)<n(a.uptime_s))restarts++;if(a.streaming==='1'&&b.streaming!=='1')stops++}
 var fps=str.map(function(r){return n(r.fps)}).filter(function(v){return v!=null});
 var lat=str.map(function(r){return n(r.latency_ms)}).filter(function(v){return v!=null});
 var mem=ok.map(function(r){return n(r.memory_mb)}).filter(function(v){return v!=null});
 var memSlope=null;if(mem.length>20&&dur>3600000){var h=dur/3600000,q=Math.floor(mem.length/10);memSlope=(avg(mem.slice(-q))-avg(mem.slice(0,q)))/h}
 var bat=ok.map(function(r){return n(r.battery_pct)}).filter(function(v){return v!=null});
 var K=[['Duration',fmtDur(dur)],['Streaming',strPct.toFixed(1)+' %',strPct<99?'bad':'ok'],
  ['FPS avg / min',fps.length?avg(fps).toFixed(1)+' / '+Math.min.apply(null,fps).toFixed(0):'n/a'],
  ['Latency avg / max',lat.length?Math.round(avg(lat))+' / '+Math.max.apply(null,lat)+' ms':'n/a'],
  ['Stream stops',stops,stops?'bad':'ok'],['App restarts',restarts,restarts?'bad':'ok'],
  ['Heat warning',fmtDur(heat),heat?'bad':'ok'],['Heat protection',fmtDur(prot),prot?'bad':'ok'],
  ['Charger short',fmtDur(short),short?'bad':'ok'],
  ['Battery min / last',bat.length?Math.min.apply(null,bat)+' / '+bat[bat.length-1]+' %':'n/a'],
  ['Memory last / max',mem.length?Math.round(mem[mem.length-1])+' / '+Math.round(Math.max.apply(null,mem))+' MB':'n/a'],
  ['Memory trend',memSlope==null?'needs 1 h':(memSlope>=0?'+':'')+memSlope.toFixed(1)+' MB/h',memSlope!=null&&memSlope>5?'bad':'']];
 document.getElementById('sum').innerHTML=K.map(function(k){return '<div class=""k '+(k[2]||'')+'""><b>'+k[0]+'</b><span>'+k[1]+'</span></div>'}).join('');
})();
// ---- charts
var GREEN='#39ff14',MINT='#9cf0b0',AMBER='#e8d040',RED='#ff5555',BLUE='#5fb8ff',GREY='#7f9a86';
var CH=[
 {t:'Frame rate',u:'fps',s:[['fps',GREEN,'fps']],min:0},
 {t:'Bitrate',u:'Mbps',s:[['mbps',MINT,'Mbps']],min:0},
 {t:'Latency',u:'ms',s:[['latency_ms',GREEN,'sensor to encoded frame']],min:0},
 {t:'Viewers',u:'',s:[['viewers',MINT,'viewers']],min:0,step:true},
 {t:'Battery',u:'%',s:[['battery_pct',GREEN,'battery %']],min:0,max:100,band:['charging','rgba(57,255,20,.07)','charging']},
 {t:'Power',u:'W',s:[['net_w',GREEN,'net into battery'],['draw_w',AMBER,'phone draw (on battery)']],zero:true},
 {t:'CPU',u:'%',s:[['cpu_phone',MINT,'phone'],['cpu_app',GREEN,'app']],min:0,max:100},
 {t:'App memory',u:'MB',s:[['memory_mb',MINT,'MB']],min:0},
 {t:'Wi-Fi',u:'bars',s:[['wifi_bars',GREEN,'bars']],min:0,max:4,step:true},
 {t:'States',u:'',flags:[['streaming',GREEN,'streaming'],['charging',BLUE,'charging'],['heat_warn',AMBER,'heat warning'],['protect',RED,'heat protection'],['safe',RED,'safe mode']]}
];
var host=document.getElementById('charts'),tip=document.getElementById('tip'),t0=R.length?R[0].t:0,t1=R.length?R[R.length-1].t:1;
CH.forEach(function(c){
 var d=document.createElement('div');d.className='c';
 var last=ok.length&&c.s?c.s.map(function(s){var v=n(ok[ok.length-1][s[0]]);return v==null?'':v+(c.u?' '+c.u:'')}).filter(Boolean).join(' / '):'';
 d.innerHTML='<h3>'+c.t+'<i>'+last+'</i></h3><canvas></canvas><div class=""legend"">'+(c.s||c.flags).map(function(s){return '<span style=""--c:'+s[1]+'"">'+s[2]+'</span>'}).join('')+'</div>';
 host.appendChild(d);c.cv=d.querySelector('canvas');draw(c);
 c.cv.addEventListener('mousemove',function(e){hover(c,e)});c.cv.addEventListener('mouseleave',function(){tip.style.display='none';draw(c)});
});
window.addEventListener('resize',function(){CH.forEach(draw)});
function geom(c){var cv=c.cv,dpr=window.devicePixelRatio||1,w=cv.clientWidth,h=cv.clientHeight;cv.width=w*dpr;cv.height=h*dpr;var g=cv.getContext('2d');g.setTransform(dpr,0,0,dpr,0,0);return {g:g,w:w,h:h,L:40,Rt:8,T:6,B:18}}
function draw(c,hx){
 var G=geom(c),g=G.g,pw=G.w-G.L-G.Rt,ph=G.h-G.T-G.B;g.clearRect(0,0,G.w,G.h);
 var X=function(t){return G.L+(t1===t0?0:(t-t0)/(t1-t0)*pw)};
 g.font='10px Consolas,monospace';g.fillStyle=GREY;g.strokeStyle='#16301e';g.lineWidth=1;
 // time axis
 for(var k=0;k<=4;k++){var tt=t0+(t1-t0)*k/4,x=X(tt);g.beginPath();g.moveTo(x,G.T);g.lineTo(x,G.T+ph);g.stroke();var lb=new Date(tt).toLocaleTimeString([], {hour:'2-digit',minute:'2-digit'});g.fillText(lb,Math.min(G.w-g.measureText(lb).width-2,Math.max(G.L,x-14)),G.h-4)}
 if(c.flags){var rh=ph/c.flags.length;c.flags.forEach(function(f,i){var y=G.T+i*rh;g.fillStyle=GREY;g.fillText(f[2].slice(0,6),2,y+rh/2+3);g.fillStyle=f[1];
   for(var j=1;j<R.length;j++){if(R[j-1][f[0]]==='1'){var x0=X(R[j-1].t),x1=X(R[j].t);g.globalAlpha=.75;g.fillRect(x0,y+2,Math.max(1,x1-x0),rh-4)}}g.globalAlpha=1});
  if(hx!=null){g.strokeStyle='#e4f5e7';g.beginPath();g.moveTo(hx,G.T);g.lineTo(hx,G.T+ph);g.stroke()}return}
 var vals=[];c.s.forEach(function(s){R.forEach(function(r){var v=n(r[s[0]]);if(v!=null)vals.push(v)})});
 var lo=c.min!=null?c.min:Math.min.apply(null,vals.concat([0])),hi=c.max!=null?c.max:Math.max.apply(null,vals.concat([1]));
 if(c.zero){lo=Math.min(lo,0);hi=Math.max(hi,0)}if(hi===lo)hi=lo+1;if(c.max==null)hi+= (hi-lo)*.08;
 var Y=function(v){return G.T+ph-(v-lo)/(hi-lo)*ph};
 if(c.band){g.fillStyle=c.band[1];for(var j=1;j<R.length;j++)if(R[j-1][c.band[0]]==='1'){var x0=X(R[j-1].t);g.fillRect(x0,G.T,Math.max(1,X(R[j].t)-x0),ph)}}
 g.fillStyle=GREY;for(var k=0;k<=4;k++){var v=lo+(hi-lo)*k/4,y=Y(v);g.strokeStyle='#10241a';g.beginPath();g.moveTo(G.L,y);g.lineTo(G.L+pw,y);g.stroke();g.fillText((Math.abs(v)>=100?v.toFixed(0):v.toFixed(1)),2,y+3)}
 if(c.zero){g.strokeStyle='#2e7d46';g.beginPath();g.moveTo(G.L,Y(0));g.lineTo(G.L+pw,Y(0));g.stroke()}
 c.s.forEach(function(s){g.strokeStyle=s[1];g.lineWidth=1.4;g.beginPath();var on=false,py=null,gap=Math.max(3*60000,(t1-t0)/200);var prevT=null;
  R.forEach(function(r){var v=n(r[s[0]]);if(v==null||(prevT!=null&&r.t-prevT>gap)){on=false}if(v==null){prevT=r.t;return}var x=X(r.t),y=Y(v);
   if(!on){g.moveTo(x,y);on=true}else{if(c.step&&py!=null)g.lineTo(x,py);g.lineTo(x,y)}py=y;prevT=r.t});g.stroke()});
 g.lineWidth=1;if(hx!=null){g.strokeStyle='#e4f5e7';g.beginPath();g.moveTo(hx,G.T);g.lineTo(hx,G.T+ph);g.stroke()}
}
function hover(c,e){
 if(!R.length)return;var rc=c.cv.getBoundingClientRect(),pw=rc.width-48,x=e.clientX-rc.left,t=t0+(x-40)/pw*(t1-t0),best=R[0];
 R.forEach(function(r){if(Math.abs(r.t-t)<Math.abs(best.t-t))best=r});
 var lines=[dt(best.t)];if(c.s)c.s.forEach(function(s){lines.push(s[2]+': '+(best[s[0]]===''?'-':best[s[0]]+(c.u?' '+c.u:'')))});
 else c.flags.forEach(function(f){lines.push(f[2]+': '+(best[f[0]]==='1'?'yes':'no'))});
 
 tip.textContent=lines.join('\n');tip.style.display='block';tip.style.left=Math.min(window.innerWidth-tip.offsetWidth-8,e.clientX+12)+'px';tip.style.top=(e.clientY+12)+'px';
 draw(c,x);
}
</script></body></html>";
    }
}
