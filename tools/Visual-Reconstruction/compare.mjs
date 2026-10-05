import fs from 'node:fs';
import path from 'node:path';
import pixelmatch from 'pixelmatch';
import { PNG } from 'pngjs';

const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, value, index, values) => {
  if (value.startsWith('--')) pairs.push([value.slice(2), values[index + 1]]);
  return pairs;
}, []));

for (const name of ['reference', 'actual', 'mask', 'output', 'state']) {
  if (!args[name]) throw new Error(`Missing --${name}`);
}

const readPng = file => PNG.sync.read(fs.readFileSync(file));
const reference = readPng(args.reference);
const actual = readPng(args.actual);
const mask = readPng(args.mask);
if (reference.width !== actual.width || reference.height !== actual.height)
  throw new Error(`Size mismatch: reference ${reference.width}x${reference.height}, actual ${actual.width}x${actual.height}`);
if (reference.width !== mask.width || reference.height !== mask.height)
  throw new Error(`Mask mismatch: ${mask.width}x${mask.height}`);

const comparedReference = new PNG({ width: reference.width, height: reference.height });
const comparedActual = new PNG({ width: reference.width, height: reference.height });
let comparedPixels = 0;
for (let offset = 0; offset < reference.data.length; offset += 4) {
  const masked = mask.data[offset] + mask.data[offset + 1] + mask.data[offset + 2] > 0;
  for (let channel = 0; channel < 4; channel++) {
    comparedReference.data[offset + channel] = reference.data[offset + channel];
    comparedActual.data[offset + channel] = masked ? reference.data[offset + channel] : actual.data[offset + channel];
  }
  if (!masked) comparedPixels++;
}

const diff = new PNG({ width: reference.width, height: reference.height });
const differentPixels = pixelmatch(
  comparedReference.data,
  comparedActual.data,
  diff.data,
  reference.width,
  reference.height,
  { threshold: 0.12, includeAA: false });

const overlay = new PNG({ width: reference.width, height: reference.height });
for (let offset = 0; offset < overlay.data.length; offset += 4) {
  for (let channel = 0; channel < 3; channel++)
    overlay.data[offset + channel] = Math.round((reference.data[offset + channel] + actual.data[offset + channel]) / 2);
  overlay.data[offset + 3] = 255;
}

fs.mkdirSync(args.output, { recursive: true });
fs.writeFileSync(path.join(args.output, 'overlay.png'), PNG.sync.write(overlay));
fs.writeFileSync(path.join(args.output, 'diff.png'), PNG.sync.write(diff));
const differenceRatio = comparedPixels === 0 ? 0 : differentPixels / comparedPixels;
const report = {
  state: args.state,
  width: reference.width,
  height: reference.height,
  differentPixels,
  comparedPixels,
  differenceRatio,
  geometryFailures: [],
  passed: differenceRatio <= 0.03
};
fs.writeFileSync(path.join(args.output, 'report.json'), `${JSON.stringify(report, null, 2)}\n`);
console.log(JSON.stringify(report));
process.exitCode = report.passed ? 0 : 2;
