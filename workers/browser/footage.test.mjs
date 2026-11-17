import test from "node:test";
import assert from "node:assert/strict";
import { pixelRect, transformRect } from "./footage.mjs";

test("normalized real-footage coordinates and crop transforms preserve exact geometry", () => {
  const r = { x: 0.1, y: 0.2, width: 0.2, height: 0.1 };
  assert.deepEqual(pixelRect(r), { x: 192, y: 216, width: 384, height: 108 });
  assert.throws(() => pixelRect({ ...r, y: 0.79 }));
  assert.throws(() => pixelRect({ ...r, x: NaN }));
  assert.throws(() => pixelRect({ ...r, x: -0.1 }));
  assert.deepEqual(transformRect(r, { x: 0.1, y: 0.1, width: 0.5, height: 0.5 }),
    { x: 0, y: 0.2, width: 0.4, height: 0.2 });
});
