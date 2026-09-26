// The notification chime (plan CALENDAR_AUDIENCES_AND_IMPORT_ROUTING E7, 2026-09-26).
//
// SYNTHESISED, NOT A FILE: two short sine tones through the Web Audio API — nothing to host, no licence, nothing to
// install (the standing no-server-dependencies rule). About 350 ms, soft, and never on a loop.
//
// THE BROWSER DECIDES WHETHER SOUND MAY PLAY. Chrome's autoplay policy creates an AudioContext "suspended" until the
// person has interacted with the page, and resume() is only allowed inside a gesture — so the context is primed on the
// first click or key press, and a chime before then is silently skipped rather than thrown (MDN: play() rejects with
// NotAllowedError, "websites should be prepared to handle this"). The caller ALWAYS shows a visible toast as well:
// WCAG 2.2 SC 1.3.3 — sound is never the only cue.
//
// THROTTLED to one chime in ten seconds: an import that notifies somebody of twelve things makes one sound, not twelve.
window.qmgrSound = (function () {
    let ctx = null;
    let lastAt = 0;
    const MIN_GAP_MS = 10000;

    function context() {
        if (ctx) return ctx;
        const Ctor = window.AudioContext || window.webkitAudioContext;
        if (!Ctor) return null;
        try { ctx = new Ctor(); } catch (e) { ctx = null; }
        return ctx;
    }

    function prime() {
        const c = context();
        if (c && c.state === 'suspended') { c.resume().catch(() => { }); }
    }
    // The first real interaction anywhere on the page unlocks sound for the rest of the circuit.
    ['pointerdown', 'keydown', 'touchstart'].forEach(type =>
        window.addEventListener(type, prime, { once: false, passive: true, capture: true }));

    function tone(c, frequency, start, duration, peak) {
        const osc = c.createOscillator();
        const gain = c.createGain();
        osc.type = 'sine';
        osc.frequency.setValueAtTime(frequency, start);
        gain.gain.setValueAtTime(0.0001, start);
        gain.gain.exponentialRampToValueAtTime(peak, start + 0.015);
        gain.gain.exponentialRampToValueAtTime(0.0001, start + duration);
        osc.connect(gain).connect(c.destination);
        osc.start(start);
        osc.stop(start + duration + 0.02);
    }

    function chime(important) {
        const c = context();
        if (!c || c.state !== 'running') return false; // not unlocked yet: the toast carries it
        const t = c.currentTime + 0.01;
        tone(c, important ? 880 : 740, t, 0.16, important ? 0.18 : 0.12);
        tone(c, important ? 1175 : 988, t + 0.14, 0.2, important ? 0.16 : 0.1);
        return true;
    }

    return {
        /** Plays the chime unless one played in the last ten seconds. Returns whether a sound was made. */
        play(important) {
            const now = Date.now();
            if (now - lastAt < MIN_GAP_MS) return false;
            const played = chime(!!important);
            if (played) lastAt = now;
            return played;
        },
        /** "Play a test sound": called from a button press, so it may resume the context itself. Never throttled. */
        async test() {
            const c = context();
            if (!c) return false;
            if (c.state === 'suspended') { try { await c.resume(); } catch (e) { return false; } }
            return chime(false);
        },
        prime
    };
})();
