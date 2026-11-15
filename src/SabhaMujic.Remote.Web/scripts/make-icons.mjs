// Generates the PNG app icons (apple-touch-icon 180, 192, 512) from simple geometry: no image tools needed.
// The design matches wwwroot/icons/icon.svg: a saffron square with a white eighth note.
import { writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { deflateSync } from 'node:zlib';

const out = join(dirname(fileURLToPath(import.meta.url)), '..', 'wwwroot', 'icons');
const BG = [0xb4, 0x53, 0x1f];
const FG = [0xff, 0xff, 0xff];

// Shapes in a 100×100 design space (same as icon.svg).
/** @param {number} x @param {number} y */
function inside(x, y) {
  const head = ((x - 40) / 13) ** 2 + ((y - 70) / 10) ** 2 <= 1; // note head (ellipse)
  const stem = x >= 49 && x <= 55 && y >= 24 && y <= 70; // stem
  const flag = y >= 24 && y <= 40 && x >= 55 && x <= 55 + (40 - y) * 0.9 + 6 && x <= 72; // flag
  return head || stem || flag;
}

const CRC_TABLE = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});

/** @param {Buffer} buf */
function crc32(buf) {
  let c = 0xffffffff;
  for (const b of buf) c = CRC_TABLE[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

/** @param {string} type @param {Buffer} data */
function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
}

/** @param {number} size */
function png(size) {
  const ss = 4; // 4×4 supersampling for smooth edges
  const raw = Buffer.alloc(size * (size * 3 + 1));
  for (let y = 0; y < size; y++) {
    raw[y * (size * 3 + 1)] = 0; // filter: none
    for (let x = 0; x < size; x++) {
      let hits = 0;
      for (let sy = 0; sy < ss; sy++) {
        for (let sx = 0; sx < ss; sx++) {
          if (inside(((x + (sx + 0.5) / ss) / size) * 100, ((y + (sy + 0.5) / ss) / size) * 100)) hits++;
        }
      }
      const a = hits / (ss * ss);
      const o = y * (size * 3 + 1) + 1 + x * 3;
      for (let c = 0; c < 3; c++) raw[o + c] = Math.round(BG[c] * (1 - a) + FG[c] * a);
    }
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 2; // RGB
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

writeFileSync(join(out, 'apple-touch-icon.png'), png(180));
writeFileSync(join(out, 'icon-192.png'), png(192));
writeFileSync(join(out, 'icon-512.png'), png(512));
console.log('icons written to', out);
