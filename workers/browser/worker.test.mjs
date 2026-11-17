import test from "node:test";
import assert from "node:assert/strict";
import { escapeHtml, stateAt, htmlAt, assertTemplateFits, main, writeRendererFailure } from "./worker.mjs";
import { chromium } from "playwright";
import { promises as fs } from "node:fs";
import path from "node:path";
import { createHash } from "node:crypto";
import os from "node:os";

test("private renderer failure retains observed Chromium crash/stderr and bounds the original message", async () => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), "renderer-diagnostic-"));
  try {
    const observation = { sceneId: "scene-01", frame: 15, stage: "capturing",
      browserDisconnected: true, disconnectedBeforeCleanup: true,
      targetCrashes: [{ targetId: "owned-target", status: "crashed", errorCode: 17 }],
      pageErrors: [], cleanupError: "Cleanup detail" };
    const file = await writeRendererFailure(directory,
      new Error("page.screenshot: Target closed\n[pid=123][err] Synthetic Chromium crash detail"), observation);
    const retained = JSON.parse(await fs.readFile(file, "utf8"));
    assert.equal(retained.frame, 15);
    assert.deepEqual(retained.targetCrashes, observation.targetCrashes);
    assert.deepEqual(retained.chromiumStderr, ["[pid=123][err] Synthetic Chromium crash detail"]);
    assert.equal(retained.cleanupError, "Cleanup detail");
    await writeRendererFailure(directory, new Error("x".repeat(70000)), observation);
    assert.equal(JSON.parse(await fs.readFile(file, "utf8")).message.length, 64000);
  } finally { await fs.rm(directory, { recursive: true }); }
});

test("private renderer diagnostic survives a real worker failure without inferring a crash or retrying", {
  skip: !process.env.VIDEO_TEST_FONT
}, async () => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), "renderer-worker-failure-"));
  try {
    const config = path.join(directory, "input.json");
    await fs.writeFile(config, JSON.stringify({
      scene: { id: "owned-fixture" }, frames: 1, outputDirectory: directory,
      fontPath: process.env.VIDEO_TEST_FONT, fontSha256: "0".repeat(64)
    }));
    await assert.rejects(() => main(["frames", config]), /Font hash mismatch[\s\S]*Private renderer diagnostic:/);
    const retained = JSON.parse(await fs.readFile(path.join(directory, "renderer-failure.json"), "utf8"));
    assert.equal(retained.sceneId, "owned-fixture");
    assert.equal(retained.frame, null);
    assert.equal(retained.disconnectedBeforeCleanup, false);
    assert.deepEqual(retained.targetCrashes, []);
    assert.deepEqual(retained.chromiumStderr, []);
    assert.equal(retained.message, "Font hash mismatch");
    assert.ok(!(await fs.readdir(directory)).some(name => name.endsWith(".png")));
  } finally { await fs.rm(directory, { recursive: true }); }
});

const presets = ["architectureFlowEmphasis", "progressiveCodeFocus", "beforeAfterComparison"];
function treated(preset = presets[0]) {
  return { id: "visual-test", kind: preset === presets[1] ? "code" : "diagram", title: "Trusted visual",
    text: "Source\nResult", effects: [], fontBase64: "", visual: { preset,
      anchors: [{ id: "source", startLine: 1, endLine: 1 }, { id: "result", startLine: 2, endLine: 2 }],
      reveals: [{ targetId: "source", startFrame: 0, endFrame: 2 }, { targetId: "result", startFrame: 3, endFrame: 5 }] } };
}

test("text is inert; deterministic frame reveal and bounded effects", () => {
  assert.equal(escapeHtml('<script>&"'), "&lt;script&gt;&amp;&quot;");
  const scene = { kind: "code", title: "<img>", text: "one\ntwo\nthree", fontBase64: "",
    effects: [{ kind: "highlight", startFrame: 5, endFrame: 9 }] };
  assert.equal(stateAt(scene, 0, 30).reveal, 1);
  assert.equal(stateAt(scene, 12, 30).reveal, 2);
  assert.equal(stateAt(scene, 8, 30).effects.length, 1);
  assert.equal(stateAt(scene, 9, 30).effects.length, 0);
  assert.throws(() => stateAt(scene, 30, 30));
  assert.ok(htmlAt(scene, stateAt(scene, 0, 30), true).includes("&lt;img&gt;"));
  assert.ok(htmlAt(scene, stateAt(scene, 0, 30), true).includes("REVIEW-ONLY"));
  const fading = { ...scene, effects: [{ kind: "fade", startFrame: 12, endFrame: 24 }] };
  assert.equal(stateAt(fading, 12, 30).fade, 1 / 12);
  assert.equal(stateAt(fading, 23, 30).fade, 1 / 12);
  assert.equal(stateAt(fading, 24, 30).fade, 1);
});

test("all three treatments share deterministic scene-local state and stable anchors", () => {
  for (const preset of presets) {
    const scene = treated(preset);
    const first = stateAt(scene, 0, 7);
    assert.deepEqual(first.visual.anchors.map(a => [a.id, a.visible, a.active]),
      [["source", true, true], ["result", false, false]]);
    assert.deepEqual(stateAt(scene, 2, 7).visual.anchors.map(a => [a.visible, a.active]), [[true, false], [false, false]]);
    assert.deepEqual(stateAt(scene, 3, 7).visual.anchors.map(a => [a.visible, a.active]), [[true, false], [true, true]]);
    assert.deepEqual(stateAt(scene, 5, 7).visual.anchors.map(a => [a.visible, a.active]), [[true, false], [true, false]]);
    assert.deepEqual(stateAt(scene, 6, 7), stateAt(scene, 5, 7));
    const final = htmlAt(scene, first, false);
    const preview = htmlAt(scene, first, true);
    assert.equal(preview.replace('<div class="preview">REVIEW-ONLY PREVIEW - NOT RELEASE</div>', ""), final);
    for (const anchor of scene.visual.anchors) assert.ok(final.includes(`id="visual-${anchor.id}"`));
    assert.deepEqual(stateAt(scene, 3, 7), stateAt(scene, 3, 7));
  }
});

test("absent or explicit null visual keeps legacy scene-state and HTML identical", () => {
  for (const kind of ["title", "diagram", "code", "terminal"]) {
    const scene = { kind, title: "Old default", text: "One\nTwo\nThree", effects: [], fontBase64: "" };
    for (const frame of [0, 12, 24]) {
      const state = stateAt(scene, frame, 30);
      const explicitNull = { ...scene, visual: null };
      assert.deepEqual(stateAt(explicitNull, frame, 30), state);
      assert.equal(htmlAt(explicitNull, state, false), htmlAt(scene, state, false));
      assert.ok(!Object.hasOwn(state, "visual"));
    }
  }
});

test("worker independently rejects untrusted visual fields, kinds, targets, timing and overflow", () => {
  const invalid = [];
  const change = mutate => { const s = treated(); mutate(s); invalid.push(s); };
  change(s => s.visual.preset = "javascript");
  change(s => s.visual.html = "<script>");
  change(s => s.kind = "media");
  change(s => s.visual.anchors[0].id = "x\" onclick=\"evil");
  change(s => s.visual.anchors[1].id = "source");
  change(s => s.visual.anchors[1].startLine = 1);
  change(s => s.visual.anchors[0].endLine = 2);
  change(s => s.visual.reveals[0].startFrame = 1);
  change(s => s.visual.reveals[1].targetId = "missing");
  change(s => s.visual.reveals[1].startFrame = 1);
  change(s => s.visual.reveals[1].endFrame = 8);
  change(s => s.visual.reveals[1].startFrame = 3.5);
  change(s => s.visual.reveals[1].endFrame = Number.MAX_SAFE_INTEGER);
  change(s => s.visual.reveals[1].endFrame = 3);
  change(s => s.text = `${"x".repeat(49)}\nResult`);
  change(s => s.text = "Source\t\nResult");
  change(s => s.text = "Source\u0000\nResult");
  change(s => s.visual.anchors = null);
  change(s => s.visual.reveals = []);
  for (const scene of invalid) assert.throws(() => stateAt(scene, 0, 7), /visual\./);
  const inert = treated();
  inert.title = '<script>alert("x")</script>';
  inert.text = "<img onerror=alert(1)>\nResult";
  const html = htmlAt(inert, stateAt(inert, 0, 7), false);
  assert.ok(html.includes("&lt;script&gt;") && html.includes("&lt;img onerror=alert(1)&gt;"));
  assert.ok(!html.includes("<script>") && !html.includes("<img "));
});

test("flow connectivity follows every sequential node across row turns without moving revealed nodes",
  { skip: !process.env.VIDEO_TEST_FONT }, async () => {
    const font = await fs.readFile(process.env.VIDEO_TEST_FONT);
    const browser = await chromium.launch({ headless: true, executablePath: chromium.executablePath() });
    try {
      const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 });
      await page.route("**/*", route => route.abort());
      for (let count = 2; count <= 6; count++) {
        const scene = { ...treated(), text: Array.from({ length: count }, (_, i) => `Stage ${i + 1}`).join("\n"),
          fontBase64: font.toString("base64") };
        scene.visual.anchors = Array.from({ length: count }, (_, i) =>
          ({ id: `stage-${i}`, startLine: i + 1, endLine: i + 1 }));
        scene.visual.reveals = scene.visual.anchors.map((a, i) =>
          ({ targetId: a.id, startFrame: i, endFrame: i + 1 }));
        let boxes;
        for (const frame of [0, 6]) {
          await page.setContent(htmlAt(scene, stateAt(scene, frame, 7), false));
          await page.evaluate(() => document.fonts.ready);
          await assertTemplateFits(page, scene, frame);
          const actual = await page.locator(".node").evaluateAll(nodes =>
            nodes.map(node => node.getBoundingClientRect().toJSON()));
          if (boxes) assert.deepEqual(actual, boxes); else boxes = actual;
          if (frame === 0)
            assert.ok(await page.locator(".connector").evaluateAll(edges =>
              edges.every(edge => getComputedStyle(edge).visibility === "hidden")));
        }
        const edges = await page.locator(".connector").evaluateAll(elements => elements.map(edge => ({
          direction: edge.dataset.direction, from: edge.dataset.flowFrom, to: edge.dataset.flowTo,
          box: edge.getBoundingClientRect().toJSON(),
          source: document.getElementById(`visual-${edge.dataset.flowFrom}`).getBoundingClientRect().toJSON(),
          target: document.getElementById(`visual-${edge.dataset.flowTo}`).getBoundingClientRect().toJSON()
        })));
        assert.equal(edges.length, count - 1);
        const near = (a, b) => assert.ok(Math.abs(a - b) < 1, `${a} must connect to ${b}`);
        edges.forEach((edge, i) => {
          assert.equal(edge.from, `stage-${i}`);
          assert.equal(edge.to, `stage-${i + 1}`);
          assert.equal(edge.direction, i === 2 ? "down" : i < 2 ? "right" : "left");
          if (edge.direction === "down") {
            near(edge.box.top, edge.source.bottom);
            near(edge.box.bottom, edge.target.top);
            near(edge.box.x + edge.box.width / 2, edge.source.x + edge.source.width / 2);
            near(edge.source.x, edge.target.x);
          } else {
            near(edge.box.left, Math.min(edge.source.right, edge.target.right));
            near(edge.box.right, Math.max(edge.source.left, edge.target.left));
            near(edge.box.y + edge.box.height / 2, edge.source.y + edge.source.height / 2);
            near(edge.source.y, edge.target.y);
          }
        });
        const final = await page.screenshot({ clip: { x: 0, y: 0, width: 1920, height: 970 } });
        await page.setContent(htmlAt(scene, stateAt(scene, 6, 7), true));
        await page.evaluate(() => document.fonts.ready);
        assert.deepEqual(await page.screenshot({ clip: { x: 0, y: 0, width: 1920, height: 970 } }), final);
      }
    } finally { await browser.close(); }
  });

test("browser observes invariant layout, preview/final visual parity, frame output and overflow rejection",
  { skip: !process.env.VIDEO_TEST_FONT }, async () => {
    const directory = path.resolve("artifacts", `visual-browser-${process.pid}`);
    await fs.mkdir(directory, { recursive: true });
    const fontPath = process.env.VIDEO_TEST_FONT;
    const font = await fs.readFile(fontPath);
    const browser = await chromium.launch({ headless: true, executablePath: chromium.executablePath() });
    try {
      const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 });
      await page.route("**/*", route => route.abort());
      for (const preset of presets) {
        const scene = { ...treated(preset), fontBase64: font.toString("base64") };
        let boxes;
        for (const frame of [0, 2, 3, 5, 6]) {
          const state = stateAt(scene, frame, 7);
          await page.setContent(htmlAt(scene, state, false));
          await page.evaluate(() => document.fonts.ready);
          assert.ok(await page.evaluate(() => document.fonts.check("26px DemoMono")));
          await assertTemplateFits(page, scene, frame);
          const actual = await page.locator('[id^="visual-"]').evaluateAll(elements => elements.map(e => {
            const r = e.getBoundingClientRect();
            return { id: e.id, x: r.x, y: r.y, width: r.width, height: r.height };
          }));
          if (boxes) assert.deepEqual(actual, boxes); else boxes = actual;
          const final = await page.screenshot({ clip: { x: 0, y: 0, width: 1920, height: 970 } });
          await page.setContent(htmlAt(scene, state, true));
          await page.evaluate(() => document.fonts.ready);
          assert.deepEqual(await page.screenshot({ clip: { x: 0, y: 0, width: 1920, height: 970 } }), final);
        }
      }
      for (const preset of presets) {
        const scene = { ...treated(preset), title: "W".repeat(100), fontBase64: font.toString("base64") };
        const lineCount = preset === presets[0] ? 6 : preset === presets[1] ? 14 : 20;
        const columns = preset === presets[0] ? 48 : preset === presets[1] ? 88 : 42;
        scene.text = Array(lineCount).fill("W".repeat(columns)).join("\n");
        scene.visual.anchors = preset === presets[0]
          ? Array.from({ length: 6 }, (_, i) => ({ id: `node-${i}`, startLine: i + 1, endLine: i + 1 }))
          : [{ id: "source", startLine: 1, endLine: lineCount / 2 },
            { id: "result", startLine: lineCount / 2 + 1, endLine: lineCount }];
        scene.visual.reveals = scene.visual.anchors.map((a, i) => ({ targetId: a.id, startFrame: i, endFrame: i + 1 }));
        await page.setContent(htmlAt(scene, stateAt(scene, 6, 7), false));
        await page.evaluate(() => document.fonts.ready);
        await assertTemplateFits(page, scene, 6);
        if (preset === presets[1])
          assert.equal((await page.locator("#visual-source").boundingBox()).height, 7 * 38);
      }
      const scene = { ...treated(), title: "W\n".repeat(50), fontBase64: font.toString("base64") };
      await page.setContent(htmlAt(scene, stateAt(scene, 0, 7), false));
      await page.evaluate(() => document.fonts.ready);
      await assert.rejects(() => assertTemplateFits(page, scene, 0), /Template overflow/);
      await browser.close();
      for (const preset of presets) {
        const scene = treated(preset);
        const configFile = path.join(directory, preset + ".json");
        const outputDirectory = path.join(directory, preset);
        await fs.writeFile(configFile, JSON.stringify({ scene, frames: 7, preview: false, outputDirectory,
          fontPath, fontSha256: createHash("sha256").update(font).digest("hex") }));
        await main(["frames", configFile]);
        assert.equal((await fs.readdir(outputDirectory)).length, 7);
        assert.deepEqual(await fs.readFile(path.join(outputDirectory, "000005.png")),
          await fs.readFile(path.join(outputDirectory, "000006.png")));
      }
    } finally { await browser.close(); await fs.rm(directory, { recursive: true, force: true }); }
  });
