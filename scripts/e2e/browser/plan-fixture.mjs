// Shared fixture for the lesson-plan browser suites (lesson-plan-ui, plan-pdf, plan-templates), 2026-09-26.
// Signs the section 14 accounts in over the API, seeds a subject in E2EMATH and the teacher's subject assignments, and
// builds the files the suites feed the page: PDFs by hand (text, padded, scanned) and a filled Word template.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import zlib from 'node:zlib';

export const WEB = process.env.WEB ?? 'http://127.0.0.1:5003';
export const API = process.env.API ?? 'http://127.0.0.1:5001';
export const BRANCH = process.env.BRANCH ?? 'a805ba99-ef62-4685-a1ad-b11b2ea7747f';
export const PWS = ['E2eTeacher!2026', 'Rwenzori#Peaks-2026'];
export const B = `/api/v1/branches/${BRANCH}`;
export const TP = `${B}/teaching-plans`;

export async function api(token, method, p, body, raw) {
  const headers = { Authorization: `Bearer ${token}` };
  let payload;
  if (raw) payload = raw; else if (body !== undefined) { headers['Content-Type'] = 'application/json'; payload = JSON.stringify(body); }
  const r = await fetch(`${API}${p}`, { method, headers, body: payload });
  const buf = Buffer.from(await r.arrayBuffer());
  let json = null; try { json = JSON.parse(buf.toString('utf8')); } catch { }
  return { status: r.status, json, buf, text: buf.toString('utf8') };
}

export async function account(email) {
  for (const password of PWS) {
    const r = await fetch(`${API}/api/v1/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password }) });
    if (r.ok) { const j = await r.json(); return { email, password, token: j.accessToken, id: j.user?.id, name: j.user?.fullName }; }
  }
  return null;
}

/** The E2EMATH subject and the teacher's assignments; returns what to undo. */
export async function seed(ad, teacher, hod) {
  const depts = (await api(ad.token, 'GET', `${B}/staff/structure/departments?includeInactive=true`)).json ?? [];
  const math = depts.find((d) => d.code === 'E2EMATH');
  if (!math) throw new Error('department E2EMATH is missing — run API section 14 first');
  if (math.headUserId !== hod.id) await api(ad.token, 'PUT', `${B}/staff/structure/departments/${math.id}`, { name: math.name, code: math.code, headUserId: hod.id, deputyHeadUserId: null, sortOrder: 1 });
  const subjects = (await api(ad.token, 'GET', `${B}/staff/subjects?includeInactive=true`)).json ?? [];
  let subject = subjects.find((s) => s.code === 'E2EPLM');
  if (!subject) subject = (await api(ad.token, 'POST', `${B}/staff/subjects`, { name: 'E2E Plans Mathematics', code: 'E2EPLM', departmentId: math.id, sortOrder: 90 })).json;
  const vocab = (await api(ad.token, 'GET', `${B}/students/vocabularies`)).json;
  const classes = (vocab?.classes ?? []).filter((c) => c.isActive !== false).map((c) => c.name);
  const seeded = [];
  for (const c of classes.slice(0, 2)) {
    const r = await api(ad.token, 'POST', `${B}/class-teachers/subject-teachers`, { className: c, userId: teacher.id, subjectId: subject.id, periodsPerWeek: 4 });
    if (r.json?.id) seeded.push(r.json.id);
  }
  return { subject, classes, undo: async () => { for (const id of seeded) await api(ad.token, 'DELETE', `${B}/class-teachers/${id}`); } };
}

export const future = (days) => new Date(Date.now() + days * 86400_000).toISOString().slice(0, 10);

export async function newPlan(teacher, subject, className, days) {
  return (await api(teacher.token, 'POST', TP, { kind: 'LessonPlan', subjectId: subject.id, classNames: [className], lessonDate: future(days), clientRequestId: crypto.randomUUID() })).json;
}

// ---- Files ---------------------------------------------------------------------------------------------------------

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'qmgr-plans-'));
export function writeTemp(name, bytes) { const p = path.join(tmp, name); fs.writeFileSync(p, bytes); return p; }

/** A text PDF by hand. `pad` adds that many bytes of dead weight, the way an embedded font subset bloats a Word export. */
export function textPdf({ lines = ['Lesson plan', 'Topic: Fractions', 'Learning outcomes: learners add fractions'], pad = 0, pages = 1 } = {}) {
  const objs = [];
  const kids = [];
  let next = 3;
  const body = [];
  for (let i = 0; i < pages; i++) {
    const p = next++, c = next++;
    kids.push(`${p} 0 R`);
    const content = lines.map((l, j) => `BT /F1 12 Tf 72 ${760 - j * 22} Td (${l}) Tj ET`).join('\n') + '\n0.5 w 60 600 m 535 600 l S';
    body.push(`${p} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents ${c} 0 R /Resources << /Font << /F1 99 0 R >> >> >>\nendobj\n`);
    body.push(`${c} 0 obj\n<< /Length ${content.length} >>\nstream\n${content}\nendstream\nendobj\n`);
  }
  objs.push(`1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n`, `2 0 obj\n<< /Type /Pages /Kids [${kids.join(' ')}] /Count ${pages} >>\nendobj\n`, ...body,
    `99 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n`);
  const deadWeight = pad > 0 ? `98 0 obj\n<< /Length ${pad} >>\nstream\n${'x'.repeat(pad)}\nendstream\nendobj\n` : '';
  return Buffer.from(`%PDF-1.4\n${objs.join('')}${deadWeight}trailer\n<< /Root 1 0 R >>\n%%EOF\n`, 'latin1');
}

/** A "scan": one grey image and no text at all — the phone photo of a handwritten plan. */
export function scanPdf() {
  const W = 800, H = 1100;
  const px = Buffer.alloc(W * H);
  let seed = 7;
  const rnd = () => (seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff;
  for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
    const ink = (y % 60 > 38 && y % 60 < 46 && x > 80 && x < 720 && (Math.floor(x / 14) % 5 !== 0));
    // Paper photographed by a phone: uneven light and grain everywhere, which is what makes a scan large.
    const light = 190 + Math.floor(40 * Math.sin(x / 170) * Math.cos(y / 230));
    px[y * W + x] = ink ? 25 + Math.floor(rnd() * 50) : Math.min(255, light + Math.floor(rnd() * 60) - 20);
  }
  const data = zlib.deflateSync(px);
  const content = `q 595 0 0 842 0 0 cm /Im1 Do Q`;
  const head = `%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n` +
    `3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /XObject << /Im1 5 0 R >> >> >>\nendobj\n` +
    `4 0 obj\n<< /Length ${content.length} >>\nstream\n${content}\nendstream\nendobj\n` +
    `5 0 obj\n<< /Type /XObject /Subtype /Image /Width ${W} /Height ${H} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode /Length ${data.length} >>\nstream\n`;
  return Buffer.concat([Buffer.from(head, 'latin1'), data, Buffer.from(`\nendstream\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n`, 'latin1')]);
}

// ---- A .docx (a zip) by hand --------------------------------------------------------------------------------------------
export function unzip(buf) {
  let eocd = buf.length - 22;
  while (eocd >= 0 && buf.readUInt32LE(eocd) !== 0x06054b50) eocd--;
  const count = buf.readUInt16LE(eocd + 10), cdOffset = buf.readUInt32LE(eocd + 16);
  const files = {};
  let p = cdOffset;
  for (let i = 0; i < count; i++) {
    const method = buf.readUInt16LE(p + 10), csize = buf.readUInt32LE(p + 20), nlen = buf.readUInt16LE(p + 28), xlen = buf.readUInt16LE(p + 30), clen = buf.readUInt16LE(p + 32), local = buf.readUInt32LE(p + 42);
    const name = buf.slice(p + 46, p + 46 + nlen).toString('utf8');
    const lnlen = buf.readUInt16LE(local + 26), lxlen = buf.readUInt16LE(local + 28);
    const data = buf.slice(local + 30 + lnlen + lxlen, local + 30 + lnlen + lxlen + csize);
    files[name] = method === 8 ? zlib.inflateRawSync(data) : data;
    p += 46 + nlen + xlen + clen;
  }
  return files;
}
export function zip(files) {
  const locals = [], centrals = [];
  let offset = 0;
  for (const [name, data] of Object.entries(files)) {
    const n = Buffer.from(name, 'utf8'), crc = zlib.crc32(data) >>> 0;
    const lh = Buffer.alloc(30); lh.writeUInt32LE(0x04034b50, 0); lh.writeUInt16LE(20, 4); lh.writeUInt32LE(crc, 14); lh.writeUInt32LE(data.length, 18); lh.writeUInt32LE(data.length, 22); lh.writeUInt16LE(n.length, 26);
    const ch = Buffer.alloc(46); ch.writeUInt32LE(0x02014b50, 0); ch.writeUInt16LE(20, 4); ch.writeUInt16LE(20, 6); ch.writeUInt32LE(crc, 16); ch.writeUInt32LE(data.length, 20); ch.writeUInt32LE(data.length, 24); ch.writeUInt16LE(n.length, 28); ch.writeUInt32LE(offset, 42);
    locals.push(lh, n, data); centrals.push(ch, n);
    offset += 30 + n.length + data.length;
  }
  const cd = Buffer.concat(centrals);
  const end = Buffer.alloc(22); end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(Object.keys(files).length, 8); end.writeUInt16LE(Object.keys(files).length, 10); end.writeUInt32LE(cd.length, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, cd, end]);
}
const EMPTY_P = '<w:p><w:pPr><w:spacing w:after="60"/></w:pPr></w:p>';
export function fillAfter(xml, label, text, skipCells = 0) {
  const at = xml.indexOf(`>${label}`);
  if (at < 0) return xml;
  let slot = xml.indexOf(EMPTY_P, at);
  for (let i = 0; i < skipCells && slot >= 0; i++) slot = xml.indexOf(EMPTY_P, slot + EMPTY_P.length);
  if (slot < 0) return xml;
  return xml.slice(0, slot) + `<w:p><w:r><w:t xml:space="preserve">${text}</w:t></w:r></w:p>` + xml.slice(slot + EMPTY_P.length);
}

/** Real mouse input on the centre of an element (never element.click(): it moves no focus and fires no mousedown). */
export async function press(t, expr) {
  const p = await t.eval(`(() => { const e = ${expr}; if (!e) return null; e.scrollIntoView({ block: 'center', behavior: 'instant' });
    const r = e.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()`);
  if (!p) return false;
  await t.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: p.x, y: p.y });
  await t.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: p.x, y: p.y, button: 'left', clickCount: 1 });
  await t.sleep(80);
  await t.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: p.x, y: p.y, button: 'left', clickCount: 1 });
  await t.sleep(400);
  return true;
}
