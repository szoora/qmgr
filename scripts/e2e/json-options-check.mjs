// EVERY Web API CLIENT READS WITH THE APP'S ONE JsonSerializerOptions (2026-09-23).
//
// The API writes every enum as a string (JsonStringEnumConverter). Program.cs registers ONE JsonSerializerOptions
// carrying the same converter, and a client that reads with the framework defaults instead fails the first time a
// response carries an enum — AFTER the server has done the work. That is exactly what happened to "Ask a colleague":
// the cover request was created and the teacher was told it had failed (ISelfServiceApiService, found by
// browser/timetable-ownership-ui.mjs). Nothing in a build or a code read can see it; the DTO compiles either way.
//
// So in Services/, a ReadFromJsonAsync must pass options. Writes are not checked: an enum WRITTEN as a number is
// accepted by the API (the converter allows integers), so the failure only ever happens on the way back. Pages
// outside Services/ that read small hand-written response records with no enum are not in scope.
//
//   node scripts/e2e/json-options-check.mjs
import fs from 'node:fs';
import path from 'node:path';

const ROOT = 'src/Q-Mgr.Web/Services';
const files = fs.readdirSync(ROOT).filter(f => f.endsWith('.cs')).map(f => path.join(ROOT, f));
// The type argument may itself be generic (List<X>), but never contains a parenthesis, so [^()]* cannot run on
// past the call into a later `Enumerable.Empty<X>()` on the same line.
const BARE_READ = /ReadFromJsonAsync<[^()]*>\(\s*\)/;

const findings = [];
for (const f of files) {
  fs.readFileSync(f, 'utf8').split('\n').forEach((line, i) => {
    if (!line.trim().startsWith('//') && BARE_READ.test(line))
      findings.push(`${f.replaceAll('\\', '/')}:${i + 1}  ${line.trim().slice(0, 140)}`);
  });
}

if (findings.length) {
  console.log(findings.join('\n'));
  console.log(`\n${findings.length} JSON read(s) without the app's options. Inject JsonSerializerOptions (Program.cs) and pass it — an enum sent as a string will not read otherwise.`);
  process.exit(1);
}
console.log(`${files.length} client file(s) scanned — every JSON read passes the app's options.`);
