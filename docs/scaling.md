# Scaling Strategy — URL Shortener Service

---

## Current Single-Node Bottlenecks

- **Redirect Service read throughput**: At 100M redirects/day (~1,200 requests/second
  sustained), a single Redirect Service instance handling all traffic with a cold cache
  would require ~1,200 Redis reads per second — well within a single Redis instance's
  capacity. The bottleneck is not Redis throughput but cache hit rate: a low hit rate
  means each miss adds a PostgreSQL read, which does not scale as linearly.

- **Analytics write throughput**: At 1,200 clicks/second, the Analytics Consumer must
  write 1,200 rows/second to the `url_clicks` table. For a single PostgreSQL instance,
  this is achievable but is the first write bottleneck that will be felt as traffic grows.
  Batching inserts mitigates this significantly.

- **Short code generation collisions at scale**: At low URL counts, random generation
  with collision retry works. As the total URL count approaches hundreds of millions,
  the collision probability during generation increases. The code pool strategy eliminates
  this concern but introduces a background job dependency.

---

## Horizontal Scaling Plan

### Redirect Service

The Redirect Service is the most performance-critical component and the highest priority
for horizontal scaling. It is fully stateless — all state is in Redis and PostgreSQL.
Scale by adding instances behind the load balancer.

Each instance connects to the same Redis cache. When one instance populates the cache on
a miss, subsequent requests from any instance are served from cache. This shared cache
semantics means that adding more Redirect Service instances does not increase cache miss
rate — it just distributes the CPU load for cache lookups and response handling.

Target: 1 Redirect Service instance per 5,000 requests/second (conservatively sized with
headroom).

### Write API

The Write API receives far less traffic than the Redirect Service. Scale it separately —
2–3 instances are sufficient for the defined scale. The Write API does not need to match
the Redirect Service instance count.

### Redis

A single Redis instance handles the redirect cache without issue:
- Memory: Each cached entry is roughly 200 bytes (short code + URL). At 10M active URLs
  cached (assuming a hot working set), that is ~2GB. Use a Redis instance with 8–16GB RAM
  to provide ample headroom.
- Throughput: 1,200 reads/second plus write-through writes on creation. Far below Redis's
  ~100K operations/second capacity on a single instance.

If a single Redis instance becomes a bottleneck (unlikely at this scale), introduce Redis
Cluster partitioned by short code hash. Each Redirect Service instance looks up the correct
shard by hashing the short code.

### PostgreSQL

**Phase 1 — Read replicas**: Route redirect fallback reads (cache misses) to a read replica.
All writes (URL creation, click analytics) go to the primary.

**Phase 2 — Partition url_clicks**: Partition by week or month. Click analytics queries are
always range-based (last 7 days, last 30 days) and benefit significantly from partition pruning.
Old partitions can be archived or dropped after the retention period.

**Phase 3 — Batch click writes**: Rather than inserting one `url_clicks` row per click event,
the Analytics Consumer accumulates events in a buffer (e.g., 100ms window) and inserts in
batches. This converts 1,200 single-row inserts/second into 12 batch inserts/second of 100 rows
each, dramatically reducing write overhead.

### RabbitMQ

The `click.events` queue handles 1,200 messages/second peak. A single RabbitMQ instance on
reasonable hardware handles tens of thousands of messages/second. Scale is not a concern at
this volume.

If the Analytics Consumer falls behind, click events accumulate in the queue — this is by
design. Queue depth should be monitored; alert when queue depth exceeds 5 minutes of click
volume (5 × 60 × 1,200 = 360,000 messages). Scale consumer instances to drain the backlog.

---

## Cache Hit Rate Targets

| Cache Key         | TTL            | Target Hit Rate | On Miss Action                          |
|-------------------|----------------|-----------------|------------------------------------------|
| `url:{code}`      | 10 min (max)   | ≥ 90%           | Query PostgreSQL replica, write to cache |

The 90% cache hit rate target means that for every 10 redirect requests, 9 are served from
Redis and 1 hits the database. At 1,200 requests/second, that is 120 database reads/second
for cache misses — well within PostgreSQL's capacity on a read replica.

**What drives cache hit rate down:**
- Short TTL relative to URL access frequency: a URL accessed once every 15 minutes has a
  low effective hit rate with a 10-minute TTL. Tuning TTL per URL based on access frequency
  is a future optimisation.
- Long tail of rarely accessed URLs: most URLs are only accessed a handful of times total.
  The cache's value comes from the hot set of frequently accessed URLs.

**Cache stampede protection**: When a heavily accessed URL's cache entry expires, many
concurrent requests may all experience a cache miss simultaneously and all query the database
in parallel. Mitigate with a short mutex: the first requester to get a miss acquires a Redis
lock, fetches from the database, and releases the lock. Others wait briefly and then hit the
now-populated cache.
