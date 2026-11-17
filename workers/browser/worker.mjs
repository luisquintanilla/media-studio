import { chromium } from "playwright";
import { createServer } from "node:http";
import { promises as fs } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { createRequire } from "node:module";
import { performance } from "node:perf_hooks";
import { createHash } from "node:crypto";
import { counterHtml } from "./fixture.mjs";
import { renderFootage } from "./footage.mjs";
import { captureJellyfin } from "./jellyfin.mjs";
import { validateVisual, visualAt, visualHtml } from "./visuals.mjs";
import { validateMotion, motionAt, renderMotionFrames } from "./motion.mjs";

async function identity() {
  const require = createRequire(import.meta.url);
  const packageRoot = path.dirname(require.resolve("playwright-core/package.json"));
  const executable = chromium.executablePath();
  const browsers = JSON.parse(await fs.readFile(path.join(packageRoot, "browsers.json"), "utf8"));
  const encoderRevision = browsers.browsers.find(b => b.name === "ffmpeg").revision;
  const browserCache = path.dirname(path.dirname(path.dirname(executable)));
  const encoder = path.join(browserCache, `ffmpeg-${encoderRevision}`, process.platform === "win32" ? "ffmpeg-win64.exe" : "ffmpeg-linux");
  return { executable, executableSha256: createHash("sha256").update(await fs.readFile(executable)).digest("hex"),
    encoder, encoderSha256: createHash("sha256").update(await fs.readFile(encoder)).digest("hex") };
}

export function escapeHtml(value) {
  return value.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;").replaceAll("'", "&#39;");
}

export function stateAt(scene, frame, frames) {
  if (!Number.isSafeInteger(frames) || frames < 1 || frames > 18000
    || !Number.isSafeInteger(frame) || frame < 0 || frame >= frames) throw new Error("frame out of range");
  validateVisual(scene, frames);
  validateMotion(scene, frames);
  const reveal = scene.visual == null && (scene.kind === "terminal" || scene.kind === "code")
    ? Math.min(scene.text.split("\n").length, 1 + Math.floor(frame / 12)) : -1;
  const effects = scene.effects.filter(e => frame >= e.startFrame && frame < e.endFrame);
  const fade = effects.filter(e => e.kind === "fade").reduce((opacity, effect) =>
    Math.min(opacity, (frame - effect.startFrame + 1) / 12, (effect.endFrame - frame) / 12), 1);
  return { reveal, effects, fade, ...(scene.visual == null ? {} : { visual: visualAt(scene, frame) }),
    ...(scene.motion == null ? {} : { motion: motionAt(scene.motion, frame * 100 / (frames - 1)) }) };
}

export function htmlAt(scene, state, preview) {
  if (scene.motion != null) throw new Error("motion.renderer: animated mechanisms require the native timeline renderer");
  if (scene.visual != null) return visualHtml(scene, state, preview, escapeHtml);
  const text = escapeHtml(state.reveal < 0 ? scene.text : scene.text.split("\n").slice(0, state.reveal).join("\n"));
  const border = state.effects.some(e => e.kind === "highlight") ? "#ffdf70" : "#30415d";
  const arrows = state.effects.filter(e => e.kind === "arrow").map(e => `<p class="arrow">&#8594; ${escapeHtml(e.text)}</p>`).join("");
  const content = scene.kind === "diagram"
    ? `<div class="diagram">${scene.text.split("\n").map(t => `<div>${escapeHtml(t)}</div>`).join('<span>&#8594;</span>')}</div>`
    : `<pre>${text}</pre>`;
  return `<!doctype html><html lang="en"><meta charset="utf-8">
  <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; font-src data:">
  <style>@font-face{font-family:DemoMono;src:url("data:font/ttf;base64,${scene.fontBase64}")}
  *{box-sizing:border-box}body{margin:0;width:1920px;height:1080px;padding:100px;background:#101827;color:#f1f5ff;font:36px DemoMono,monospace}
  main{opacity:${state.fade}}h1{font-size:62px;margin:0 0 58px;color:#79dcff;white-space:pre-wrap}
  pre{font:34px/1.6 DemoMono,monospace;white-space:pre-wrap;overflow-wrap:anywhere;margin:0;padding:36px;border:4px solid ${border};border-radius:16px}
  .diagram{display:flex;gap:18px;align-items:center;flex-wrap:wrap}.diagram div{border:4px solid ${border};padding:28px;border-radius:16px;max-width:450px}
  .arrow{color:#ffdf70;font-size:36px}.preview{position:absolute;right:70px;bottom:48px;color:#ffdf70;font-size:30px}</style>
  <main><h1>${escapeHtml(scene.title)}</h1>${content}${arrows}</main>
  ${preview ? '<div class="preview">REVIEW-ONLY PREVIEW - NOT RELEASE</div>' : ""}</html>`;
}

async function capture(config) {
  const font = await fs.readFile(config.fontPath);
  if (createHash("sha256").update(font).digest("hex") !== config.fontSha256) throw new Error("Capture font hash mismatch");
  const html = counterHtml.replace("FONT_DATA", font.toString("base64"));
  const server = createServer((req, res) => {
    if (req.url !== "/") { res.writeHead(404); res.end(); return; }
    res.writeHead(200, { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" });
    res.end(html);
  });
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  let browser;
  let context;
  try {
    const origin = `http://127.0.0.1:${server.address().port}`;
    browser = await chromium.launch({ headless: true, executablePath: chromium.executablePath() });
    context = await browser.newContext({
      viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1, locale: "en-US", timezoneId: "UTC",
      colorScheme: "dark", reducedMotion: "reduce", serviceWorkers: "block",
      recordVideo: { dir: config.outputDirectory, size: { width: 1920, height: 1080 } }
    });
    await context.route("**/*", route => route.request().url() === origin + "/" ? route.continue() : route.abort());
    const page = await context.newPage();
    const video = page.video();
    const ledger = [];
    const observations = [];
    const start = performance.now();
    await page.goto(origin, { waitUntil: "load" });
    await page.evaluate(() => document.fonts.ready);
    if (!await page.evaluate(() => document.fonts.check("38px FixtureFont"))) throw new Error("Capture font did not load");
    await page.locator("#count").waitFor({ state: "visible" });
    if (await page.locator("#count").textContent() !== "0") throw new Error("Initial counter assertion failed");
    ledger.push({ milliseconds: performance.now() - start, assertion: "counter=0" });
    await page.screenshot({ path: path.join(config.outputDirectory, "before.png") });
    async function observeCount(id, identity) {
      const box = await page.locator("#count").evaluate(element => {
        const range = document.createRange(); range.selectNodeContents(element);
        const r = range.getBoundingClientRect();
        return { x: r.x, y: r.y, width: r.width, height: r.height };
      });
      const region = { x: box.x / 1920, y: box.y / 1080, width: box.width / 1920, height: box.height / 1080 };
      observations.push({ id, targetIdentity: identity, region, screenshot: id + ".png", event: "observed",
        clickX: null, clickY: null, layoutSha256: createHash("sha256").update(JSON.stringify({ identity, region })).digest("hex") });
    }
    await observeCount("before", "Actual synthetic counter text 0");
    // Visible authored acquisition holds follow readiness; they are not readiness polling.
    await page.waitForTimeout(1100);
    await page.locator("#increment").click();
    if (await page.locator("#count").textContent() !== "1") throw new Error("Increment assertion failed");
    ledger.push({ milliseconds: performance.now() - start, assertion: "counter=1" });
    await page.waitForTimeout(2200);
    await page.screenshot({ path: path.join(config.outputDirectory, "after.png") });
    await observeCount("after", "Actual synthetic counter text 1 after genuine increment");
    ledger.push({ milliseconds: performance.now() - start, assertion: "capture-end; authored hold complete" });
    await context.close();
    context = undefined;
    await video.saveAs(path.join(config.outputDirectory, "browser.webm"));
    const rawPath = await video.path();
    if (rawPath !== path.join(config.outputDirectory, "browser.webm")) await fs.unlink(rawPath);
    await fs.writeFile(path.join(config.outputDirectory, "landmarks.json"), JSON.stringify({
      schemaVersion: 1, profile: "trusted-bundled-loopback-counter", nativeRecorderFps: 25,
      clock: "worker-monotonic milliseconds since navigation start; not an exact recording-PTS or narration alignment",
      browserVersion: browser.version(), ledger
    }, null, 2));
    await fs.writeFile(path.join(config.outputDirectory, "observations.json"), JSON.stringify({ schemaVersion: 1, anchors: observations }, null, 2));
  } finally {
    if (context) await context.close();
    if (browser) await browser.close();
    await new Promise(resolve => server.close(resolve));
  }
}

export async function assertTemplateFits(page, scene, frame) {
  const overflow = await page.evaluate(() => document.documentElement.scrollHeight > 1080 || document.documentElement.scrollWidth > 1920
    || [...document.querySelectorAll("[data-visual-box]")].some(element => {
      const box = element.getBoundingClientRect();
      return element.scrollHeight > element.clientHeight + 1 || element.scrollWidth > element.clientWidth + 1
        || box.left < 0 || box.top < 0 || box.right > 1920 || box.bottom > 970;
    }));
  if (overflow) throw new Error(`Template overflow in ${scene.id} at frame ${frame}`);
}

export async function writeRendererFailure(directory, error, observation) {
  const bounded = value => String(value).slice(-64000);
  const message = bounded(error.message);
  const diagnostic = {
    schemaVersion: 1, ...observation, message,
    chromiumStderr: message.split("\n").filter(line => /\[pid[=:]\d+\]\[err\]/.test(line)),
    cleanupError: observation.cleanupError == null ? null : bounded(observation.cleanupError),
    notice: "Private renderer diagnostics only; no automatic retry or inferred crash reason."
  };
  const file = path.join(directory, "renderer-failure.json");
  await fs.writeFile(file, JSON.stringify(diagnostic, null, 2));
  return file;
}

async function frames(config) {
  if (!Number.isSafeInteger(config.frames) || config.frames < 1 || config.frames > 18000) throw new Error("Invalid frame count");
  let browser, failure;
  const observation = { sceneId: config.scene.id, frame: null, stage: "launching",
    browserDisconnected: false, targetCrashes: [], pageErrors: [] };
  try {
    browser = await chromium.launch({ headless: true, executablePath: chromium.executablePath() });
    observation.browserVersion = browser.version();
    browser.on("disconnected", () => { observation.browserDisconnected = true; });
    const protocol = await browser.newBrowserCDPSession();
    protocol.on("Target.targetCrashed", event => {
      if (observation.targetCrashes.length < 16)
        observation.targetCrashes.push({ targetId: event.targetId, status: event.status, errorCode: event.errorCode });
    });
    await protocol.send("Target.setDiscoverTargets", { discover: true });
    const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1, locale: "en-US" });
    page.on("pageerror", error => {
      if (observation.pageErrors.length < 16) observation.pageErrors.push(error.message.slice(0, 2048));
    });
    page.on("crash", () => { observation.pageCrashed = true; });
    await page.route("**/*", route => route.abort());
    const font = await fs.readFile(config.fontPath);
    if (createHash("sha256").update(font).digest("hex") !== config.fontSha256) throw new Error("Font hash mismatch");
    const scene = { ...config.scene, fontBase64: font.toString("base64") };
    observation.stage = "capturing";
    if (scene.motion != null) {
      await renderMotionFrames(config, page, scene, assertTemplateFits, frame => { observation.frame = frame; });
    } else {
      const cache = new Map();
      for (let frame = 0; frame < config.frames; frame++) {
        observation.frame = frame;
        const state = stateAt(scene, frame, config.frames);
        const key = JSON.stringify(state);
        const target = path.join(config.outputDirectory, `${String(frame).padStart(6, "0")}.png`);
        if (cache.has(key)) {
          await fs.link(cache.get(key), target);
        } else {
          await page.setContent(htmlAt(scene, state, config.preview), { waitUntil: "load" });
          await page.evaluate(() => document.fonts.ready);
          if (!await page.evaluate(() => document.fonts.check("34px DemoMono"))) throw new Error("Pinned font did not load");
          await assertTemplateFits(page, scene, frame);
          await page.screenshot({ path: target, animations: "disabled" });
          cache.set(key, target);
        }
      }
    }
  } catch (error) { failure = error; }
  observation.disconnectedBeforeCleanup = observation.browserDisconnected;
  if (browser) {
    try { await browser.close(); }
    catch (error) { observation.cleanupError = error.message; failure ??= error; }
  }
  if (failure) {
    const file = await writeRendererFailure(config.outputDirectory, failure, observation);
    failure.message += "\nPrivate renderer diagnostic: " + file;
    throw failure;
  }
}

export async function main(argv) {
  if (argv.length === 1 && argv[0] === "identity") {
    console.log(JSON.stringify(await identity()));
    return;
  }
  if (argv.length === 1 && argv[0] === "info") {
    const executable = chromium.executablePath();
    const browser = await chromium.launch({ headless: true, executablePath: executable });
    try {
      console.log(JSON.stringify({ ...await identity(),
        browserVersion: browser.version(), playwrightVersion: "1.61.0", nativeRecorderFps: 25 }));
    } finally { await browser.close(); }
    return;
  }
  if (argv.length !== 2 || !["capture", "frames", "footage", "jellyfin"].includes(argv[0])) throw new Error("usage: worker.mjs capture|frames|footage|jellyfin CONFIG.json");
  const config = JSON.parse(await fs.readFile(argv[1], "utf8"));
  await fs.mkdir(config.outputDirectory, { recursive: true });
  if (argv[0] === "capture") await capture(config);
  else if (argv[0] === "jellyfin") {
    let text = "";
    for await (const chunk of process.stdin) { text += chunk; if (text.length > 8192) throw new Error("capture.auth-limit"); }
    await captureJellyfin(config, chromium, JSON.parse(text));
  }
  else if (argv[0] === "footage") await renderFootage(config, chromium);
  else await frames(config);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main(process.argv.slice(2)).then(() => { if (!["info", "identity"].includes(process.argv[2])) console.log(JSON.stringify({ ok: true })); })
    .catch(error => { console.error(JSON.stringify({ ok: false, code: "worker.failed",
      message: process.argv[2] === "jellyfin"
        ? (String(error.message).startsWith("capture.network-denied:")
          ? String(error.message)
          : /^capture\.[a-z0-9-]+/.exec(String(error.message))?.[0] ?? "capture.worker-error: check pinned client profile/readiness; private call logs suppressed")
        : error.message })); process.exitCode = 1; });
}
