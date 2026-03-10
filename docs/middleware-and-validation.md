# Middleware Chain & Validation Rules

> This document applies to all 7 systems. Each system's `Program.cs` registers this
> middleware pipeline in the order listed below.

---

## Middleware Pipeline Order

```
Request IN
    │
    ▼
1. GlobalExceptionHandler     (catches all unhandled exceptions)
    │
    ▼
2. RequestLoggingMiddleware   (structured JSON log: method, path, status, duration_ms)
    │
    ▼
3. Authentication Middleware  (JWT Bearer validation, claims attachment)
    │
    ▼
4. Authorization Middleware   (role and policy enforcement)
    │
    ▼
5. RateLimiter                (token bucket per API key / user)
    │
    ▼
6. IdempotencyMiddleware      (POST write endpoints only)
    │
    ▼
7. Controller / Action        (validation → service → response)

Response OUT
```

---

## 1. Global Exception Handler

**Registration:** `app.UseExceptionHandler()` + `IExceptionHandler` implementation

**Behaviour:**
- Catches every unhandled exception from the middleware pipeline and controller layer
- Maps domain exceptions to HTTP status codes and standard error envelopes
- Always includes a `request_id` in the response for traceability
- Never exposes stack traces or internal exception messages in production
- Logs the full exception with stack trace at `Error` level

**Domain exception → HTTP status code mapping:**

| Exception                      | HTTP Status | Error Code                  |
|--------------------------------|-------------|-----------------------------|
| `NotFoundException`            | 404         | `{ENTITY}_NOT_FOUND`        |
| `AccessDeniedException`        | 403         | `ACCESS_DENIED`             |
| `ConflictException`            | 409         | `CONFLICT`                  |
| `ValidationException`          | 400         | `VALIDATION_ERROR`          |
| `BusinessRuleException`        | 422         | Domain-specific code        |
| `IdempotencyConflictException` | 409         | `IDEMPOTENCY_CONFLICT`      |
| All others                     | 500         | `INTERNAL_SERVER_ERROR`     |

---

## 2. Request Logging Middleware

**Registration:** `app.UseMiddleware<RequestLoggingMiddleware>()`

**Structured log fields (JSON):**
```json
{
  "level": "Information",
  "method": "POST",
  "path": "/api/v1/wallets/transfer",
  "status_code": 201,
  "duration_ms": 47.3,
  "request_id": "abc-123-def",
  "user_id": "usr_abc123",
  "timestamp": "2024-01-15T10:30:00.047Z"
}
```

**Rules:**
- Request bodies are NOT logged (contains PII and financial data)
- Response bodies are NOT logged
- Authorization headers are NOT logged
- `duration_ms` is measured from middleware entry to response flush

---

## 3. Authentication Middleware

**Registration:** `app.UseAuthentication()` + `AddJwtBearer()`

**Behaviour:**
- Validates JWT Bearer token: signature, issuer, audience, expiry
- On valid token: attaches `ClaimsPrincipal` to `HttpContext.User`
- On missing token: sets `User` to unauthenticated (401 returned by authorization)
- On invalid/expired token: returns 401 Unauthorized immediately

**Claims extracted:**
- `sub` / `NameIdentifier` → user ID
- `role` → user role (rider, driver, analyst, admin, service, worker)

---

## 4. Rate Limiting Middleware

**Registration:** `app.UseRateLimiter()`

**Algorithm:** Fixed window with per-user key (authenticated user ID or API key)

**Response headers on every request:**
```
X-RateLimit-Limit: 100
X-RateLimit-Remaining: 87
X-RateLimit-Reset: 1705312260
```

**When limit is exceeded:**
```
HTTP 429 Too Many Requests
Retry-After: 37
Content-Type: application/json

{
  "error": {
    "code": "RATE_LIMIT_EXCEEDED",
    "message": "Too many requests. Retry after 37 seconds."
  }
}
```

**Limits by system and endpoint type:**

| System           | Standard endpoints  | Write endpoints            |
|------------------|---------------------|----------------------------|
| Digital Wallet   | 100/min per user    | 10/min per wallet (transfer)|
| Ride Sharing     | 60/min per user     | 30/min per driver (location)|
| Chat             | 120/min per user    | 60/min per user (messages) |
| Fraud Detection  | 60/min per user     | 500/min per service        |
| Payment Processing| 60/min per API key | 200/min per API key        |
| URL Shortener    | 120/min per user    | 100/hour per user (create) |
| Job Queue        | 120/min per user    | 1000/min per service       |

---

## 5. Idempotency Middleware

**Registration:** `app.UseMiddleware<IdempotencyMiddleware>()`

**Applies to:** POST endpoints on write operations (transfer, deposit, payment, message send)

**Header required:**
```
X-Idempotency-Key: <uuid-v4>
```

**Behaviour flow:**
```
Receive POST request
    │
    ├─► X-Idempotency-Key missing?
    │       └─► 400 Bad Request: IDEMPOTENCY_KEY_REQUIRED
    │
    ├─► Key is not a valid UUID v4?
    │       └─► 400 Bad Request: INVALID_IDEMPOTENCY_KEY
    │
    ├─► Key found in Redis cache?
    │       └─► Return cached response (status code + body)
    │           Add header: X-Idempotency-Replayed: true
    │
    └─► Key not found → proceed to controller
            After response: cache {status_code, body} in Redis EX 86400
```

**Cache TTL:** 24 hours

**Storage:** Redis key `idempotency:{user_id}:{key}` — scoped per user to prevent
cross-user idempotency key collisions.

---

## 6. Controller Validation Rules

All controllers use `[ApiController]` which automatically returns 400 on model binding
failures. Additional validation rules enforced per endpoint:

### Amount fields (wallet, payment, refund)
- Must be a decimal value representable as a string or numeric
- Must be greater than zero (`> 0`)
- Must have at most 2 decimal places
- Must not exceed system maximum (9,999,999.99 for wallet; unlimited for payment)

### UUID fields (IDs, idempotency keys)
- Must parse as a valid GUID
- Must not be the zero GUID (`00000000-0000-0000-0000-000000000000`)

### Coordinate fields (ride sharing)
- Latitude: `-90.0` to `90.0`
- Longitude: `-180.0` to `180.0`
- NaN and Infinity values are rejected

### String fields
- All string fields have explicit `StringLength` constraints (see request models)
- Empty strings are rejected for required fields
- HTML and script injection characters do not require escaping (API is JSON-only,
  no HTML rendering) but input is stored verbatim

### Business rule validation (service layer, not controller)
- `source_wallet_id != destination_wallet_id` (transfer)
- `capture_amount <= authorised_amount` (payment)
- `refund_amount <= remaining_capturable` (payment)
- `member_ids.length >= 2 && <= 500` (chat conversation)
- `job.payload size <= 64KB` (job queue)

**Field-level error response format (400):**
```json
{
  "error": {
    "code": "VALIDATION_ERROR",
    "message": "One or more fields failed validation.",
    "details": [
      { "field": "amount", "issue": "Must be greater than zero." },
      { "field": "currency", "issue": "Must be a 3-letter ISO 4217 code." }
    ]
  }
}
```
