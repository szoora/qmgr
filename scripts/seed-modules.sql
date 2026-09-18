-- =============================================================================
-- Q-Mgr — seed the 5-module catalog into an existing database
-- =============================================================================
--
-- WHY THIS EXISTS
--   Registration requires the applicant to pick exactly one module. The wizard
--   fills that picker from GET /api/v1/modules, which reads rows out of
--   qmgr.subscription_plans whose Code is one of the five module codes and
--   whose IsActive is true. On a server whose subscription_plans table has no
--   such rows, the picker renders empty and sign-up cannot be completed at all.
--
--   The application seeds these itself on every start (DbSeeder.SeedModulesAsync),
--   so this script is the manual equivalent for a server where that has not run
--   or did not take effect.
--
-- HOW TO RUN
--   This is plain SQL with no psql meta-commands, so it runs either from the
--   command line or pasted whole into pgAdmin, DBeaver, Azure Data Studio or
--   any other SQL editor.
--
--     psql -v ON_ERROR_STOP=1 -h localhost -U qmgr_app -d qmgr -f seed-modules.sql
--     (or: sudo -u postgres psql -v ON_ERROR_STOP=1 -d qmgr -f seed-modules.sql)
--
--   In a GUI client, run the whole file in one go rather than statement by
--   statement — the BEGIN below and the COMMIT near the end are what make a
--   failure leave the database untouched.
--
-- SAFETY
--   Idempotent, and safe to run more than once. It only ever touches rows whose
--   Code is one of the five module codes: existing ones are refreshed in place
--   and reactivated, missing ones are inserted. It does not touch the older
--   any organization, any
--   user, or any purchase. It runs in a single transaction, so a failure
--   anywhere leaves the database exactly as it was.
--
--   Prices below are the catalog's list prices. They do not change what any
--   existing customer is already being charged — a purchase records its own
--   price on the OrganizationModule row.
-- =============================================================================

BEGIN;

-- ── Guard: the table must actually have the module-era columns ───────────────
-- A database still on an older migration set will not have MonthlyPriceUgx,
-- Badge or SortOrder. Inserting into it would fail halfway with a column error
-- that reads like a typo in this script; this says plainly what is wrong
-- instead, and the real fix in that case is to let the application run its
-- migrations rather than to hand-write more SQL.
DO $$
DECLARE
    missing text;
BEGIN
    SELECT string_agg(c, ', ' ORDER BY c) INTO missing
    FROM unnest(ARRAY[
        'Id','Name','Code','Description','MonthlyPriceUsd','AnnualPriceUsd',
        'MonthlyPriceUgx','AnnualPriceUgx','MaxBranches','MaxDisplays','MaxUsersPerBranch',
        'MaxCountersPerBranch','MaxTokensPerMonth','MaxApiCallsPerMonth','MaxStorageMb',
        'ShowAds','RequiresDedicatedSchema','TrialDays','SortOrder','IsPublic','Badge',
        'CreatedAt','IsActive'
    ]) AS c
    WHERE NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'qmgr' AND table_name = 'subscription_plans' AND column_name = c
    );

    IF missing IS NOT NULL THEN
        RAISE EXCEPTION
            'qmgr.subscription_plans is missing column(s): %. This database is behind the application''s migrations — start the API so it migrates, rather than running this script.',
            missing;
    END IF;
END $$;

-- ── The catalog ─────────────────────────────────────────────────────────────
-- These values mirror DbSeeder.SeedModulesAsync exactly. If you change one
-- here, change it there too, or the next application start will disagree with
-- the row this script wrote.
WITH catalog(code, name, description, badge, sort_order,
             usd_month, usd_year, ugx_month, ugx_year,
             max_branches, max_displays, max_users, max_counters,
             max_tokens, max_api_calls, max_storage_mb) AS (
    VALUES
    ('core-queue',
     'Core Queue Management',
     'Live queue board, counter terminal, self-service kiosk, customer display, counters, service types, and tokens.',
     NULL, 0,
     19.0, 190.0, 80000.0, 800000.0,
     5, 1, 10, 10, 20000, 5000, 500),

    ('engagement-communications',
     'Communication',
     'Digital signage, campaign marketing (SMS/WhatsApp/email broadcasts), and customer feedback & surveys.',
     'Most Popular', 1,
     29.0, 290.0, 120000.0, 1200000.0,
     5, 10, 10, 2, 1000, 5000, 5000),

    ('visitor-management',
     'Visitor Management',
     'Visitor check-in and check-out, badges and group passes, pre-registered arrivals, watchlist and contractor induction, and the evacuation roll-call.',
     NULL, 2,
     22.0, 220.0, 95000.0, 950000.0,
     5, 1, 10, 2, 1000, 5000, 1000),

    ('student-welfare',
     'Student Welfare',
     'Student roster and guardians, visiting-day passes, and the welfare ledger: achievements, behaviour, safeguarding concerns, assigned actions, statements and reports.',
     'For schools', 3,
     25.0, 250.0, 105000.0, 1050000.0,
     5, 1, 10, 2, 1000, 5000, 2000),

    ('integrations-api',
     'Integrations & API Access',
     'API clients, webhooks, and partner integration adapters (hospital/pharmacy/banking).',
     NULL, 4,
     15.0, 150.0, 60000.0, 600000.0,
     3, 1, 5, 2, 1000, 100000, 200)
)
INSERT INTO qmgr.subscription_plans (
    "Id", "Name", "Code", "Description",
    "MonthlyPriceUsd", "AnnualPriceUsd", "MonthlyPriceUgx", "AnnualPriceUgx",
    "MaxBranches", "MaxDisplays", "MaxUsersPerBranch", "MaxCountersPerBranch",
    "MaxTokensPerMonth", "MaxApiCallsPerMonth", "MaxStorageMb",
    "ShowAds", "RequiresDedicatedSchema", "TrialDays", "SortOrder",
    "IsPublic", "Badge", "CreatedAt", "IsActive"
)
SELECT
    gen_random_uuid(), c.name, c.code, c.description,
    c.usd_month, c.usd_year, c.ugx_month, c.ugx_year,
    c.max_branches, c.max_displays, c.max_users, c.max_counters,
    c.max_tokens, c.max_api_calls, c.max_storage_mb,
    false, false, 14, c.sort_order,
    true, c.badge, now() AT TIME ZONE 'utc', true
FROM catalog c
WHERE NOT EXISTS (
    SELECT 1 FROM qmgr.subscription_plans p WHERE p."Code" = c.code
);

-- Refresh any that already existed, and — the part that matters most — make
-- sure they are active. A row that is present but inactive is invisible to the
-- catalog endpoint, which looks identical from the browser to no row at all.
WITH catalog(code, name, description, badge, sort_order,
             usd_month, usd_year, ugx_month, ugx_year,
             max_branches, max_displays, max_users, max_counters,
             max_tokens, max_api_calls, max_storage_mb) AS (
    VALUES
    ('core-queue', 'Core Queue Management',
     'Live queue board, counter terminal, self-service kiosk, customer display, counters, service types, and tokens.',
     NULL, 0, 19.0, 190.0, 80000.0, 800000.0, 5, 1, 10, 10, 20000, 5000, 500),
    ('engagement-communications', 'Communication',
     'Digital signage, campaign marketing (SMS/WhatsApp/email broadcasts), and customer feedback & surveys.',
     'Most Popular', 1, 29.0, 290.0, 120000.0, 1200000.0, 5, 10, 10, 2, 1000, 5000, 5000),
    ('visitor-management', 'Visitor Management',
     'Visitor check-in and check-out, badges and group passes, pre-registered arrivals, watchlist and contractor induction, and the evacuation roll-call.',
     NULL, 2, 22.0, 220.0, 95000.0, 950000.0, 5, 1, 10, 2, 1000, 5000, 1000),
    ('student-welfare', 'Student Welfare',
     'Student roster and guardians, visiting-day passes, and the welfare ledger: achievements, behaviour, safeguarding concerns, assigned actions, statements and reports.',
     'For schools', 3, 25.0, 250.0, 105000.0, 1050000.0, 5, 1, 10, 2, 1000, 5000, 2000),
    ('integrations-api', 'Integrations & API Access',
     'API clients, webhooks, and partner integration adapters (hospital/pharmacy/banking).',
     NULL, 4, 15.0, 150.0, 60000.0, 600000.0, 3, 1, 5, 2, 1000, 100000, 200)
)
UPDATE qmgr.subscription_plans p
SET "Name"                 = c.name,
    "Description"          = c.description,
    "Badge"                = c.badge,
    "SortOrder"            = c.sort_order,
    "MonthlyPriceUsd"      = c.usd_month,
    "AnnualPriceUsd"       = c.usd_year,
    "MonthlyPriceUgx"      = c.ugx_month,
    "AnnualPriceUgx"       = c.ugx_year,
    "MaxBranches"          = c.max_branches,
    "MaxDisplays"          = c.max_displays,
    "MaxUsersPerBranch"    = c.max_users,
    "MaxCountersPerBranch" = c.max_counters,
    "MaxTokensPerMonth"    = c.max_tokens,
    "MaxApiCallsPerMonth"  = c.max_api_calls,
    "MaxStorageMb"         = c.max_storage_mb,
    "IsPublic"             = true,
    "IsActive"             = true,
    "UpdatedAt"            = now() AT TIME ZONE 'utc'
FROM catalog c
WHERE p."Code" = c.code;

COMMIT;

-- ── What you should see ─────────────────────────────────────────────────────
-- Five rows, every one of them IsActive = t. That is exactly what the
-- registration wizard reads.
SELECT "SortOrder"       AS "#",
       "Code",
       "Name",
       "MonthlyPriceUgx" AS "UGX/mo",
       "IsActive",
       "IsPublic"
FROM qmgr.subscription_plans
WHERE "Code" IN ('core-queue', 'engagement-communications',
                 'visitor-management', 'student-welfare', 'integrations-api')
ORDER BY "SortOrder";

-- ── If the picker is still empty after this ─────────────────────────────────
-- The rows are right, so the problem is on the other side of them. In order:
--
--   1. Restart the API, which caches nothing here but confirms it is running
--      the build you think it is:
--        sudo systemctl restart qmgr-api && sudo systemctl status qmgr-api
--
--   2. Ask the API directly, on the server itself. This should print five
--      objects. If it prints [] the API is reading a different database than
--      the one you just seeded — check ConnectionStrings in
--      /var/www/sites/qmgr/api/appsettings.Production.json:
--        curl -s http://localhost:8586/api/v1/modules
--
--   3. Ask through nginx, from anywhere. If step 2 printed five and this
--      prints [] or an error, the problem is the proxy, not the app:
--        curl -s https://qmgr.cashbook.ug/api/v1/modules
--
--   4. Check what the API logged at startup:
--        sudo journalctl -u qmgr-api -n 200 --no-pager
