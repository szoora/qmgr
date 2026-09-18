// Pipes a suite's stdout to the terminal AND to the live viewer (viewer.mjs on 5010), so a curl or
// Node suite can be watched line by line. The tag is the first argument.
//
//   bash scripts/e2e/class-teacher-e2e.sh | node scripts/e2e/browser/tee-to-viewer.mjs api
//   node scripts/e2e/duty-rota-e2e.mjs    | node scripts/e2e/browser/tee-to-viewer.mjs rota
//
// If no viewer is listening the POST is dropped and the run is unaffected.
const key = process.argv[2] || 'run';
const post = (text) =>
  fetch(`http://127.0.0.1:5010/append?key=${encodeURIComponent(key)}`, { method: 'POST', body: text })
    .catch(() => {});

let buf = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', (chunk) => {
  process.stdout.write(chunk);
  buf += chunk;
  const parts = buf.split('\n');
  buf = parts.pop() ?? '';
  if (parts.length) post(parts.join('\n') + '\n');
});
process.stdin.on('end', async () => {
  if (buf.trim()) await post(buf + '\n');
  process.exit(0);
});
