import fs from "node:fs/promises";
import { createHash } from "node:crypto";
import { chromium } from "playwright";

async function main() {
  if (process.argv.length !== 3) throw new Error("Expected one server-owned raster configuration");
  const config = JSON.parse(await fs.readFile(process.argv[2], "utf8"));
  if (typeof config.svg !== "string" || config.svg.length > 32000) throw new Error("Invalid bounded SVG");
  if (!Number.isSafeInteger(config.width) || config.width < 128 || config.width > 1920
      || !Number.isSafeInteger(config.height) || config.height < 128 || config.height > 1080) throw new Error("Invalid canvas");
  const font = await fs.readFile(config.fontPath);
  if (createHash("sha256").update(font).digest("hex") !== config.fontSha256) throw new Error("Pinned font differs");
  const browser = await chromium.launch({ headless: true, executablePath: chromium.executablePath() });
  try {
    const page = await browser.newPage({ viewport: { width: config.width, height: config.height }, deviceScaleFactor: 1, locale: "en-US" });
    await page.route("**/*", route => route.abort());
    await page.setContent(`<!doctype html><meta charset="utf-8"><meta http-equiv="Content-Security-Policy"
      content="default-src 'none'; img-src data:; font-src data:; style-src 'unsafe-inline'">
      <style>body{margin:0;background:transparent}svg{display:block}</style>`);
    const fontEvidence = await page.evaluate(async ({ svg, font }) => {
      const doc = new DOMParser().parseFromString(svg, "image/svg+xml");
      if (doc.querySelector("parsererror")) throw new Error("Malformed SVG");
      const allowed = new Set(["svg", "g", "rect", "circle", "line", "polyline", "polygon", "text"]);
      for (const node of doc.querySelectorAll("*")) {
        if (!allowed.has(node.localName)) throw new Error("Active/unsupported SVG element");
        for (const attr of node.attributes)
          if (/^on/i.test(attr.name) || /href|style|src|url|transform/i.test(attr.name)
              || /url\s*\(|https?:|data:/i.test(attr.value)) {
            if (attr.name !== "xmlns") throw new Error("Active/unsupported SVG attribute");
          }
      }
      const bytes = Uint8Array.from(atob(font), c => c.charCodeAt(0));
      const face = new FontFace("PinnedIllustration", bytes.buffer);
      await face.load();
      document.fonts.add(face);
      const root = document.importNode(doc.documentElement, true);
      for (const text of root.querySelectorAll("text")) text.style.fontFamily = "PinnedIllustration";
      document.body.append(root);
      await document.fonts.ready;
      const texts = [...root.querySelectorAll("text")];
      const pinnedFontApplied = face.status === "loaded" && document.fonts.has(face)
        && texts.every(text => getComputedStyle(text).fontFamily === "PinnedIllustration"
          && document.fonts.check(`${getComputedStyle(text).fontSize} PinnedIllustration`, text.textContent));
      if (!pinnedFontApplied) throw new Error("Pinned font did not load/apply");
      return { fontStatus: face.status, textElementCount: texts.length, pinnedFontApplied };
    }, { svg: config.svg, font: font.toString("base64") });
    await page.screenshot({ path: config.outputFile, animations: "disabled", omitBackground: true });
    console.log(JSON.stringify({ browserVersion: browser.version(), ...fontEvidence }));
  } finally { await browser.close(); }
}
main().catch(() => { console.error("illustration.raster-failed"); process.exitCode = 1; });
