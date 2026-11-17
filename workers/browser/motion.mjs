import fs from "node:fs/promises";
import { createHash } from "node:crypto";
import path from "node:path";

const shapes = ["circle", "box", "cloud", "drop", "sun", "arrow"];
const tones = { water: "#67c9ff", warm: "#ffc56e", air: "#7ee3c5", neutral: "#d7dded" };
const glyphs = {
  circle: '<ellipse cx="0" cy="0" rx="50" ry="50"/>',
  box: '<rect x="-50" y="-50" width="100" height="100" rx="8"/>',
  cloud: '<path d="M-38 22 C-50 0 -32 -22 -18 -14 C-10 -47 28 -44 30 -15 C49 -14 50 26 35 28 L-34 28 Z"/>',
  drop: '<path d="M0 -50 C-12 -23 -43 3 -34 25 C-24 58 24 58 34 25 C43 3 12 -23 0 -50 Z"/>',
  sun: '<circle r="28"/><path fill="none" stroke="currentColor" stroke-width="6" d="M0 -50 V-36 M0 36 V50 M-50 0 H-36 M36 0 H50 M-35 -35 L-26 -26 M26 26 L35 35 M-35 35 L-26 26 M26 -26 L35 -35"/>',
  arrow: '<path d="M-12 50 V-8 H-36 L0 -50 L36 -8 H12 V50 Z"/>'
};
const require = (condition, message) => { if (!condition) throw new Error(message); };
const exact = (value, keys) => value && typeof value === "object" && !Array.isArray(value)
  && Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key));
const text = (value, maximum, empty = false) => typeof value === "string" && value.length <= maximum
  && (empty || value.trim().length > 0) && !/[\u0000-\u001f\u007f-\u009f\uD800-\uDFFF]/u.test(value);
const escape = value => value.replace(/[&<>"']/g, character =>
  ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[character]);
const data = value => JSON.stringify(value).replaceAll("<", "\\u003c");

export function validateMotion(scene, frames) {
  const motion = scene.motion;
  if (motion == null) return;
  require(scene.kind === "diagram" && scene.visual == null && scene.visualAssetId == null,
    "motion.compatibility");
  require(Number.isSafeInteger(frames) && frames >= 60 && frames <= 18000, "motion.duration");
  require(exact(motion, ["entities", "stages"]) && Array.isArray(motion.entities)
    && motion.entities.length > 0 && motion.entities.length <= 20
    && Array.isArray(motion.stages) && motion.stages.length > 0 && motion.stages.length <= 6, "motion.bounds");
  const ids = new Set();
  for (const entity of motion.entities) {
    require(exact(entity, ["id", "shape", "tone", "label", "width", "height", "keys"])
      && typeof entity.id === "string" && /^[a-z][a-z0-9-]{0,63}$/.test(entity.id) && !ids.has(entity.id)
      && shapes.includes(entity.shape) && Object.hasOwn(tones, entity.tone) && text(entity.label, 28, true)
      && Number.isSafeInteger(entity.width) && entity.width >= 12 && entity.width <= 240
      && Number.isSafeInteger(entity.height) && entity.height >= 12 && entity.height <= 140
      && Array.isArray(entity.keys) && entity.keys.length >= 2 && entity.keys.length <= 8, "motion.entity");
    ids.add(entity.id);
    let previous = -1;
    const margin = Math.max(entity.width / 2 + 12, entity.label.length * 9 + 12);
    for (const key of entity.keys) {
      require(exact(key, ["percent", "x", "y", "opacity"]) && Number.isSafeInteger(key.percent)
        && key.percent > previous && key.percent >= 0 && key.percent <= 100
        && Number.isSafeInteger(key.x) && key.x >= margin && key.x <= 1000 - margin
        && Number.isSafeInteger(key.y) && key.y >= entity.height / 2 + 45 && key.y <= 580 - entity.height / 2
        && Number.isFinite(key.opacity) && key.opacity >= 0 && key.opacity <= 1, "motion.pose");
      previous = key.percent;
    }
    require(entity.keys[0].percent === 0 && entity.keys.at(-1).percent === 100, "motion.pose");
  }
  let previous = -1;
  for (const stage of motion.stages) {
    require(exact(stage, ["percent", "caption"]) && Number.isSafeInteger(stage.percent)
      && stage.percent > previous && stage.percent >= 0 && stage.percent <= 95 && text(stage.caption, 100), "motion.stage");
    previous = stage.percent;
  }
  require(motion.stages[0].percent === 0, "motion.stage");
  require(motion.entities.some(entity => entity.keys.slice(1).some((end, index) => {
    const start = entity.keys[index];
    return (start.opacity > 0 || end.opacity > 0)
      && (start.x !== end.x || start.y !== end.y || start.opacity !== end.opacity);
  })), "motion.change");
  require(scene.effects == null || scene.effects.length === 0, "motion.compatibility");
}

export function motionAt(motion, percent) {
  require(Number.isFinite(percent) && percent >= 0 && percent <= 100, "motion.seek");
  return motion.entities.map(entity => {
    const index = entity.keys.findIndex(key => key.percent >= percent);
    const end = entity.keys[index], start = entity.keys[Math.max(0, index - 1)];
    const fraction = end.percent === start.percent ? 0 : (percent - start.percent) / (end.percent - start.percent);
    return { id: entity.id, x: start.x + (end.x - start.x) * fraction,
      y: start.y + (end.y - start.y) * fraction, opacity: start.opacity + (end.opacity - start.opacity) * fraction };
  });
}

export async function runtimeAssets() {
  const assets = {};
  for (const [name, file, expected] of [
    ["gsap", "gsap.min.js", "c174bfce53a729418d57a8ad8625e7247c793a22fef8e2851e3cfa3de9cd8280"],
    ["hyperframes", "hyperframe.runtime.iife.js", "696c9c6ff46c173d319b17ce873a3cdcbc290cfe94cd4c0e307a76caff9c8a98"]
  ]) {
    const bytes = await fs.readFile(new URL("./runtime/" + file, import.meta.url));
    require(createHash("sha256").update(bytes).digest("hex") === expected, "motion.runtime-pin");
    assets[name] = bytes.toString("utf8");
  }
  return assets;
}

export function motionDocument(scene, frames, preview, assets) {
  validateMotion(scene, frames);
  const motion = scene.motion;
  const entities = motion.entities.map(entity => `<g id="motion-${entity.id}" data-motion-entity="${entity.id}">
    <g fill="${tones[entity.tone]}" color="${tones[entity.tone]}" transform="scale(${entity.width / 100} ${entity.height / 100})">${glyphs[entity.shape]}</g>
    <text text-anchor="middle" y="${-entity.height / 2 - 16}" fill="#f1f5ff" font-size="17">${escape(entity.label)}</text></g>`).join("");
  return `<!doctype html><html lang="en"><meta charset="utf-8">
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; font-src data:; connect-src 'none'">
    <style>@font-face{font-family:DemoMono;src:url(data:font/ttf;base64,${scene.fontBase64})}
    *{box-sizing:border-box}body{margin:0;width:1920px;height:1080px;background:#101827;color:#f1f5ff;font-family:DemoMono,monospace}
    h1{position:absolute;left:100px;top:65px;width:1720px;margin:0;font-size:52px;overflow-wrap:anywhere}
    svg{position:absolute;left:100px;top:220px;width:1720px;height:650px}
    .caption{position:absolute;left:100px;top:890px;width:1720px;font-size:30px;margin:0;opacity:0}
    .boundary{position:absolute;left:100px;bottom:55px;font-size:21px;color:#a3b8d3}
    .preview{position:absolute;right:100px;bottom:55px;font-size:21px;color:#ffc56e}
    </style>
    <div id="root" data-root="true" data-composition-id="root" data-width="1920" data-height="1080" data-duration="${frames / 30}">
      <h1 data-visual-box>${escape(scene.title)}</h1>
      <svg viewBox="0 0 1000 600" role="img" aria-label="Animated mechanism schematic">${entities}</svg>
      ${motion.stages.map((stage, index) => `<p id="motion-caption-${index}" class="caption" data-visual-box>${escape(stage.caption)}</p>`).join("")}
      <p class="boundary">Authored schematic · not a measured physical simulation</p>
      ${preview ? '<p class="preview">REVIEW ONLY · NOT RELEASE</p>' : ""}
    </div>
    <script>${assets.gsap}</script>
    <script>
      window.__HF_EXPORT_RENDER_SEEK_CONFIG={fps:30};window.__timelines={};
      const motion=${data(motion)}, duration=${(frames - 1) / 30};
      const tl=gsap.timeline({paused:true});
      for(const entity of motion.entities){
        const selector='#motion-'+entity.id, first=entity.keys[0];
        gsap.set(selector,{attr:{transform:'translate('+first.x+' '+first.y+')'},opacity:first.opacity});
        for(let i=1;i<entity.keys.length;i++){
          const start=entity.keys[i-1],end=entity.keys[i];
          tl.fromTo(selector,{attr:{transform:'translate('+start.x+' '+start.y+')'},opacity:start.opacity},
            {attr:{transform:'translate('+end.x+' '+end.y+')'},opacity:end.opacity,
            duration:(end.percent-start.percent)*duration/100,ease:'none',immediateRender:false},start.percent*duration/100);
        }
      }
      motion.stages.forEach((stage,index)=>{
        if(index)tl.set('#motion-caption-'+(index-1),{opacity:0},stage.percent*duration/100);
        tl.set('#motion-caption-'+index,{opacity:1},stage.percent*duration/100);
      });
      tl.to({},{duration:${frames / 30}},0);
      window.__timelines.root=tl;
    </script>
    <script>${assets.hyperframes}</script></html>`;
}

export async function seekMotion(page, frame) {
  return page.evaluate(async frame => {
    window.__player.renderSeek(frame / 30, { suppressEvents: true });
    await window.__hfWaitForSeekCompletion();
    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    return { time: window.__timelines.root.totalTime(), playing: window.__player.isPlaying(),
      entities: [...document.querySelectorAll("[data-motion-entity]")].map(element => {
        const matrix = element.transform.baseVal.consolidate().matrix;
        return { id: element.dataset.motionEntity, x: matrix.e, y: matrix.f,
          opacity: Number(getComputedStyle(element).opacity) };
      }) };
  }, frame);
}

export async function renderMotionFrames(config, page, scene, assertFits, observeFrame = () => {}) {
  const assets = await runtimeAssets();
  const errors = [];
  page.on("pageerror", error => errors.push(error.message));
  await page.setContent(motionDocument(scene, config.frames, config.preview, assets), { waitUntil: "load" });
  await page.evaluate(() => document.fonts.ready);
  require(await page.evaluate(() => document.fonts.check("34px DemoMono")), "motion.font");
  await page.waitForFunction(() => window.__renderReady && window.__playerReady);
  for (let frame = 0; frame < config.frames; frame++) {
    observeFrame(frame);
    const observed = await seekMotion(page, frame);
    require(!observed.playing && Math.abs(observed.time - frame / 30) < .001, "motion.seek");
    const expected = motionAt(scene.motion, frame * 100 / (config.frames - 1));
    require(observed.entities.every((entity, index) => Math.abs(entity.x - expected[index].x) < .01
      && Math.abs(entity.y - expected[index].y) < .01 && Math.abs(entity.opacity - expected[index].opacity) < .001), "motion.pose-mismatch");
    require(errors.length === 0, "motion.runtime: " + errors.join("; "));
    await assertFits(page, scene, frame);
    await page.screenshot({ path: path.join(config.outputDirectory, `${String(frame).padStart(6, "0")}.png`), animations: "allow" });
  }
}
