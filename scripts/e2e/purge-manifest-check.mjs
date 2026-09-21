// `TenantPurgeModelGuard` refuses to start the API when a table in the model is not classified in
// `TenantDataManifest`. That is deliberate and must stay — the alternative is a purge that reports
// success and leaves rows behind, found long after a tenant was told their data was gone.
//
// But it is a runtime answer to an edit-time mistake. Add an entity, run the app, and the first
// thing that happens is a dead API — which reads like a broken build or a bad migration until you
// get to the message. This says the same thing in a second, with no database and no build, so the
// developer who added the DbSet learns about it beside the line they just wrote.
//
// The runtime guard remains the authority: it walks the real EF model, so it also sees an entity
// that reaches the model by navigation rather than by a DbSet. This check covers the way every
// entity in this codebase is actually declared today (all 81 of them are a DbSet on QMgrDbContext).
//
//   node scripts/e2e/purge-manifest-check.mjs
import fs from 'node:fs';

const CONTEXT = 'src/Q-Mgr.API/Infrastructure/Data/QMgrDbContext.cs';
const MANIFEST = 'src/Q-Mgr.API/Infrastructure/Data/Purge/TenantDataManifest.cs';

const context = fs.readFileSync(CONTEXT, 'utf8');
const manifest = fs.readFileSync(MANIFEST, 'utf8');

// `DbSet<Branch>` and `DbSet<QMgr.Domain.Entities.Staff.Timetable>` are both written here; the
// manifest is keyed on the CLR name, so take the last dotted segment of either.
const entities = new Set(
  [...context.matchAll(/DbSet<\s*([A-Za-z0-9_.]+)\s*>/g)].map(m => m[1].split('.').pop()),
);

const classified = new Map(
  [...manifest.matchAll(/\["([A-Za-z0-9_]+)"\]\s*=\s*TenantDataClass\.([A-Za-z]+)/g)]
    .map(m => [m[1], m[2]]),
);

const unclassified = [...entities].filter(e => !classified.has(e)).sort();
const stale = [...classified.keys()].filter(e => !entities.has(e)).sort();

for (const s of stale) {
  console.log(`warning: ${MANIFEST} classifies ${s}, which is no longer a DbSet on QMgrDbContext.`);
}

if (unclassified.length === 0) {
  const counts = {};
  for (const c of classified.values()) counts[c] = (counts[c] ?? 0) + 1;
  const summary = Object.entries(counts).map(([k, v]) => `${v} ${k}`).join(', ');
  console.log(`${entities.size} entities, all classified (${summary}).`);
  process.exit(stale.length ? 1 : 0);
}

console.log(`
${unclassified.length} entity/entities are not classified, and the API will refuse to start:

${unclassified.map(e => `  ${e}`).join('\n')}

Add each to TenantDataManifest.ByEntity as one of:

  TenantOwned         it has its own OrganizationId
  TenantDerived       it reaches one through a parent
  PlatformOwned       it is shared, and must survive a purge
  StatutoryRetention  a statute requires it to be kept, de-identified

Without this a tenant purge would silently leave those rows behind.`);
process.exit(1);
