-- =============================================================================
-- V001__create_urls_table.sql
-- URL Shortener Service — urls table (core redirect entity)
--
-- ROLLBACK:
--   DROP TABLE IF EXISTS urls CASCADE;
-- =============================================================================

CREATE TABLE urls (
    id            VARCHAR(36)  NOT NULL,
    short_code    VARCHAR(32)  NOT NULL,
    original_url  TEXT         NOT NULL,
    created_by    VARCHAR(36)  NOT NULL,
    title         VARCHAR(128) NULL,
    expires_at    TIMESTAMPTZ  NULL,
    is_active     BOOLEAN      NOT NULL DEFAULT TRUE,
    click_count   BIGINT       NOT NULL DEFAULT 0,
    created_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    updated_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),

    CONSTRAINT urls_pkey PRIMARY KEY (id),

    -- The most critical unique constraint in this system.
    -- short_code must be globally unique — two URLs cannot share a code.
    -- Application generates base62 codes and retries on collision, but this
    -- constraint is the authoritative uniqueness guarantee.
    CONSTRAINT urls_short_code_unique UNIQUE (short_code),

    CONSTRAINT urls_click_count_non_negative CHECK (click_count >= 0),

    -- Ensure original_url is not empty
    CONSTRAINT urls_original_url_not_empty CHECK (LENGTH(TRIM(original_url)) > 0)
);

COMMENT ON TABLE urls IS
    'Core URL record. short_code is either system-generated (8-char base62) '
    'or a user-supplied custom alias. '
    'is_active = FALSE causes redirect service to return 410 Gone. '
    'expires_at = NULL means the URL never expires.';

COMMENT ON COLUMN urls.click_count IS
    'Denormalised counter updated by the analytics consumer. '
    'Enables O(1) total-count reads without scanning url_clicks. '
    'Lags real click count by up to one analytics processing batch (acceptable).';

COMMENT ON COLUMN urls.short_code IS
    'UNIQUE constraint enforces globally unique codes. '
    'On collision during generation: application retries up to 3 times. '
    'At scale (>100M URLs): use pre-generated code pool (SELECT FOR UPDATE SKIP LOCKED).';

CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER urls_updated_at_trigger
    BEFORE UPDATE ON urls
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();

-- =============================================================================
-- V002__create_url_clicks_table.sql
-- URL Shortener Service — url_clicks table (partitioned analytics)
--
-- ROLLBACK:
--   DROP TABLE IF EXISTS url_clicks CASCADE;
-- =============================================================================

-- url_clicks is designed for future range partitioning by clicked_at (monthly).
-- For now it is a regular table — partitioning is added via ALTER TABLE when
-- volume exceeds 100M rows.

CREATE TABLE url_clicks (
    id            VARCHAR(36)  NOT NULL,
    url_id        VARCHAR(36)  NOT NULL,
    clicked_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    user_agent    TEXT         NULL,
    referer       TEXT         NULL,
    country_code  CHAR(2)      NULL,
    ip_hash       VARCHAR(64)  NULL,

    CONSTRAINT url_clicks_pkey PRIMARY KEY (id),

    CONSTRAINT url_clicks_url_fk
        FOREIGN KEY (url_id) REFERENCES urls (id)
        ON DELETE CASCADE
        -- CASCADE: if a URL is hard-deleted (rare), remove orphaned clicks.
        -- In practice URLs are deactivated (is_active=false), never hard-deleted.
);

COMMENT ON TABLE url_clicks IS
    'Click event log. Written asynchronously by RabbitMQ analytics consumer. '
    'Partition by clicked_at (monthly) when row count exceeds 100M. '
    'ip_hash is SHA-256 of IP + daily salt — enables unique visitor counting '
    'without storing raw IP addresses (GDPR consideration).';

COMMENT ON COLUMN url_clicks.ip_hash IS
    'SHA-256(IP + daily_rotating_salt). Not reversible to original IP. '
    'Enables unique visitor counting within a day. '
    'Salted daily so cross-day tracking is not possible.';

-- =============================================================================
-- V003__create_custom_aliases_table.sql
-- URL Shortener Service — custom_aliases table
--
-- ROLLBACK:
--   DROP TABLE IF EXISTS custom_aliases CASCADE;
-- =============================================================================

CREATE TABLE custom_aliases (
    alias       VARCHAR(32)  NOT NULL,
    url_id      VARCHAR(36)  NOT NULL,
    created_at  TIMESTAMPTZ  NOT NULL DEFAULT NOW(),

    CONSTRAINT custom_aliases_pkey PRIMARY KEY (alias),
    -- alias is the PK — globally unique, cannot be reassigned.

    CONSTRAINT custom_aliases_url_fk
        FOREIGN KEY (url_id) REFERENCES urls (id)
        ON DELETE RESTRICT
);

COMMENT ON TABLE custom_aliases IS
    'User-supplied short codes separate from system-generated ones. '
    'alias is the PRIMARY KEY — globally unique, once claimed it is permanent. '
    'A deactivated URL''s alias is never released back to the pool — '
    'prevents confusing users who cached the old destination.';

-- Reserved word validation happens at the application layer.
-- The DB cannot easily enumerate reserved words (api, admin, etc.).

-- =============================================================================
-- V004__add_constraint_verification.sql
-- URL Shortener — Schema validation
-- =============================================================================

DO $$
BEGIN
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'urls'), 'urls missing';
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'url_clicks'), 'url_clicks missing';
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'custom_aliases'), 'custom_aliases missing';
    RAISE NOTICE 'URL Shortener schema verified.';
END;
$$;

-- =============================================================================
-- V005__add_indexes.sql
-- URL Shortener Service — All performance indexes
--
-- ROLLBACK (reverse order):
--   DROP INDEX IF EXISTS url_clicks_ip_hash_date_idx;
--   DROP INDEX IF EXISTS url_clicks_url_id_clicked_at_idx;
--   DROP INDEX IF EXISTS urls_active_expires_idx;
--   DROP INDEX IF EXISTS urls_created_by_idx;
-- =============================================================================

-- Query: User's URL management — "List my short URLs"
CREATE INDEX urls_created_by_idx
    ON urls (created_by, created_at DESC);

-- Query: Scheduled expiry job — find URLs that need to be expired
-- Partial index: only active URLs (inactive ones are already expired)
CREATE INDEX urls_active_expires_idx
    ON urls (expires_at ASC)
    WHERE is_active = TRUE AND expires_at IS NOT NULL;

COMMENT ON INDEX urls_active_expires_idx IS
    'Partial index for scheduled expiry job. '
    'Much smaller than a full index — only active URLs with an expiry date. '
    'Scheduled job: SELECT ... WHERE is_active=TRUE AND expires_at < NOW().';

-- Query: Analytics — time-range click count for a URL
CREATE INDEX url_clicks_url_id_clicked_at_idx
    ON url_clicks (url_id, clicked_at DESC);

COMMENT ON INDEX url_clicks_url_id_clicked_at_idx IS
    'Primary analytics query: clicks for URL X in date range. '
    'DESC ordering supports "last 30 days" queries without a sort step.';

-- Query: Unique visitor counting by day
CREATE INDEX url_clicks_ip_hash_date_idx
    ON url_clicks ((clicked_at::date), ip_hash);

COMMENT ON INDEX url_clicks_ip_hash_date_idx IS
    'Unique visitor count per day: COUNT DISTINCT ip_hash WHERE clicked_at::date = ?';

ANALYZE urls;
ANALYZE url_clicks;
ANALYZE custom_aliases;
