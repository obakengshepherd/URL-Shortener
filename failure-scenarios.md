# Failure Scenarios — URL Shortener Service

> **Status**: Skeleton — stubs defined on Day 2. Full mitigations and implementations added on Day 27.

---

## Scenario 1 — Redis Cache Failure

**Trigger**: Redis becomes unavailable. All redirect requests result in cache misses.

**Component that fails**: Redis cache layer.

**Impact**: User-facing — redirect latency increases as every request falls through to
PostgreSQL. At high traffic volumes, PostgreSQL may be overwhelmed. No data loss;
no incorrect redirects.

**Mitigation strategy**: TBD Day 27 — involves circuit breaker on Redis client, automatic
fallback to PostgreSQL for redirect lookups, alerting on Redis unavailability, Redis
recovery and cache warm-up procedure.

---

## Scenario 2 — Short Code Collision at Scale

**Trigger**: As the total number of stored URLs grows, random 8-character code generation
produces increasingly frequent collisions. Retry logic eventually exhausts its attempt limit.

**Component that fails**: Code generation strategy.

**Impact**: User-facing — URL creation fails after exhausting retry attempts. Client
receives a 500 error.

**Mitigation strategy**: TBD Day 27 — involves migration to pre-generated code pool strategy
when URL count exceeds 100M. Background job pre-populates the pool; write path claims from
pool using SELECT FOR UPDATE SKIP LOCKED.

---

## Scenario 3 — Analytics Consumer Falls Behind

**Trigger**: Click event volume spikes (a URL goes viral). The Analytics Consumer cannot
process events as fast as they are published. RabbitMQ queue depth grows.

**Component that fails**: Analytics Consumer throughput.

**Impact**: Internal — click counts in the analytics database lag behind real activity.
Redirect service is completely unaffected.

**Mitigation strategy**: TBD Day 27 — involves consumer auto-scaling, batch insert
optimisation in the consumer, queue depth alerting, and documented SLA for analytics
delay (e.g., counts accurate within 5 minutes under normal load).

---

## Scenario 4 — Expired URL Served from Cache

**Trigger**: A URL has an `expires_at` set. The Redis cache entry was populated before
expiry with a 10-minute TTL. The URL expires, but the cache entry has not yet expired.
Redirect requests during this window receive a 301 instead of a 410.

**Component that fails**: Cache TTL / expiry coordination.

**Impact**: User-facing — users are redirected to an expired URL destination during the
cache stale window (up to 10 minutes).

**Mitigation strategy**: TBD Day 27 — involves setting cache TTL to min(10 minutes,
time_until_expiry) at cache write time; active cache invalidation when a URL is manually
deactivated or its expiry is updated.

---

## Scenario 5 — Custom Alias Race Condition

**Trigger**: Two users simultaneously submit `POST /urls` with the same custom alias.
Both pass the application-level uniqueness check before either commits to the database.

**Component that fails**: Application-level uniqueness check under concurrency.

**Impact**: User-facing — one request succeeds; the other receives an unhandled constraint
violation error rather than a clean 409 Conflict response.

**Mitigation strategy**: TBD Day 27 — involves relying on the PostgreSQL unique index on
`custom_aliases.alias` as the authoritative constraint check, catching the constraint
violation at the service layer, and returning a clean 409 Conflict to the client.
