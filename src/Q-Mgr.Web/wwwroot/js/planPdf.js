// LESSON PLAN PDFs, SHRUNK IN THE BROWSER (plan LESSON_PLANS_AND_SCHEMES_OF_WORK §5.4, 2026-09-26).
//
// A teacher who is not at ease with computers cannot be asked to shrink a PDF, so the page does it. Nothing is installed
// on the server: pdf.js (already used by the flip-book and the programme import) reads the file, pdf-lib writes the new
// one, both loaded from jsDelivr only when a teacher opens a file.
//
// The pipeline stops at the first step that meets the school's target:
//   0. The gate, before any work: a .pdf, at most the read limit, at most the page limit, opens in pdf.js.
//   1. Small already: kept as it is. (The server checks it again either way.)
//   2. Text rebuilt: every run of text is set again at its own position in a Standard-14 font (Helvetica, Times,
//      Courier — ISO 32000-1 §9.6.2.2: no embedding needed), ruled lines and boxes redrawn. The embedded fonts and
//      pictures are what make Word's PDFs large; they are gone. Nothing of the original file's structure survives, so
//      no script or action can either — this is also Content Disarm & Reconstruction.
//   3. Letters outside the Standard-14 set (Luganda's ŋ): one free font is embedded, subset to the letters used; if it
//      cannot be fetched, the letter is written in its plainest form and the teacher is told.
//   4. A page with no text (a scan, a phone photo of a handwritten plan): rendered at ~110 dpi, turned black-and-white with
//      an adaptive threshold (a document scanner's treatment) and stored as a 1-bit image — a few tens of KB a page.
//   5. Pictures in a text PDF are kept only while the budget allows, as small grey JPEGs, largest first; the rest are
//      replaced by a box saying a picture was removed.
// The teacher sees the result beside the original before anything is sent. The original never leaves the browser.
window.qmgrPlanPdf = (function () {
    'use strict';

    const PDFJS = 'https://cdn.jsdelivr.net/npm/pdfjs-dist@3.11.174/build/pdf.min.js';
    const PDFJS_WORKER = 'https://cdn.jsdelivr.net/npm/pdfjs-dist@3.11.174/build/pdf.worker.min.js';
    const PDFLIB = 'https://cdn.jsdelivr.net/npm/pdf-lib@1.17.1/dist/pdf-lib.min.js';
    const FONTKIT = 'https://cdn.jsdelivr.net/npm/@pdf-lib/fontkit@1.1.1/dist/fontkit.umd.min.js';
    const NOTO = 'https://cdn.jsdelivr.net/gh/notofonts/notofonts.github.io/fonts/NotoSans/hinted/ttf/NotoSans-Regular.ttf';

    const loaded = {};
    function loadScript(src) {
        if (loaded[src]) return loaded[src];
        loaded[src] = new Promise((resolve, reject) => {
            const s = document.createElement('script');
            s.src = src; s.async = true;
            s.onload = () => resolve(); s.onerror = () => { delete loaded[src]; reject(new Error('Could not load ' + src)); };
            document.head.appendChild(s);
        });
        return loaded[src];
    }
    async function ensureLibs() {
        await loadScript(PDFJS);
        if (window.pdfjsLib && !window.pdfjsLib.GlobalWorkerOptions.workerSrc) window.pdfjsLib.GlobalWorkerOptions.workerSrc = PDFJS_WORKER;
        await loadScript(PDFLIB);
    }

    // Bytes waiting to be collected by .NET, so the result object stays small.
    const pending = {};
    let nextToken = 1;

    function kb(n) { return Math.round(n / 102.4) / 10; }

    // WinAnsi (the Standard-14 encoding) covers Latin-1 plus a few typographic marks.
    const EXTRA = '€‚ƒ„…†‡ˆ‰Š‹ŒŽ‘’“”•–—˜™š›œžŸ';
    function winAnsi(ch) {
        const c = ch.charCodeAt(0);
        return (c >= 0x20 && c <= 0x7e) || (c >= 0xa0 && c <= 0xff) || EXTRA.indexOf(ch) >= 0;
    }
    const PLAIN = { 'ŋ': 'ng', 'Ŋ': 'NG', 'ɛ': 'e', 'ɔ': 'o', 'ĩ': 'i', 'ũ': 'u', '−': '-', '≤': '<=', '≥': '>=', '→': '->', '•': '-', '✓': 'v', '×': 'x', '÷': '/' };
    function plain(text) {
        let out = '';
        for (const ch of text) out += winAnsi(ch) ? ch : (PLAIN[ch] !== undefined ? PLAIN[ch] : (ch.normalize('NFD').replace(/[̀-ͯ]/g, '').split('').every(winAnsi) ? ch.normalize('NFD').replace(/[̀-ͯ]/g, '') : '?'));
        return out;
    }

    function family(name) {
        const n = (name || '').toLowerCase();
        const bold = /bold|black|heavy|semibold|demi/.test(n);
        const italic = /italic|oblique/.test(n);
        let base = 'Helvetica';
        if (/times|roman|serif|georgia|cambria|garamond|book/.test(n) && !/sans/.test(n)) base = 'Times';
        if (/courier|mono|consol/.test(n)) base = 'Courier';
        return { base, bold, italic };
    }
    function standardFont(lib, f) {
        const S = lib.StandardFonts;
        if (f.base === 'Times') return f.bold ? (f.italic ? S.TimesRomanBoldItalic : S.TimesRomanBold) : (f.italic ? S.TimesRomanItalic : S.TimesRoman);
        if (f.base === 'Courier') return f.bold ? (f.italic ? S.CourierBoldOblique : S.CourierBold) : (f.italic ? S.CourierOblique : S.Courier);
        return f.bold ? (f.italic ? S.HelveticaBoldOblique : S.HelveticaBold) : (f.italic ? S.HelveticaOblique : S.Helvetica);
    }

    function mul(m, n) { return [m[0] * n[0] + m[2] * n[1], m[1] * n[0] + m[3] * n[1], m[0] * n[2] + m[2] * n[3], m[1] * n[2] + m[3] * n[3], m[0] * n[4] + m[2] * n[5] + m[4], m[1] * n[4] + m[3] * n[5] + m[5]]; }
    function apply(m, x, y) { return [m[0] * x + m[2] * y + m[4], m[1] * x + m[3] * y + m[5]]; }

    // Lines, boxes and pictures from the page's drawing operators, in PDF space.
    async function readShapes(page, OPS) {
        const list = await page.getOperatorList();
        const lines = [], pictures = [];
        let ctm = [1, 0, 0, 1, 0, 0];
        const stack = [];
        let path = [];
        for (let i = 0; i < list.fnArray.length; i++) {
            const fn = list.fnArray[i], args = list.argsArray[i];
            if (fn === OPS.save) stack.push(ctm.slice());
            else if (fn === OPS.restore) ctm = stack.pop() || [1, 0, 0, 1, 0, 0];
            else if (fn === OPS.transform) ctm = mul(ctm, args);
            else if (fn === OPS.constructPath) {
                const ops = args[0], coords = args[1];
                let c = 0, cur = null, start = null;
                for (const op of ops) {
                    if (op === OPS.moveTo) { cur = apply(ctm, coords[c], coords[c + 1]); start = cur; c += 2; }
                    else if (op === OPS.lineTo) { const p = apply(ctm, coords[c], coords[c + 1]); if (cur) path.push([cur, p]); cur = p; c += 2; }
                    else if (op === OPS.rectangle) {
                        const x = coords[c], y = coords[c + 1], w = coords[c + 2], h = coords[c + 3]; c += 4;
                        const a = apply(ctm, x, y), b = apply(ctm, x + w, y), d = apply(ctm, x + w, y + h), e = apply(ctm, x, y + h);
                        path.push([a, b], [b, d], [d, e], [e, a]);
                        path.rect = path.rect || []; path.rect.push([a, d]);
                    }
                    else if (op === OPS.curveTo) c += 6;
                    else if (op === OPS.curveTo2 || op === OPS.curveTo3) c += 4;
                    else if (op === OPS.closePath) { if (cur && start) path.push([cur, start]); cur = start; }
                }
            }
            else if (fn === OPS.stroke || fn === OPS.closeStroke) { for (const s of path) lines.push(s); path = []; }
            else if (fn === OPS.fill || fn === OPS.eoFill || fn === OPS.fillStroke || fn === OPS.eoFillStroke || fn === OPS.closeFillStroke || fn === OPS.closeEOFillStroke) {
                // A filled THIN rectangle is how Word draws a table rule; a big fill is shading, and is dropped.
                for (const r of (path.rect || [])) {
                    const w = Math.abs(r[1][0] - r[0][0]), h = Math.abs(r[1][1] - r[0][1]);
                    if (w < 2.5 || h < 2.5) lines.push(w < h ? [[(r[0][0] + r[1][0]) / 2, r[0][1]], [(r[0][0] + r[1][0]) / 2, r[1][1]]] : [[r[0][0], (r[0][1] + r[1][1]) / 2], [r[1][0], (r[0][1] + r[1][1]) / 2]]);
                }
                if (fn === OPS.fillStroke || fn === OPS.eoFillStroke || fn === OPS.closeFillStroke || fn === OPS.closeEOFillStroke) for (const s of path) lines.push(s);
                path = [];
            }
            else if (fn === OPS.endPath) path = [];
            else if (fn === OPS.paintImageXObject || fn === OPS.paintInlineImageXObject || fn === OPS.paintJpegXObject || fn === OPS.paintImageMaskXObject) {
                const pts = [apply(ctm, 0, 0), apply(ctm, 1, 0), apply(ctm, 0, 1), apply(ctm, 1, 1)];
                const xs = pts.map(p => p[0]), ys = pts.map(p => p[1]);
                const box = { x: Math.min(...xs), y: Math.min(...ys), w: Math.max(...xs) - Math.min(...xs), h: Math.max(...ys) - Math.min(...ys) };
                if (box.w > 12 && box.h > 12) pictures.push(box);
            }
        }
        return { lines, pictures };
    }

    async function renderPage(page, scale) {
        const vp = page.getViewport({ scale });
        const canvas = document.createElement('canvas');
        canvas.width = Math.ceil(vp.width); canvas.height = Math.ceil(vp.height);
        const ctx = canvas.getContext('2d');
        ctx.fillStyle = '#fff'; ctx.fillRect(0, 0, canvas.width, canvas.height);
        await page.render({ canvasContext: ctx, viewport: vp }).promise;
        return { canvas, vp };
    }

    // Bradley's adaptive threshold over an integral image: ink stays ink under uneven light, a phone photo included.
    function toBits(canvas) {
        const w = canvas.width, h = canvas.height;
        const px = canvas.getContext('2d').getImageData(0, 0, w, h).data;
        const gray = new Uint8Array(w * h);
        for (let i = 0, j = 0; i < gray.length; i++, j += 4) gray[i] = (px[j] * 299 + px[j + 1] * 587 + px[j + 2] * 114) / 1000;
        const integral = new Float64Array((w + 1) * (h + 1));
        for (let y = 1; y <= h; y++) { let row = 0; for (let x = 1; x <= w; x++) { row += gray[(y - 1) * w + x - 1]; integral[y * (w + 1) + x] = integral[(y - 1) * (w + 1) + x] + row; } }
        const s = Math.max(8, Math.floor(w / 16)), t = 0.15, rowBytes = Math.ceil(w / 8);
        const bits = new Uint8Array(rowBytes * h).fill(0xff);
        for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
            const x1 = Math.max(0, x - s), x2 = Math.min(w - 1, x + s), y1 = Math.max(0, y - s), y2 = Math.min(h - 1, y + s);
            const count = (x2 - x1 + 1) * (y2 - y1 + 1);
            const sum = integral[(y2 + 1) * (w + 1) + x2 + 1] - integral[y1 * (w + 1) + x2 + 1] - integral[(y2 + 1) * (w + 1) + x1] + integral[y1 * (w + 1) + x1];
            if (gray[y * w + x] * count <= sum * (1 - t)) bits[y * rowBytes + (x >> 3)] &= ~(0x80 >> (x & 7));
        }
        return { bits, w, h };
    }

    async function thumb(pdf, pageNo, width) {
        const page = await pdf.getPage(pageNo);
        const base = page.getViewport({ scale: 1 });
        const { canvas } = await renderPage(page, width / base.width);
        return canvas.toDataURL('image/png');
    }

    let notoBytes = null, notoFailed = false;
    async function noto(lib, doc) {
        if (notoFailed) return null;
        try {
            await loadScript(FONTKIT);
            if (!notoBytes) { const r = await fetch(NOTO); if (!r.ok) throw new Error('font'); notoBytes = await r.arrayBuffer(); }
            doc.registerFontkit(window.fontkit);
            return await doc.embedFont(notoBytes, { subset: true });
        } catch { notoFailed = true; return null; }
    }

    /**
     * Reads the file chosen in <input type=file id=inputId>, shrinks it, and returns a summary. The bytes are collected
     * afterwards with take(token). `opts`: { targetKb, capKb, maxPages, readLimitMb }.
     */
    async function shrink(inputId, opts) {
        const input = document.getElementById(inputId);
        const file = input && input.files && input.files[0];
        if (!file) return { ok: false, problem: 'Choose a PDF first.' };
        const result = { ok: false, fileName: file.name, originalSize: file.size, size: 0, pages: 0, mode: '', notes: [], removedPictures: 0 };
        if (!/\.pdf$/i.test(file.name) && file.type !== 'application/pdf') return { ...result, problem: 'Choose a PDF file. A Word file is read through the template button instead.' };
        if (file.size > opts.readLimitMb * 1024 * 1024) return { ...result, problem: `The file is ${kb(file.size)} KB, larger than the ${opts.readLimitMb} MB this page can open. Type the plan on the form instead.` };

        try { await ensureLibs(); }
        catch { return { ...result, problem: 'The PDF tools could not be loaded. Check the connection and try again, or type the plan on the form.' }; }
        const lib = window.PDFLib, pdfjs = window.pdfjsLib;
        const original = new Uint8Array(await file.arrayBuffer());

        let src;
        try { src = await pdfjs.getDocument({ data: original.slice(), isEvalSupported: false, disableFontFace: true }).promise; }
        catch { return { ...result, problem: 'That PDF could not be opened. It may be damaged or protected with a password.' }; }
        result.pages = src.numPages;
        if (src.numPages > opts.maxPages) return { ...result, problem: `The PDF has ${src.numPages} pages; the school allows ${opts.maxPages}.` };

        const targetBytes = opts.targetKb * 1024, capBytes = opts.capKb * 1024;
        let before = null;
        try { before = await thumb(src, 1, 320); } catch { }

        // 1. Small already.
        if (original.length <= targetBytes) {
            const token = String(nextToken++); pending[token] = original;
            return { ...result, ok: true, size: original.length, mode: 'original', token, notes: ['Already small enough; kept as it is.'], previewBefore: before, previewAfter: before };
        }

        // 2–5. Rebuild.
        const out = await lib.PDFDocument.create();
        out.setProducer(''); out.setCreator('');
        const fonts = {};
        async function font(f) { const k = standardFont(lib, f); return fonts[k] || (fonts[k] = await out.embedFont(k)); }
        let unicodeFont = null, simplified = false;
        const OPS = pdfjs.OPS;
        const pictureQueue = [];

        for (let n = 1; n <= src.numPages; n++) {
            const page = await src.getPage(n);
            const [x0, y0, x1, y1] = page.view;
            const W = x1 - x0, H = y1 - y0;
            const target = out.addPage([W, H]);
            const text = await page.getTextContent();
            const items = text.items.filter(i => i.str && i.str.trim().length > 0);

            if (items.length === 0) {
                // 4. A scan: black-and-white at ~110 dpi, stored as a 1-bit image.
                const { canvas } = await renderPage(page, 110 / 72);
                const { bits, w, h } = toBits(canvas);
                const stream = out.context.flateStream(bits, { Type: 'XObject', Subtype: 'Image', Width: w, Height: h, ColorSpace: 'DeviceGray', BitsPerComponent: 1 });
                const ref = out.context.register(stream);
                const name = target.node.newXObject('Scan', ref);
                target.pushOperators(lib.pushGraphicsState(), lib.concatTransformationMatrix(W, 0, 0, H, 0, 0), lib.drawObject(name), lib.popGraphicsState());
                result.mode = result.mode || 'scan';
                continue;
            }

            result.mode = 'rebuilt';
            const shapes = await readShapes(page, OPS);
            for (const [a, b] of shapes.lines) {
                const len = Math.hypot(b[0] - a[0], b[1] - a[1]);
                if (len < 1) continue;
                target.drawLine({ start: { x: a[0] - x0, y: a[1] - y0 }, end: { x: b[0] - x0, y: b[1] - y0 }, thickness: 0.5, color: lib.rgb(0.35, 0.35, 0.35) });
            }
            for (const item of items) {
                const [a, b, c, d, e, f] = item.transform;
                let size = Math.hypot(c, d) || Math.hypot(a, b) || 10;
                if (size < 3) continue;
                let name = '';
                try { const fo = page.commonObjs.get(item.fontName); name = (fo && (fo.name || fo.loadedName)) || ''; } catch { }
                if (!name && text.styles && text.styles[item.fontName]) name = text.styles[item.fontName].fontFamily || '';
                let str = item.str;
                let useFont;
                if ([...str].some(ch => !winAnsi(ch))) {
                    unicodeFont = unicodeFont || await noto(lib, out);
                    if (unicodeFont) useFont = unicodeFont; else { str = plain(str); simplified = true; useFont = await font(family(name)); }
                } else useFont = await font(family(name));
                // A standard font is rarely the same width as the original: never let a run spill into the next column.
                try {
                    const natural = useFont.widthOfTextAtSize(str, size);
                    if (item.width > 0 && natural > item.width * 1.03) size = Math.max(size * 0.7, size * item.width / natural);
                } catch { }
                const angle = Math.atan2(b, a) * 180 / Math.PI;
                try { target.drawText(str, { x: e - x0, y: f - y0, size, font: useFont, rotate: lib.degrees(angle), color: lib.rgb(0, 0, 0) }); }
                catch { try { target.drawText(plain(str), { x: e - x0, y: f - y0, size, font: await font(family(name)) }); simplified = true; } catch { } }
            }
            for (const box of shapes.pictures) pictureQueue.push({ n, page, target, box, x0, y0 });
        }

        let bytes = await out.save({ useObjectStreams: false });

        // 5. Pictures while the budget allows, largest first; the rest become a labelled box.
        if (pictureQueue.length) {
            pictureQueue.sort((p, q) => q.box.w * q.box.h - p.box.w * p.box.h);
            const renders = {};
            let budget = targetBytes - bytes.length;
            for (const pic of pictureQueue) {
                let kept = false;
                if (budget > 6000) {
                    try {
                        const r = renders[pic.n] || (renders[pic.n] = await renderPage(pic.page, 96 / 72));
                        const s = 96 / 72, [vx0, , , vy1] = pic.page.view;
                        const cx = (pic.box.x - vx0) * s, cy = (vy1 - (pic.box.y + pic.box.h)) * s, cw = pic.box.w * s, ch = pic.box.h * s;
                        const crop = document.createElement('canvas'); crop.width = Math.max(1, Math.round(cw)); crop.height = Math.max(1, Math.round(ch));
                        const g = crop.getContext('2d'); g.filter = 'grayscale(1)'; g.drawImage(r.canvas, cx, cy, cw, ch, 0, 0, crop.width, crop.height);
                        const jpg = await new Promise(res => crop.toBlob(res, 'image/jpeg', 0.5));
                        const data = new Uint8Array(await jpg.arrayBuffer());
                        if (data.length < budget) {
                            const img = await out.embedJpg(data);
                            pic.target.drawImage(img, { x: pic.box.x - pic.x0, y: pic.box.y - pic.y0, width: pic.box.w, height: pic.box.h });
                            budget -= data.length; kept = true;
                        }
                    } catch { }
                }
                if (!kept) {
                    result.removedPictures++;
                    pic.target.drawRectangle({ x: pic.box.x - pic.x0, y: pic.box.y - pic.y0, width: pic.box.w, height: pic.box.h, borderColor: lib.rgb(0.6, 0.6, 0.6), borderWidth: 0.5 });
                    try { pic.target.drawText('picture removed', { x: pic.box.x - pic.x0 + 4, y: pic.box.y - pic.y0 + 4, size: 7, font: await font({ base: 'Helvetica' }), color: lib.rgb(0.45, 0.45, 0.45) }); } catch { }
                }
            }
            bytes = await out.save({ useObjectStreams: false });
        }

        let after = null;
        try { const rebuilt = await pdfjs.getDocument({ data: bytes.slice(), isEvalSupported: false }).promise; after = await thumb(rebuilt, 1, 320); } catch { }

        if (simplified) result.notes.push('Some letters were written in a plainer form (for example ŋ as ng).');
        if (result.removedPictures) result.notes.push(`${result.removedPictures} picture${result.removedPictures > 1 ? 's were' : ' was'} removed to keep the file small.`);
        if (result.mode === 'scan') result.notes.push('The pages were scans, so they were kept as black-and-white pictures.');

        if (bytes.length > capBytes)
            return { ...result, size: bytes.length, previewBefore: before, previewAfter: after,
                problem: `Even shrunk, the file is ${kb(bytes.length)} KB and the school allows ${opts.capKb} KB. Type the plan on the form, or fill the Word template — both use almost no space.` };
        if (bytes.length > original.length) {
            // Rebuilding made it bigger (a very plain file): keep the original if it is within the cap.
            if (original.length <= capBytes) { bytes = original; result.mode = 'original'; result.notes = ['Kept as it is: it was already about as small as it can be.']; }
        }
        if (bytes.length > targetBytes) result.notes.push(`It is ${kb(bytes.length)} KB — over the ${opts.targetKb} KB the school aims for, but within its limit.`);
        const token = String(nextToken++); pending[token] = bytes;
        return { ...result, ok: true, size: bytes.length, token, previewBefore: before, previewAfter: after };
    }

    function take(token) { const b = pending[token]; delete pending[token]; return b || new Uint8Array(0); }
    function drop(token) { delete pending[token]; }

    function saveBytes(bytes, fileName, mime) {
        const blob = new Blob([bytes], { type: mime || 'application/octet-stream' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url; a.download = fileName; document.body.appendChild(a); a.click(); document.body.removeChild(a);
        setTimeout(() => URL.revokeObjectURL(url), 1000);
        return true;
    }

    function clear(inputId) { const el = document.getElementById(inputId); if (el) el.value = ''; }

    return { shrink, take, drop, saveBytes, clear };
})();
