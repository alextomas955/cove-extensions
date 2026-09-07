#!/usr/bin/env node
// Writes a valid, clearly-labeled placeholder PNG for every docs screenshot slot that has no real
// capture yet, so no doc image link is ever broken. Zero npm dependencies — a hand-built PNG encoder
// (native node:zlib deflate + IHDR/IDAT/IEND with a CRC32) and a 5x7 bitmap font, mirroring the
// approach in fixtures/generate-fixtures.mjs. The slot list is imported from the e2e harness's
// screenshot-targets.mjs, the single source of truth the capture spec also reads.
//
// CONTENT SAFETY: the only images this project ships under website/static/img/whisparr-sync/ are (a)
// these generated solids and (b) real captures from the synthetic-fixture-seeded Cove. No binary image
// is ever hand-added. A slot with an existing file is left untouched, so a real capture is never
// clobbered. Output is deterministic: re-running leaves git clean.
import { existsSync, mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import zlib from "node:zlib";

import { SCREENSHOT_TARGETS, IMG_OUTPUT_SUBPATH } from "../../extensions/WhisparrSync/e2e/lib/screenshot-targets.mjs";

const HERE = dirname(fileURLToPath(import.meta.url)); // …/website/scripts
const REPO_ROOT = join(HERE, "..", ".."); // repo root
const OUT_DIR = join(REPO_ROOT, IMG_OUTPUT_SUBPATH);

const WIDTH = 1000;
const HEIGHT = 640;
const BG = [30, 41, 59]; // slate-800: obviously not a real UI screenshot
const BORDER = [71, 85, 105]; // slate-600
const INK = [226, 232, 240]; // slate-200
const MUTED = [148, 163, 184]; // slate-400

// --- Table-based CRC32 + minimal truecolor PNG encoder (same shape as the fixtures generator). ---
const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) {
      c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    }
    table[n] = c >>> 0;
  }
  return table;
})();

function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) {
    c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  }
  return (c ^ 0xffffffff) >>> 0;
}

function pngChunk(type, data) {
  const typeBuf = Buffer.from(type, "latin1");
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length, 0);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(Buffer.concat([typeBuf, data])), 0);
  return Buffer.concat([len, typeBuf, data, crc]);
}

function encodePng(width, height, fb) {
  const signature = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 2; // color type: truecolor RGB

  const raw = Buffer.alloc((width * 3 + 1) * height);
  let p = 0;
  for (let y = 0; y < height; y++) {
    raw[p++] = 0; // filter: none
    for (let x = 0; x < width; x++) {
      const i = (y * width + x) * 3;
      raw[p++] = fb[i];
      raw[p++] = fb[i + 1];
      raw[p++] = fb[i + 2];
    }
  }
  const idat = zlib.deflateSync(raw, { level: 9 });
  return Buffer.concat([
    signature,
    pngChunk("IHDR", ihdr),
    pngChunk("IDAT", idat),
    pngChunk("IEND", Buffer.alloc(0)),
  ]);
}

// --- 5x7 bitmap font: enough of the uppercase Latin/digit set to render a slot label. ---
const GLYPHS = {
  A: ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
  B: ["11110", "10001", "10001", "11110", "10001", "10001", "11110"],
  C: ["01111", "10000", "10000", "10000", "10000", "10000", "01111"],
  D: ["11110", "10001", "10001", "10001", "10001", "10001", "11110"],
  E: ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
  F: ["11111", "10000", "10000", "11110", "10000", "10000", "10000"],
  G: ["01111", "10000", "10000", "10011", "10001", "10001", "01111"],
  H: ["10001", "10001", "10001", "11111", "10001", "10001", "10001"],
  I: ["11111", "00100", "00100", "00100", "00100", "00100", "11111"],
  J: ["00111", "00010", "00010", "00010", "00010", "10010", "01100"],
  K: ["10001", "10010", "10100", "11000", "10100", "10010", "10001"],
  L: ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
  M: ["10001", "11011", "10101", "10101", "10001", "10001", "10001"],
  N: ["10001", "11001", "10101", "10101", "10011", "10001", "10001"],
  O: ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
  P: ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
  Q: ["01110", "10001", "10001", "10001", "10101", "10010", "01101"],
  R: ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
  S: ["01111", "10000", "10000", "01110", "00001", "00001", "11110"],
  T: ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
  U: ["10001", "10001", "10001", "10001", "10001", "10001", "01110"],
  V: ["10001", "10001", "10001", "10001", "10001", "01010", "00100"],
  W: ["10001", "10001", "10001", "10101", "10101", "11011", "10001"],
  X: ["10001", "10001", "01010", "00100", "01010", "10001", "10001"],
  Y: ["10001", "10001", "01010", "00100", "00100", "00100", "00100"],
  Z: ["11111", "00001", "00010", "00100", "01000", "10000", "11111"],
  0: ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
  1: ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
  2: ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
  3: ["11111", "00010", "00100", "00010", "00001", "10001", "01110"],
  4: ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
  5: ["11111", "10000", "11110", "00001", "00001", "10001", "01110"],
  6: ["01110", "10000", "10000", "11110", "10001", "10001", "01110"],
  7: ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
  8: ["01110", "10001", "10001", "01110", "10001", "10001", "01110"],
  9: ["01110", "10001", "10001", "01111", "00001", "00001", "01110"],
  " ": ["00000", "00000", "00000", "00000", "00000", "00000", "00000"],
  "-": ["00000", "00000", "00000", "11111", "00000", "00000", "00000"],
  ".": ["00000", "00000", "00000", "00000", "00000", "01100", "01100"],
  "/": ["00001", "00001", "00010", "00100", "01000", "10000", "10000"],
};

const GLYPH_W = 5;
const GLYPH_H = 7;
const CHAR_GAP = 1; // in glyph cells

function normalizeChar(ch) {
  const up = ch.toUpperCase();
  if (Object.prototype.hasOwnProperty.call(GLYPHS, up)) return up;
  if (up === "·" || up === "•") return ".";
  return " ";
}

function textCellWidth(text) {
  return text.length * (GLYPH_W + CHAR_GAP) - CHAR_GAP;
}

function makeFramebuffer(width, height, fill) {
  const fb = new Uint8Array(width * height * 3);
  for (let i = 0; i < fb.length; i += 3) {
    fb[i] = fill[0];
    fb[i + 1] = fill[1];
    fb[i + 2] = fill[2];
  }
  return fb;
}

function setPixel(fb, width, x, y, color) {
  if (x < 0 || y < 0 || x >= width || y >= HEIGHT) return;
  const i = (y * width + x) * 3;
  fb[i] = color[0];
  fb[i + 1] = color[1];
  fb[i + 2] = color[2];
}

// Draws `text` at cell scale `scale`, left edge `x0` / top `y0` (device pixels), in `color`.
function drawText(fb, width, text, x0, y0, scale, color) {
  let cursor = x0;
  for (const raw of text) {
    const glyph = GLYPHS[normalizeChar(raw)];
    for (let gy = 0; gy < GLYPH_H; gy++) {
      const row = glyph[gy];
      for (let gx = 0; gx < GLYPH_W; gx++) {
        if (row[gx] !== "1") continue;
        for (let sy = 0; sy < scale; sy++) {
          for (let sx = 0; sx < scale; sx++) {
            setPixel(fb, width, cursor + gx * scale + sx, y0 + gy * scale + sy, color);
          }
        }
      }
    }
    cursor += (GLYPH_W + CHAR_GAP) * scale;
  }
}

function drawCenteredText(fb, width, text, y0, scale, color) {
  const w = textCellWidth(text) * scale;
  drawText(fb, width, text, Math.round((width - w) / 2), y0, scale, color);
}

function renderPlaceholder(label, file) {
  const fb = makeFramebuffer(WIDTH, HEIGHT, BG);

  // A thick border makes it unmistakably a placeholder frame, not a captured screenshot.
  const b = 6;
  for (let y = 0; y < HEIGHT; y++) {
    for (let x = 0; x < WIDTH; x++) {
      if (x < b || y < b || x >= WIDTH - b || y >= HEIGHT - b) {
        setPixel(fb, WIDTH, x, y, BORDER);
      }
    }
  }

  drawCenteredText(fb, WIDTH, "SYNTHETIC PLACEHOLDER", 210, 5, MUTED);
  drawCenteredText(fb, WIDTH, label.toUpperCase(), 300, 6, INK);
  drawCenteredText(fb, WIDTH, file, 420, 3, MUTED);
  return encodePng(WIDTH, HEIGHT, fb);
}

function main() {
  mkdirSync(OUT_DIR, { recursive: true });
  let written = 0;
  let kept = 0;
  for (const target of SCREENSHOT_TARGETS) {
    const outPath = join(OUT_DIR, target.file);
    if (existsSync(outPath)) {
      kept++;
      continue;
    }
    writeFileSync(outPath, renderPlaceholder(target.label, target.file));
    written++;
  }
  console.log(
    `placeholders: wrote ${String(written)}, kept ${String(kept)} existing (never clobbered) of ${String(SCREENSHOT_TARGETS.length)} slots -> ${IMG_OUTPUT_SUBPATH}`,
  );
}

main();
