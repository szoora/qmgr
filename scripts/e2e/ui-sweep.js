// =====================================================================================================
// Staff Performance UI sweep — runs INSIDE a signed-in Q-Mgr tab (pasted or injected), so a person can
// watch it. Each route is loaded in a same-origin iframe at desktop width and at a real 390px phone
// width (media queries apply to the iframe's own viewport, which is what resize_window cannot give),
// then checked for: the Blazor error bar, an /unauthorized or /login bounce, a spinner that never
// resolves, an error toast, sideways scrolling, and the elements responsible for it.
//
//   window.__qmSweep.run(routes)   → results array; a report panel renders over the page as it runs.
// =====================================================================================================
(() => {
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  function panel() {
    let el = document.getElementById("qm-sweep");
    if (el) el.remove();
    el = document.createElement("div");
    el.id = "qm-sweep";
    el.innerHTML = `
      <style>
        #qm-sweep{position:fixed;inset:0;z-index:99999;background:#14110f;color:#eee;font:13px/1.45 Poppins,system-ui,sans-serif;display:grid;grid-template-columns:minmax(0,1fr) 420px;gap:0}
        #qm-sweep .log{overflow:auto;padding:16px 20px}
        #qm-sweep h2{margin:0 0 4px;font:700 18px Montserrat,system-ui}
        #qm-sweep .sub{color:#aaa;margin-bottom:12px}
        #qm-sweep .row{display:grid;grid-template-columns:54px 70px minmax(0,1fr);gap:8px;padding:5px 0;border-bottom:1px solid #2a2522;align-items:start}
        #qm-sweep .p{color:#4ade80;font-weight:700}#qm-sweep .f{color:#f87171;font-weight:700}#qm-sweep .w{color:#fbbf24;font-weight:700}
        #qm-sweep .det{color:#bbb;font-size:12px;white-space:pre-wrap;word-break:break-word}
        #qm-sweep .stage{background:#000;display:flex;flex-direction:column;align-items:center;padding:12px;gap:8px;border-left:1px solid #333}
        #qm-sweep .stage .cap{color:#aaa;font-size:12px;text-align:center}
        #qm-sweep iframe{background:#fff;border:0;border-radius:18px;box-shadow:0 0 0 6px #333}
        #qm-sweep .tot{font-weight:700;font-size:15px;margin-top:10px}
      </style>
      <div class="log"><h2>Staff Performance — UI sweep</h2><div class="sub" id="qm-sub">starting…</div><div id="qm-rows"></div><div class="tot" id="qm-tot"></div></div>
      <div class="stage"><div class="cap" id="qm-cap">preview</div><iframe id="qm-frame" width="390" height="680"></iframe></div>`;
    document.body.appendChild(el);
    return el;
  }

  function addRow(status, width, text, detail) {
    const cls = status === "PASS" ? "p" : status === "WARN" ? "w" : "f";
    const row = document.createElement("div");
    row.className = "row";
    row.innerHTML = `<span class="${cls}">${status}</span><span>${width}</span><span>${text}${detail ? `<div class="det">${detail.replace(/</g, "&lt;")}</div>` : ""}</span>`;
    document.getElementById("qm-rows").appendChild(row);
    row.scrollIntoView({ block: "nearest" });
  }

  async function loadIn(frame, url, width, height) {
    frame.width = width; frame.height = height;
    await new Promise((resolve) => { frame.onload = resolve; frame.src = url; });
    const w = frame.contentWindow;
    // Wait for Blazor to render something meaningful: a heading, and no page-level spinner for 1.5s.
    const start = Date.now();
    let settledSince = 0;
    while (Date.now() - start < 20000) {
      await sleep(300);
      const d = w.document;
      const busy = d.querySelector(".loading-container, .q-spinner, .qm-spinner, .webster-spinner");
      const hasContent = d.querySelector("h1, .qm-main h2, .login-card, .wr-sheet, .q-print-sheet");
      if (hasContent && !busy) { if (!settledSince) settledSince = Date.now(); if (Date.now() - settledSince > 1500) break; }
      else settledSince = 0;
    }
    return w;
  }

  function inspect(w, route) {
    const d = w.document;
    const problems = [];
    const path = w.location.pathname;
    if (/\/unauthorized|\/login|\/billing\/modules/.test(path) && !route.expectRedirect) problems.push(`redirected to ${path}`);
    const errUi = d.getElementById("blazor-error-ui");
    if (errUi && w.getComputedStyle(errUi).display !== "none") problems.push("Blazor error bar is showing (unhandled exception)");
    if (d.querySelector(".loading-container")) problems.push("still loading after 20s");
    const toasts = [...d.querySelectorAll(".q-toast, .toast")].map((t) => t.innerText.trim()).filter((t) => /could not|error|failed/i.test(t));
    if (toasts.length) problems.push("error toast: " + toasts.join(" | "));
    const vw = w.innerWidth;
    const sw = d.documentElement.scrollWidth;
    const warnings = [];
    if (sw > vw + 1) {
      const wide = [...d.querySelectorAll("body *")]
        .filter((e) => {
          const r = e.getBoundingClientRect();
          if (r.width === 0 || r.right <= vw + 1) return false;
          // ignore things inside their own horizontal scroller (tables, tab strips)
          for (let p = e.parentElement; p && p !== d.body; p = p.parentElement) {
            const ox = w.getComputedStyle(p).overflowX;
            if ((ox === "auto" || ox === "scroll" || ox === "hidden" || ox === "clip") && p.getBoundingClientRect().right <= vw + 1) return false;
          }
          return true;
        })
        .slice(0, 6)
        .map((e) => `${e.tagName.toLowerCase()}${e.id ? "#" + e.id : ""}.${[...e.classList].slice(0, 3).join(".")} right=${Math.round(e.getBoundingClientRect().right)}`);
      problems.push(`page scrolls sideways: scrollWidth ${sw} > viewport ${vw}` + (wide.length ? `\n  widest: ${wide.join("\n          ")}` : ""));
    }
    // Tap targets on phones: buttons under 32px tall are a warning, not a failure.
    if (vw <= 480) {
      const small = [...d.querySelectorAll(".qm-main button, .qm-main a.q-btn, .qm-main .q-btn")]
        .filter((b) => { const r = b.getBoundingClientRect(); return r.width > 0 && r.height > 0 && r.height < 30; })
        .slice(0, 4).map((b) => `"${(b.innerText || b.title || b.getAttribute("aria-label") || "").trim().slice(0, 24)}" ${Math.round(b.getBoundingClientRect().height)}px`);
      if (small.length) warnings.push("small tap targets: " + small.join(", "));
    }
    return { problems, warnings, title: d.title };
  }

  async function run(routes) {
    panel();
    const frame = document.getElementById("qm-frame");
    const results = [];
    let n = 0;
    for (const route of routes) {
      n++;
      for (const [label, width, height] of [["desktop", 1280, 800], ["phone", 390, 780]]) {
        document.getElementById("qm-sub").textContent = `${n}/${routes.length}  ${route.name}  (${label})`;
        document.getElementById("qm-cap").textContent = `${route.path} — ${label} ${width}px`;
        // Show the phone at its true width; the desktop pass is scaled into the stage.
        frame.style.transform = width > 420 ? `scale(${390 / width})` : "none";
        frame.style.transformOrigin = "top center";
        let res;
        try {
          const w = await loadIn(frame, route.path, width, height);
          res = inspect(w, route);
        } catch (e) {
          res = { problems: ["could not load: " + e.message], warnings: [] };
        }
        const status = res.problems.length ? "FAIL" : res.warnings.length ? "WARN" : "PASS";
        addRow(status, label, `${route.name} <span style="color:#888">${route.path}</span>`, [...res.problems, ...res.warnings].join("\n"));
        results.push({ route: route.path, name: route.name, width: label, status, problems: res.problems, warnings: res.warnings });
      }
    }
    const f = results.filter((r) => r.status === "FAIL").length, wn = results.filter((r) => r.status === "WARN").length;
    document.getElementById("qm-tot").textContent = `${results.length - f - wn} passed, ${wn} warnings, ${f} failed`;
    document.getElementById("qm-sub").textContent = "done";
    window.__qmSweepResults = results;
    return results;
  }

  window.__qmSweep = { run, close: () => document.getElementById("qm-sweep")?.remove() };
})();
