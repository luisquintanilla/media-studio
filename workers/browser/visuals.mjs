const presets = ["architectureFlowEmphasis", "progressiveCodeFocus", "beforeAfterComparison"];
const id = /^[a-z][a-z0-9-]{0,63}$/;
const exact = (value, keys) => value && typeof value === "object" && !Array.isArray(value)
  && Object.keys(value).length === keys.length && keys.every(k => Object.hasOwn(value, k));
const require = (condition, code) => { if (!condition) throw new Error(code); };

export function validateVisual(scene, frames) {
  const visual = scene.visual;
  if (visual == null) return;
  require(exact(visual, ["preset", "anchors", "reveals"]), "visual.fields");
  const architecture = visual.preset === presets[0];
  const code = visual.preset === presets[1];
  const comparison = visual.preset === presets[2];
  require(presets.includes(visual.preset) && (architecture && scene.kind === "diagram"
    || code && scene.kind === "code" || comparison && ["code", "diagram"].includes(scene.kind)), "visual.preset");
  const lines = scene.text.split("\n");
  require(lines.length <= (architecture ? 6 : comparison ? 20 : 14)
    && lines.every(l => l.length <= (architecture ? 48 : comparison ? 42 : 88)
      && !/[\u0000-\u001f\u007f-\u009f\uD800-\uDFFF]/u.test(l)), "visual.overflow");
  require(Array.isArray(visual.anchors) && visual.anchors.length >= (architecture || comparison ? 2 : 1)
    && visual.anchors.length <= (architecture ? 6 : comparison ? 2 : 8), "visual.anchors");
  let previousLine = 0;
  const ids = new Set();
  for (const anchor of visual.anchors) {
    require(exact(anchor, ["id", "startLine", "endLine"]) && typeof anchor.id === "string" && id.test(anchor.id)
      && !ids.has(anchor.id) && Number.isSafeInteger(anchor.startLine) && Number.isSafeInteger(anchor.endLine)
      && anchor.startLine > previousLine && anchor.endLine >= anchor.startLine && anchor.endLine <= lines.length
      && (!architecture || anchor.startLine === anchor.endLine)
      && (!comparison || anchor.endLine - anchor.startLine < 10)
      && (!(architecture || comparison) || anchor.startLine === previousLine + 1), "visual.anchors");
    previousLine = anchor.endLine;
    ids.add(anchor.id);
  }
  require(!(architecture || comparison) || previousLine === lines.length, "visual.anchors");
  require(Array.isArray(visual.reveals) && visual.reveals.length === visual.anchors.length, "visual.reveal");
  let previousEnd = 0;
  visual.reveals.forEach((reveal, i) => {
    require(exact(reveal, ["targetId", "startFrame", "endFrame"]) && reveal.targetId === visual.anchors[i].id
      && Number.isSafeInteger(reveal.startFrame) && Number.isSafeInteger(reveal.endFrame)
      && reveal.startFrame >= previousEnd && (i !== 0 || reveal.startFrame === 0)
      && reveal.endFrame > reveal.startFrame && reveal.endFrame <= 18000, "visual.reveal");
    require(reveal.endFrame <= frames, "visual.overrun");
    previousEnd = reveal.endFrame;
  });
}

export function visualAt(scene, frame) {
  return {
    preset: scene.visual.preset,
    anchors: scene.visual.anchors.map((anchor, i) => {
      const reveal = scene.visual.reveals[i];
      return { ...anchor, visible: frame >= reveal.startFrame,
        active: frame >= reveal.startFrame && frame < reveal.endFrame };
    })
  };
}

export function visualHtml(scene, state, preview, escape) {
  const visual = state.visual;
  const lines = scene.text.split("\n");
  const status = a => a.active ? "active" : a.visible ? "revealed" : "pending";
  const content = a => escape(lines.slice(a.startLine - 1, a.endLine).join("\n"));
  let body;
  if (visual.preset === presets[0]) {
    const position = i => ({ row: Math.floor(i / 3), column: i < 3 ? i : 5 - i });
    const width = (1720 - 80) / 3;
    const nodes = visual.anchors.map((a, i) => {
      const { row, column } = position(i);
      return `<article id="visual-${a.id}" data-visual-box class="node ${status(a)}" style="grid-row:${row + 1};grid-column:${column + 1}"><div class="step">${i + 1}</div><pre data-visual-box>${content(a)}</pre></article>`;
    }).join("");
    const connectors = visual.anchors.slice(0, -1).map((a, i) => {
      const source = position(i), target = position(i + 1);
      const direction = source.row !== target.row ? "down" : target.column > source.column ? "right" : "left";
      const left = direction === "down" ? source.column * (width + 40) + width / 2 - 20
        : Math.min(source.column, target.column) * (width + 40) + width;
      const top = direction === "down" ? 280 : source.row * 320 + 120;
      const arrow = direction === "down" ? "&#8595;" : direction === "right" ? "&#8594;" : "&#8592;";
      return `<span class="connector ${visual.anchors[i + 1].visible ? "" : "pending"}" data-visual-box data-flow-from="${a.id}" data-flow-to="${visual.anchors[i + 1].id}" data-direction="${direction}" style="left:${left}px;top:${top}px" aria-hidden="true">${arrow}</span>`;
    }).join("");
    body = `<section class="flow" data-visual-box>${nodes}${connectors}</section>`;
  } else if (visual.preset === presets[1]) {
    const row = (line, i, a) => `<div class="code-line ${a ? status(a) : "context"}" data-visual-box><span class="number">${i + 1}</span><pre data-visual-box>${escape(line) || "&#32;"}</pre></div>`;
    const groups = [];
    for (let i = 0; i < lines.length;) {
      const a = visual.anchors.find(a => a.startLine === i + 1);
      if (a) {
        groups.push(`<div id="visual-${a.id}" data-visual-box>${lines.slice(i, a.endLine).map((line, j) => row(line, i + j, a)).join("")}</div>`);
        i = a.endLine;
      } else {
        groups.push(row(lines[i], i, null));
        i++;
      }
    }
    body = `<section class="code" data-visual-box>${groups.join("")}</section>`;
  } else {
    body = `<section class="comparison" data-visual-box>${visual.anchors.map((a, i) =>
      `<article id="visual-${a.id}" class="panel ${status(a)}" data-visual-box><h2>${i ? "After" : "Before"}</h2><pre data-visual-box>${content(a)}</pre></article>`).join("")}</section>`;
  }
  const arrows = state.effects.filter(e => e.kind === "arrow").map(e =>
    `<span>${escape(e.text)} &#8594;</span>`).join("");
  return `<!doctype html><html lang="en"><meta charset="utf-8">
  <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; font-src data:">
  <style>@font-face{font-family:DemoMono;src:url("data:font/ttf;base64,${scene.fontBase64}")}
  *{box-sizing:border-box}body{margin:0;width:1920px;height:1080px;background:#101827;color:#f1f5ff;font:30px DemoMono,monospace}
  main{opacity:${state.fade}}h1{position:absolute;left:100px;top:76px;width:1720px;height:154px;margin:0;font:52px/1.3 DemoMono,monospace;color:#79dcff;white-space:pre-wrap;overflow-wrap:anywhere}
  section{position:absolute;left:100px;top:270px;width:1720px;height:620px}
  pre{margin:0;font:inherit;white-space:pre-wrap;overflow-wrap:anywhere}
  .flow{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));grid-auto-rows:280px;gap:40px}
  .node{position:relative;padding:28px 36px;border:4px solid #30415d;border-radius:16px;display:flex;align-items:center;gap:20px;min-width:0}
  .node pre{min-width:0}.step{color:#79dcff;font-size:26px}.connector{position:absolute;width:40px;height:40px;display:grid;place-items:center;color:#ffdf70}
  .node.pending,.panel.pending,.connector.pending{visibility:hidden}.active{border-color:#ffdf70!important;background:#23334a}
  .comparison{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:36px}
  .panel{border:4px solid #30415d;border-radius:16px;padding:28px;min-width:0}.panel pre{font:26px/1.5 DemoMono,monospace;white-space:pre}
  h2{color:#79dcff;margin:0 0 28px;font-size:36px}.code{padding:24px;border:4px solid #30415d;border-radius:16px}
  .code-line{display:flex;gap:24px;height:38px;border-left:4px solid transparent;padding-left:12px;font:26px/38px DemoMono,monospace}
  .code-line pre{white-space:pre}.number{flex:0 0 40px;color:#79dcff;text-align:right}.code-line.pending{opacity:.25}.code-line.context{opacity:.4}.code-line.revealed{opacity:.75}
  .notes{position:absolute;left:100px;top:920px;width:1720px;height:48px;color:#ffdf70;display:flex;gap:30px;white-space:nowrap}
  .preview{position:absolute;right:70px;bottom:48px;color:#ffdf70;font-size:30px}
  ${state.effects.some(e => e.kind === "highlight") ? "section{outline:4px solid #ffdf70}" : ""}</style>
  <main><h1 data-visual-box>${escape(scene.title)}</h1>${body}<div class="notes" data-visual-box>${arrows}</div></main>
  ${preview ? '<div class="preview">REVIEW-ONLY PREVIEW - NOT RELEASE</div>' : ""}</html>`;
}
