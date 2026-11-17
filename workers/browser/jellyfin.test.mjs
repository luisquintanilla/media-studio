import test from "node:test";
import assert from "node:assert/strict";
import { permittedRequest, validateOrigin, scrollResultIntoView } from "./jellyfin.mjs";
import { chromium } from "playwright";

test("reviewed native capture only permits exact loopback origin, pinned assets and bounded endpoints", () => {
  const p = {
    origin: "http://127.0.0.1:12345", userId: "1".repeat(32),
    webAssets: [{ path: "web/index.html" }, { path: "web/search.pinned.chunk.js" }, { path: "web/node_modules.@jellyfin.sdk.bundle.js" }],
    allowedApiPaths: ["/System/Info/Public", "/Items", "/Users/AuthenticateByName", "/DisplayPreferences/usersettings"]
  };
  assert.equal(validateOrigin(p.origin).origin, p.origin);
  for (const origin of ["https://127.0.0.1:12345", "http://localhost:12345", "http://127.0.0.1:80",
    "http://127.0.0.1:12345/", "http://127.0.0.1:12345/path", "http://user:pass@127.0.0.1:12345"])
    assert.throws(() => validateOrigin(origin));
  assert.equal(permittedRequest(p.origin + "/web/index.html", "GET", p), true);
  assert.equal(permittedRequest(p.origin + "/web/node_modules.%40jellyfin.sdk.bundle.js", "GET", p), true);
  assert.equal(permittedRequest(p.origin + "/web/search.unreviewed.chunk.js", "GET", p), false);
  assert.equal(permittedRequest(p.origin + "/web/trace.har", "GET", p), false);
  assert.equal(permittedRequest("http://127.0.0.1:12346/Items", "GET", p), false);
  assert.equal(permittedRequest("https://example.invalid/Items", "GET", p), false);
  assert.equal(permittedRequest(p.origin + "/Items?SearchTerm=actual", "GET", p), true);
  assert.equal(permittedRequest(p.origin + "/Items", "POST", p), false);
  assert.equal(permittedRequest(p.origin + "/Users/AuthenticateByName", "POST", p), true);
  assert.equal(permittedRequest(p.origin + "/Plugins/Admin/Rebuild", "POST", p), false);
});

test("real native scrolling exposes a below-fold card without changing its identity or caption-safe guards", async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, reducedMotion: "reduce" });
    await page.setContent(`<style>body{margin:0}.scroll{height:1080px;overflow:auto}
      .spacer{height:1200px}.card{height:240px;width:320px}</style>
      <div class="scroll"><div class="spacer"></div><div class="card" data-id="actual-fixture-id">
      <a href="#/details?id=actual-fixture-id">Native fixture title</a></div><div class="spacer"></div></div>`);
    const card = page.locator(".card");
    assert.ok((await card.boundingBox()).y > 1080);
    const events = [];
    await scrollResultIntoView(card, (event, box) => events.push({ event, box }));
    assert.deepEqual(events.map(e => e.event), ["native-result-scroll-start", "native-result-scroll-end"]);
    const box = await card.boundingBox();
    assert.ok(box.y >= 0 && box.y + box.height <= 864);
    assert.ok(await page.locator(".scroll").evaluate(element => element.scrollTop) > 0);
    assert.equal(await card.getAttribute("data-id"), "actual-fixture-id");
    assert.equal(await card.locator("a").getAttribute("href"), "#/details?id=actual-fixture-id");
    await scrollResultIntoView(card, () => assert.fail("An already safe card must not scroll"));
    await page.locator(".spacer").first().evaluate(element => element.style.height = "800px");
    await page.locator(".scroll").evaluate(element => element.scrollTop = 0);
    assert.equal((await card.boundingBox()).y, 800);
    await scrollResultIntoView(card, () => {});
    assert.ok((await card.boundingBox()).y + (await card.boundingBox()).height <= 864);
    await card.evaluate(element => element.style.height = "900px");
    await assert.rejects(scrollResultIntoView(card, () => assert.fail("Oversize target must fail before scrolling")),
      /capture.target-geometry/);
  } finally { await browser.close(); }
});
