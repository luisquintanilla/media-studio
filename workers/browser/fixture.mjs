// This entire trusted fixture is served only by the capture worker on loopback.
export const counterHtml = `<!doctype html><html lang="en"><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; font-src data:">
<title>Synthetic counter</title>
<style>@font-face{font-family:FixtureFont;src:url("data:font/ttf;base64,FONT_DATA")}
body{margin:0;background:#101827;color:#f5f7ff;font:38px FixtureFont,monospace;padding:110px}
h1{font-size:64px}button{font:36px FixtureFont,monospace;padding:24px 50px;border:0;border-radius:12px;background:#79dcff}
#count{font-size:120px;color:#79dcff;margin:60px 0}canvas{position:absolute;left:110px;bottom:80px}</style>
<h1>Real headless browser demo</h1><p>Controlled local fixture. No personal data.</p>
<div id="count" role="status">0</div><button id="increment">Increment</button><canvas id="clock" width="1200" height="16"></canvas>
<script>document.getElementById('increment').addEventListener('click',()=>{document.getElementById('count').textContent=String(Number(document.getElementById('count').textContent)+1)});
const canvas=document.getElementById('clock');const ctx=canvas.getContext('2d');const start=performance.now();
function tick(now){ctx.fillStyle='#30415d';ctx.fillRect(0,0,1200,16);ctx.fillStyle='#79dcff';ctx.fillRect(0,0,Math.min(1200,(now-start)/5),16);requestAnimationFrame(tick)}requestAnimationFrame(tick);</script>
</html>`;
