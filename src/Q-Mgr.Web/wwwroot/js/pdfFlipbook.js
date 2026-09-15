/*
    PDF flip-book viewer — pdf.js for rasterising, page-flip (St.PageFlip) for the page turn.

    Four things in here are load-bearing and easy to undo by accident:

    1. THE LIBRARIES LOAD ON DEMAND, not from a <script> tag in App.razor. pdf.min.js alone is
       ~340KB and it was previously fetched, parsed and executed on EVERY page of the app for the
       benefit of the handful that show a PDF. ensureLibs() injects both, once, the first time a
       flipbook is actually opened.

    2. PAGES ARE <img>, NEVER <canvas>. page-flip animates a soft page turn by doing
       element.cloneNode(true) on the page element (Page.newTemporaryCopy). Canvas PIXELS do not
       survive cloneNode, so a canvas-backed page goes blank for the whole flip animation. The
       bitmap therefore goes canvas -> toBlob -> object URL -> <img src>. toBlob rather than
       toDataURL because the data URL is a base64 string of the same bitmap that has to be built,
       held and parsed again — on an 8-page A4 document that was several megabytes of string.

    3. RENDERING IS PROGRESSIVE. The old version rasterised every page at a fixed scale 2 and
       only then showed anything, so open time grew with page count and a long document looked
       broken. Now page one (and its spread partner) render, the book appears, and the rest fill
       in behind it. Render scale follows the size the page is actually DISPLAYED at rather than a
       constant, and is redone at higher quality when zoomed in.

    4. THE BOOK IS SIZED BY THE BOX WE SET ON THE SURFACE ELEMENT, with autoSize off.
       page-flip's own autoSize writes a padding-bottom aspect ratio onto its wrapper, which
       derives the book height from its width alone — in a landscape container that overflows
       vertically and shows a magnified corner of page one. We hand it an explicit box and let its
       stretch branch letterbox inside it. Note HTMLUI's constructor writes `minWidth` and
       `minHeight` from the settings straight onto that element, so applyLayout() clears them
       every time; the single-page sentinel in buildBook() depends on that.
*/

window.pdfFlipbookInterop = (function () {
    'use strict';

    var PDFJS_JS = 'https://cdn.jsdelivr.net/npm/pdfjs-dist@3.11.174/build/pdf.min.js';
    var PDFJS_WORKER = 'https://cdn.jsdelivr.net/npm/pdfjs-dist@3.11.174/build/pdf.worker.min.js';
    var PAGEFLIP_JS = 'https://cdn.jsdelivr.net/npm/page-flip@2.0.7/dist/js/page-flip.browser.js';

    // A rendered page never needs to be bigger than this on either edge. Caps memory on a
    // long document and on an unattended screen that runs for days.
    var MAX_PAGE_EDGE = 2600;
    var THUMB_WIDTH = 104;
    var MAX_MATCHES = 500;
    var IDLE_MS = 6000;

    var instances = new Map();
    var libsPromise = null;
    var generationSeq = 0;

    /* ---------------------------------------------------------------- *
     * Library loading
     * ---------------------------------------------------------------- */

    function loadScript(src) {
        return new Promise(function (resolve, reject) {
            var existing = document.querySelector('script[data-pdfflipbook="' + src + '"]');
            if (existing) {
                if (existing.dataset.loaded === '1') { resolve(); return; }
                existing.addEventListener('load', function () { resolve(); });
                existing.addEventListener('error', function () { reject(new Error('Failed to load ' + src)); });
                return;
            }
            var el = document.createElement('script');
            el.src = src;
            el.async = true;
            el.dataset.pdfflipbook = src;
            el.addEventListener('load', function () { el.dataset.loaded = '1'; resolve(); });
            el.addEventListener('error', function () { reject(new Error('Failed to load ' + src)); });
            document.head.appendChild(el);
        });
    }

    function ensureLibs() {
        if (!libsPromise) {
            libsPromise = Promise.all([
                window.pdfjsLib ? Promise.resolve() : loadScript(PDFJS_JS),
                window.St ? Promise.resolve() : loadScript(PAGEFLIP_JS)
            ]).then(function () {
                if (!window.pdfjsLib || !window.St) {
                    throw new Error('PDF viewer libraries failed to load');
                }
                window.pdfjsLib.GlobalWorkerOptions.workerSrc = PDFJS_WORKER;
            }).catch(function (err) {
                // Do not cache a failure — a display that lost the network for a moment should be
                // able to retry on the playlist's next pass rather than be broken until reload.
                libsPromise = null;
                throw err;
            });
        }
        return libsPromise;
    }

    /* ---------------------------------------------------------------- *
     * Helpers
     * ---------------------------------------------------------------- */

    function alive(inst, gen) {
        return inst && !inst.disposed && inst.generation === gen;
    }

    function idle() {
        return new Promise(function (resolve) {
            if (window.requestIdleCallback) {
                window.requestIdleCallback(function () { resolve(); }, { timeout: 200 });
            } else {
                setTimeout(resolve, 0);
            }
        });
    }

    function invoke(inst, method) {
        var args = Array.prototype.slice.call(arguments, 2);
        if (!inst || inst.disposed || !inst.dotNetRef) return;
        try {
            inst.dotNetRef.invokeMethodAsync.apply(inst.dotNetRef, [method].concat(args));
        } catch (e) {
            // Circuit gone mid-flight; nothing useful to do from here.
        }
    }

    function canvasToObjectUrl(canvas) {
        return new Promise(function (resolve) {
            if (canvas.toBlob) {
                canvas.toBlob(function (blob) {
                    resolve(blob ? URL.createObjectURL(blob) : canvas.toDataURL('image/jpeg', 0.85));
                }, 'image/jpeg', 0.85);
            } else {
                resolve(canvas.toDataURL('image/jpeg', 0.85));
            }
        });
    }

    function releaseUrl(url) {
        if (url && url.indexOf('blob:') === 0) {
            try { URL.revokeObjectURL(url); } catch (e) { /* already gone */ }
        }
    }

    /* ---------------------------------------------------------------- *
     * Layout
     * ---------------------------------------------------------------- */

    // The size one page occupies, mirroring page-flip's own stretch maths in
    // Render.calculateBoundsRect so render quality is chosen for the size actually drawn.
    function pageBox(inst) {
        var ratio = inst.pageW / inst.pageH;
        if (inst.mode === 'scroll') {
            // Fit-to-width: the page is as wide as the surface and as tall as its aspect says.
            var sw = Math.max(1, inst.surfaceW || 1);
            return { w: sw, h: Math.max(1, sw / ratio) };
        }
        var w = inst.mode === 'spread' ? inst.surfaceW / 2 : inst.surfaceW;
        var h = w / ratio;
        if (h > inst.surfaceH) {
            h = inst.surfaceH;
            w = h * ratio;
        }
        return { w: Math.max(1, w), h: Math.max(1, h) };
    }

    // Measures the stage with the surface collapsed, so a scrollbar left over from the previous
    // zoom level does not shrink the box we are about to compute (which would shrink it again on
    // the next pass, and so on).
    function measureStage(inst) {
        var s = inst.surfaceEl;
        var prevW = s ? s.style.width : null;
        var prevH = s ? s.style.height : null;
        if (s) { s.style.width = '0px'; s.style.height = '0px'; }
        var box = { w: inst.stage.clientWidth, h: inst.stage.clientHeight };
        if (s) { s.style.width = prevW; s.style.height = prevH; }
        return box;
    }

    function applyLayout(inst, recenter) {
        if (!inst.stage || !inst.surfaceEl) return;

        if (inst.mode === 'scroll') {
            // Width only; height is each page's own. The stage scrolls vertically, and
            // horizontally too once zoomed past the screen.
            var stageW = inst.stage.clientWidth;
            if (stageW < 40) return;
            // Fit-to-width, but a desktop stage is wider than a page is comfortable to read;
            // cap the column and let the stylesheet centre it. Zoom still goes past the cap.
            inst.surfaceW = Math.round(Math.min(stageW, SCROLL_MAX_WIDTH) * inst.zoom);
            inst.surfaceH = 0;
            inst.surfaceEl.style.width = inst.surfaceW + 'px';
            scheduleQualityPass(inst);
            return;
        }

        var fit = measureStage(inst);
        if (fit.w < 40 || fit.h < 40) return;

        inst.surfaceW = Math.round(fit.w * inst.zoom);
        inst.surfaceH = Math.round(fit.h * inst.zoom);

        var s = inst.surfaceEl.style;
        // HTMLUI wrote these from the settings; clear them or the single-page sentinel below
        // becomes a real 1,000,000px min-width.
        s.minWidth = '0px';
        s.minHeight = '0px';
        s.maxWidth = 'none';
        s.maxHeight = 'none';
        s.width = inst.surfaceW + 'px';
        s.height = inst.surfaceH + 'px';

        try { if (inst.pageFlip) inst.pageFlip.update(); } catch (e) { /* mid-teardown */ }

        if (recenter) {
            inst.stage.scrollLeft = Math.max(0, (inst.stage.scrollWidth - inst.stage.clientWidth) / 2);
            inst.stage.scrollTop = Math.max(0, (inst.stage.scrollHeight - inst.stage.clientHeight) / 2);
        }

        scheduleQualityPass(inst);
    }

    /* ---------------------------------------------------------------- *
     * Page elements and the book
     * ---------------------------------------------------------------- */

    function buildPageElements(inst) {
        inst.holder = document.createElement('div');
        inst.pageEls = [];
        for (var i = 0; i < inst.pageCount; i++) {
            var el = document.createElement('div');
            el.className = 'pdf-flipbook-page';
            el.setAttribute('data-page', String(i));

            var img = document.createElement('img');
            img.alt = 'Page ' + (i + 1);
            img.draggable = false;
            el.appendChild(img);

            var overlay = document.createElement('div');
            overlay.className = 'pdf-flipbook-page-overlay';
            el.appendChild(overlay);

            inst.holder.appendChild(el);
            inst.pageEls.push(el);
        }
    }

    /*
        Scroll mode: the pages stacked vertically at fit-to-width, no page-flip at all. The same
        page elements and bitmaps as the book (so search highlights, thumbnails and the quality
        pass all keep working); what changes is the surface they sit in, how "current page" is
        found (an IntersectionObserver on the stage), and how zoom is driven (pinch and double-tap,
        because the app's viewport meta forbids browser zoom for the kiosk screens' sake).
    */
    function buildScroll(inst) {
        // Coming from the book: page-flip owns the page elements' inline styles (absolute
        // position, transform, clip-path, size) and tags them .stf__item. Take the book down
        // first, then hand the elements back as plain blocks, or they stack on top of each other.
        if (inst.pageFlip) {
            try { inst.pageFlip.destroy(); } catch (e) { /* already gone */ }
            inst.pageFlip = null;
        }
        if (inst.surfaceEl && inst.surfaceEl.parentNode) {
            inst.surfaceEl.parentNode.removeChild(inst.surfaceEl);
        }
        inst.stage.classList.add('pdf-flipbook-stage--scroll');

        var surface = document.createElement('div');
        surface.className = 'pdf-flipbook-scroll';
        inst.stage.appendChild(surface);
        inst.surfaceEl = surface;

        var ratio = inst.pageW / inst.pageH;
        for (var i = 0; i < inst.pageEls.length; i++) {
            var el = inst.pageEls[i];
            el.className = 'pdf-flipbook-page';
            el.removeAttribute('style');
            el.style.aspectRatio = String(ratio);
            el.style.height = 'auto';
            surface.appendChild(el);
        }

        // The page that covers the middle of the stage is the current one.
        if (inst.pageObserver) { try { inst.pageObserver.disconnect(); } catch (e) { } }
        inst.pageObserver = new IntersectionObserver(function (entries) {
            var best = null;
            for (var k = 0; k < entries.length; k++) {
                var en = entries[k];
                if (!en.isIntersecting) continue;
                if (!best || en.intersectionRatio > best.intersectionRatio) best = en;
            }
            if (best) {
                var idx = parseInt(best.target.getAttribute('data-page'), 10);
                if (!isNaN(idx)) notifyPage(inst, idx, true);
            }
        }, { root: inst.stage, threshold: [0.25, 0.5, 0.75] });
        for (var j = 0; j < inst.pageEls.length; j++) inst.pageObserver.observe(inst.pageEls[j]);

        if (!inst.pinchDown) bindPinch(inst);
        applyLayout(inst, false);
        var landing = Math.min(inst.currentPage, inst.pageCount - 1);
        notifyPage(inst, landing, false);
        // Open on the page the reader was on, not the top of the document.
        if (landing > 0 && inst.pageEls[landing]) inst.stage.scrollTop = inst.pageEls[landing].offsetTop - 8;
    }

    // The scroll surface's own bindings, undone before the book takes the stage back.
    function leaveScroll(inst) {
        if (inst.pageObserver) {
            try { inst.pageObserver.disconnect(); } catch (e) { }
            inst.pageObserver = null;
        }
        if (inst.pinchDown) {
            inst.stage.removeEventListener('pointerdown', inst.pinchDown);
            inst.stage.removeEventListener('pointermove', inst.pinchMove);
            inst.stage.removeEventListener('pointerup', inst.pinchUp);
            inst.stage.removeEventListener('pointercancel', inst.pinchUp);
            inst.pinchDown = inst.pinchMove = inst.pinchUp = null;
        }
        inst.stage.classList.remove('pdf-flipbook-stage--scroll');
        inst.stage.scrollTop = 0;
        inst.stage.scrollLeft = 0;
        for (var i = 0; i < inst.pageEls.length; i++) {
            inst.pageEls[i].removeAttribute('style');
            inst.holder.appendChild(inst.pageEls[i]);
        }
    }

    /*
        The reader's own choice between the book and the scrolling column, remembered in the
        browser. Absent, the screen width decides (see init). Signage never reads it: a wall
        screen is not a reader, and its auto-advance is built on page turns.
    */
    var VIEW_PREF_KEY = 'qmgr-pdf-view';
    var SCROLL_MAX_WIDTH = 1100;

    function readViewPreference() {
        try {
            var v = window.localStorage.getItem(VIEW_PREF_KEY);
            return v === 'book' || v === 'scroll' ? v : null;
        } catch (e) { return null; }
    }

    function saveViewPreference(mode) {
        try { window.localStorage.setItem(VIEW_PREF_KEY, mode); } catch (e) { /* private mode */ }
    }

    function isNarrow() {
        return !!(window.matchMedia && window.matchMedia('(max-width: 900px)').matches);
    }

    // The book a reader gets when they ask for one: a single page on a narrow screen, because a
    // two-page spread there is two half-width pages — the very thing scroll mode exists to avoid.
    function bookModeFor(inst) {
        if (inst.pageCount === 1) return 'single';
        if (isNarrow()) return 'single';
        return inst.bookMode || 'spread';
    }

    // Pinch-to-zoom and double-tap, driving the same zoom the toolbar buttons do. Two pointers'
    // distance ratio scales the surface width; a double tap toggles 1x / 2x on the tapped spot.
    function bindPinch(inst) {
        var stage = inst.stage;
        var pointers = new Map();
        var startDist = 0, startZoom = 1, lastTap = 0;

        function dist() {
            var pts = Array.from(pointers.values());
            var dx = pts[0].x - pts[1].x, dy = pts[0].y - pts[1].y;
            return Math.sqrt(dx * dx + dy * dy);
        }

        inst.pinchDown = function (e) {
            pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
            if (pointers.size === 2) { startDist = dist(); startZoom = inst.zoom; }
            if (pointers.size === 1 && e.pointerType === 'touch') {
                var now = Date.now();
                if (now - lastTap < 300) {
                    var target = Math.abs(inst.zoom - 1) < 0.05 ? 2 : 1;
                    var rect = stage.getBoundingClientRect();
                    var fx = (stage.scrollLeft + (e.clientX - rect.left)) / Math.max(1, stage.scrollWidth);
                    var fy = (stage.scrollTop + (e.clientY - rect.top)) / Math.max(1, stage.scrollHeight);
                    inst.zoom = target;
                    applyLayout(inst, false);
                    stage.scrollLeft = fx * stage.scrollWidth - (e.clientX - rect.left);
                    stage.scrollTop = fy * stage.scrollHeight - (e.clientY - rect.top);
                    invoke(inst, 'OnZoomChanged', inst.zoom);
                    lastTap = 0;
                } else {
                    lastTap = now;
                }
            }
        };
        inst.pinchMove = function (e) {
            if (!pointers.has(e.pointerId)) return;
            pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
            if (pointers.size === 2 && startDist > 0) {
                e.preventDefault();
                var z = Math.max(0.5, Math.min(4, startZoom * (dist() / startDist)));
                if (Math.abs(z - inst.zoom) > 0.02) {
                    var rect = stage.getBoundingClientRect();
                    var pts = Array.from(pointers.values());
                    var cx = (pts[0].x + pts[1].x) / 2 - rect.left, cy = (pts[0].y + pts[1].y) / 2 - rect.top;
                    var fx = (stage.scrollLeft + cx) / Math.max(1, stage.scrollWidth);
                    var fy = (stage.scrollTop + cy) / Math.max(1, stage.scrollHeight);
                    inst.zoom = z;
                    applyLayout(inst, false);
                    stage.scrollLeft = fx * stage.scrollWidth - cx;
                    stage.scrollTop = fy * stage.scrollHeight - cy;
                }
            }
        };
        inst.pinchUp = function (e) {
            pointers.delete(e.pointerId);
            if (pointers.size < 2) {
                if (startDist > 0) invoke(inst, 'OnZoomChanged', inst.zoom);
                startDist = 0;
            }
        };
        stage.addEventListener('pointerdown', inst.pinchDown);
        stage.addEventListener('pointermove', inst.pinchMove, { passive: false });
        stage.addEventListener('pointerup', inst.pinchUp);
        stage.addEventListener('pointercancel', inst.pinchUp);
    }

    function buildBook(inst) {
        if (inst.mode === 'scroll') { buildScroll(inst); return; }

        // Park the page elements somewhere safe first: PageFlip.destroy() takes its wrapper — and
        // everything inside it — out of the DOM, and these elements carry the rendered bitmaps.
        if (inst.pageEls) {
            for (var i = 0; i < inst.pageEls.length; i++) {
                inst.holder.appendChild(inst.pageEls[i]);
            }
        }
        if (inst.pageFlip) {
            try { inst.pageFlip.destroy(); } catch (e) { /* already gone */ }
            inst.pageFlip = null;
        }
        if (inst.surfaceEl && inst.surfaceEl.parentNode) {
            inst.surfaceEl.parentNode.removeChild(inst.surfaceEl);
        }

        var surface = document.createElement('div');
        surface.className = 'pdf-flipbook-surface';
        inst.stage.appendChild(surface);
        inst.surfaceEl = surface;

        var single = inst.mode !== 'spread';
        var pageFlip = new window.St.PageFlip(surface, {
            width: Math.round(inst.pageW),
            height: Math.round(inst.pageH),
            size: 'stretch',
            // page-flip has no "always one page" switch. Its only route to a single page is the
            // portrait orientation, chosen when `blockWidth < 2 * minWidth && usePortrait`. A
            // sentinel minWidth makes that test always true; applyLayout clears the min-width it
            // leaves on the element.
            minWidth: single ? 1000000 : 1,
            maxWidth: 100000,
            minHeight: 1,
            maxHeight: 100000,
            autoSize: false,
            usePortrait: single,
            showCover: false,
            maxShadowOpacity: 0.4,
            mobileScrollSupport: false,
            useMouseEvents: true,
            flippingTime: 700,
            startPage: Math.min(inst.currentPage, inst.pageCount - 1)
        });

        pageFlip.loadFromHTML(inst.pageEls);
        inst.pageFlip = pageFlip;

        pageFlip.on('flip', function (e) {
            notifyPage(inst, e.data, true);
        });

        applyLayout(inst, false);
        notifyPage(inst, pageFlip.getCurrentPageIndex(), false);
    }

    function notifyPage(inst, index, userDriven) {
        if (!inst || inst.disposed) return;
        inst.currentPage = index;
        setActiveThumb(inst, index);
        if (inst.lastNotified !== index) {
            inst.lastNotified = index;
            invoke(inst, 'OnPageFlipped', index + 1, inst.pageCount);
        }
        ensurePageRendered(inst, index);
        ensurePageRendered(inst, index + 1);
        if (userDriven && inst.autoTimer) {
            // A person just turned a page by hand: give them the full dwell time on it rather
            // than whatever was left of the previous tick.
            startAutoAdvance(inst);
        }
    }

    /* ---------------------------------------------------------------- *
     * Rasterising
     * ---------------------------------------------------------------- */

    function baseScale(inst) {
        var box = pageBox(inst);
        // Phones are 3x; a 2x cap left text soft exactly where it is smallest.
        var dpr = Math.min(window.devicePixelRatio || 1, 3);
        var scale = (box.w * dpr) / inst.pageW;
        return Math.max(0.5, Math.min(scale, 5));
    }

    function clampScale(inst, scale) {
        var maxByWidth = MAX_PAGE_EDGE / inst.pageW;
        var maxByHeight = MAX_PAGE_EDGE / inst.pageH;
        return Math.max(0.4, Math.min(scale, maxByWidth, maxByHeight));
    }

    // Diagonal, repeated, low-contrast text across the whole page. Sized from the page so it
    // reads the same at every zoom (the bitmap is re-rendered per zoom level, so this runs again).
    function drawWatermark(ctx, width, height, text) {
        var size = Math.max(14, Math.round(Math.min(width, height) / 22));
        ctx.save();
        // A small render (a phone at fit-width) has 9pt body text at a few pixels; the same
        // watermark density that reads as a tint on a desktop page competes with the words there.
        // Lighter and sparser below ~900px, still on every page.
        var small = Math.min(width, height) < 900;
        ctx.globalAlpha = small ? 0.11 : 0.16;
        ctx.fillStyle = '#7a2847';
        ctx.font = '600 ' + size + 'px Poppins, Arial, sans-serif';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.translate(width / 2, height / 2);
        ctx.rotate(-Math.PI / 5);
        var stepY = size * (small ? 9 : 6);
        var stepX = Math.max(ctx.measureText(text).width + size * 4, size * 12);
        var span = Math.max(width, height) * 1.5;
        var row = 0;
        for (var y = -span; y <= span; y += stepY, row++) {
            var offset = (row % 2) * (stepX / 2);
            for (var x = -span + offset; x <= span; x += stepX) {
                ctx.fillText(text, x, y);
            }
        }
        ctx.restore();
    }

    function renderPage(inst, index, wantedScale) {
        if (index < 0 || index >= inst.pageCount) return Promise.resolve();

        var scale = clampScale(inst, wantedScale || baseScale(inst));
        if ((inst.renderedScale[index] || 0) >= scale - 0.02) return Promise.resolve();
        if (inst.renderJobs[index]) return inst.renderJobs[index];

        var gen = inst.generation;
        var job = (async function () {
            var page = await inst.pdf.getPage(index + 1);
            if (!alive(inst, gen)) return;

            var viewport = page.getViewport({ scale: scale });
            var canvas = document.createElement('canvas');
            canvas.width = Math.max(1, Math.floor(viewport.width));
            canvas.height = Math.max(1, Math.floor(viewport.height));

            var ctx = canvas.getContext('2d', { alpha: false });
            ctx.fillStyle = '#ffffff';
            ctx.fillRect(0, 0, canvas.width, canvas.height);
            await page.render({ canvasContext: ctx, viewport: viewport }).promise;
            if (!alive(inst, gen)) return;

            // Into the bitmap itself, not an overlay element: an overlay is one "delete node"
            // away in devtools, a watermark baked into every page image is not.
            if (inst.watermark) drawWatermark(ctx, canvas.width, canvas.height, inst.watermark);

            var url = await canvasToObjectUrl(canvas);
            if (!alive(inst, gen)) { releaseUrl(url); return; }

            var img = inst.pageEls[index].querySelector('img');
            var previous = inst.pageUrls[index];
            img.src = url;
            // Decode before dropping the old bitmap, so a re-render at higher quality does not
            // flash an empty page.
            try { if (img.decode) await img.decode(); } catch (e) { /* decode is best-effort */ }
            if (!alive(inst, gen)) { releaseUrl(url); return; }

            releaseUrl(previous);
            inst.pageUrls[index] = url;
            inst.renderedScale[index] = scale;

            if (!inst.thumbUrls[index]) buildThumbFrom(inst, index, canvas);

            page.cleanup();
        })();

        inst.renderJobs[index] = job;
        return job.catch(function (e) {
            console.error('PDF flipbook: page ' + (index + 1) + ' failed to render', e);
        }).then(function () {
            delete inst.renderJobs[index];
        });
    }

    function ensurePageRendered(inst, index) {
        if (index < 0 || index >= inst.pageCount) return;
        renderPage(inst, index);
    }

    // Fills in every page that is not on screen yet, one at a time, yielding between pages so the
    // flip animation and the rest of the app keep their frames.
    async function backgroundRender(inst) {
        var gen = inst.generation;
        for (var i = 0; i < inst.pageCount; i++) {
            if (!alive(inst, gen)) return;
            await renderPage(inst, i);
            inst.renderedCount = Math.min(inst.pageCount, inst.renderedCount + 1);
            reportProgress(inst, false);
            await idle();
        }
        if (alive(inst, gen)) reportProgress(inst, true);
    }

    function reportProgress(inst, force) {
        var now = Date.now();
        if (!force && now - inst.lastProgressAt < 250) return;
        inst.lastProgressAt = now;
        invoke(inst, 'OnRenderProgress', inst.renderedCount, inst.pageCount);
    }

    // Re-renders what is on screen at the quality the current zoom deserves. Debounced, and only
    // the visible spread, so zooming stays responsive on a long document.
    function scheduleQualityPass(inst) {
        clearTimeout(inst.qualityTimer);
        inst.qualityTimer = setTimeout(function () {
            if (inst.disposed) return;
            var wanted = baseScale(inst);
            var current = inst.currentPage;
            renderPage(inst, current, wanted);
            if (inst.mode === 'spread') renderPage(inst, current + 1, wanted);
        }, 350);
    }

    /* ---------------------------------------------------------------- *
     * Thumbnails
     * ---------------------------------------------------------------- */

    function buildThumbRail(inst) {
        var rail = document.getElementById(inst.thumbsId);
        if (!rail) return;
        rail.innerHTML = '';
        inst.thumbEls = [];

        for (var i = 0; i < inst.pageCount; i++) {
            (function (index) {
                var btn = document.createElement('button');
                btn.type = 'button';
                btn.className = 'pdf-flipbook-thumb' + (index === inst.currentPage ? ' pdf-flipbook-thumb--active' : '');
                btn.setAttribute('data-page-index', String(index));
                btn.title = 'Page ' + (index + 1);
                btn.setAttribute('aria-label', 'Go to page ' + (index + 1));

                var img = document.createElement('img');
                img.alt = '';
                img.style.aspectRatio = inst.pageW + ' / ' + inst.pageH;
                btn.appendChild(img);

                var label = document.createElement('span');
                label.textContent = String(index + 1);
                btn.appendChild(label);

                btn.addEventListener('click', function () {
                    goToPageIndex(inst, index);
                });

                rail.appendChild(btn);
                inst.thumbEls.push(btn);
            })(i);
        }
    }

    // Downscales the page bitmap we have just drawn rather than asking pdf.js for a second
    // render of the same page.
    function buildThumbFrom(inst, index, sourceCanvas) {
        try {
            var scale = THUMB_WIDTH / sourceCanvas.width;
            var c = document.createElement('canvas');
            c.width = THUMB_WIDTH;
            c.height = Math.max(1, Math.round(sourceCanvas.height * scale));
            var ctx = c.getContext('2d');
            ctx.fillStyle = '#ffffff';
            ctx.fillRect(0, 0, c.width, c.height);
            ctx.drawImage(sourceCanvas, 0, 0, c.width, c.height);
            var url = c.toDataURL('image/jpeg', 0.7);
            inst.thumbUrls[index] = url;
            if (inst.thumbEls && inst.thumbEls[index]) {
                inst.thumbEls[index].querySelector('img').src = url;
            }
        } catch (e) {
            // A thumbnail is decoration; never let it take the page down with it.
        }
    }

    function setActiveThumb(inst, index) {
        if (!inst.thumbEls) return;
        for (var i = 0; i < inst.thumbEls.length; i++) {
            inst.thumbEls[i].classList.toggle('pdf-flipbook-thumb--active', i === index);
        }
        var el = inst.thumbEls[index];
        if (el) el.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    }

    function markThumbHits(inst, pagesWithHits) {
        if (!inst.thumbEls) return;
        var set = new Set(pagesWithHits || []);
        for (var i = 0; i < inst.thumbEls.length; i++) {
            inst.thumbEls[i].classList.toggle('pdf-flipbook-thumb--hit', set.has(i));
        }
    }

    /* ---------------------------------------------------------------- *
     * Navigation
     * ---------------------------------------------------------------- */

    // Which spread a page index sits in. Empirically (and matching PageCollection's own
    // landscapeSpread construction with showCover off) pages pair up [0,1], [2,3], … so this is
    // integer division by the number of pages on screen.
    function spreadOf(inst, pageIndex) {
        return Math.floor(pageIndex / (inst.mode === 'spread' ? 2 : 1));
    }

    /*
        Jump to a page — from a thumbnail, the page-number box, or a search hit.

        `pageFlip.flip()` ANIMATES, but only correctly to an ADJACENT spread. Its `flipToPage` primes
        `currentSpreadIndex` to one-before-target and then calls `flipNext`/`flipPrev`, which animate
        from whatever is actually RENDERED — so a jump across several spreads moves exactly one, in
        the right direction, and stops. Worse, `flipToPage` wraps that in its own `try/catch`, so it
        never throws and a `catch`-based fallback around it is dead code.

        Measured before this fix: stepping through search matches from pages 9,10 went 7,8 → 5,6 →
        3,4 — one spread per step, ignoring the target every time.

        So: animate only when the target really is the next or previous spread, which is the case
        that makes a page turn feel right. Anything further is an instant `turnToPage`, which goes
        through `PageCollection.show()` and lands exactly.
    */
    function goToPageIndex(inst, index) {
        if (index < 0 || index >= inst.pageCount) return;

        if (inst.mode === 'scroll') {
            var el = inst.pageEls[index];
            if (el) inst.stage.scrollTop = el.offsetTop - 8;
            notifyPage(inst, index, true);
            return;
        }

        if (!inst.pageFlip) return;

        // Land any flip that is still animating BEFORE deciding where we are.
        //
        // An animation in flight owns an `onAnimateEnd` that calls showNext()/showPrev() when it
        // lands. Jump over the top of it and that handler fires afterwards, moving the book one
        // spread off the page we just asked for. On signage that is a live race, not a theoretical
        // one: the auto-advance flip takes 700ms, and a visitor tapping a thumbnail during it got
        // the wrong page. `finishAnimation` runs the final frame and that handler immediately, so
        // the reading below is of a settled book.
        var render = inst.pageFlip.getRender && inst.pageFlip.getRender();
        if (render && render.finishAnimation) {
            try { render.finishAnimation(); } catch (e) { /* nothing was animating */ }
        }

        var from = spreadOf(inst, inst.pageFlip.getCurrentPageIndex());
        var to = spreadOf(inst, index);

        if (Math.abs(to - from) === 1) {
            inst.pageFlip.flip(index);
        } else if (to !== from) {
            inst.pageFlip.turnToPage(index);
        }

        notifyPage(inst, index, true);
    }

    function startAutoAdvance(inst) {
        stopAutoAdvance(inst);
        if (!inst.autoAdvance || inst.pageCount <= 1) return;

        inst.autoTimer = setInterval(function () {
            if (inst.disposed || !inst.pageFlip) return;

            var step = inst.mode === 'spread' ? 2 : 1;
            var current = inst.pageFlip.getCurrentPageIndex();

            if (current + step < inst.pageCount) {
                inst.pageFlip.flipNext();
                return;
            }

            if (inst.loop) {
                // Straight back to the front rather than flipping backwards through the whole
                // document: on signage the rewind reads as a fault, not as a transition.
                inst.pageFlip.turnToPage(0);
                notifyPage(inst, 0, false);
                return;
            }

            stopAutoAdvance(inst);
            invoke(inst, 'OnFlipbookEnded');
        }, Math.max(1, inst.pageDuration) * 1000);
    }

    function stopAutoAdvance(inst) {
        if (inst.autoTimer) {
            clearInterval(inst.autoTimer);
            inst.autoTimer = null;
        }
    }

    /* ---------------------------------------------------------------- *
     * Search
     * ---------------------------------------------------------------- */

    // Built on first use only. On an unattended display nobody ever searches, and walking the
    // text of every page costs real CPU on the low-powered boxes these screens run on.
    function buildIndex(inst) {
        if (inst.textIndex) return Promise.resolve(inst.textIndex);
        if (inst.indexPromise) return inst.indexPromise;

        var gen = inst.generation;
        inst.indexPromise = (async function () {
            var pages = [];
            for (var i = 0; i < inst.pageCount; i++) {
                if (!alive(inst, gen)) return pages;

                var page = await inst.pdf.getPage(i + 1);
                var viewport = page.getViewport({ scale: 1 });
                var content = await page.getTextContent();

                var text = '';
                var items = [];
                for (var j = 0; j < content.items.length; j++) {
                    var it = content.items[j];
                    var str = it.str || '';
                    if (str) {
                        var tx = window.pdfjsLib.Util.transform(viewport.transform, it.transform);
                        var fontHeight = Math.hypot(tx[2], tx[3]) || it.height || 10;
                        items.push({
                            start: text.length,
                            len: str.length,
                            x: (tx[4] / viewport.width) * 100,
                            y: ((tx[5] - fontHeight) / viewport.height) * 100,
                            w: ((it.width || 0) / viewport.width) * 100,
                            h: (fontHeight / viewport.height) * 100
                        });
                        text += str;
                    }
                    if (it.hasEOL) text += '\n';
                }

                pages.push({ text: text, lower: text.toLowerCase(), items: items });
                page.cleanup();
                await idle();
            }
            inst.textIndex = pages;
            return pages;
        })();

        return inst.indexPromise;
    }

    function snippetAround(text, at, len) {
        var start = Math.max(0, at - 32);
        var end = Math.min(text.length, at + len + 32);
        var s = text.slice(start, end).replace(/\s+/g, ' ').trim();
        return (start > 0 ? '…' : '') + s + (end < text.length ? '…' : '');
    }

    // Rectangles covering [start, start+len) of a page's text. A match is split across every text
    // run it touches, and clipped proportionally inside the first and last run — pdf.js gives a
    // run's box but not per-character metrics, so the ends are an approximation by character
    // count. It is a highlight, not a caret.
    function rectsForMatch(pageIndex, inst, start, len) {
        var items = inst.textIndex[pageIndex].items;
        var end = start + len;
        var rects = [];
        for (var i = 0; i < items.length; i++) {
            var it = items[i];
            var from = Math.max(start, it.start);
            var to = Math.min(end, it.start + it.len);
            if (to <= from || it.len === 0) continue;
            var f0 = (from - it.start) / it.len;
            var f1 = (to - it.start) / it.len;
            rects.push({
                x: it.x + it.w * f0,
                y: it.y,
                w: Math.max(0.2, it.w * (f1 - f0)),
                h: it.h
            });
        }
        return rects;
    }

    function paintHighlights(inst) {
        for (var i = 0; i < inst.pageCount; i++) {
            var overlay = inst.pageEls[i].querySelector('.pdf-flipbook-page-overlay');
            if (overlay) overlay.innerHTML = '';
        }
        if (!inst.matches || !inst.matches.length) return;

        for (var m = 0; m < inst.matches.length; m++) {
            var match = inst.matches[m];
            var overlay2 = inst.pageEls[match.page].querySelector('.pdf-flipbook-page-overlay');
            if (!overlay2) continue;
            var rects = rectsForMatch(match.page, inst, match.start, match.len);
            for (var r = 0; r < rects.length; r++) {
                var div = document.createElement('div');
                div.className = 'pdf-flipbook-hit' + (m === inst.matchIndex ? ' pdf-flipbook-hit--current' : '');
                div.style.left = rects[r].x + '%';
                div.style.top = rects[r].y + '%';
                div.style.width = rects[r].w + '%';
                div.style.height = rects[r].h + '%';
                overlay2.appendChild(div);
            }
        }
    }

    /* ---------------------------------------------------------------- *
     * Idle chrome (signage)
     * ---------------------------------------------------------------- */

    // Idle drives TWO things off one concept: the chrome fades, and auto-advance pauses.
    //
    // The pause is not a nicety. A signage kiosk that keeps turning pages every few seconds while
    // somebody is reading makes search and zoom useless — you find page 9, and three seconds later
    // the document has moved on without you. So any real interaction stops the timer, and the same
    // idle timeout that fades the chrome back out is what resumes it. Found by watching the page
    // flip away mid-search during the e2e.
    function bindIdle(inst, rootId) {
        var root = document.getElementById(rootId);
        if (!root) return;
        inst.rootEl = root;

        var sleep = function () {
            root.classList.add('pdf-flipbook--idle');
            // Back to unattended playback, from wherever the reader left off.
            startAutoAdvance(inst);
        };

        // fromUser is false for the initial call, which only arms the idle timer: the screen has
        // not been touched, so there is nothing to pause and signage should start flipping at once.
        var wake = function (fromUser) {
            root.classList.remove('pdf-flipbook--idle');
            if (fromUser) stopAutoAdvance(inst);
            clearTimeout(inst.idleTimer);
            inst.idleTimer = setTimeout(sleep, IDLE_MS);
        };

        inst.idleHandler = function () { wake(true); };
        root.addEventListener('pointermove', inst.idleHandler);
        root.addEventListener('pointerdown', inst.idleHandler);
        root.addEventListener('keydown', inst.idleHandler);
        wake(false);
    }

    // Reported from the document, not from the toolbar button, so leaving full screen with Escape
    // still corrects the button's own label.
    function bindFullscreen(inst) {
        inst.fullscreenHandler = function () {
            invoke(inst, 'OnFullscreenChanged', !!document.fullscreenElement);
        };
        document.addEventListener('fullscreenchange', inst.fullscreenHandler);
    }

    /* ---------------------------------------------------------------- *
     * Public interop surface
     * ---------------------------------------------------------------- */

    return {
        init: async function (options) {
            var containerId = options.containerId;
            var stage = document.getElementById(containerId);
            if (!stage) {
                return { success: false, pageCount: 0, errorMessage: 'Viewer container not found' };
            }

            this.dispose(containerId);

            var inst = {
                id: containerId,
                stage: stage,
                thumbsId: options.thumbsId,
                rootId: options.rootId,
                url: options.url,
                dotNetRef: options.dotNetRef,
                autoAdvance: !!options.autoAdvance,
                loop: !!options.loop,
                pageDuration: options.pageDurationSeconds || 6,
                mode: options.viewMode === 'single' ? 'single' : 'spread',
                // Drawn onto every rendered bitmap (see drawWatermark): the viewer's identity,
                // so a photograph of the screen names its source. Null for signage.
                watermark: options.watermark || null,
                viewOnly: !!options.viewOnly,
                zoom: 1,
                generation: ++generationSeq,
                disposed: false,
                currentPage: 0,
                lastNotified: -1,
                renderedCount: 0,
                lastProgressAt: 0,
                renderJobs: {},
                renderedScale: {},
                pageUrls: {},
                thumbUrls: {},
                matches: [],
                matchIndex: -1
            };
            instances.set(containerId, inst);

            try {
                await ensureLibs();
                if (inst.disposed) return { success: false, pageCount: 0, errorMessage: 'Cancelled' };

                var pdf = await window.pdfjsLib.getDocument({ url: options.url }).promise;
                if (inst.disposed) { try { pdf.destroy(); } catch (e) { } return { success: false, pageCount: 0, errorMessage: 'Cancelled' }; }

                inst.pdf = pdf;
                inst.pageCount = pdf.numPages;

                var first = await pdf.getPage(1);
                var vp = first.getViewport({ scale: 1 });
                inst.pageW = vp.width;
                inst.pageH = vp.height;
                first.cleanup();

                if (inst.pageCount === 1) inst.mode = 'single';

                // SCROLL MODE on a phone. A flip-book letterboxes a whole A4 page into a stage
                // shorter than the screen, which on a 390px phone puts body text at about 6px —
                // seen in the field on 2026-09-15. The industry answer (Drive, Adobe's mobile
                // reader, DocSend) is fit-to-width with continuous vertical scroll and native
                // pinch-zoom, so that is what narrow screens get. Signage keeps the book: a screen
                // on a wall is not a phone, and the auto-advance loop is built on page turns.
                //
                // A reader who has pressed the Book / Scroll toggle gets what they chose, on any
                // screen — the default below only applies when they have not said.
                if (options.scrollOnNarrow !== false && !inst.autoAdvance) {
                    var pref = readViewPreference();
                    var narrow = isNarrow();
                    if (pref === 'scroll' || (pref !== 'book' && narrow)) {
                        inst.bookMode = inst.mode;
                        inst.mode = 'scroll';
                    } else if (narrow) {
                        inst.bookMode = inst.mode;
                        inst.mode = bookModeFor(inst);
                    }
                }

                buildPageElements(inst);
                buildThumbRail(inst);
                buildBook(inst);

                // Page one, and its partner in a spread, before the viewer is revealed — so what
                // appears is the document, not an empty book that fills in afterwards.
                await renderPage(inst, 0);
                if (inst.mode === 'spread' && inst.pageCount > 1) await renderPage(inst, 1);
                if (inst.disposed) return { success: false, pageCount: 0, errorMessage: 'Cancelled' };

                inst.resizeObserver = new ResizeObserver(function () {
                    clearTimeout(inst.resizeTimer);
                    inst.resizeTimer = setTimeout(function () { applyLayout(inst, false); }, 120);
                });
                inst.resizeObserver.observe(stage);

                bindIdle(inst, options.rootId);
                bindFullscreen(inst);
                if (inst.viewOnly) {
                    // A deterrent for the casual right-click-save on a view-only share. Not a
                    // control and not presented as one — the page says so.
                    inst.contextHandler = function (e) { e.preventDefault(); };
                    stage.addEventListener('contextmenu', inst.contextHandler);
                }
                startAutoAdvance(inst);
                backgroundRender(inst);

                return { success: true, pageCount: inst.pageCount, errorMessage: null, mode: inst.mode };
            } catch (e) {
                console.error('PDF flipbook init failed:', e);
                return {
                    success: false,
                    pageCount: 0,
                    errorMessage: (e && e.message) ? e.message : 'Failed to load PDF'
                };
            }
        },

        next: function (containerId) {
            var inst = instances.get(containerId);
            if (!inst) return;
            if (inst.mode === 'scroll') { goToPageIndex(inst, inst.currentPage + 1); return; }
            if (inst.pageFlip) {
                inst.pageFlip.flipNext();
                if (inst.autoTimer) startAutoAdvance(inst);
            }
        },

        prev: function (containerId) {
            var inst = instances.get(containerId);
            if (!inst) return;
            if (inst.mode === 'scroll') { goToPageIndex(inst, inst.currentPage - 1); return; }
            if (inst.pageFlip) {
                inst.pageFlip.flipPrev();
                if (inst.autoTimer) startAutoAdvance(inst);
            }
        },

        goToPage: function (containerId, pageNumber) {
            var inst = instances.get(containerId);
            if (!inst) return 0;
            var index = Math.max(0, Math.min(inst.pageCount - 1, (pageNumber | 0) - 1));
            goToPageIndex(inst, index);
            return index + 1;
        },

        setZoom: function (containerId, zoom) {
            var inst = instances.get(containerId);
            if (!inst) return 1;
            inst.zoom = Math.max(0.5, Math.min(4, zoom));
            applyLayout(inst, true);
            return inst.zoom;
        },

        // 'scroll' and 'book' switch between the scrolling column and the flip-book (and are
        // remembered); 'single' and 'spread' pick the book's page layout. Returns the mode in
        // force, which is what the toolbar renders from.
        setViewMode: function (containerId, mode) {
            var inst = instances.get(containerId);
            if (!inst || !inst.pageEls) return inst ? inst.mode : 'spread';

            if (mode === 'scroll' || mode === 'book') {
                if (inst.autoAdvance) return inst.mode;
                var wantScroll = mode === 'scroll';
                if (wantScroll === (inst.mode === 'scroll')) return inst.mode;
                saveViewPreference(mode);
                inst.zoom = 1;
                if (wantScroll) {
                    inst.bookMode = inst.mode;
                    inst.mode = 'scroll';
                    buildScroll(inst);
                } else {
                    leaveScroll(inst);
                    inst.mode = bookModeFor(inst);
                    buildBook(inst);
                }
                paintHighlights(inst);
                invoke(inst, 'OnZoomChanged', inst.zoom);
                return inst.mode;
            }

            if (inst.mode === 'scroll') return 'scroll';
            if (!inst.pageFlip) return 'spread';

            var wanted = mode === 'single' ? 'single' : 'spread';
            if (wanted === inst.mode) return inst.mode;

            inst.mode = wanted;
            inst.currentPage = inst.pageFlip.getCurrentPageIndex();
            // Rebuilding is cheap: the bitmaps live on the page elements, which are carried over
            // rather than re-rendered.
            buildBook(inst);
            paintHighlights(inst);
            startAutoAdvance(inst);
            return inst.mode;
        },

        // Blazor removes the rail element when the rail is hidden and creates a fresh, empty one
        // when it is shown again, so the thumbnails have to be rebuilt into it from the bitmaps
        // already made. Cheap: nothing is re-rendered.
        refreshThumbs: function (containerId) {
            var inst = instances.get(containerId);
            if (!inst || !inst.pageEls) return;
            buildThumbRail(inst);
            for (var i = 0; i < inst.pageCount; i++) {
                if (inst.thumbUrls[i] && inst.thumbEls[i]) inst.thumbEls[i].querySelector('img').src = inst.thumbUrls[i];
            }
            markThumbHits(inst, inst.matches.map(function (m) { return m.page; }));
        },

        search: async function (containerId, query) {
            var inst = instances.get(containerId);
            if (!inst) return { total: 0, current: 0, page: 0 };

            var text = (query || '').trim();
            if (text.length < 2) {
                inst.matches = [];
                inst.matchIndex = -1;
                paintHighlights(inst);
                markThumbHits(inst, []);
                return { total: 0, current: 0, page: 0 };
            }

            await buildIndex(inst);
            if (inst.disposed || !inst.textIndex) return { total: 0, current: 0, page: 0 };

            var needle = text.toLowerCase();
            var matches = [];
            var hitPages = [];
            for (var p = 0; p < inst.textIndex.length && matches.length < MAX_MATCHES; p++) {
                var hay = inst.textIndex[p].lower;
                var from = 0;
                var at;
                while ((at = hay.indexOf(needle, from)) !== -1 && matches.length < MAX_MATCHES) {
                    matches.push({
                        page: p,
                        start: at,
                        len: needle.length,
                        snippet: snippetAround(inst.textIndex[p].text, at, needle.length)
                    });
                    from = at + needle.length;
                }
                if (matches.length && matches[matches.length - 1].page === p) hitPages.push(p);
            }

            inst.matches = matches;
            inst.matchIndex = matches.length ? 0 : -1;
            paintHighlights(inst);
            markThumbHits(inst, hitPages);

            if (matches.length) {
                goToPageIndex(inst, matches[0].page);
                return { total: matches.length, current: 1, page: matches[0].page + 1 };
            }
            return { total: 0, current: 0, page: 0 };
        },

        stepMatch: function (containerId, delta) {
            var inst = instances.get(containerId);
            if (!inst || !inst.matches.length) return { total: 0, current: 0, page: 0 };

            var count = inst.matches.length;
            inst.matchIndex = ((inst.matchIndex + delta) % count + count) % count;
            paintHighlights(inst);
            var match = inst.matches[inst.matchIndex];
            goToPageIndex(inst, match.page);
            return { total: count, current: inst.matchIndex + 1, page: match.page + 1 };
        },

        clearSearch: function (containerId) {
            var inst = instances.get(containerId);
            if (!inst) return;
            inst.matches = [];
            inst.matchIndex = -1;
            paintHighlights(inst);
            markThumbHits(inst, []);
        },

        toggleFullscreen: function (elementId) {
            var el = document.getElementById(elementId);
            if (!el) return false;
            if (!document.fullscreenElement) {
                var request = el.requestFullscreen || el.webkitRequestFullscreen || el.msRequestFullscreen;
                if (request) request.call(el);
                return true;
            }
            var exit = document.exitFullscreen || document.webkitExitFullscreen || document.msExitFullscreen;
            if (exit) exit.call(document);
            return false;
        },

        isFullscreen: function () {
            return !!document.fullscreenElement;
        },

        dispose: function (containerId) {
            var inst = instances.get(containerId);
            if (!inst) {
                var orphan = document.getElementById(containerId);
                if (orphan) orphan.innerHTML = '';
                return;
            }

            inst.disposed = true;
            inst.generation = ++generationSeq;

            stopAutoAdvance(inst);
            clearTimeout(inst.qualityTimer);
            clearTimeout(inst.resizeTimer);
            clearTimeout(inst.idleTimer);

            if (inst.resizeObserver) {
                try { inst.resizeObserver.disconnect(); } catch (e) { }
            }
            if (inst.fullscreenHandler) {
                document.removeEventListener('fullscreenchange', inst.fullscreenHandler);
            }
            if (inst.contextHandler && inst.stage) {
                inst.stage.removeEventListener('contextmenu', inst.contextHandler);
            }
            if (inst.pageObserver) {
                try { inst.pageObserver.disconnect(); } catch (e) { }
            }
            if (inst.pinchDown && inst.stage) {
                inst.stage.removeEventListener('pointerdown', inst.pinchDown);
                inst.stage.removeEventListener('pointermove', inst.pinchMove);
                inst.stage.removeEventListener('pointerup', inst.pinchUp);
                inst.stage.removeEventListener('pointercancel', inst.pinchUp);
                inst.stage.classList.remove('pdf-flipbook-stage--scroll');
            }
            if (inst.rootEl && inst.idleHandler) {
                inst.rootEl.removeEventListener('pointermove', inst.idleHandler);
                inst.rootEl.removeEventListener('pointerdown', inst.idleHandler);
                inst.rootEl.removeEventListener('keydown', inst.idleHandler);
            }
            if (inst.pageFlip) {
                try { inst.pageFlip.destroy(); } catch (e) { }
            }

            // Object URLs are held by the browser until revoked. A signage screen replays its
            // playlist for days, so leaking a document's worth of page bitmaps per pass is the
            // difference between a screen that stays up and one that has to be rebooted weekly.
            Object.keys(inst.pageUrls).forEach(function (k) { releaseUrl(inst.pageUrls[k]); });
            inst.pageUrls = {};

            if (inst.pdf) {
                try { inst.pdf.destroy(); } catch (e) { }
                inst.pdf = null;
            }

            if (inst.stage) inst.stage.innerHTML = '';
            var rail = inst.thumbsId ? document.getElementById(inst.thumbsId) : null;
            if (rail) rail.innerHTML = '';

            instances.delete(containerId);
        }
    };
})();
