// Minimal CDP driver for a local headless Chrome on :9333 (CLAUDE.md "When the connected Chrome is on another machine").
import fs from 'node:fs';

export async function openTab() {
  const res = await fetch('http://127.0.0.1:9333/json/new?about:blank', { method: 'PUT' });
  const { webSocketDebuggerUrl } = await res.json();
  const ws = new WebSocket(webSocketDebuggerUrl);
  await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
  let id = 0;
  const pending = new Map();
  const listeners = [];
  ws.onmessage = (m) => {
    const msg = JSON.parse(m.data);
    if (msg.id && pending.has(msg.id)) { const { resolve, reject } = pending.get(msg.id); pending.delete(msg.id); msg.error ? reject(new Error(msg.error.message)) : resolve(msg.result); }
    else if (msg.method) listeners.forEach(l => l(msg));
  };
  const send = (method, params = {}) => new Promise((resolve, reject) => { const i = ++id; pending.set(i, { resolve, reject }); ws.send(JSON.stringify({ id: i, method, params })); });
  await send('Page.enable'); await send('Runtime.enable');
  const consoleErrors = [];
  listeners.push(m => {
    if (m.method === 'Runtime.exceptionThrown') consoleErrors.push(m.params.exceptionDetails?.exception?.description ?? m.params.exceptionDetails?.text);
    if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'error') consoleErrors.push(m.params.args.map(a => a.value ?? a.description).join(' '));
  });
  const tab = {
    send, consoleErrors,
    async eval(expr) {
      const r = await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true });
      if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text);
      return r.result.value;
    },
    async goto(url) { await send('Page.navigate', { url }); await tab.sleep(500); },
    sleep: (ms) => new Promise(r => setTimeout(r, ms)),
    async waitFor(expr, ms = 20000) {
      const end = Date.now() + ms;
      while (Date.now() < end) { try { if (await tab.eval(expr)) return true; } catch { } await tab.sleep(250); }
      return false;
    },
    async viewport(width, height, mobile = false) {
      await send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile });
    },
    async shot(path) {
      const { data } = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
      fs.writeFileSync(path, Buffer.from(data, 'base64'));
    },
    // Blazor @bind: native value setter plus input and change events (Input.insertText does not reach @bind).
    async setValue(selector, value) {
      return tab.eval(`(() => { const el = document.querySelector(${JSON.stringify(selector)}); if (!el) return false;
        const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
        Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, ${JSON.stringify(value)});
        el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`);
    },
    async clickText(text, selector = 'button, a') {
      return tab.eval(`(() => { const els = [...document.querySelectorAll(${JSON.stringify(selector)})].filter(e => e.offsetParent !== null && e.textContent.trim().includes(${JSON.stringify(text)}));
        if (!els.length) return false; els[0].click(); return true; })()`);
    },
    close() { ws.close(); }
  };
  return tab;
}

export async function signIn(tab, base, identifier, password) {
  await tab.goto(`${base}/login`);
  await tab.waitFor(`!!document.querySelector('input')`);
  await tab.sleep(800);
  const inputs = await tab.eval(`[...document.querySelectorAll('input')].map(i => i.type + ':' + (i.name||i.id||i.placeholder)).join('|')`);
  return inputs;
}
