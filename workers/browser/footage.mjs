import { promises as fs } from "node:fs";
import path from "node:path";
import { createHash } from "node:crypto";

export function pixelRect(region, width = 1920, height = 1080) {
  const { x, y, width: w, height: h } = region;
  if (![x, y, w, h].every(Number.isFinite) || x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > 1 || y + h > 0.80)
    throw new Error("Invalid or caption-unsafe geometry");
  return { x: Math.round(x * width), y: Math.round(y * height), width: Math.round(w * width), height: Math.round(h * height) };
}

export function transformRect(region, crop) {
  if (!crop) return region;
  return { x: (region.x - crop.x) / crop.width, y: (region.y - crop.y) / crop.height,
    width: region.width / crop.width, height: region.height / crop.height };
}

export async function renderFootage(config, chromium) {
  if (config.map.length < 1 || config.map.length > 18000) throw new Error("Invalid footage map");
  const browser = await chromium.launch({ headless: true, executablePath: chromium.executablePath() });
  try {
    const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1, locale: "en-US" });
    await page.route("**/*", route => route.abort());
    const font = await fs.readFile(config.fontPath);
    if (createHash("sha256").update(font).digest("hex") !== config.fontSha256) throw new Error("Font mismatch");
    await page.setContent(`<!doctype html><meta charset="utf-8"><meta http-equiv="Content-Security-Policy"
      content="default-src 'none'; img-src data:; font-src data:; style-src 'unsafe-inline'">
      <style>@font-face{font-family:DemoMono;src:url(data:font/ttf;base64,${font.toString("base64")})}
      body{margin:0;background:#000}canvas{display:block}</style><canvas width="1920" height="1080"></canvas>`);
    await page.evaluate(async () => { await document.fonts.load("28px DemoMono"); await document.fonts.ready; });
    const cache = new Map();
    for (let frame = 0; frame < config.map.length; frame++) {
      const source = config.map[frame].sourceFrame;
      const effects = config.edit.effects.filter(e => frame >= e.startFrame && frame < e.endFrame);
      const state = { source, effects, preview: config.preview };
      const key = JSON.stringify(state);
      const target = path.join(config.outputDirectory, `${String(frame).padStart(6, "0")}.png`);
      if (cache.has(key)) { await fs.link(cache.get(key), target); continue; }
      const input = path.join(config.inputDirectory, `${String(source - config.edit.startFrame).padStart(6, "0")}.png`);
      const png = await fs.readFile(input);
      const anchors = config.ledger.anchors.filter(a => effects.some(e => e.anchorId === a.id));
      await page.evaluate(async ({ image, effects, anchors, preview }) => {
        const canvas = document.querySelector("canvas");
        const ctx = canvas.getContext("2d");
        const img = new Image();
        img.src = image;
        await img.decode();
        const zoom = effects.find(e => e.kind === "zoom");
        const crop = zoom?.expectedRegion ?? { x: 0, y: 0, width: 1, height: 1 };
        ctx.drawImage(img, crop.x * 1920, crop.y * 1080, crop.width * 1920, crop.height * 1080, 0, 0, 1920, 1080);
        for (const effect of effects.filter(e => e.kind !== "zoom")) {
          const r = effect.expectedRegion;
          const rect = { x: (r.x - crop.x) / crop.width * 1920, y: (r.y - crop.y) / crop.height * 1080,
            w: r.width / crop.width * 1920, h: r.height / crop.height * 1080 };
          if (rect.x < 0 || rect.y < 0 || rect.x + rect.w > 1920 || rect.y + rect.h > 864)
            throw new Error("Transformed target outside viewport/caption-safe region");
          const anchor = anchors.find(a => a.id === effect.anchorId);
          ctx.lineWidth = 5; ctx.strokeStyle = "#ffdf70"; ctx.fillStyle = "#ffdf70";
          if (effect.kind === "dim") {
            ctx.fillStyle = "rgba(0,0,0,0.48)";
            ctx.fillRect(0, 0, 1920, rect.y);
            ctx.fillRect(0, rect.y, rect.x, rect.h);
            ctx.fillRect(rect.x + rect.w, rect.y, 1920 - rect.x - rect.w, rect.h);
            ctx.fillRect(0, rect.y + rect.h, 1920, 864 - rect.y - rect.h);
          } else if (effect.kind === "highlight") {
            ctx.strokeRect(rect.x + 3, rect.y + 3, rect.w - 6, rect.h - 6);
          } else if (effect.kind === "callout") {
            ctx.font = "28px DemoMono";
            const maxWidth = 1600;
            const width = Math.min(maxWidth, ctx.measureText(effect.text).width + 36);
            if (ctx.measureText(effect.text).width + 36 > maxWidth) throw new Error("Callout text overflows");
            const x = Math.max(24, Math.min(rect.x, 1920 - width - 24));
            const y = rect.y >= 90 ? rect.y - 70 : rect.y + rect.h + 14;
            if (y + 54 > 864) throw new Error("Callout overlaps captions");
            ctx.fillStyle = "#111827"; ctx.fillRect(x, y, width, 54);
            ctx.strokeRect(x, y, width, 54);
            ctx.fillStyle = "#ffdf70"; ctx.fillText(effect.text, x + 18, y + 36);
          } else if (effect.kind === "pointer" || effect.kind === "click") {
            const x = (anchor.clickX - crop.x) / crop.width * 1920;
            const y = (anchor.clickY - crop.y) / crop.height * 1080;
            ctx.beginPath(); ctx.arc(x, y, effect.kind === "click" ? 23 : 10, 0, Math.PI * 2);
            ctx.stroke();
          } else throw new Error("Unknown footage effect");
        }
        if (effects.length || preview) {
          ctx.font = "22px DemoMono"; ctx.fillStyle = "#ffdf70"; ctx.fillText(
            preview ? "REVIEW-ONLY PREVIEW - NOT RELEASE" : "POSTPRODUCTION FOCUS - ORIGINAL TAKE PRESERVED", 36, 40);
        }
      }, { image: `data:image/png;base64,${png.toString("base64")}`, effects, anchors, preview: config.preview });
      await page.screenshot({ path: target, animations: "disabled" });
      cache.set(key, target);
    }
  } finally { await browser.close(); }
}
