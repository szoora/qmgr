// Ambient background for the public-facing auth/document pages (login, register, privacy,
// terms, support) — thin falling bars drifting down over the solid brand-wine background, with
// a soft cursor-follow spotlight that brightens nearby bars. Deliberately restrained: no glow,
// no color gradient, no blur — just quiet, signage-ticker-like motion so the page doesn't read
// as a static block of color. Pure canvas 2D, no library.

window.ambientBackground = (function () {
    let raf = null;
    let canvas = null;
    let ctx = null;
    let bars = [];
    let pointer = { x: -9999, y: -9999 };
    let dpr = 1;
    let running = false;
    let reduceMotion = false;

    function resize() {
        if (!canvas) return;
        dpr = Math.min(window.devicePixelRatio || 1, 2);
        canvas.width = canvas.clientWidth * dpr;
        canvas.height = canvas.clientHeight * dpr;
    }

    function makeBar(seedY) {
        const w = canvas.clientWidth;
        const h = canvas.clientHeight;
        return {
            x: Math.random() * w,
            y: seedY !== undefined ? seedY : Math.random() * h,
            len: 14 + Math.random() * 46,
            speed: 10 + Math.random() * 26, // px/sec
            baseAlpha: 0.03 + Math.random() * 0.07,
            width: Math.random() < 0.15 ? 2 : 1
        };
    }

    function seedBars() {
        const w = canvas.clientWidth;
        const density = Math.max(24, Math.min(90, Math.floor((w * canvas.clientHeight) / 26000)));
        bars = Array.from({ length: density }, () => makeBar());
    }

    let last = 0;
    function tick(ts) {
        if (!running) return;
        const dt = last ? Math.min((ts - last) / 1000, 0.05) : 0;
        last = ts;

        const w = canvas.clientWidth;
        const h = canvas.clientHeight;
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.clearRect(0, 0, w, h);

        for (const bar of bars) {
            if (!reduceMotion) bar.y += bar.speed * dt;
            if (bar.y - bar.len > h) {
                Object.assign(bar, makeBar(-bar.len));
            }

            // Cursor-follow "spotlight": bars within ~180px of the pointer get brighter,
            // falling off smoothly with distance — the one bit of direct interactivity.
            const dx = bar.x - pointer.x;
            const dy = (bar.y - bar.len / 2) - pointer.y;
            const dist = Math.sqrt(dx * dx + dy * dy);
            const boost = Math.max(0, 1 - dist / 180);
            const alpha = Math.min(0.55, bar.baseAlpha + boost * 0.28);

            ctx.strokeStyle = `rgba(255,255,255,${alpha})`;
            ctx.lineWidth = bar.width;
            ctx.beginPath();
            ctx.moveTo(bar.x, bar.y - bar.len);
            ctx.lineTo(bar.x, bar.y);
            ctx.stroke();
        }

        raf = requestAnimationFrame(tick);
    }

    function onPointerMove(e) {
        const rect = canvas.getBoundingClientRect();
        pointer.x = e.clientX - rect.left;
        pointer.y = e.clientY - rect.top;
    }

    function onPointerLeave() {
        pointer.x = -9999;
        pointer.y = -9999;
    }

    function start(canvasId) {
        stop();
        canvas = document.getElementById(canvasId);
        if (!canvas) return;
        ctx = canvas.getContext('2d');
        reduceMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

        resize();
        seedBars();
        running = true;
        last = 0;

        window.addEventListener('resize', resize);
        canvas.addEventListener('pointermove', onPointerMove);
        canvas.addEventListener('pointerleave', onPointerLeave);

        // A still first frame is enough for reduced-motion users — no continuous rAF loop.
        if (reduceMotion) {
            tick(0);
            running = false;
        } else {
            raf = requestAnimationFrame(tick);
        }
    }

    function stop() {
        running = false;
        if (raf) cancelAnimationFrame(raf);
        raf = null;
        if (canvas) {
            window.removeEventListener('resize', resize);
            canvas.removeEventListener('pointermove', onPointerMove);
            canvas.removeEventListener('pointerleave', onPointerLeave);
        }
        canvas = null;
        ctx = null;
        bars = [];
    }

    return { start, stop };
})();
