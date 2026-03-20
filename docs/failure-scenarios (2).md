# Failure Scenarios — URL Shortener Service

> **Status**: Complete — Days 25–27 implementation. Replaces Phase 1 skeleton.

---

## Scenario 1 — Redis Cache Failure (All Redirects Hit DB)

**Trigger**
Redis becomes unreachable. Every redirect request results in a cache miss. All
redirect traffic falls through to PostgreSQL.

**Affected Components**
UrlCacheService, RedirectService, PostgreSQL (sudden read spike).

**User-Visible Impact**
Redirect latency increases from ~2ms (cache hit) to ~15ms (DB read). At peak load
(1M redirects/hour ≈ 278/sec), PostgreSQL receives all 278 reads/second on top of
its write load. At extreme scale this causes PostgreSQL saturation.

**System Behaviour Without Mitigation**
If Redis exceptions propagate, every redirect returns a 500. No links resolve.
At high volume, PostgreSQL connection pool exhausts as all threads wait for DB.

**Mitigation**

1. **Fail-open Redis client:** All Redis calls in `UrlCacheServiceV2` are wrapped
   in try/catch returning null on failure. A null cache result triggers the DB
   fallback — no exception propagates to the caller.

2. **Circuit breaker on Redis:** After 5 consecutive Redis failures, the circuit
   opens. All requests go directly to PostgreSQL without attempting Redis (skipping
   the 3-second Redis timeout). This keeps redirect latency predictable even
   when Redis is down.

3. **PostgreSQL read replica for redirects:** The redirect fallback path reads
   from a PostgreSQL read replica. Multiple replicas absorb the spike.

4. **CDN layer for ultra-hot links:** Popular short codes can be served from a CDN
   with a 10-minute TTL (matching the Redis TTL). CDN serves these even when both
   Redis and PostgreSQL are unavailable.

**Detection**
- Alert: Redis health check → Degraded.
- Metric: `cache_miss_rate > 0.95` sustained 2 minutes → Redis likely down.
- Alert: `postgresql_redirect_reads_per_second > 200` → fallback is active.

---

## Scenario 2 — Expired URL Served from Cache

**Trigger**
A URL has `expires_at = 2024-01-15T12:00:00Z`. The Redis cache entry was populated
at 11:55 AM with a 10-minute TTL (expires in cache at 12:05). From 12:00 to 12:05,
redirects return 301 instead of 410.

**Affected Components**
UrlCacheService TTL calculation, RedirectService expiry check.

**User-Visible Impact**
Users are redirected to a destination that the URL owner considers expired. For
marketing URLs this may route users to a campaign that has ended or a promotional
price no longer valid.

**Mitigation**

1. **Cache TTL = min(DefaultTtl, time_until_expiry):**
   ```csharp
   var ttl = expiresAt.HasValue
       ? new[] { DefaultTtl, expiresAt.Value - DateTimeOffset.UtcNow }.Min()
       : DefaultTtl;
   if (ttl <= TimeSpan.Zero) return; // already expired — never cache
   ```
   This ensures the cache entry expires at or before the URL's own expiry.

2. **Active cache invalidation on expiry update:** When a URL's `expires_at`
   is changed via `PATCH /urls/{code}`, the service immediately calls
   `InvalidateAsync(shortCode)` to force the next request to re-read from DB.

3. **Double-check in cache value:** The cached `CachedUrlEntry` record includes
   `ExpiresAt`. `RedirectService` checks it on cache hit:
   ```csharp
   if (cached.ExpiresAt.HasValue && cached.ExpiresAt.Value <= DateTimeOffset.UtcNow)
       return RedirectResult.Gone;
   ```
   This catches the narrow window between cache TTL and actual expiry.

**Detection**
- Metric: `expired_url_served_from_cache_total` — should always be zero with
  the TTL calculation in place.
- Test: Automated integration test that creates a URL expiring in 5 seconds and
  verifies it returns 410 immediately after expiry.

---

## Scenario 3 — Custom Alias Race Condition

**Trigger**
Two users simultaneously submit `POST /urls` with the same custom alias `"summer24"`.
Both pass the application-level `AliasExistsAsync` check before either commits.

**Affected Components**
UrlService, `custom_aliases` table UNIQUE constraint (PRIMARY KEY on alias).

**User-Visible Impact**
Without mitigation: both requests receive a 201 Created, but only one is stored.
The second receives a 500 from an unhandled constraint violation.

**Mitigation**
The `custom_aliases.alias` column is the PRIMARY KEY — an implicit unique index.
`UrlService` catches `NpgsqlException` with SqlState `23505` (unique violation)
and returns a clean 409 Conflict:

```csharp
catch (NpgsqlException ex) when (ex.SqlState == "23505")
{
    throw new AliasConflictException(request.Alias);
}
```

`AliasConflictException` is mapped to 409 by `GlobalExceptionHandler`.
The application-level check (`AliasExistsAsync`) is a fast-path optimisation
to avoid the DB write on obvious duplicates — the DB constraint is the
authoritative guard.

**Detection**
- Metric: `alias_conflict_total` — a small rate is expected (concurrent requests);
  a sustained spike indicates a client bug or intentional enumeration attack.

---

## Scenario 4 — Analytics Consumer Falls Behind (Queue Depth Grows)

**Trigger**
A URL is shared in a viral post. Click volume spikes from 100/sec to 50,000/sec.
RabbitMQ `click.events` queue depth grows faster than the analytics consumer can drain it.

**Affected Components**
ClickAnalyticsConsumer, RabbitMQ `click.events` queue, `url_clicks` table write throughput.

**User-Visible Impact**
None — redirect performance is completely unaffected. Click counts in the analytics
dashboard lag behind real activity by minutes or hours during the spike.

**Mitigation**

1. **Redirect path is fully decoupled:** `RedirectService` publishes click events
   fire-and-forget using `BasicPublish` (no blocking wait). If RabbitMQ is
   unavailable, the click event is dropped silently — the redirect still happens.

2. **Consumer batch size scales with queue depth:** Normal batches are 100 events.
   During a spike, the consumer increases to 500-event batches (the `BasicQos`
   `prefetchCount: 200` allows this naturally as more messages are ready).

3. **Add consumer instances:** RabbitMQ competing consumers allow horizontal
   scaling. Each additional consumer instance independently drains the queue.
   Scale trigger: `click.events` queue depth > 500K messages.

4. **Queue TTL as safety valve:** Messages older than 24 hours are discarded
   (`x-message-ttl: 86400000`). A 24-hour-old click count is analytically useless.

**Detection**
- Alert: `rabbitmq_queue_depth{queue="click.events"} > 500000`.
- Metric: `click_consumer_processing_lag_minutes` — alert if > 15 minutes.

---

## Scenario 5 — Short Code Generation Failure (Collision Exhaustion)

**Trigger**
At > 100M stored URLs, random 8-char base62 code generation produces collisions on
every attempt. After 3 retries, `UrlService` throws `CodeGenerationException`.
URL creation fails with 500.

**Mitigation**
The current retry-based approach is replaced by a pre-generated code pool for scale:

```sql
CREATE TABLE code_pool (
    code VARCHAR(8) PRIMARY KEY,
    claimed_at TIMESTAMPTZ NULL
);
```

A background job pre-fills the pool with 1M unclaimed codes. `UrlService` claims
from the pool using `SELECT FOR UPDATE SKIP LOCKED`:

```sql
SELECT code FROM code_pool WHERE claimed_at IS NULL LIMIT 1 FOR UPDATE SKIP LOCKED;
UPDATE code_pool SET claimed_at = NOW() WHERE code = @code;
```

This eliminates real-time collision resolution entirely and guarantees constant-time
code acquisition regardless of URL count.

**Detection**
- Alert: `code_generation_retry_rate > 0.01` (1% collision rate) → migration to pool
  strategy should be initiated.
- Alert: `code_pool_unclaimed_count < 10000` → pool refill job not running.
