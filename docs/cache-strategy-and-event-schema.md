# Cache Strategy & Event Schema — URL Shortener Service

---

## Cache Strategy

### Pattern: Cache-Aside with Write-Through on Create

```
Read (GET /{code} — hot path):
  1. GET url:{shortCode}           ← Redis lookup (~1ms)
  2. HIT  → check IsActive + ExpiresAt → return 301 or 410
  3. MISS → query PostgreSQL (replica)
         → SET url:{shortCode} {json} EX {min(600, time_until_expiry)}
         → return result

Write (POST /urls):
  1. INSERT into PostgreSQL         ← authoritative store
  2. SET url:{shortCode} {json} EX 600    ← write-through cache population
     ← This avoids the first-access cache miss for new URLs
```

Write-through on creation is appropriate here because we have all the data needed
to populate the cache at creation time. This contrasts with the wallet system where
write-through would race with the commit.

### Cache Stampede Protection (hot viral links)

When a popular URL's cache entry expires:
```
1. GET url:{code} → MISS
2. SET mutex:url:{code} 1 NX EX 5  ← try mutex
   - Acquired: query DB, SET cache, DEL mutex
   - Not acquired: wait 30ms, retry GET — if still miss, query DB directly
```

This prevents a thundering herd of DB queries when a viral link's 10-minute
cache entry expires under heavy load.

### TTL Strategy

Every cached key has a TTL — no unbounded keys:

| Key                       | TTL                          | Rationale                                |
|--------------------------|------------------------------|------------------------------------------|
| `url:{shortCode}`         | min(10 min, time_to_expiry)  | Balance freshness vs DB load             |
| `mutex:url:{shortCode}`   | 5 seconds                    | Must expire even if holder crashes       |
| `clicks:pending:{urlId}`  | 5 minutes                    | Flushed to DB by background job          |

The URL expiry TTL is computed at cache write time: `min(600s, expires_at - now())`.
This ensures an expired URL is never served from cache after its expiry time.

---

## Event Schema

### RabbitMQ Queue: `click.events` (durable)

- **Producer:** RedirectService (fire-and-forget, after serving redirect)
- **Consumer:** ClickAnalyticsConsumer (group: background service)
- **Dead letter:** → `url.dlx` exchange → `click.dead` queue
- **Message TTL:** 24 hours (clicks older than 24h are not analytically useful)

**Message schema:**
```json
{
  "short_code": "summer24",
  "url_id": "url_01j9...",
  "clicked_at": "2024-01-15T10:30:00Z",
  "user_agent": "Mozilla/5.0 ...",
  "referer": "https://gmail.com",
  "country_code": "ZA",
  "ip_hash": "sha256:dailysalt:192.168.1.1"
}
```

**Processing:** Batched inserts — consumer accumulates up to 100 messages or 5
seconds (whichever comes first) before writing to `url_clicks` in a single
database transaction. This converts ~1,200 individual INSERTs/sec into ~12
batch INSERTs/sec.

**Dead Letter Queue (`click.dead`):**
Messages that fail processing (malformed JSON, constraint violation) are routed
to the DLQ via the dead letter exchange. The DLQ is monitored by operators for
data quality issues. Failed click events represent a small analytics gap, not a
correctness failure.

**Delivery semantics:** At-least-once. The consumer ACKs immediately after
receiving the message (best-effort for analytics). A small under-count during
consumer crashes is acceptable — redirect performance is never affected.
