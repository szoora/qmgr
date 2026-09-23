// A PUBLIC PAGE THAT FORGETS THE HOST RENDERS THE VENDOR'S COLOURS ON A SCHOOL'S OWN DOMAIN.
//
// Found 2026-09-22 on three of the nine public pages at once — VerifyEmail, ForgotPassword and
// JoinCode. Two of them CASCADED TenantHostContext and read it nowhere; the third had its own card
// styles entirely. A school clicking a verification or password-reset link from its own domain met
// the shipped wine one click from its own crest, and nothing anywhere said so:
//
//   * the build is clean — a style attribute nobody wrote is not an error;
//   * QBrandMark still shows the school's LOGO (it takes the host itself), so the page looks
//     branded at a glance while every colour on it is ours;
//   * white-label-ui.mjs cannot see it. That suite signs in and walks the SHELL. A public page is
//     branded from the HOST, and on 127.0.0.1 the host resolves to no tenant by design
//     (OrganizationsController.GetBrandingForHost matches Organization.CustomDomain exactly), so a
//     browser sweep of these pages against a dev box would report the platform palette either way.
//
// Which is why this is STATIC. The rule is a property of the markup, not of a running page:
//
//     an element carrying class="login-container" carries style="@Host.BrandingStyle"
//
// .login-container is the shared public shell — sign in, register, join, forgot, reset, set,
// verify, get-app — and BrandingStyle is the inline style that points --qm-primary and the whole
// family BrandPalette derives at the tenant's own colour. It is empty on the platform host, so
// writing it costs nothing and omitting it is always a bug.
//
//   node scripts/e2e/public-branding-check.mjs
import fs from 'node:fs';
import path from 'node:path';

const ROOT = 'src/Q-Mgr.Web/Components';
const SHELL = 'login-container';
const STYLE = '@Host.BrandingStyle';

const files = [];
(function walk(d) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    if (['bin', 'obj', 'node_modules'].includes(e.name)) continue;
    const p = path.join(d, e.name);
    e.isDirectory() ? walk(p) : e.name.endsWith('.razor') && files.push(p);
  }
})(ROOT);

const bad = [];
let shells = 0;

for (const f of files) {
  const lines = fs.readFileSync(f, 'utf8').split(/\r?\n/);
  lines.forEach((line, i) => {
    // The opening tag of the shell element. Matching the class attribute rather than the bare word
    // keeps a CSS rule or a comment mentioning the name out of it.
    if (!/class="[^"]*\blogin-container\b[^"]*"/.test(line)) return;
    shells++;
    // The style may sit on the same line (it does on all nine today) or on the next one, since a
    // long opening tag is sometimes wrapped. Nothing beyond that: a style set three lines down is
    // far enough away to be worth failing on and re-reading.
    const window = line + (lines[i + 1] ?? '');
    if (!window.includes(STYLE)) bad.push(`${f}:${i + 1}  ${line.trim()}`);
  });
}

if (bad.length) {
  console.log(`\n  ${bad.length} public page(s) render the shared shell without the tenant's palette:\n`);
  for (const b of bad) console.log('    ' + b);
  console.log(`\n  Add style="${STYLE}" to the .${SHELL} element — and a`);
  console.log('  [CascadingParameter] public TenantHostContext Host { get; set; } = TenantHostContext.Platform;');
  console.log('  to the @code block if the page has not got one. It is empty on the platform host.\n');
  process.exitCode = 1;
} else {
  console.log(`${shells} public page(s) on the shared shell — every one carries the tenant's palette.`);
}
