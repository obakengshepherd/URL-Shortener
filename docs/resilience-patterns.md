# Resilience Patterns — All Systems

> Applies to all 7 systems. System-specific notes are called out inline.
> Day 27 implementation documentation.

---

## Overview

Three complementary patterns work together to make these systems reliable under
partial failures:

| Pattern        | Protects Against                       | Implementation            |
|----------------|----------------------------------------|---------------------------|
| Retry          | Transient failures (temporary glitches)| `RetryPolicy.cs`          |
| Idempotency    | Duplicate execution on client retry    | Redis SETNX + DB unique   |
| Circuit Breaker| Cascading failures (service overload)  | `CircuitBreaker.cs`       |

They are applied in this order in the call stack:
```
Client → API → [Circuit Breaker] → [Retry] → Database / External Service
                    ↑                  ↑
           fast-fail if open     retry transient failures
```

---

## Pattern 1: Retry with Exponential Backoff and Jitter

### Configuration

```csharp
RetryPolicy.Default:
  maxAttempts  = 3
  initialDelay = 100ms
  maxDelay     = 2s
  jitter       = 0–100ms random

Retry sequence:
  Attempt 1: execute immediately
  Attempt 2: wait 100ms + jitter (≈100–200ms from attempt 1)
  Attempt 3: wait 200ms + jitter (≈200–300ms from attempt 2)
  Give up:   throw RetryExhaustedException wrapping the last exception
```

### Why Jitter Matters

Without jitter, 100 service instances that all fail at T=0 retry at exactly T=100ms,
T=200ms, T=400ms. Each retry wave hits the recovering service simultaneously —
potentially preventing recovery. With jitter, each instance retries at a slightly
different time, spreading the retry load.

At 100 instances with 100ms jitter, retries arrive across a 100ms window rather
than in a single spike. This reduces peak retry load by 10–50x.

### What to Retry vs What Not To

**RETRY (transient):**
- `NpgsqlException { IsTransient: true }` — connection lost, server restart
- `RedisConnectionException` — Redis momentarily unreachable
- `KafkaException { Error.IsTransient: true }` — broker partition leader election
- `TimeoutException` — operation took too long; service may recover
- `HttpRequestException` — network blip to external service

**DO NOT RETRY:**
- `4xx` responses — client error; retrying with the same request will produce the same 4xx
- `WalletNotFoundException`, `InsufficientFundsException` — business logic rejections
- `NpgsqlException (23505)` — unique constraint violation; retrying would always fail
- `IdempotencyConflictException` — duplicate request; the first result is correct
- `OperationCanceledException` — the client cancelled; don't retry a cancelled operation

### Per-System Application

| System         | What's Retried                           | Policy      |
|----------------|------------------------------------------|-------------|
| Digital Wallet | DB reads (balance, wallet lookup)        | Default     |
| Digital Wallet | Kafka publish after transfer commit      | Default     |
| Ride Sharing   | DB reads (ride state, driver lookup)     | Default     |
| Ride Sharing   | Redis geo-search (transient failures)    | Default     |
| Fraud Detection| PostgreSQL evaluation INSERT             | Aggressive  |
| Fraud Detection| Kafka consumer processing (per message)  | Default     |
| Payment Proc.  | External processor call                  | Default     |
| Payment Proc.  | Kafka settlement consumer                | Aggressive (5 attempts) |
| URL Shortener  | PostgreSQL redirect lookup (cache miss)  | Default     |
| Job Queue      | DB connection on claim                   | Default     |
| Chat           | PostgreSQL message INSERT                | Default     |

---

## Pattern 2: Idempotency

### Design

Every mutating API endpoint that could be retried by a client MUST support
idempotency. The client generates a UUID and includes it as `X-Idempotency-Key`.
The server stores the response against this key and returns it on duplicate requests.

### Two-Layer Implementation

```
Layer 1 (Fast): Redis SETNX
  Key:   idempotency:{userId}:{key}
  Value: serialised response body
  TTL:   24 hours
  Flag:  NX (Set if Not eXists — first writer wins atomically)

Layer 2 (Durable): Database unique constraint
  Column: idempotency_key (or similar) with UNIQUE index
  Catches: concurrent requests that race past Layer 1
           Redis-unavailable scenarios
```

### Workflow

```
Client: POST /wallets/transfer  X-Idempotency-Key: abc-123

Server:
  1. Check Redis: GET idempotency:usr_001:abc-123
     → Cache HIT: return stored response (no DB touch, no execution)
     → Cache MISS: proceed

  2. Check DB: SELECT * FROM transfer_requests WHERE idempotency_key = 'abc-123'
     → Found: return stored result (DB-level dedup)
     → Not found: proceed with execution

  3. Execute transfer (DB transaction)

  4. On success: store response in Redis with NX flag
     SET idempotency:usr_001:abc-123 {response} NX EX 86400
```

### Endpoints Requiring Idempotency Keys (all systems)

| System         | Endpoint                           | Idempotency Layer Used        |
|----------------|------------------------------------|-------------------------------|
| Digital Wallet | `POST /wallets/transfer`          | Redis SETNX + transfer_requests.idempotency_key |
| Digital Wallet | `POST /wallets/{id}/deposit`      | Redis SETNX + transactions.reference_id |
| Payment Proc.  | `POST /payments`                  | Redis SETNX + payments.idempotency_key |
| Payment Proc.  | `POST /payments/{id}/capture`     | Redis SETNX + captures (unique authorisation_id) |
| Payment Proc.  | `POST /payments/{id}/refunds`     | Redis SETNX + refunds.idempotency_key |
| URL Shortener  | `POST /urls`                      | short_code UNIQUE (implicit)   |
| Job Queue      | `POST /jobs`                      | Redis SETNX + job dedup key    |

### Idempotency Key Lifecycle

```
T+0h:   Client submits request with key "abc-123" → stored in Redis + DB
T+1h:   Client retries with same key → Redis returns cached response
T+24h:  Redis TTL expires → DB check required
T+24h+: Client retries (rare) → DB idempotency_key column is permanent → returns historical result
```

---

## Pattern 3: Circuit Breaker

### State Machine

```
         failure_count >= threshold
CLOSED ────────────────────────────────→ OPEN
  ↑                                         │
  │ probe succeeds                          │ cooldown elapses
  │                                         ↓
  └──────────────────────────────── HALF-OPEN
                              probe fails ↗
                              (returns to OPEN)
```

### Configuration Per System

| System          | Protected Resource    | Threshold | Cooldown | Notes                        |
|-----------------|-----------------------|-----------|----------|------------------------------|
| Digital Wallet  | PostgreSQL            | 5         | 30s      | Financial writes — conservative |
| Digital Wallet  | Kafka producer        | 3         | 20s      | Post-commit publishes        |
| Ride Sharing    | PostgreSQL            | 5         | 30s      |                              |
| Ride Sharing    | Redis geo-index       | 5         | 10s      | Redis restarts fast          |
| Fraud Detection | PostgreSQL            | 5         | 30s      |                              |
| Fraud Detection | Kafka consumer        | 3         | 20s      | Consumer-side circuit        |
| Payment Proc.   | PostgreSQL            | 5         | 30s      |                              |
| Payment Proc.   | External processor    | 5         | 60s      | Processors take longer to recover |
| URL Shortener   | PostgreSQL            | 5         | 30s      |                              |
| Job Queue       | PostgreSQL            | 5         | 30s      | Workers retry DB independently |
| Chat            | PostgreSQL            | 5         | 30s      |                              |

### Why Redis-Backed (Not In-Process)

```
Scenario: 3 API instances; PostgreSQL primary has 5-second outage.

In-process circuit breakers:
  Instance 1: sees 5 failures → circuit OPENS
  Instance 2: sees 3 failures → circuit CLOSED (still routing)
  Instance 3: sees 4 failures → circuit CLOSED (still routing)
  Result: 2/3 instances still hammering the recovering PostgreSQL

Redis-backed circuit breaker:
  Instance 1: sees 5 failures → writes "open" to Redis
  Instance 2: sees Redis state "open" → immediately starts fast-failing
  Instance 3: sees Redis state "open" → immediately starts fast-failing
  Result: ALL instances protect the recovering PostgreSQL within milliseconds
```

### Circuit Breaker in the Response

When the circuit is OPEN, the API returns:
```http
HTTP/1.1 503 Service Unavailable
Retry-After: 28
Content-Type: application/json

{
  "error": {
    "code": "SERVICE_TEMPORARILY_UNAVAILABLE",
    "message": "The service is temporarily unavailable. Please retry after 28 seconds."
  }
}
```

The `Retry-After` header tells compliant clients exactly when to retry — eliminating
guesswork and reducing unnecessary retry traffic.

---

## Circuit Breaker Monitoring Endpoint

All systems expose:
```
GET /health/circuit-breakers
```

Response:
```json
[
  { "name": "postgresql", "state": { "status": "closed", "failure_count": 0 } },
  { "name": "kafka",      "state": { "status": "open",   "failure_count": 5,
                                     "opened_at": "2024-01-15T10:30:00Z",
                                     "time_until_half_open": "00:00:22" } },
  { "name": "external",   "state": { "status": "closed", "failure_count": 1 } }
]
```

Returns 503 if any circuit is OPEN — useful for deployment health gates.

---

## Combining All Three Patterns

The patterns work together. Here is the complete flow for a payment authorisation:

```
POST /payments (merchant submits payment)

1. IDEMPOTENCY CHECK (middleware)
   → Redis: GET idempotency:mrc_abc:key-xyz
   → HIT: return cached response immediately
   → MISS: continue

2. CIRCUIT BREAKER CHECK (service layer)
   → external-service circuit state?
   → OPEN: throw CircuitOpenException → 503 returned to merchant
   → CLOSED/HALF-OPEN: continue

3. RETRY POLICY (service layer wrapping external call)
   → Call external processor with 5s timeout
   → TimeoutException: retry up to 3 times with backoff
   → All retries fail:
     - Record failure in circuit breaker
     - If circuit trips to OPEN: log at ERROR
     - Throw RetryExhaustedException → 503

4. ON SUCCESS
   → Record circuit success (reset if HALF-OPEN → CLOSED)
   → Write payment record to PostgreSQL (circuit-breaker protected)
   → Cache idempotency result in Redis (NX)
   → Return 201 Created

5. KAFKA PUBLISH (post-commit, fire-and-forget)
   → Circuit breaker on Kafka
   → Retry on transient failure
   → On permanent failure: log at ERROR, merchant still gets 201
```
