-- Script to make a user a SuperAdmin
-- Run this in your PostgreSQL database
--
-- NOTE: all tables live under the "qmgr" schema, not "public" — this
-- database's search_path is "$user", public (confirmed via `SHOW search_path`),
-- which does NOT include qmgr, so every table reference below is explicitly
-- schema-qualified. The default seeded SuperAdmin account is
-- support@getsacc.com / admin (username: superadmin) — this script is for
-- promoting a *different*, already-existing user to SuperAdmin instead.

-- Step 1: Check current users
SELECT
    u."Id",
    u."Username",
    u."Email",
    r."Code" as "CurrentRole",
    r."Name" as "CurrentRoleName"
FROM qmgr.users u
JOIN qmgr.roles r ON u."RoleId" = r."Id"
WHERE u."IsActive" = true
ORDER BY u."CreatedAt";

-- Step 2: Find the super-admin role
SELECT "Id", "Code", "Name"
FROM qmgr.roles
WHERE "Code" = 'super-admin' AND "OrganizationId" IS NULL;

-- Step 3: Update YOUR user to super-admin
-- IMPORTANT: Replace 'your-email@example.com' with your actual email address
DO $$
DECLARE
    v_super_admin_role_id UUID;
    v_user_id UUID;
BEGIN
    -- Get super-admin role ID
    SELECT "Id" INTO v_super_admin_role_id
    FROM qmgr.roles
    WHERE "Code" = 'super-admin' AND "OrganizationId" IS NULL;

    IF v_super_admin_role_id IS NULL THEN
        RAISE EXCEPTION 'Super-admin role not found. Run the application in Development mode to seed roles.';
    END IF;

    -- Get your user ID (CHANGE THE EMAIL HERE)
    SELECT "Id" INTO v_user_id
    FROM qmgr.users
    WHERE "Email" = 'your-email@example.com' AND "IsActive" = true;

    IF v_user_id IS NULL THEN
        RAISE EXCEPTION 'User with email your-email@example.com not found';
    END IF;

    -- Update user to super-admin role
    UPDATE qmgr.users
    SET "RoleId" = v_super_admin_role_id,
        "UpdatedAt" = NOW()
    WHERE "Id" = v_user_id;

    RAISE NOTICE 'User updated to super-admin successfully!';
END $$;

-- Step 4: Verify the change
SELECT
    u."Id",
    u."Username",
    u."Email",
    r."Code" as "NewRole",
    r."Name" as "NewRoleName"
FROM qmgr.users u
JOIN qmgr.roles r ON u."RoleId" = r."Id"
WHERE u."Email" = 'your-email@example.com';

-- Step 5 (Optional): If super-admin role doesn't exist, create it
-- Only run this if Step 2 showed no results
/*
INSERT INTO qmgr.roles ("Id", "OrganizationId", "Name", "Code", "Description", "Color", "Icon", "SortOrder", "IsSystem", "IsActive", "CreatedAt")
VALUES (
    gen_random_uuid(),
    NULL,
    'SuperAdmin',
    'super-admin',
    'Platform administrator with full system access',
    '#000000',
    'shield-check',
    0,
    true,
    true,
    NOW()
);
*/
