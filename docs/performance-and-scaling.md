# Performance — URL Shortener Service

---

## Current Bottlenecks

### Bottleneck 1: Cache expiry stampede on viral links
A URL shared in a trending post can receive thousands of concurrent requests.
When its 10-minute cache entry expires simultaneously for all of them, a stampede
of PostgreSQL queries occurs.

**Mitigation implemented in Phase 5:** `UrlCacheServiceV2.GetWithStampedeProtectionAsync`
uses Redis NX mutex. Only one caller queries the database; others wait 30ms.

### Bottleneck 2: Analytics write throughput at peak
100M redirects/day ≈ 1,200 clicks/second. If written synchronously per redirect,
the `url_clicks` table would receive 1,200 INSERTs/second — unsustainable for a
single PostgreSQL instance.

**Mitigation implemented in Phase 5:** RabbitMQ consumer batches 100 events per
INSERT → ~12 batch writes/second.

### Bottleneck 3: Short code generation collision at high URL count
At >100M stored URLs, the probability of a random 8-char base62 code colliding
with an existing code rises from negligible to meaningful (~0.5%). Each collision
adds one more retry loop.

**Mitigation (not yet implemented — Day 15 note):** Pre-generated code pool using
`SELECT FOR UPDATE SKIP LOCKED` from a `code_pool` table. Eliminates real-time
collision resolution entirely.

---

## Cache Hit Rate Targets

| Key                   | Target | TTL        | Notes                                          |
|-----------------------|--------|------------|------------------------------------------------|
| `url:{shortCode}`     | ≥ 90%  | 10 min     | Long tail of rarely-accessed URLs misses more  |
| Hot links (top 1%)    | ≥ 99%  | 10 min     | Stampede protection ensures they stay warm     |
| `mutex:url:{code}`    | N/A    | 5s         | Very short-lived; only present during stampede |

---

## Database Read Replica Routing

| Operation                            | Target       | Reason                              |
|--------------------------------------|--------------|--------------------------------------|
| Redirect fallback (cache miss)       | Read replica | High volume; 1-min eventual OK       |
| `GET /urls/{code}/stats`             | Read replica | Analytics display; eventual OK       |
| `POST /urls` (create + unique check) | **Primary**  | Unique constraint must be on primary |
| `DELETE /urls/{code}` (deactivate)   | **Primary**  | Write path                           |
| `PATCH /urls/{code}` (update)        | **Primary**  | Write path                           |

---

## Connection Pool Sizing

| Service           | Pool Size | Rationale                                         |
|-------------------|-----------|---------------------------------------------------|
| Redirect Service  | 5         | Most requests hit Redis cache; DB pool rarely used|
| Write API         | 10        | Lower traffic; URL creations are infrequent        |
| Analytics Consumer| 3         | Batch writer; holds connection briefly per flush   |

---

## Query Performance Targets

| Query                                         | Target p95 | Index                         |
|----------------------------------------------|-----------|-------------------------------|
| `SELECT * FROM urls WHERE short_code = ?`    | < 2ms     | `urls_short_code_uniq`        |
| `INSERT INTO url_clicks` (batch 100)         | < 15ms    | Sequential                    |
| `UPDATE urls SET click_count = click_count+1`| < 5ms     | PK                            |
| `SELECT url_clicks WHERE url_id` (analytics) | < 20ms    | `url_clicks_url_id_clicked_at_idx` |

---

## Rate Limiting Configuration

| Policy        | Limit | Window | Endpoint                        |
|---------------|-------|--------|---------------------------------|
| url-create    | 100   | 1 hour | `POST /urls`                    |
| authenticated | 120   | 1 min  | All other authenticated endpoints|
| unauthenticated| 0    | —      | Redirect endpoint is public — no auth limit, but DDoS protection at LB layer |

---

# Scaling Strategy — URL Shortener Service

---

## Horizontal Scaling Table

| Component                | Scales Horizontally? | Notes                                              |
|--------------------------|---------------------|----------------------------------------------------|
| Redirect Service         | ✅ Yes               | Most critical to scale; fully stateless            |
| Write API                | ✅ Yes               | Lower traffic; scales separately from Redirect      |
| Analytics Consumer       | ✅ Yes               | RabbitMQ competing consumers; add freely           |
| Redis (URL cache)        | ✅ Yes (Cluster)     | Shard by short_code hash                           |
| RabbitMQ                 | ✅ Yes               | Clustered; click.events queue is durable           |
| PostgreSQL primary       | ❌ No (writes)       | Single primary; very few writes relative to reads  |
| PostgreSQL replicas      | ✅ Yes               | Redirect fallback and analytics reads here         |

**Key insight:** The URL Shortener has an extreme read:write ratio (~100:1).
The Redirect Service should receive almost all horizontal scaling investment.
The Write API can remain at 2–3 instances indefinitely.

---

## Load Balancing Configuration

**Redirect Service (separate subdomain: go.short.internal):**
```
Algorithm:  Round-Robin (fully stateless)
Affinity:   None
Health:     GET /health every 10s
CDN:        Optional — hot links can be served from CDN edge with 10-min TTL
            matching the Redis TTL (CDN + Redis = near-zero DB load for viral links)
```

**Write API (management endpoints: api.short.internal):**
```
Algorithm:  Round-Robin
Affinity:   None
Health:     GET /health every 10s
```

---

## Stateless Design Guarantees

1. **Redis is the shared URL cache.** A short code cached by Redirect Instance-1
   is readable by Redirect Instance-2 — no instance-local caching.

2. **Stampede mutex is Redis-based.** Two Redirect instances seeing a concurrent
   cache miss both attempt the same Redis NX key — only one acquires it.

3. **Click events are fire-and-forget.** Redirect instances publish to RabbitMQ
   without waiting for acknowledgement. Instance failure loses at most the
   in-flight batch — acceptable for analytics.

---

## Scaling Triggers

| Metric                           | Threshold    | Action                                   |
|----------------------------------|--------------|------------------------------------------|
| Redirect p99                     | > 10ms       | Add Redirect Service instance            |
| Cache hit rate                   | < 85%        | Investigate access pattern; increase TTL |
| RabbitMQ `click.events` depth    | > 500K msgs  | Add analytics consumer instances         |
| PostgreSQL read replica CPU      | > 60%        | Add read replica                         |
