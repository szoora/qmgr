-- Q-Mgr Database Migration Script
-- Version: 1.0.0
-- Date: 2024
-- Description: Initial database schema creation

-- Enable UUID extension
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- Create schema
CREATE SCHEMA IF NOT EXISTS qmgr;

-- Set search path
SET search_path TO qmgr, public;

-- ============================================
-- ORGANIZATION & BRANCH MANAGEMENT
-- ============================================

CREATE TABLE qmgr.organizations (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    name VARCHAR(255) NOT NULL,
    brand_name VARCHAR(100),
    logo_url VARCHAR(500),
    contact_email VARCHAR(255),
    contact_phone VARCHAR(50),
    website VARCHAR(255),
    address TEXT,
    settings JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ,
    created_by UUID,
    updated_by UUID,
    is_active BOOLEAN DEFAULT TRUE
);

CREATE UNIQUE INDEX idx_organizations_name ON qmgr.organizations(name);

CREATE TABLE qmgr.branches (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    organization_id UUID NOT NULL REFERENCES qmgr.organizations(id) ON DELETE RESTRICT,
    name VARCHAR(255) NOT NULL,
    code VARCHAR(20) NOT NULL,
    address TEXT,
    timezone VARCHAR(50) DEFAULT 'UTC',
    operating_hours JSONB,
    settings JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ,
    created_by UUID,
    updated_by UUID,
    is_active BOOLEAN DEFAULT TRUE
);

CREATE UNIQUE INDEX idx_branches_code ON qmgr.branches(code);
CREATE INDEX idx_branches_org ON qmgr.branches(organization_id);

CREATE TABLE qmgr.branch_settings (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    branch_id UUID NOT NULL REFERENCES qmgr.branches(id) ON DELETE CASCADE,
    default_kiosk_printer VARCHAR(255),
    kiosk_time_between_slides INTEGER DEFAULT 5000,
    kiosk_scroller_speed INTEGER DEFAULT 50,
    display_time_between_slides INTEGER DEFAULT 5000,
    enable_voice_announcement BOOLEAN DEFAULT FALSE,
    voice_language VARCHAR(10) DEFAULT 'en-US',
    enable_sms_notification BOOLEAN DEFAULT FALSE,
    enable_email_notification BOOLEAN DEFAULT FALSE,
    token_expiry_hours INTEGER DEFAULT 24,
    reset_token_numbers_daily BOOLEAN DEFAULT TRUE,
    sms_template_token_created VARCHAR(500),
    sms_template_token_called VARCHAR(500),
    email_template_token_created TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ,
    is_active BOOLEAN DEFAULT TRUE
);

-- ============================================
-- QUEUE MANAGEMENT
-- ============================================

CREATE TABLE qmgr.service_types (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    branch_id UUID NOT NULL REFERENCES qmgr.branches(id) ON DELETE RESTRICT,
    name VARCHAR(255) NOT NULL,
    code VARCHAR(10) NOT NULL,
    description TEXT,
    prefix VARCHAR(5) DEFAULT '',
    average_service_time_minutes INTEGER DEFAULT 10,
    priority INTEGER DEFAULT 0,
    icon_url VARCHAR(500),
    color VARCHAR(10),
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ,
    created_by UUID,
    updated_by UUID,
    is_active BOOLEAN DEFAULT TRUE
);

CREATE UNIQUE INDEX idx_service_types_branch_code ON qmgr.service_types(branch_id, code);

CREATE TABLE qmgr.counters (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    branch_id UUID NOT NULL REFERENCES qmgr.branches(id) ON DELETE RESTRICT,
    counter_number VARCHAR(20) NOT NULL,
    display_name VARCHAR(100),
    status VARCHAR(20) DEFAULT 'inactive',
    current_token_id UUID,
    assigned_user_id UUID,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ,
    created_by UUID,
    updated_by UUID,
    is_active BOOLEAN DEFAULT TRUE
);

CREATE UNIQUE INDEX idx_counters_branch_number ON qmgr.counters(branch_id, counter_number);

CREATE TABLE qmgr.counter_service_types (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    counter_id UUID NOT NULL REFERENCES qmgr.counters(id) ON DELETE CASCADE,
    service_type_id UUID NOT NULL REFERENCES qmgr.service_types(id) ON DELETE CASCADE,
    priority INTEGER DEFAULT 0,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    is_active BOOLEAN DEFAULT TRUE
);

CREATE UNIQUE INDEX idx_counter_service_types_unique ON qmgr.counter_service_types(counter_id, service_type_id);

CREATE TABLE qmgr.tokens (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    branch_id UUID NOT NULL REFERENCES qmgr.branches(id) ON DELETE RESTRICT,
    service_type_id UUID NOT NULL REFERENCES qmgr.service_types(id) ON DELETE RESTRICT,
    counter_id UUID REFERENCES qmgr.counters(id) ON DELETE SET NULL,

    -- Token identification
    token_number VARCHAR(20) NOT NULL,
    display_number VARCHAR(30) NOT NULL,

    -- Customer information
    customer_id VARCHAR(100),
    customer_name VARCHAR(255),
    customer_phone VARCHAR(50),
    customer_email VARCHAR(255),

    -- Source tracking
    source VARCHAR(50) DEFAULT 'kiosk',
    external_reference VARCHAR(100),
    external_system VARCHAR(100),

    -- Status tracking
    status VARCHAR(30) DEFAULT 'waiting',
    priority INTEGER DEFAULT 0,

    -- Timestamps
    created_at TIMESTAMPTZ DEFAULT NOW(),
    called_at TIMESTAMPTZ,
    service_started_at TIMESTAMPTZ,
    service_completed_at TIMESTAMPTZ,
    updated_at TIMESTAMPTZ,

    -- Metrics
    estimated_wait_minutes INTEGER,
    actual_wait_minutes INTEGER,
    service_duration_minutes INTEGER,

    -- Additional data
    notes TEXT,
    metadata JSONB DEFAULT '{}',
    is_active BOOLEAN DEFAULT TRUE
);

CREATE INDEX idx_tokens_branch_date ON qmgr.tokens(branch_id, created_at);
CREATE INDEX idx_tokens_status ON qmgr.tokens(branch_id, status);
CREATE INDEX idx_tokens_customer ON qmgr.tokens(customer_id);
CREATE INDEX idx_tokens_external ON qmgr.tokens(external_system, external_reference);
CREATE INDEX idx_tokens_display_number ON qmgr.tokens(display_number);
CREATE INDEX idx_tokens_waiting ON qmgr.tokens(branch_id, status, created_at) WHERE status = 'waiting';
CREATE INDEX idx_tokens_today ON qmgr.tokens(branch_id, created_at) WHERE created_at >= CURRENT_DATE;

CREATE TABLE qmgr.token_history (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    token_id UUID NOT NULL REFERENCES qmgr.tokens(id) ON DELETE CASCADE,
    from_status VARCHAR(30),
    to_status VARCHAR(30) NOT NULL,
    counter_id UUID REFERENCES qmgr.counters(id) ON DELETE SET NULL,
    user_id UUID,
    notes TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    is_active BOOLEAN DEFAULT TRUE
);

CREATE INDEX idx_token_history_token ON qmgr.token_history(token_id);

-- ============================================
-- CONTENT & DIGITAL SIGNAGE
-- ============================================

CREATE TABLE qmgr.media_content (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    organization_id UUID NOT NULL REFERENCES qmgr.organizations(id) ON DELETE RESTRICT,
    name VARCHAR(255) NOT NULL,
    description TEXT,
    content_type VARCHAR(50) NOT NULL,
    mime_type VARCHAR(100),
    storage_type VARCHAR(20) DEFAULT 'local',
    file_path VARCHAR(500),
    file_url VARCHAR(500),
    thumbnail_url VARCHAR(500),
    file_size_bytes BIGINT,
    duration_seconds INTEGER,
    dimensions JSONB,
    text_content TEXT,
    tags TEXT[],
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ,
    created_by UUID,
    updated_by UUID,
    is_active BOOLEAN DEFAULT TRUE
);

CREATE INDEX idx_media_content_org_type ON qmgr.media_content(organization_id, content_type);

CREATE TABLE qmgr.playlists (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    branch_id UUID NOT NULL REFERENCES qmgr.branches(id) ON DELETE RESTRICT,
    name VARCHAR(255) NOT NULL,
    description TEXT,
    schedule_type VARCHAR(20) DEFAULT 'always',
    schedule JSONB,
    transition_type VARCHAR(50) DEFAULT 'fade',
    default_duration_seconds INTEGER DEFAULT 10,
    loop BOOLEAN DEFAULT TRUE,
    shuffle BOOLEAN DEFAULT FALSE,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ,
    created_by UUID,
    updated_by UUID,
    is_active BOOLEAN DEFAULT TRUE
);

CREATE TABLE qmgr.playlist_items (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    playlist_id UUID NOT NULL REFERENCES qmgr.playlists(id) ON DELETE CASCADE,
    media_content_id UUID NOT NULL REFERENCES qmgr.media_content(id) ON DELETE RESTRICT,
    position INTEGER NOT NULL,
    duration_seconds INTEGER,
    conditions JSONB,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    is_active BOOLEAN DEFAULT TRUE
);

CREATE UNIQUE INDEX idx_playlist_items_position ON qmgr.playlist_items(playlist_id, position);

CREATE TABLE qmgr.displays (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    branch_id UUID NOT NULL REFERENCES qmgr.branches(id) ON DELETE RESTRICT,
    name VARCHAR(255) NOT NULL,
    display_type VARCHAR(50) NOT NULL,
    device_id VARCHAR(100),
    resolution JSONB,
    orientation VARCHAR(20) DEFAULT 'landscape',
    status VARCHAR(20) DEFAULT 'offline',
    last_heartbeat TIMESTAMPTZ,
    settings JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ,
    created_by UUID,
    updated_by UUID,
    is_active BOOLEAN DEFAULT TRUE
);

CREATE TABLE qmgr.display_zones (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    display_id UUID NOT NULL REFERENCES qmgr.displays(id) ON DELETE CASCADE,
    name VARCHAR(255) NOT NULL,
    zone_type VARCHAR(50) NOT NULL,
    position_x INTEGER DEFAULT 0,
    position_y INTEGER DEFAULT 0,
    width INTEGER DEFAULT 100,
    height INTEGER DEFAULT 100,
    z_index INTEGER DEFAULT 0,
    playlist_id UUID REFERENCES qmgr.playlists(id) ON DELETE SET NULL,
    settings JSONB DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT NOW(),
    is_active BOOLEAN DEFAULT TRUE
);

CREATE TABLE qmgr.quotes (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    organization_id UUID NOT NULL REFERENCES qmgr.organizations(id) ON DELETE RESTRICT,
    category VARCHAR(100) DEFAULT 'Motivational',
    text TEXT NOT NULL,
    author VARCHAR(255),
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ,
    created_by UUID,
    updated_by UUID,
    is_active BOOLEAN DEFAULT TRUE
);

-- ============================================
-- USER & ACCESS MANAGEMENT
-- ============================================

CREATE TABLE qmgr.users (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    organization_id UUID NOT NULL REFERENCES qmgr.organizations(id) ON DELETE RESTRICT,
    username VARCHAR(100) NOT NULL,
    email VARCHAR(255) NOT NULL,
    password_hash VARCHAR(255) NOT NULL,
    first_name VARCHAR(100),
    last_name VARCHAR(100),
    phone VARCHAR(50),
    employee_number VARCHAR(50),
    role VARCHAR(50) NOT NULL DEFAULT 'staff',
    assigned_branch_id UUID REFERENCES qmgr.branches(id) ON DELETE SET NULL,
    assigned_counter_id UUID,
    last_login TIMESTAMPTZ,
    refresh_token VARCHAR(500),
    refresh_token_expiry TIMESTAMPTZ,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ,
    created_by UUID,
    updated_by UUID,
    is_active BOOLEAN DEFAULT TRUE
);

CREATE UNIQUE INDEX idx_users_username ON qmgr.users(username);
CREATE UNIQUE INDEX idx_users_email ON qmgr.users(email);

-- Add foreign key for counters.assigned_user_id
ALTER TABLE qmgr.counters ADD CONSTRAINT fk_counters_assigned_user
    FOREIGN KEY (assigned_user_id) REFERENCES qmgr.users(id) ON DELETE SET NULL;

-- Add foreign key for users.assigned_counter_id
ALTER TABLE qmgr.users ADD CONSTRAINT fk_users_assigned_counter
    FOREIGN KEY (assigned_counter_id) REFERENCES qmgr.counters(id) ON DELETE SET NULL;

CREATE TABLE qmgr.user_sessions (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    user_id UUID NOT NULL REFERENCES qmgr.users(id) ON DELETE CASCADE,
    counter_id UUID REFERENCES qmgr.counters(id) ON DELETE SET NULL,
    login_time TIMESTAMPTZ DEFAULT NOW(),
    logout_time TIMESTAMPTZ,
    tokens_served INTEGER DEFAULT 0,
    average_service_time_seconds INTEGER,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    is_active BOOLEAN DEFAULT TRUE
);

CREATE INDEX idx_user_sessions_user_login ON qmgr.user_sessions(user_id, login_time);

-- ============================================
-- INTEGRATION & API MANAGEMENT
-- ============================================

CREATE TABLE qmgr.api_clients (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    organization_id UUID NOT NULL REFERENCES qmgr.organizations(id) ON DELETE RESTRICT,
    name VARCHAR(255) NOT NULL,
    client_id VARCHAR(100) NOT NULL,
    client_secret_hash VARCHAR(255) NOT NULL,
    system_type VARCHAR(100),
    description TEXT,
    scopes TEXT[],
    allowed_branches UUID[],
    rate_limit_per_minute INTEGER DEFAULT 100,
    webhook_url VARCHAR(500),
    webhook_events TEXT[],
    webhook_secret VARCHAR(255),
    last_used_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ,
    created_by UUID,
    updated_by UUID,
    is_active BOOLEAN DEFAULT TRUE
);

CREATE UNIQUE INDEX idx_api_clients_client_id ON qmgr.api_clients(client_id);

CREATE TABLE qmgr.api_logs (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    api_client_id UUID REFERENCES qmgr.api_clients(id) ON DELETE SET NULL,
    endpoint VARCHAR(255),
    method VARCHAR(10),
    request_body JSONB,
    response_status INTEGER,
    response_time_ms INTEGER,
    ip_address VARCHAR(45),
    error_message TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    is_active BOOLEAN DEFAULT TRUE
);

CREATE INDEX idx_api_logs_client_date ON qmgr.api_logs(api_client_id, created_at);

CREATE TABLE qmgr.webhooks_outgoing (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    api_client_id UUID NOT NULL REFERENCES qmgr.api_clients(id) ON DELETE CASCADE,
    event_type VARCHAR(100) NOT NULL,
    payload JSONB,
    status VARCHAR(20) DEFAULT 'pending',
    attempts INTEGER DEFAULT 0,
    last_attempt_at TIMESTAMPTZ,
    last_error TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    is_active BOOLEAN DEFAULT TRUE
);

CREATE INDEX idx_webhooks_status_date ON qmgr.webhooks_outgoing(status, created_at);

-- ============================================
-- ANALYTICS & REPORTING
-- ============================================

CREATE TABLE qmgr.daily_statistics (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    branch_id UUID NOT NULL REFERENCES qmgr.branches(id) ON DELETE CASCADE,
    date DATE NOT NULL,
    total_tokens INTEGER DEFAULT 0,
    tokens_served INTEGER DEFAULT 0,
    tokens_cancelled INTEGER DEFAULT 0,
    tokens_no_show INTEGER DEFAULT 0,
    avg_wait_time_seconds INTEGER,
    max_wait_time_seconds INTEGER,
    avg_service_time_seconds INTEGER,
    peak_hour INTEGER,
    peak_hour_tokens INTEGER,
    service_type_breakdown JSONB,
    counter_breakdown JSONB,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    is_active BOOLEAN DEFAULT TRUE
);

CREATE UNIQUE INDEX idx_daily_statistics_branch_date ON qmgr.daily_statistics(branch_id, date);

-- ============================================
-- EF CORE MIGRATIONS HISTORY
-- ============================================

CREATE TABLE qmgr."__EFMigrationsHistory" (
    "MigrationId" VARCHAR(150) NOT NULL,
    "ProductVersion" VARCHAR(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

-- ============================================
-- SEED DATA
-- ============================================

-- Insert default organization
INSERT INTO qmgr.organizations (id, name, brand_name, contact_email, is_active)
VALUES ('00000000-0000-0000-0000-000000000001', 'Default Organization', 'Q-Mgr', 'admin@qmgr.local', TRUE);

-- Insert default branch
INSERT INTO qmgr.branches (id, organization_id, name, code, timezone, is_active)
VALUES ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000001', 'Main Branch', 'MAIN', 'UTC', TRUE);

-- Insert default admin user (password: Admin123!)
INSERT INTO qmgr.users (id, organization_id, username, email, password_hash, first_name, last_name, role, assigned_branch_id, is_active)
VALUES ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000001', 'admin', 'admin@qmgr.local',
        '$2a$11$rBNqPVqyQBQVDqbQjJHvq.A8lqQ5oZMHVBgDzHNuC/jXL.qfVBFam', 'System', 'Administrator', 'SuperAdmin',
        '00000000-0000-0000-0000-000000000001', TRUE);

-- Insert sample service types
INSERT INTO qmgr.service_types (branch_id, name, code, prefix, average_service_time_minutes, is_active) VALUES
('00000000-0000-0000-0000-000000000001', 'General Inquiry', 'GEN', 'G', 10, TRUE),
('00000000-0000-0000-0000-000000000001', 'Account Services', 'ACC', 'A', 15, TRUE),
('00000000-0000-0000-0000-000000000001', 'Premium Services', 'VIP', 'V', 20, TRUE);

-- Insert sample counters
INSERT INTO qmgr.counters (branch_id, counter_number, display_name, status, is_active) VALUES
('00000000-0000-0000-0000-000000000001', '1', 'Counter 1', 'inactive', TRUE),
('00000000-0000-0000-0000-000000000001', '2', 'Counter 2', 'inactive', TRUE),
('00000000-0000-0000-0000-000000000001', '3', 'Counter 3', 'inactive', TRUE);

-- Insert sample quotes
INSERT INTO qmgr.quotes (organization_id, category, text, author, is_active) VALUES
('00000000-0000-0000-0000-000000000001', 'Motivational', 'The only way to do great work is to love what you do.', 'Steve Jobs', TRUE),
('00000000-0000-0000-0000-000000000001', 'Motivational', 'Success is not final, failure is not fatal: it is the courage to continue that counts.', 'Winston Churchill', TRUE),
('00000000-0000-0000-0000-000000000001', 'Motivational', 'Quality is not an act, it is a habit.', 'Aristotle', TRUE);

-- Record migration
INSERT INTO qmgr."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20240101000000_InitialCreate', '9.0.0');

-- Grant permissions
GRANT ALL PRIVILEGES ON SCHEMA qmgr TO postgres;
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA qmgr TO postgres;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA qmgr TO postgres;

COMMENT ON SCHEMA qmgr IS 'Q-Mgr Queue Management System Schema';
