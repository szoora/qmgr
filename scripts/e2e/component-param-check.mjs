// EVERY PARAMETER PASSED TO ONE OF OUR COMPONENTS MUST EXIST ON IT.
//
// Razor does not check this. An unknown parameter compiles, ships, and then throws the moment the
// component first renders:
//
//     System.InvalidOperationException: Object of type 'QMgr.Web.Components.Shared.UI.QModal'
//     does not have a property matching the name 'Open'.
//
// On Blazor Server that kills the circuit, so the whole page becomes "An unhandled error has
// occurred" with nothing left on screen. That is what <QModal Open="@askOpen"> did to My Workspace's
// teaching tab: QModal takes Visible, the build was clean, and the tab was dead for everybody who
// opened it. Same shape as RZ10012 (an unresolved tag renders as inert HTML) except that the
// compiler says nothing at all — so this check is the only thing between a typo and a blank page.
//
// It reads OUR components only, skips any that capture unmatched values, and skips attribute VALUES,
// including the quotes that nest inside an @(...) expression — a lambda's own "=>" lives there, and
// reading it as a parameter name is how the first draft of this script reported 200 false positives.
//
//   node scripts/e2e/component-param-check.mjs
import fs from 'node:fs';
import path from 'node:path';

const ROOTS = ['src/Q-Mgr.Web/Components', 'src/Q-Mgr.Web/Layout'];
const files = [];
const walk = (dir) => {
  if (!fs.existsSync(dir)) return;
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p);
    else if (e.name.endsWith('.razor') || e.name.endsWith('.razor.cs')) files.push(p);
  }
};
ROOTS.forEach(walk);

// ---- what each component accepts ---------------------------------------------------------------
// A .razor component is named after its FILE (CLAUDE.md), and a partner .razor.cs may hold more of
// its parameters, so both fold into one entry.
const components = new Map();
const entry = (name) => {
  if (!components.has(name)) components.set(name, { params: new Set(), splat: false });
  return components.get(name);
};
for (const f of files) {
  const name = path.basename(f).replace(/\.razor(\.cs)?$/, '');
  const src = fs.readFileSync(f, 'utf8');
  const c = entry(name);
  // The type may carry spaces (Func<TItem, TValue>?), so take the last identifier before "{ get".
  for (const m of src.matchAll(/\[Parameter[^\]]*\][\s\S]{0,200}?public\s+([^\n;{}]+?)\s*\{\s*get/g)) {
    const decl = m[1].trim().split(/\s+/).pop();
    if (decl) c.params.add(decl.replace(/[^\w]/g, ''));
  }
  if (/CaptureUnmatchedValues\s*=\s*true/.test(src)) c.splat = true;
  for (const m of src.matchAll(/@typeparam\s+(\w+)/g)) c.params.add(m[1]);      // passed as an attribute
}
// What the framework supplies to any component, whatever it declares.
const ALWAYS = new Set(['ChildContent', 'Context', 'Body', 'Type']);

// ---- what each call site passes ----------------------------------------------------------------
// Walks a tag by hand rather than by regex: an attribute value can hold a lambda, a nested string
// and a ">" all at once, and none of that is a parameter name.
const skipExpression = (src, i) => {            // i points at "(" of an @( ... )
  let depth = 0;
  for (; i < src.length; i++) {
    const ch = src[i];
    if (ch === '"' || ch === "'") { const q = ch; for (i++; i < src.length && src[i] !== q; i++) if (src[i] === '\\') i++; continue; }
    if (ch === '(') depth++;
    else if (ch === ')') { depth--; if (depth === 0) return i + 1; }
  }
  return i;
};

const problems = [];
for (const f of files.filter(f => f.endsWith('.razor'))) {
  const src = fs.readFileSync(f, 'utf8');
  for (const open of src.matchAll(/<([A-Z]\w*)(?=[\s/>])/g)) {
    const name = open[1];
    const c = components.get(name);
    if (!c || c.splat) continue;
    const attrs = [];
    let token = '';
    for (let i = open.index + open[0].length; i < src.length; i++) {
      const ch = src[i];
      if (ch === '"' || ch === "'") {                       // an attribute value: read past it
        if (token) { attrs.push([token, i]); token = ''; }
        const q = ch;
        const code = src[i + 1] === '@';   // "@expr…": a C# expression, whose calls may carry quotes of their own
        for (i++; i < src.length; i++) {
          if (src[i] === '@' && src[i + 1] === '(') { i = skipExpression(src, i + 1) - 1; continue; }
          // An implicit expression's call — "@errors.Contains("audience")" — nests the attribute's own
          // quote character inside its parentheses. Read the call whole, or its argument is taken for
          // the next attribute's NAME (which is how "audience=" was once reported as a parameter).
          if (code && src[i] === '(') { i = skipExpression(src, i) - 1; continue; }
          if (src[i] === q) break;
        }
        continue;
      }
      if (ch === '>') break;
      if (ch === '=') { if (token) attrs.push([token, i]); token = ''; continue; }
      if (/[\s/]/.test(ch)) { token = ''; continue; }
      token += ch;
    }
    for (const [a, at] of attrs) {
      if (a.startsWith('@') && !a.startsWith('@bind-')) continue;          // @ref, @key, @onclick, @attributes
      const bare = a.replace(/^@bind-/, '').replace(/:(get|set|after|event)$/, '');
      // A LOWERCASE attribute is NOT safe on a component (fixed 2026-09-23). This used to skip it as
      // "an html attribute, never a parameter" — but Blazor hands EVERY attribute on a component to it as
      // a parameter, matched case-insensitively, and one that captures no unmatched values throws on its
      // first render. `<QSelect id="...">` passed this guard and took the Settings page down with
      // "does not have a property matching the name 'id'". So names are compared without case, and a
      // lowercase one is checked like any other.
      const lower = bare.toLowerCase();
      const has = (n) => [...c.params].some((p) => p.toLowerCase() === n);
      if ([...ALWAYS].some((p) => p.toLowerCase() === lower) || has(lower) || has(lower + 'changed')) continue;
      problems.push(`${f.replace(/\\/g, '/')}:${src.slice(0, at).split('\n').length}  <${name} ${bare}=...>  — ${name} has no ${bare} parameter`);
    }
  }
}

if (problems.length) {
  console.log('  FAIL  component-param-check');
  for (const p of [...new Set(problems)]) console.log(`        ${p}`);
  process.exitCode = 1;
} else {
  console.log(`${components.size} component(s) known — every parameter passed to one exists on it.`);
}
