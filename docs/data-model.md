# Data Model — URL Shortener Service

---

## Database Technology Choices

### PostgreSQL (URL records and analytics)
URL records require strong uniqueness guarantees: two concurrent creates with the same
custom alias must result in exactly one success. PostgreSQL's unique index and atomic
`INSERT ... ON CONFLICT` semantics handle this correctly — Redis `SETNX` would not be
sufficient because the authoritative record must survive a Redis restart.

Click analytics are stored in PostgreSQL in a time-partitioned table (`url_clicks`).
The analytics path is entirely async (published via RabbitMQ and written by a consumer),
so write latency does not affect the redirect response time.

### Redis (Short code → URL cache)
The redirect path is the highest-volume operation in the system. Redis caches the
mapping of `short_code → {original_url, expires_at, is_active}` with a 10-minute TTL.
A cache hit serves the redirect without touching PostgreSQL. At a 90%+ cache hit rate
for active links, PostgreSQL sees only ~10% of redirect traffic.

### RabbitMQ (Click event queue)
Click events are published fire-and-forget to RabbitMQ after each redirect. A consumer
writes them to PostgreSQL in batches. The redirect response never waits for the analytics
write — click count accuracy is best-effort.

---

## Entity Relationship Overview

A **User** (authenticated via upstream service) creates **ShortUrls**. Each ShortUrl has
a `short_code` — either system-generated (base62, 8 chars) or a user-supplied alias
stored in **CustomAliases**.

**UrlClicks** records every redirect event. This table is partitioned by date for
efficient range queries and eventual archiving of old click data.

---

## Table Definitions

### `urls`

| Column        | Type          | Constraints                         | Description                                   |
|---------------|---------------|-------------------------------------|-----------------------------------------------|
| `id`          | `VARCHAR(36)` | PRIMARY KEY                         | Prefixed UUID: `url_<uuid>`                   |
| `short_code`  | `VARCHAR(32)` | NOT NULL, UNIQUE                    | 8-char base62 code or custom alias            |
| `original_url`| `TEXT`        | NOT NULL                            | Destination URL (up to 2048 chars typical)    |
| `created_by`  | `VARCHAR(36)` | NOT NULL                            | Owner user ID                                 |
| `title`       | `VARCHAR(128)`| NULL                                | Human-readable label                          |
| `expires_at`  | `TIMESTAMPTZ` | NULL                                | NULL = never expires                          |
| `is_active`   | `BOOLEAN`     | NOT NULL, DEFAULT TRUE              | FALSE = returns 410 Gone                      |
| `click_count` | `BIGINT`      | NOT NULL, DEFAULT 0                 | Denormalised counter — updated by analytics consumer |
| `created_at`  | `TIMESTAMPTZ` | NOT NULL, DEFAULT NOW()             | Immutable                                     |
| `updated_at`  | `TIMESTAMPTZ` | NOT NULL, DEFAULT NOW()             | Updated on title/expiry/status changes        |

**Why `click_count` is denormalised on the `urls` table?** Counting rows in `url_clicks`
for every request to `GET /urls/{code}/stats` would require a full table scan (even with
indexes) as click counts grow into the millions. The denormalised counter enables an O(1)
total-count lookup. It is updated by the analytics consumer asynchronously, so it lags by
at most one processing batch — acceptable for a display metric.

### `url_clicks`

| Column        | Type          | Constraints              | Description                                       |
|---------------|---------------|--------------------------|---------------------------------------------------|
| `id`          | `VARCHAR(36)` | NOT NULL                 | UUID — part of composite PK with partition key    |
| `url_id`      | `VARCHAR(36)` | NOT NULL, FK → urls      | The short URL that was clicked                    |
| `clicked_at`  | `TIMESTAMPTZ` | NOT NULL, DEFAULT NOW()  | Partition key — table is partitioned by this      |
| `user_agent`  | `TEXT`        | NULL                     | Raw user agent string                             |
| `referer`     | `TEXT`        | NULL                     | HTTP Referer header value                         |
| `country_code`| `CHAR(2)`     | NULL                     | ISO 3166-1 alpha-2, derived from IP at collection |
| `ip_hash`     | `VARCHAR(64)` | NULL                     | SHA-256 of IP address — for unique visitor count  |

**Partitioning:** `url_clicks` is range-partitioned by `clicked_at` (monthly partitions).
Partition pruning means a query for "clicks in January 2024" only scans the January
partition. Old partitions can be detached and archived without affecting the active table.

**Why `ip_hash` and not `ip_address`?** Storing raw IP addresses is a GDPR concern in
many jurisdictions. Hashing the IP with a daily rotating salt provides enough consistency
for unique visitor counting within a day while making individual IP addresses
unrecoverable.

### `custom_aliases`

| Column       | Type          | Constraints              | Description                               |
|--------------|---------------|--------------------------|-------------------------------------------|
| `alias`      | `VARCHAR(32)` | PRIMARY KEY              | The custom alias string                   |
| `url_id`     | `VARCHAR(36)` | NOT NULL, FK → urls      | The URL this alias maps to                |
| `created_at` | `TIMESTAMPTZ` | NOT NULL, DEFAULT NOW()  | Immutable                                 |

**Why a separate table for custom aliases?** Custom aliases share the same namespace as
system-generated short codes. By keeping them in a dedicated table with `alias` as the
primary key, the uniqueness constraint is enforced independently of the `urls.short_code`
column. This also allows the redirect service to query: "is this code a custom alias?"
vs "is this a system-generated code?" without a scan of the main `urls` table.

---

## Index Strategy

| Index Name                         | Table        | Columns                         | Type    | Query Pattern                                    |
|------------------------------------|--------------|---------------------------------|---------|--------------------------------------------------|
| `urls_short_code_uniq`             | `urls`       | `(short_code)`                  | UNIQUE  | Redirect lookup — the most critical read path    |
| `urls_created_by_idx`              | `urls`       | `(created_by, created_at DESC)` | B-tree  | User's URL management list                       |
| `urls_is_active_expires_idx`       | `urls`       | `(is_active, expires_at) WHERE is_active = TRUE` | Partial B-tree | Find active URLs with upcoming expiry |
| `url_clicks_url_id_clicked_at_idx` | `url_clicks` | `(url_id, clicked_at DESC)`     | B-tree  | Time-range analytics per URL                     |
| `url_clicks_ip_hash_clicked_at`    | `url_clicks` | `(ip_hash, clicked_at::date)`   | B-tree  | Unique visitor count per day                     |

---

## Relationship Types

- **User → ShortUrls**: one-to-many.
- **ShortUrl → UrlClicks**: one-to-many (partitioned).
- **ShortUrl → CustomAlias**: one-to-one (a URL has at most one custom alias, which is also its short code).

---

## Soft Delete Strategy

ShortUrls are deactivated by setting `is_active = FALSE`. They are never hard-deleted.
The short code is never released back into the pool — a deactivated code permanently
returns 410 Gone, preventing it from being reassigned to a different URL and confusing
users who cached the old destination.

---

## Audit Trail

| Table          | `created_at` | `updated_at` | Notes                                               |
|----------------|--------------|--------------|-----------------------------------------------------|
| `urls`         | ✓            | ✓            | Updated on title, expiry, and is_active changes     |
| `url_clicks`   | `clicked_at` | ✗            | Append-only, immutable, partitioned by `clicked_at` |
| `custom_aliases`| ✓           | ✗            | Immutable — aliases cannot be reassigned            |
