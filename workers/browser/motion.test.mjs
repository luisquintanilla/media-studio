import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs/promises";
import path from "node:path";
import { createHash } from "node:crypto";
import { chromium } from "playwright";
import { validateMotion, motionAt, motionDocument, runtimeAssets, seekMotion } from "./motion.mjs";
import { stateAt, htmlAt, assertTemplateFits, main } from "./worker.mjs";

const motion = JSON.parse(await fs.readFile(new URL("../../fixtures/rain-motion.json", import.meta.url), "utf8"));
const scene = { id: "rain", kind: "diagram", title: "How rain forms", text: "", effects: [], motion, fontBase64: "" };
const frames = 150;

test("bounded inert motion rejects incompatible, unknown, invisible and malformed data", () => {
  validateMotion(scene, 60);
  validateMotion(scene, 18000);
  for (const frames of [59, 18001, NaN, 60.5]) assert.throws(() => validateMotion(scene, frames), /motion.duration/);
  for (const mutate of [
    s => s.kind = "title", s => s.visualAssetId = "asset", s => s.visual = {},
    s => s.effects = [{ kind: "fade" }], s => s.motion.html = "<script>",
    s => s.motion.entities[0].id = "bad' selector", s => s.motion.entities[1].id = "sun",
    s => s.motion.entities[0].shape = "javascript", s => s.motion.entities[0].width = 241,
    s => s.motion.entities[0].keys[0].x = 0, s => s.motion.entities[0].keys[0].opacity = NaN,
    s => s.motion.entities[0].keys[0].percent = 1, s => s.motion.entities[0].keys[1].percent = 99,
    s => s.motion.entities[0].keys[0].x = 230.5, s => s.motion.entities[0].keys[0].expression = "evil",
    s => s.motion.stages[0].percent = 1, s => s.motion.stages[1].percent = 0,
    s => s.motion.stages[1].caption = "", s => s.motion.entities = [],
    s => s.motion.entities.forEach(e => e.keys.forEach(k => k.opacity = 0)),
    s => s.motion.entities.forEach(e => e.keys = [e.keys[0], { ...e.keys[0], percent: 100 }])
  ]) {
    const invalid = structuredClone(scene); mutate(invalid);
    assert.throws(() => validateMotion(invalid, frames), /motion\./);
  }
  for (const percent of [-1, 101, NaN, Infinity]) assert.throws(() => motionAt(motion, percent), /motion.seek/);
  assert.equal(motionAt(motion, 100).find(e => e.id === "rain").y, 490);
  assert.equal(motionAt(motion, 60).find(e => e.id === "droplet-left").x, 675);
  assert.deepEqual(stateAt(scene, 149, frames).motion, motionAt(motion, 100));
  assert.throws(() => htmlAt(scene, stateAt(scene, 0, frames), true), /motion.renderer/);
});

test("C# and JS motion interpolation use the same fixture and every sampled pose", {
  skip: !process.env.MOTION_CSHARP_POSES
}, async () => {
  const poses = JSON.parse(await fs.readFile(process.env.MOTION_CSHARP_POSES, "utf8"));
  for (const sample of poses) assert.deepEqual(motionAt(motion, sample.percent), sample.poses);
});

test("only pinned runtime scripts execute; authored markup stays inert", async () => {
  const inert = structuredClone(scene);
  inert.title = '</script><img src="https://invalid.example" onerror="alert(1)">';
  inert.motion.entities[0].label = "<svg onload=evil>";
  const html = motionDocument(inert, frames, true, await runtimeAssets());
  assert.ok(html.includes("&lt;/script&gt;&lt;img"));
  assert.ok(html.includes("&lt;svg onload=evil&gt;"));
  assert.ok(html.includes("\\u003csvg onload=evil>"));
  assert.ok(!html.includes(inert.title));
  assert.ok(html.includes('data-duration="5"'));
  assert.ok(html.includes("connect-src 'none'"));
  assert.ok(html.includes("window.__player") && html.includes("window.__timelines.root=tl"));
});

test("native HyperFrames/GSAP seeks agree, reverse/repeated seeks are identical, actual worker exports every frame", {
  skip: !process.env.VIDEO_TEST_FONT, timeout: 180_000
}, async () => {
  const root = process.env.MOTION_PROOF_ROOT
    ? path.resolve(process.env.MOTION_PROOF_ROOT)
    : await fs.mkdtemp(path.join(path.resolve("../../.local"), "motion-test-"));
  await fs.mkdir(root, { recursive: true });
  const font = await fs.readFile(process.env.VIDEO_TEST_FONT);
  const browser = await chromium.launch({ headless: true });
  const observations = [], errors = [], network = [];
  try {
    const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 });
    await page.route("**/*", route => { network.push(route.request().url()); return route.abort(); });
    page.on("pageerror", error => errors.push(error.message));
    await page.setContent(motionDocument({ ...scene, fontBase64: font.toString("base64") }, frames, true, await runtimeAssets()));
    await page.evaluate(() => document.fonts.ready);
    await page.waitForFunction(() => window.__renderReady && window.__playerReady);
    for (const frame of [0, 74, 149, 74, 0, 149]) {
      const observed = await seekMotion(page, frame);
      assert.equal(observed.playing, false);
      assert.ok(Math.abs(observed.time - frame / 30) < .001);
      const expected = motionAt(motion, frame * 100 / (frames - 1));
      for (let i = 0; i < expected.length; i++) {
        assert.equal(observed.entities[i].id, expected[i].id);
        for (const key of ["x", "y", "opacity"])
          assert.ok(Math.abs(observed.entities[i][key] - expected[i][key]) < .001,
            `${frame}: ${expected[i].id}.${key}: ${observed.entities[i][key]} != ${expected[i][key]}`);
      }
      const captions = await page.locator(".caption").evaluateAll(elements =>
        elements.filter(e => Number(getComputedStyle(e).opacity) > .99).map(e => e.textContent));
      const percent = frame * 100 / (frames - 1);
      assert.deepEqual(captions, [motion.stages.filter(s => s.percent <= percent).at(-1).caption]);
      await assertTemplateFits(page, scene, frame);
      const bytes = await page.screenshot({ path: path.join(root, `seek-${observations.length}-${frame}.png`), animations: "allow" });
      observations.push({ frame, sha256: createHash("sha256").update(bytes).digest("hex"), observed });
    }
    for (const frame of [0, 74, 149]) {
      const repeated = observations.filter(o => o.frame === frame);
      assert.equal(repeated[0].sha256, repeated[1].sha256);
    }
    assert.notEqual(observations[0].sha256, observations[1].sha256);
    assert.notEqual(observations[1].sha256, observations[2].sha256);
    assert.deepEqual(errors, []); assert.deepEqual(network, []);
  } finally { await browser.close(); }
  const outputDirectory = path.join(root, "frames");
  const config = { frames: 60, scene, preview: true, outputDirectory,
    fontPath: path.resolve(process.env.VIDEO_TEST_FONT), fontSha256: createHash("sha256").update(font).digest("hex") };
  const file = path.join(root, "frames.json");
  await fs.writeFile(file, JSON.stringify(config));
  await main(["frames", file]);
  const exported = (await fs.readdir(outputDirectory)).filter(f => f.endsWith(".png")).sort();
  assert.equal(exported.length, 60);
  assert.equal(exported[0], "000000.png"); assert.equal(exported.at(-1), "000059.png");
  const hash = async file => createHash("sha256").update(await fs.readFile(file)).digest("hex");
  assert.notEqual(await hash(path.join(outputDirectory, exported[0])), await hash(path.join(outputDirectory, exported.at(-1))));
  await fs.writeFile(path.join(root, "receipt.json"), JSON.stringify({
    mode: "offline deterministic original fixture; no model invocation or narrated production",
    frames: 60, hyperframes: "0.8.33", gsap: "3.14.2", observations, errors, network
  }, null, 2));
});
