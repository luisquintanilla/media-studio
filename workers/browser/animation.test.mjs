import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs/promises";
import path from "node:path";
import { createHash } from "node:crypto";
import { validateScene, makeDocument, render } from "./animation.mjs";

const scene = { schemaVersion: 1, frames: 120, kind: "cards",
  title: "A useful capability", rows: [{ label: "Request -> reviewed result" }] };

test("trusted animation scene data has bounded frames, code, rows and explicit map nodes", () => {
  assert.equal(validateScene(scene), scene);
  for (const frames of [0, 29, 1801, 120.5, NaN])
    assert.throws(() => validateScene({ ...scene, frames }));
  assert.throws(() => validateScene({ ...scene, kind: "javascript" }));
  assert.throws(() => validateScene({ ...scene, code: "x\n".repeat(17) }));
  assert.throws(() => validateScene({ ...scene, rows: [{ label: "bad", value: -1 }] }));
  assert.throws(() => validateScene({ ...scene, rows: [{ label: "bad", value: 101 }] }));
  assert.throws(() => validateScene({ ...scene, map: true, active: [6] }));
  assert.throws(() => validateScene({ ...scene, rows: [] }));
  assert.throws(() => validateScene({ ...scene, kind: "code" }));
  assert.throws(() => validateScene({ ...scene, decimals: -1 }));
  assert.throws(() => validateScene({ ...scene, note: {} }));
});

test("authored text/code stays inert; only code-owned timelines execute", async () => {
  const attack = '</script><img src="https://invalid.example" onerror="alert(1)">';
  const html = await makeDocument({ ...scene, title: attack, rows: [{ label: attack }] },
    { font: "", gsap: "/* trusted pinned runtime */", hyperframes: "/* trusted pinned runtime */" });
  assert.ok(html.includes("&lt;/script&gt;&lt;img"));
  assert.ok(!html.includes(attack));
  assert.ok(html.includes("timeline({paused:true})"));
  assert.ok(html.includes("window.__timelines.root=tl"));
  assert.ok(html.includes("connect-src 'none'"));
  assert.ok(!html.includes("https://cdn"));
});

test("frame-derived duration, fixed map and explicit labels survive document compilation", async () => {
  const html = await makeDocument({ ...scene, frames: 900, map: true, active: [2, 5],
    location: "filtered retrieval", breadcrumb: "Author > Produce > FIND" },
  { font: "", gsap: "", hyperframes: "" });
  assert.ok(html.includes('data-duration="30"'));
  assert.ok(html.includes("YOU ARE HERE: filtered retrieval"));
  assert.equal((html.match(/<small>ACTIVE<\/small>/g) ?? []).length, 2);
  assert.ok(html.includes("Author &gt; Produce &gt; FIND"));
});

test("actual pinned HyperFrames render is seekable and encoder failure is explicit", {
  skip: !process.env.VIDEO_ANIMATION_RUNTIME || !process.env.VIDEO_TEST_FONT || !process.env.VIDEO_FFMPEG,
  timeout: 120_000
}, async () => {
  const parent = path.resolve("artifacts");
  await fs.mkdir(parent, { recursive: true });
  const directory = await fs.mkdtemp(path.join(parent, "animation-test-"));
  const font = await fs.readFile(process.env.VIDEO_TEST_FONT);
  const config = {
    scene: { ...scene, kind: "chart", frames: 30, rows: [{ label: "Baseline", value: 53.38 }, { label: "Comparison", value: 58.04 }],
      note: "Real runtime test; deterministic authored data, not a model invocation." },
    runtimeReceipt: process.env.VIDEO_ANIMATION_RUNTIME,
    font: { file: process.env.VIDEO_TEST_FONT, sha256: createHash("sha256").update(font).digest("hex") },
    ffmpeg: process.env.VIDEO_FFMPEG, output: path.join(directory, "valid")
  };
  try {
    await render(config);
    const receipt = JSON.parse(await fs.readFile(path.join(config.output, "render-receipt.json"), "utf8"));
    assert.equal(receipt.hyperframes, "0.8.33");
    assert.equal(receipt.frames, 30);
    assert.equal(receipt.observations[1].sha256, receipt.observations[3].sha256);
    assert.deepEqual(receipt.layout.overflowing, []);
    assert.ok(Math.abs(receipt.layout.chartAxis.zeroX - receipt.layout.chartAxis.trackLeft) <= 1);
    assert.ok(Math.abs(receipt.layout.chartAxis.hundredRight - receipt.layout.chartAxis.trackRight) <= 1);
    assert.deepEqual(receipt.pageErrors, []);
    await assert.rejects(() => render({ ...config, ffmpeg: path.join(directory, "missing-encoder.exe"),
      output: path.join(directory, "invalid-encoder") }), /animation.encode:/);
  } finally { await fs.rm(directory, { recursive: true, force: true }); }
});
