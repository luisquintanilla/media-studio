import fs from "node:fs/promises";
import path from "node:path";
import { createHash } from "node:crypto";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { pathToFileURL } from "node:url";
import { chromium } from "playwright";

const hash = bytes => createHash("sha256").update(bytes).digest("hex");
const escape = text => String(text).replace(/[&<>"']/g, c =>
  ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
const colors = ["#52D2BA", "#5796FF", "#B9AAEE", "#FFC16E"];

export function validateScene(scene) {
  if (!scene || scene.schemaVersion !== 1 || !Number.isInteger(scene.frames) || scene.frames < 30
      || scene.frames > 1800 || !["cards", "clocks", "figure", "chart", "code", "flow", "excerpt"].includes(scene.kind))
    throw new Error("animation.scene: bounded data-only scene required");
  for (const key of ["title", "subtitle", "credit", "location", "note", "breadcrumb"])
    if (scene[key] !== undefined && (typeof scene[key] !== "string" || scene[key].length > 700))
      throw new Error("animation.text: invalid text");
  if (scene.code && (typeof scene.code !== "string" || scene.code.length > 2000 || scene.code.split("\n").length > 16))
    throw new Error("animation.code: use a short readable excerpt");
  if (scene.rows && (!Array.isArray(scene.rows) || scene.rows.length > 8
      || scene.rows.some(row => typeof row.label !== "string" || row.label.length > 160
        || row.value !== undefined && (!Number.isFinite(row.value) || row.value < 0 || row.value > 100))))
    throw new Error("animation.rows: invalid bounded rows");
  if (["cards", "chart", "flow"].includes(scene.kind) && !scene.rows?.length)
    throw new Error("animation.rows: this scene requires content");
  if (scene.kind === "code" && !scene.code)
    throw new Error("animation.code: this scene requires an excerpt");
  if (scene.decimals !== undefined && (!Number.isInteger(scene.decimals) || scene.decimals < 0 || scene.decimals > 4))
    throw new Error("animation.decimals: use zero to four places");
  if (scene.map && (!Array.isArray(scene.active) || scene.active.some(n => !Number.isInteger(n) || n < 0 || n > 5)))
    throw new Error("animation.map: invalid active nodes");
  return scene;
}

function mapMarkup(scene) {
  const nodes = ["Intent & review", "Model capability", "Information", "Permitted operations", "Media production", "App discovery"];
  return `<aside class="map">${nodes.map((label, index) =>
    `<div class="node ${scene.active.includes(index) ? "active" : ""}">
      <small>${scene.active.includes(index) ? "ACTIVE" : "CAPABILITY"}</small>${label}</div>`).join("")}
    <div class="mapnote">Fixed map / choose an arrangement<br>Not every feature needs every piece.</div></aside>`;
}

export async function makeDocument(scene, assets) {
  validateScene(scene);
  let content = "";
  if (scene.kind === "clocks") {
    content = `<div class="clock-intro">ONE MODEL, DIFFERENT UPDATE TIMESCALES</div>` +
      ["High frequency", "Mid frequency", "Low frequency"].map((label, row) =>
        `<div class="clock-row reveal"><div>${label}<small>${["changes often", "changes less often", "changes slowly"][row]}</small></div>
          <div class="ticks">${Array.from({ length: 12 }, (_, i) =>
            `<i data-cadence="${[2, 6, 12][row]}" class="tick ${i % [1, 3, 6][row] === 0 ? "update" : ""}" style="--accent:${colors[row]}"></i>`).join("")}</div></div>`).join("") +
      `<div class="callout reveal">${escape(scene.note || "Conceptual cadence, not measured frequencies or a full Hope implementation.")}</div>`;
  } else if (scene.kind === "figure") {
    content = `<img class="source-image reveal" src="${assets.image}" alt="Attributed source crop">
      ${scene.note ? `<div class="source-note reveal">${escape(scene.note)}</div>` : ""}`;
  } else if (scene.kind === "chart") {
    content = `${assets.image ? `<img class="table-image reveal" src="${assets.image}" alt="Original source table">` : ""}
      <div class="chart ${assets.image ? "with-source" : ""}">
      <div class="chart-heading reveal">${escape(scene.note)}</div>
      ${scene.rows.map((row, i) => `<div class="chart-row reveal"><div>${escape(row.label)}
      <strong>${row.value.toFixed(scene.decimals ?? 1)}</strong></div>
      <div class="track"><div class="bar" data-value="${row.value}" style="background:${colors[i]}"></div></div></div>`).join("")}
      <div class="chart-axis reveal"><span>0</span><span>COMMON ZERO BASELINE</span><span>100</span></div></div>`;
  } else if (scene.kind === "code") {
    content = `<pre class="code">${scene.code.split("\n").map(line =>
      `<div class="code-line">${escape(line) || " "}</div>`).join("")}</pre>
      <div class="callout reveal">${escape(scene.note || "")}</div>`;
  } else if (scene.kind === "flow") {
    content = `<div class="flow">${scene.rows.map((row, i) =>
      `<div class="flow-row reveal"><b>${String(i + 1).padStart(2, "0")}</b>${escape(row.label)}</div>
       ${i < scene.rows.length - 1 ? '<div class="arrow reveal">&#8595;</div>' : ""}`).join("")}</div>
      ${scene.code ? `<pre class="flow-code">${escape(scene.code)}</pre>` : ""}
      <div class="callout reveal">${escape(scene.note || "")}</div>`;
  } else if (scene.kind === "excerpt") {
    content = `${assets.video ? `<video id="excerpt" class="excerpt reveal" muted preload="auto" src="${assets.video}"></video>`
      : `<img class="excerpt reveal" src="${assets.image}" alt="Prepared actual output frame">`}
      ${assets.video ? '<div id="excerpt-mode" class="excerpt-mode">PREPARED CLIP / original speed</div>' : ""}
      <div class="callout reveal">${escape(scene.note)}</div>`;
  } else {
    content = `<div class="cards">${scene.rows.map((row, i) =>
      `<div class="card reveal"><small>${String(i + 1).padStart(2, "0")}</small>${escape(row.label)}</div>`).join("")}</div>
      <div class="callout reveal">${escape(scene.note || "")}</div>`;
  }
  const duration = scene.frames / 30;
  const revealWindow = scene.kind === "flow" && scene.code ? duration * .35 : duration;
  return `<!doctype html><html lang="en"><meta charset="utf-8">
  <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; media-src data:; font-src data:; connect-src 'none';">
  <style>
  @font-face{font-family:Keynote;src:url(data:font/ttf;base64,${assets.font})}
  *{box-sizing:border-box}html,body{margin:0;width:1920px;height:1080px;background:#1E1C30;color:#fff;font-family:Keynote,sans-serif}
  #root{width:1920px;height:1080px;position:relative;overflow:hidden}
  header{position:absolute;left:80px;top:48px;right:80px}h1{font-size:58px;line-height:1.08;margin:0 0 16px;font-weight:650;letter-spacing:-1.6px}
  .subtitle{font-size:28px;color:#C8C5D8}.location{position:absolute;left:80px;top:166px;font-size:26px;color:#52D2BA;letter-spacing:.5px}
  .trail{position:absolute;right:80px;top:170px;font-size:24px;color:#B9AAEE}
  .content{position:absolute;left:${scene.map ? 650 : 100}px;right:100px;top:238px;height:606px}
  .map{position:absolute;left:80px;top:240px;width:510px;display:grid;grid-template-columns:1fr 1fr;gap:15px}
  .node{height:138px;padding:20px;border:2px solid #655A85;border-radius:14px;background:#2C2542;font-size:28px;line-height:1.2}
  .node small{display:block;color:#C8C5D8;font-size:16px;letter-spacing:1.6px;margin-bottom:12px}
  .node.active{border:3px solid #52D2BA;background:#173C3B}.node.active small{color:#52D2BA}
  .mapnote{grid-column:span 2;color:#C8C5D8;font-size:23px;margin-top:12px;line-height:1.4}
  .credit{position:absolute;left:80px;right:80px;top:839px;font-size:17px;color:#C8C5D8;line-height:1.25}
  .progress{position:absolute;left:80px;bottom:219px;height:4px;background:#52D2BA;transform-origin:left}
  .cards{display:grid;gap:24px;grid-template-columns:${scene.map ? "1fr" : "1fr 1fr"}}
  .card{padding:28px 32px;background:#2C2542;border-left:5px solid #5796FF;border-radius:10px;font-size:${scene.map ? 32 : 39}px;line-height:1.25}
  .card small{color:#B9AAEE;font-size:21px;display:block;margin-bottom:8px}
  .callout{font-size:29px;line-height:1.35;color:#B9AAEE;margin-top:30px}
  .clock-intro{font-size:26px;color:#B9AAEE;margin:4px 0 30px}
  .clock-row{display:flex;align-items:center;margin:25px 0;padding:24px;background:#2C2542;border-radius:12px}
  .clock-row>div:first-child{width:340px;font-size:37px}.clock-row small{display:block;font-size:23px;color:#C8C5D8;margin-top:6px}
  .ticks{display:flex;align-items:center;gap:34px}.tick{width:38px;height:56px;border-radius:6px;background:#353448}
  .tick.update{background:var(--accent);height:72px}
  .source-image{width:100%;height:510px;object-fit:contain;background:#fff;border-radius:10px}
  .source-note{font-size:24px;color:#B9AAEE;margin-top:14px}.table-image{width:760px;height:588px;object-fit:contain;background:white;position:absolute;left:0;top:0}
  .chart.with-source{position:absolute;left:825px;right:0;top:10px}.chart-heading{font-size:31px;color:#B9AAEE;line-height:1.4;margin-bottom:48px}
  .chart-row{margin-bottom:45px;font-size:35px}.chart-row>div:first-child{display:flex;justify-content:space-between;align-items:center}
  .chart-row strong{font-size:55px}.track{height:30px;background:#353448;margin-top:18px;border-left:2px solid white}
  .bar{height:30px;width:0}.chart-axis{display:grid;grid-template-columns:1fr auto 1fr;font-size:20px;color:#C8C5D8;border-top:1px solid #655A85;padding-top:15px}
  .chart-axis>span:last-child{text-align:right}
  .code{margin:0;padding:24px;background:#141322;font:26px/1.35 Consolas,monospace;white-space:pre-wrap;overflow-wrap:anywhere;border:1px solid #655A85}
  .code-line{border-left:3px solid #5796FF;padding-left:14px}.code-line:nth-child(even){color:#B9AAEE}
  .flow{display:flex;flex-direction:column;align-items:center}  .flow-row{width:100%;padding:${scene.code ? 8 : 13}px 22px;background:#2C2542;border:1px solid #655A85;font-size:${scene.code ? 26 : 30}px}
  .flow-row b{color:#52D2BA;margin-right:22px;font-size:24px}.arrow{font-size:24px;color:#52D2BA;line-height:1.05}
  .flow-code{position:absolute;top:95px;left:0;right:0;opacity:0;font:28px/1.5 Consolas,monospace;color:#B9AAEE;white-space:pre-wrap;background:#141322;padding:24px;margin:0}
  ${scene.kind === "flow" ? ".content>.callout{position:absolute;top:475px;left:0;right:0;font-size:26px;margin-top:0}" : ""}
  .excerpt{width:100%;height:450px;object-fit:contain;border:2px solid #B9AAEE}
  .excerpt-mode{position:absolute;top:416px;right:12px;padding:5px 10px;background:#141322;color:#52D2BA;font-size:20px}
  </style>
  <div id="root" data-root="true" data-composition-id="root" data-width="1920" data-height="1080" data-duration="${duration}">
    <header><h1>${escape(scene.title)}</h1><div class="subtitle">${escape(scene.subtitle || "")}</div></header>
    <div class="location">YOU ARE HERE: ${escape(scene.location || scene.title)}</div>
    <div class="trail">${escape(scene.breadcrumb || "Research  /  Evidence  /  C# implications")}</div>
    ${scene.map ? mapMarkup(scene) : ""}<main class="content">${content}</main>
    <footer class="credit">${escape(scene.credit || "")}</footer><div class="progress"></div>
  </div>
  <script>${assets.gsap}</script>
  <script>
    window.__HF_EXPORT_RENDER_SEEK_CONFIG={fps:30};
    window.__timelines={};const tl=gsap.timeline({paused:true});
    const reveals=[...document.querySelectorAll('.reveal')];
    reveals.forEach((element,index)=>tl.fromTo(element,{opacity:0,y:18},{opacity:1,y:0,duration:.65,ease:'power2.out'},.3+index*Math.min(2.4,${revealWindow}/Math.max(3,reveals.length+2))));
    document.querySelectorAll('.bar').forEach((el,i)=>tl.to(el,{width:el.dataset.value+'%',duration:1.3,ease:'power2.out'},2+i*2.5));
    document.querySelectorAll('.tick.update').forEach((el,i)=>{
      const cadence=Number(el.dataset.cadence);
      for(let time=1+(i%12)*.1;time<${duration}-.7;time+=cadence)
        tl.fromTo(el,{opacity:.4,scaleY:.75},{opacity:1,scaleY:1,duration:.35,repeat:1,yoyo:true},time);
    });
    document.querySelectorAll('.code-line').forEach((el,i)=>tl.fromTo(el,{opacity:.15},{opacity:1,duration:.4},.6+i*Math.min(1.7,${duration}/18)));
    if(document.querySelector('.flow-code')){
      tl.to('.flow',{opacity:0,duration:.5},${duration}*.5);
      tl.to('.flow-code',{opacity:1,duration:.5},${duration}*.52);
    }
    tl.fromTo('.progress',{scaleX:0,width:1760},{scaleX:1,duration:${duration},ease:'none'},0);
    tl.to({},{duration:${duration}},0);window.__timelines.root=tl;
    const video=document.getElementById('excerpt');
    const videoReady=video&&new Promise((resolve,reject)=>{
      if(video.readyState>=1&&Number.isFinite(video.duration))return resolve();
      const timeout=setTimeout(()=>reject(new Error('Prepared excerpt metadata timed out')),10000);
      video.addEventListener('loadedmetadata',()=>{
        clearTimeout(timeout);
        Number.isFinite(video.duration)?resolve():reject(new Error('Prepared excerpt has no finite duration'));
      },{once:true});
      video.addEventListener('error',()=>{clearTimeout(timeout);reject(new Error('Prepared excerpt metadata failed'))},{once:true});
    });
    if(video)window.addEventListener('hf-seek',event=>{
      event.detail.waitUntil(videoReady.then(()=>new Promise((resolve,reject)=>{
        const target=Math.min(Math.max(0,event.detail.time-1),Math.max(0,video.duration-1/30));
        document.getElementById('excerpt-mode').textContent=event.detail.time>=video.duration+1
          ?'PREPARED CLIP / editorial hold':'PREPARED CLIP / original speed';
        if(Math.abs(video.currentTime-target)<.00001&&!video.seeking)return resolve();
        const timeout=setTimeout(()=>reject(new Error('Prepared excerpt seek timed out')),5000);
        video.addEventListener('seeked',()=>{clearTimeout(timeout);resolve()},{once:true});
        video.pause();video.currentTime=target;
      })));
    });
  </script>
  <script>${assets.hyperframes}</script></html>`;
}

async function pinnedFile(file, expected) {
  const bytes = await fs.readFile(file);
  if (hash(bytes) !== expected) throw new Error("animation.pin: asset changed: " + path.basename(file));
  return bytes;
}

export async function render(config) {
  validateScene(config.scene);
  if (Number(process.versions.node.split(".")[0]) !== 24) throw new Error("animation.runtime: Node24 required");
  const receipt = JSON.parse(await fs.readFile(config.runtimeReceipt, "utf8"));
  const resource = async (name, version, suffix) => {
    const item = receipt.find(i => i.package === name && i.version === version && i.file.endsWith(suffix));
    if (!item) throw new Error("animation.runtime: missing exact runtime");
    return (await pinnedFile(item.path, item.sha256)).toString("utf8");
  };
  const assets = {
    font: (await pinnedFile(config.font.file, config.font.sha256)).toString("base64"),
    gsap: await resource("gsap", "3.14.2", "/gsap.min.js"),
    hyperframes: await resource("hyperframes", "0.8.33", "/hyperframe.runtime.iife.js")
  };
  if (config.image) {
    const bytes = await pinnedFile(config.image.file, config.image.sha256);
    assets.image = "data:image/png;base64," + bytes.toString("base64");
  }
  if (config.video) {
    const bytes = await pinnedFile(config.video.file, config.video.sha256);
    if (bytes.length > 50_000_000) throw new Error("animation.excerpt: use a short bounded prepared clip");
    assets.video = "data:video/mp4;base64," + bytes.toString("base64");
  }
  await fs.mkdir(config.output, { recursive: false });
  const html = await makeDocument(config.scene, assets);
  await fs.writeFile(path.join(config.output, "index.html"), html);
  await fs.writeFile(path.join(config.output, "scene.json"), JSON.stringify(config.scene, null, 2));
  const browser = await chromium.launch({ headless: true,
    args: ["--disable-gpu", "--disable-lcd-text", "--font-render-hinting=none"] });
  let encoder;
  let encoderError = "";
  try {
    const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 });
    page.setDefaultTimeout(30_000);
    const pageErrors = [];
    page.on("pageerror", e => pageErrors.push(e.message));
    await page.route("**/*", route => route.abort());
    await page.setContent(html, { waitUntil: "load" });
    await page.evaluate(async () => {
      await document.fonts.ready;
      await Promise.all([...document.images].map(image => image.decode()));
      await Promise.all([...document.querySelectorAll("video")].map(video => video.readyState >= 2 ? Promise.resolve()
        : new Promise((resolve, reject) => {
          const timeout = setTimeout(() => reject(new Error("Prepared excerpt readiness timed out")), 10_000);
          video.addEventListener("loadeddata", () => { clearTimeout(timeout); resolve(); }, { once: true });
          video.addEventListener("error", () => {
            clearTimeout(timeout); reject(new Error("Prepared excerpt decode failed"));
          }, { once: true });
        })));
      if (!document.fonts.check("32px Keynote")) throw new Error("Pinned font not ready");
    });
    await page.waitForFunction(() => window.__renderReady && window.__playerReady);
    const seek = async frame => page.evaluate(async frame => {
      window.__player.renderSeek(frame / 30, { suppressEvents: true });
      await window.__hfWaitForSeekCompletion();
      await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
      return { time: window.__player.getTime(), playing: window.__player.isPlaying(),
        timelineTime: window.__timelines.root.totalTime() };
    }, frame);
    const probes = [0, Math.floor(config.scene.frames / 2), 3, Math.floor(config.scene.frames / 2), config.scene.frames - 1];
    const observations = [];
    for (const [index, frame] of probes.entries()) {
      const state = await seek(frame);
      if (state.playing || Math.abs(state.timelineTime - frame / 30) > .001) throw new Error("animation.seek: wrong frame time");
      const png = await page.screenshot({ animations: "allow", type: "png" });
      observations.push({ frame, ...state, sha256: hash(png) });
      await fs.writeFile(path.join(config.output, `seek-${index}.png`), png);
    }
    if (observations[1].sha256 !== observations[3].sha256) throw new Error("animation.seek: repeated backward seek pixels differ");
    const layout = await page.evaluate(() => {
      const axis = document.querySelector(".chart-axis");
      const track = document.querySelector(".track")?.getBoundingClientRect();
      const textBounds = element => {
        const range = document.createRange();
        range.selectNodeContents(element);
        return range.getBoundingClientRect();
      };
      return {
        width: document.documentElement.scrollWidth, height: document.documentElement.scrollHeight,
        font: document.fonts.check("32px Keynote"),
        contentBottom: document.querySelector(".content").getBoundingClientRect().bottom,
        chartAxis: axis ? { zeroX: textBounds(axis.firstElementChild).left,
          hundredRight: textBounds(axis.lastElementChild).right, trackLeft: track.left, trackRight: track.right } : null,
        overflowing: [...document.querySelectorAll(".code,.flow-code,.card,.flow-row,.source-note,.callout")].filter(el =>
          el.getBoundingClientRect().bottom > 832 || el.scrollWidth > el.clientWidth + 2).map(el => el.className)
      };
    });
    const axis = layout.chartAxis;
    if (layout.width !== 1920 || layout.height !== 1080 || layout.overflowing.length || pageErrors.length
        || axis && (Math.abs(axis.zeroX - axis.trackLeft) > 1 || Math.abs(axis.hundredRight - axis.trackRight) > 1))
      throw new Error("animation.layout: " + JSON.stringify({ layout, pageErrors }));
    const movie = path.join(config.output, "animation.mp4");
    encoder = spawn(config.ffmpeg, ["-hide_banner", "-loglevel", "error", "-n", "-f", "image2pipe", "-vcodec", "png",
      "-framerate", "30", "-i", "pipe:0", "-an", "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
      "-pix_fmt", "yuv420p", "-vf", "scale=in_range=full:out_range=tv:out_color_matrix=bt709",
      "-color_range", "tv", "-colorspace", "bt709", "-color_trc", "bt709", "-color_primaries", "bt709",
      "-bsf:v", "h264_metadata=colour_primaries=1:transfer_characteristics=1:matrix_coefficients=1:video_full_range_flag=0",
      "-movflags", "+faststart", movie], { windowsHide: true, stdio: ["pipe", "ignore", "pipe"] });
    let encoderFailure;
    const completion = new Promise(resolve => {
      encoder.once("error", error => { encoderFailure = error; resolve({ code: null }); });
      encoder.once("close", code => resolve({ code }));
    });
    encoder.stderr.on("data", bytes => { encoderError = (encoderError + bytes.toString()).slice(-4000); });
    encoder.stdin.on("error", error => { encoderFailure = error; });
    for (let frame = 0; frame < config.scene.frames; frame++) {
      if (encoderFailure || encoder.exitCode !== null)
        throw new Error("animation.encode: " + (encoderFailure?.message || encoderError || "Encoder stopped early"));
      await seek(frame);
      const png = await page.screenshot({ type: "png", animations: "allow" });
      if (!encoder.stdin.write(png)) await Promise.race([
        once(encoder.stdin, "drain"),
        completion.then(() => { throw new Error("animation.encode: " + (encoderFailure?.message || encoderError)); })
      ]);
      if (frame % 300 === 0)
        await fs.writeFile(path.join(config.output, "render-progress.json"),
          JSON.stringify({ renderedFrames: frame + 1, totalFrames: config.scene.frames }));
    }
    encoder.stdin.end();
    const encoded = await completion;
    if (encoded.code !== 0 || encoderFailure)
      throw new Error("animation.encode: " + (encoderFailure?.message || encoderError));
    const evidence = { schemaVersion: 1, method: "HyperFrames browser runtime __player.renderSeek + awaited seek completion; paused GSAP timeline; frame n/30; Playwright capture, no HTTP listener",
      hyperframes: "0.8.33", gsap: "3.14.2", node: process.version, browser: browser.version(),
      frames: config.scene.frames, fps: 30, observations, layout, pageErrors,
      sourceSha256: hash(Buffer.from(html)), movieSha256: hash(await fs.readFile(movie)) };
    await fs.writeFile(path.join(config.output, "render-receipt.json"), JSON.stringify(evidence, null, 2));
    console.log(JSON.stringify({ movie, ...evidence }));
  } finally {
    if (encoder && encoder.exitCode === null) encoder.kill();
    await browser.close();
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  render(JSON.parse(await fs.readFile(process.argv[2], "utf8"))).catch(error => {
    console.error(error.message); process.exitCode = 1;
  });
}
