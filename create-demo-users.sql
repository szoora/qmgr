-- Create Demo Users for Quick Login Buttons
-- Run this if the demo users don't exist after migration
--
-- NOTE: all tables live under the "qmgr" schema (lowercase snake_case table
-- names — qmgr.users/roles/organizations/branches — but PascalCase-quoted
-- column names), not "public"/PascalCase-quoted-table-names as this script
-- originally assumed. This database's search_path is "$user", public
-- (confirmed via `SHOW search_path`), which does NOT include qmgr, so table
-- references must be schema-qualified.
--
-- Also fixed: the role code was 'agent' (doesn't exist — the real system
-- role for a queue agent is 'staff'); Status/Tier are integer enum columns,
-- not the string literals ('Active'/'Enterprise') originally used;
-- Organization.IndustryType/PreferredCurrency/OnboardingStep are NOT NULL
-- with no default and were missing from the original INSERT entirely; the
-- SuperAdmin credentials are updated to match the current seeded account
-- (support@getsacc.com / admin); and all three password hashes below are
-- freshly-generated real bcrypt hashes (via pgcrypto's crypt()/gen_salt('bf')
-- — verified compatible with BCrypt.Net, the hashing library the app uses)
-- for the passwords they're labeled with — the previous hashes did not
-- actually correspond to their labeled passwords.

-- Variables
DO $$
DECLARE
    v_super_admin_role_id UUID;
    v_admin_role_id UUID;
    v_staff_role_id UUID;
    v_platform_org_id UUID;
    v_demo_org_id UUID;
    v_demo_branch_id UUID;
BEGIN
    -- Get role IDs
    SELECT "Id" INTO v_super_admin_role_id FROM qmgr.roles WHERE "Code" = 'super-admin' AND "OrganizationId" IS NULL;
    SELECT "Id" INTO v_admin_role_id FROM qmgr.roles WHERE "Code" = 'admin' LIMIT 1;
    SELECT "Id" INTO v_staff_role_id FROM qmgr.roles WHERE "Code" = 'staff' LIMIT 1;

    -- Get or create Platform Organization
    SELECT "Id" INTO v_platform_org_id FROM qmgr.organizations WHERE "Slug" = 'platform';
    IF v_platform_org_id IS NULL THEN
        INSERT INTO qmgr.organizations ("Id", "Name", "BrandName", "ContactEmail", "Slug", "Status", "Tier", "IndustryType", "PreferredCurrency", "OnboardingCompleted", "OnboardingStep", "VerifiedAt", "CreatedAt")
        VALUES (
            'ffffffff-ffff-ffff-ffff-ffffffffffff'::uuid,
            'Platform Administration',
            'Q-Mgr Platform',
            'admin@qmgr.platform',
            'platform',
            2, -- TenantStatus.Active
            3, -- TenantTier.Enterprise
            0, -- IndustryType.General
            'USD',
            true,
            0,
            NOW(),
            NOW()
        )
        RETURNING "Id" INTO v_platform_org_id;
    END IF;

    -- Get or create Demo Organization
    SELECT "Id" INTO v_demo_org_id FROM qmgr.organizations WHERE "Slug" = 'demo';
    IF v_demo_org_id IS NULL THEN
        INSERT INTO qmgr.organizations ("Id", "Name", "BrandName", "ContactEmail", "Slug", "Status", "Tier", "IndustryType", "PreferredCurrency", "OnboardingCompleted", "OnboardingStep", "VerifiedAt", "CreatedAt")
        VALUES (
            gen_random_uuid(),
            'Demo Organization',
            'Q-Mgr Demo',
            'admin@qmgr.demo',
            'demo',
            2, -- TenantStatus.Active
            2, -- TenantTier.Professional
            0, -- IndustryType.General
            'USD',
            true,
            0,
            NOW(),
            NOW()
        )
        RETURNING "Id" INTO v_demo_org_id;

        -- Create a demo branch
        INSERT INTO qmgr.branches ("Id", "OrganizationId", "Name", "Code", "Address", "IsActive", "CreatedAt")
        VALUES (
            gen_random_uuid(),
            v_demo_org_id,
            'Main Branch',
            'MAIN',
            '123 Demo Street, Demo City',
            true,
            NOW()
        )
        RETURNING "Id" INTO v_demo_branch_id;
    ELSE
        -- Get existing demo branch
        SELECT "Id" INTO v_demo_branch_id FROM qmgr.branches WHERE "OrganizationId" = v_demo_org_id LIMIT 1;
    END IF;

    -- Create SuperAdmin user if doesn't exist
    IF NOT EXISTS (SELECT 1 FROM qmgr.users WHERE "Email" = 'support@getsacc.com' OR "Username" = 'superadmin') THEN
        INSERT INTO qmgr.users ("Id", "OrganizationId", "Username", "Email", "PasswordHash", "FirstName", "LastName", "RoleId", "IsActive", "CreatedAt")
        VALUES (
            gen_random_uuid(),
            v_platform_org_id,
            'superadmin',
            'support@getsacc.com',
            '$2a$10$BH0GLRRIs9CvpSKhINLIy.kBupYQ9CNtJQbzqd6X6hyqUWyDrIK4e', -- admin
            'Platform',
            'Administrator',
            v_super_admin_role_id,
            true,
            NOW()
        );
        RAISE NOTICE 'Created support@getsacc.com / username superadmin (password: admin)';
    ELSE
        RAISE NOTICE 'SuperAdmin (support@getsacc.com / superadmin) already exists';
    END IF;

    -- Create Admin user if doesn't exist
    IF NOT EXISTS (SELECT 1 FROM qmgr.users WHERE "Email" = 'admin@qmgr.demo') THEN
        INSERT INTO qmgr.users ("Id", "OrganizationId", "Username", "Email", "PasswordHash", "FirstName", "LastName", "RoleId", "IsActive", "CreatedAt")
        VALUES (
            gen_random_uuid(),
            v_demo_org_id,
            'admin',
            'admin@qmgr.demo',
            '$2a$10$O8nRy/Q8FzJ5YSEAf33P0.2GxFiU1Mc49zsjGDpG.B9RX68wcplWK', -- admin123
            'System',
            'Administrator',
            v_admin_role_id,
            true,
            NOW()
        );
        RAISE NOTICE 'Created admin@qmgr.demo (password: admin123)';
    ELSE
        RAISE NOTICE 'admin@qmgr.demo already exists';
    END IF;

    -- Create Staff user if doesn't exist
    IF NOT EXISTS (SELECT 1 FROM qmgr.users WHERE "Email" = 'agent1@qmgr.demo') THEN
        INSERT INTO qmgr.users ("Id", "OrganizationId", "Username", "Email", "PasswordHash", "FirstName", "LastName", "RoleId", "AssignedBranchId", "IsActive", "CreatedAt")
        VALUES (
            gen_random_uuid(),
            v_demo_org_id,
            'agent1',
            'agent1@qmgr.demo',
            '$2a$10$zGnfzRDQh5/NrwIkgG/TsuHAFOXoTfHIy8G.8uU3dSqbFk1wCrUGC', -- agent123
            'John',
            'Agent',
            v_staff_role_id,
            v_demo_branch_id,
            true,
            NOW()
        );
        RAISE NOTICE 'Created agent1@qmgr.demo (password: agent123)';
    ELSE
        RAISE NOTICE 'agent1@qmgr.demo already exists';
    END IF;

    RAISE NOTICE 'Demo users setup complete!';
END $$;

-- Verify the users exist
SELECT
    u."Username",
    u."Email",
    r."Code" as "Role",
    o."Name" as "Organization"
FROM qmgr.users u
JOIN qmgr.roles r ON u."RoleId" = r."Id"
JOIN qmgr.organizations o ON u."OrganizationId" = o."Id"
WHERE u."Email" IN ('support@getsacc.com', 'admin@qmgr.demo', 'agent1@qmgr.demo')
   OR u."Username" = 'superadmin'
ORDER BY u."Email";
