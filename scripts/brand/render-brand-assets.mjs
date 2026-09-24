// RENDERS THE PNG COPIES OF THE MARK (rebrand 2026-09-24).
//
// The mark's one drawing is ProductMark (src/Q-Mgr.Shared/Application/Branding/ProductMark.cs), and
// the Web serves every /brand/*.svg from it on request. A few places cannot take an SVG — the 32px
// PNG favicon older browsers ask for, the iOS home-screen icon, and the PWA manifest's PNG icons —
// so this script rasterises the SERVED SVGs with a real browser and writes the PNGs beside them in
// wwwroot/brand/. Those PNGs are the only derived copies of the mark in the Web project.
//
// Re-run it whenever ProductMark changes. It needs the Web running and a Chrome on CDP_PORT (9333),
// the same recipe as the browser suites; it installs nothing.
//
//   WEB=http://127.0.0.1:5003 node scripts/brand/render-brand-assets.mjs
import fs from 'node:fs';
import path from 'node:path';
import { openTab } from '../e2e/browser/cdp.mjs';

const WEB = process.env.WEB ?? 'http://127.0.0.1:5003';
const OUT = path.resolve('src/Q-Mgr.Web/wwwroot/brand');

// [served svg, output file, size in px]
const RENDERS = [
  ['favicon.svg', 'favicon-32.png', 32],
  ['app-icon.svg', 'apple-touch-icon.png', 180],
  ['app-icon.svg', 'icon-192.png', 192],
  ['app-icon.svg', 'icon-512.png', 512],
];

fs.mkdirSync(OUT, { recursive: true });
const tab = await openTab();
let failed = 0;

for (const [svg, file, size] of RENDERS) {
  const url = `${WEB}/brand/${svg}`;
  const res = await fetch(url);
  if (!res.ok) { console.log(`FAIL  ${url} answered ${res.status}`); failed++; continue; }
  const markup = await res.text();

  // The SVG inlined into an empty page, sized exactly, on a transparent ground — so the PNG carries
  // the mark's own rounded corners rather than a white square behind them.
  const html = `<!doctype html><html><head><style>html,body{margin:0;background:transparent}
    svg{display:block;width:${size}px;height:${size}px}</style></head><body>${markup}</body></html>`;
  await tab.send('Emulation.setDeviceMetricsOverride', { width: size, height: size, deviceScaleFactor: 1, mobile: false });
  await tab.send('Emulation.setDefaultBackgroundColorOverride', { color: { r: 0, g: 0, b: 0, a: 0 } });
  await tab.goto('data:text/html;base64,' + Buffer.from(html).toString('base64'));
  await tab.sleep(300);
  const { data } = await tab.send('Page.captureScreenshot', { format: 'png', clip: { x: 0, y: 0, width: size, height: size, scale: 1 } });
  fs.writeFileSync(path.join(OUT, file), Buffer.from(data, 'base64'));
  console.log(`ok    ${file.padEnd(22)} ${size}x${size} from /brand/${svg}`);
}

tab.close();
if (failed) process.exitCode = 1;
