# Soak monitor: records the camera's status every few seconds to a CSV file and keeps an HTML report with graphs of
# every metric over time (frame rate, bitrate, battery, power, CPU, memory, latency, Wi-Fi, heat, restarts).
#
#   powershell -ExecutionPolicy Bypass -File tools\Soak-Monitor.ps1
#   powershell -ExecutionPolicy Bypass -File tools\Soak-Monitor.ps1 -Phone 192.168.0.50 -User admin -Password secret -Every 15
#
# Output (in tools\soak\, kept out of git):  soak-YYYYMMDD-HHMM.csv, soak-YYYYMMDD-HHMM.html and
# soak-YYYYMMDD-HHMM-phone-log.txt (the phone's own log for the whole run, collected every few minutes).
# Open the HTML file in a browser at any time; it reloads itself every minute while the monitor runs.
# Stop with Ctrl+C. Run it again with -Csv <file> to continue an earlier run, or -ReportOnly -Csv <file> to redraw a report.
param(
  [string]$Phone,
  [string]$User = 'admin',
  [string]$Password = 'admin',
  [int]$Every = 30,                 # seconds between samples
  [int]$ReportEvery = 60,           # seconds between report rewrites
  [int]$LogEvery = 300,             # seconds between phone-log captures (the phone keeps only its last ~2 hours)
  [string]$Csv,
  [switch]$ReportOnly
)
$ErrorActionPreference = 'Stop'
if (-not $Phone) { $Phone = (Get-Content "$PSScriptRoot\phone-ip.txt" -TotalCount 1 -ErrorAction SilentlyContinue); if ($Phone) { $Phone = $Phone.Trim() } }
if (-not $Phone -and -not $ReportOnly) { throw 'Give -Phone ADDRESS or put the phone address in tools\phone-ip.txt' }
$dir = Join-Path $PSScriptRoot 'soak'; New-Item -ItemType Directory -Force $dir | Out-Null
if (-not $Csv) { $Csv = Join-Path $dir ('soak-' + (Get-Date -Format 'yyyyMMdd-HHmm') + '.csv') }
$Html = [IO.Path]::ChangeExtension($Csv, '.html')
$PhoneLog = [IO.Path]::ChangeExtension($Csv, $null).TrimEnd('.') + '-phone-log.txt'
$script:LastLogLine = $null
$jar = Join-Path $env:LOCALAPPDATA 'W10MSecurityCam\portal.jar'   # optional Device Portal pairing (Monitor-Portal.ps1 -Pair)

$cols = 'time,ok,version,streaming,fps,mbps,viewers,format,bitrate,engine,latency_ms,battery_pct,battery_status,charging,net_w,draw_w,heat,heat_warn,protect,cpu_phone,cpu_app,memory_mb,wifi_bars,uptime_s,blackout,previewing,gpu_max_pct'
if (-not (Test-Path $Csv)) { $cols | Out-File $Csv -Encoding ascii }

function Num($v) { if ($null -eq $v) { '' } else { [string]([double]$v).ToString([Globalization.CultureInfo]::InvariantCulture) } }
function B($v) { if ($v) { '1' } else { '0' } }

function Sample {
  $t = Get-Date -Format 's'
  try {
    $raw = curl.exe -s -m 10 --digest -u "${User}:${Password}" "http://${Phone}:5001/camera/state"
    $s = $raw | ConvertFrom-Json
  } catch { return "$t,0" + (',' * 25) }
  $cp = ''; $ca = ''
  if ($s.cpuText -match 'phone (\d+) %, app (\d+)') { $cp = $Matches[1]; $ca = $Matches[2] }
  $gpu = ''
  if (Test-Path $jar) {
    try {
      $p = curl.exe -sk -m 8 -b $jar "https://$Phone/api/resourcemanager/systemperf" | ConvertFrom-Json
      $gpu = [math]::Round(($p.GPUData.AvailableAdapters[0].EnginesUtilization | Measure-Object -Maximum).Maximum, 1)
    } catch { }
  }
  $f = @($t, '1', $s.version, (B $s.streaming), (Num $s.fps), (Num $s.mbps), $s.viewers, $s.format, $s.bitrate, $s.engine,
         (Num $s.latencyMs), (Num $s.battery), $s.batteryStatus, (B $s.charging), (Num $s.netWatts), (Num $s.drawWatts),
         $s.heat, (B $s.heatWarn), (B $s.protect), $cp, $ca, (Num $s.memoryMB), (Num $s.wifiBars), (Num $s.uptime),
         (B $s.blackout), (B $s.previewing), $gpu)
  ($f | ForEach-Object { ([string]$_) -replace ',', ';' }) -join ','
}

# Appends the phone log lines that are new since the last capture. The phone's /log lists the current app session
# (oldest first) and then '--- previous session'. Lines carry only a time of day, so new lines are found by locating
# the last line saved; if it is gone (app restarted, or more than ~2 h passed), the whole session is saved after a marker.
function Save-PhoneLog {
  try { $raw = curl.exe -s -m 20 --digest -u "${User}:${Password}" "http://${Phone}:5001/log" } catch { return }
  if (-not $raw) { return }
  $lines = @(); foreach ($l in $raw) { if ($l -like '--- previous session*') { break }; if ($l -match '^\d\d:\d\d:\d\d') { $lines += $l } }
  if ($lines.Count -eq 0) { return }
  $start = 0
  if ($script:LastLogLine) {
    $i = [array]::LastIndexOf($lines, $script:LastLogLine)
    if ($i -ge 0) { $start = $i + 1 } else { Add-Content -Path $PhoneLog -Value ("--- " + (Get-Date -Format s) + ": new app session or gap; earlier lines may be missing ---") -Encoding utf8 }
  }
  if ($start -lt $lines.Count) {
    $now = Get-Date
    Add-Content -Path $PhoneLog -Encoding utf8 -Value ($lines[$start..($lines.Count - 1)] | ForEach-Object {
      $d = $now.Date; $tod = [TimeSpan]::Parse($_.Substring(0, 8))
      if ($tod -gt $now.TimeOfDay.Add([TimeSpan]::FromMinutes(5))) { $d = $d.AddDays(-1) }   # logged before midnight
      $d.ToString('yyyy-MM-dd') + ' ' + $_ })
  }
  $script:LastLogLine = $lines[$lines.Count - 1]
}

function Write-Report {
  $rows = Get-Content $Csv | Select-Object -Skip 1
  $data = ($rows | ForEach-Object { '"' + ($_ -replace '"', '') + '"' }) -join ",`n"
  $page = $script:Template.Replace('/*COLS*/', '"' + $cols + '"').Replace('/*ROWS*/', $data).Replace('/*TITLE*/', [IO.Path]::GetFileNameWithoutExtension($Csv)).Replace('/*LIVE*/', $(if ($ReportOnly) { 'false' } else { 'true' }))
  [IO.File]::WriteAllText($Html, $page, (New-Object Text.UTF8Encoding $false))
}

$script:Template = @'
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>/*TITLE*/ - W10MSecurityCam soak</title>
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
<h1>W10MSecurityCam soak<small id="ver"></small></h1><div class="sub" id="span"></div>
<div class="sum" id="sum"></div><div class="charts" id="charts"></div><div id="tip"></div>
<script>
var COLS=/*COLS*/.split(','),LIVE=/*LIVE*/,ROWS=[
/*ROWS*/
].map(function(r){var a=r.split(','),o={};COLS.forEach(function(c,i){o[c]=a[i]===undefined?'':a[i]});o.t=new Date(o.time).getTime();return o});
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
 document.getElementById('span').textContent=dt(t0)+'  to  '+dt(t1)+'   ('+fmtDur(dur)+', '+R.length+' samples'+(LIVE?', updates every minute':'')+')';
 var last=ok[ok.length-1];if(last)document.getElementById('ver').textContent='v'+last.version+'  '+last.format+'  '+last.bitrate+(last.bitrate==='auto'?'':' Mbps');
 var reach=ok.length/R.length*100,str=ok.filter(function(r){return r.streaming==='1'}).length/Math.max(1,ok.length)*100;
 var appRestarts=0,streamDrops=0,prot=0,heat=0,ver={};
 for(var i=1;i<ok.length;i++){var a=ok[i-1],b=ok[i];if(n(b.uptime_s)<n(a.uptime_s))appRestarts++;if(a.streaming==='1'&&b.streaming!=='1')streamDrops++;if(b.protect==='1')prot+=b.t-a.t;if(b.heat_warn==='1')heat+=b.t-a.t;if(a.version!==b.version)ver[b.version]=1}
 var fps=ok.filter(function(r){return r.streaming==='1'}).map(function(r){return n(r.fps)}).filter(function(v){return v!=null});
 var mem=ok.map(function(r){return n(r.memory_mb)}).filter(function(v){return v!=null});
 var memSlope=null;if(mem.length>20&&dur>3600000){var h=dur/3600000,q=Math.floor(mem.length/10);memSlope=(avg(mem.slice(-q))-avg(mem.slice(0,q)))/h}
 var bat=ok.map(function(r){return n(r.battery_pct)}).filter(function(v){return v!=null});
 var K=[['Duration',fmtDur(dur)],['Phone reachable',reach.toFixed(1)+' %',reach<99?'bad':'ok'],['Streaming',str.toFixed(1)+' %',str<99?'bad':'ok'],
  ['App restarts',appRestarts,appRestarts?'bad':'ok'],['Stream stops',streamDrops,streamDrops>0?'bad':'ok'],
  ['FPS avg / min',fps.length?avg(fps).toFixed(1)+' / '+Math.min.apply(null,fps).toFixed(0):'n/a'],
  ['Heat warning',fmtDur(heat),heat?'bad':'ok'],['Heat protection',fmtDur(prot),prot?'bad':'ok'],
  ['Battery min / last',bat.length?Math.min.apply(null,bat)+' / '+bat[bat.length-1]+' %':'n/a'],
  ['Memory last / max',mem.length?Math.round(mem[mem.length-1])+' / '+Math.round(Math.max.apply(null,mem))+' MB':'n/a'],
  ['Memory trend',memSlope==null?'needs 1 h':(memSlope>=0?'+':'')+memSlope.toFixed(1)+' MB/h',memSlope!=null&&memSlope>5?'bad':'']];
 document.getElementById('sum').innerHTML=K.map(function(k){return '<div class="k '+(k[2]||'')+'"><b>'+k[0]+'</b><span>'+k[1]+'</span></div>'}).join('');
})();
// ---- charts
var GREEN='#39ff14',MINT='#9cf0b0',AMBER='#e8d040',RED='#ff5555',BLUE='#5fb8ff',GREY='#7f9a86';
var CH=[
 {t:'Frame rate',u:'fps',s:[['fps',GREEN,'fps']],min:0},
 {t:'Bitrate',u:'Mbps',s:[['mbps',MINT,'Mbps']],min:0},
 {t:'Battery',u:'%',s:[['battery_pct',GREEN,'battery %']],min:0,max:100,band:['charging','rgba(57,255,20,.07)','charging']},
 {t:'Power',u:'W',s:[['net_w',GREEN,'net into battery'],['draw_w',AMBER,'phone draw (on battery)']],zero:true},
 {t:'CPU',u:'%',s:[['cpu_phone',MINT,'phone'],['cpu_app',GREEN,'app'],['gpu_max_pct',BLUE,'GPU busiest engine (Device Portal)']],min:0,max:100},
 {t:'App memory',u:'MB',s:[['memory_mb',MINT,'MB']],min:0},
 {t:'Encoder latency',u:'ms',s:[['latency_ms',GREEN,'sensor to encoded']],min:0},
 {t:'Wi-Fi',u:'bars',s:[['wifi_bars',GREEN,'bars']],min:0,max:4,step:true},
 {t:'Viewers',u:'',s:[['viewers',MINT,'viewers']],min:0,step:true},
 {t:'Heat and states',u:'',flags:[['streaming',GREEN,'streaming'],['heat_warn',AMBER,'heat warning'],['protect',RED,'heat protection'],['previewing',BLUE,'preview on'],['ok',GREY,'reachable']]}
];
var host=document.getElementById('charts'),tip=document.getElementById('tip'),t0=R.length?R[0].t:0,t1=R.length?R[R.length-1].t:1;
CH.forEach(function(c){
 var d=document.createElement('div');d.className='c';
 var last=ok.length&&c.s?c.s.map(function(s){var v=n(ok[ok.length-1][s[0]]);return v==null?'':v+(c.u?' '+c.u:'')}).filter(Boolean).join(' / '):'';
 d.innerHTML='<h3>'+c.t+'<i>'+last+'</i></h3><canvas></canvas><div class="legend">'+(c.s||c.flags).map(function(s){return '<span style="--c:'+s[1]+'">'+s[2]+'</span>'}).join('')+'</div>';
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
 if(best.ok!=='1')lines.push('phone not reachable');
 tip.textContent=lines.join('\n');tip.style.display='block';tip.style.left=Math.min(window.innerWidth-tip.offsetWidth-8,e.clientX+12)+'px';tip.style.top=(e.clientY+12)+'px';
 draw(c,x);
}
</script></body></html>
'@

if ($ReportOnly) { Write-Report; Write-Host "Report: $Html"; return }
Write-Host "Recording every $Every s to $Csv"
Write-Host "Report:  $Html  (open it in a browser; it refreshes every minute)"
Write-Host "Phone log: $PhoneLog (every $LogEvery s)"
Write-Host 'Stop with Ctrl+C.'
$lastReport = [DateTime]::MinValue
$lastLog = [DateTime]::MinValue
while ($true) {
  $line = Sample
  Add-Content -Path $Csv -Value $line -Encoding ascii
  if (((Get-Date) - $lastLog).TotalSeconds -ge $LogEvery) { Save-PhoneLog; $lastLog = Get-Date }
  if (((Get-Date) - $lastReport).TotalSeconds -ge $ReportEvery) { try { Write-Report } catch { Write-Warning "Report: $_" }; $lastReport = Get-Date }
  $f = $line.Split(',')
  Write-Host ("{0}  {1}  fps {2}  {3} Mbps  battery {4}%  heat {5}  mem {6} MB" -f $f[0], $(if ($f[1] -eq '1') { if ($f[3] -eq '1') { 'streaming' } else { 'stopped  ' } } else { 'UNREACHABLE' }), $f[4], $f[5], $f[11], $f[16], $f[21])
  Start-Sleep -Seconds $Every
}
