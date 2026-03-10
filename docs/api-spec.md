# API Specification — URL Shortener Service

---

## Overview

The URL Shortener API provides URL creation, management, redirect resolution, and click
analytics. It is consumed by web clients, mobile apps, and internal marketing tools.
The redirect endpoint (`GET /{code}`) is the highest-volume path and is architecturally
separated from management endpoints. Write operations produce short codes immediately.
Analytics are recorded asynchronously and should not be treated as real-time.

---

## Base URL and Versioning

```
https://api.short.internal/api/v1     (management)
https://go.short.internal/{code}       (redirect — separate subdomain)
```

---

## Authentication

Management endpoints require Bearer token:

```
Authorization: Bearer <jwt_token>
```

The redirect endpoint (`GET /{code}`) is **public** — no authentication required.
Anonymous stats aggregation is supported for public link analytics.

---

## Common Response Envelope

### Success
```json
{
  "data": { ... },
  "meta": { "request_id": "uuid", "timestamp": "2024-01-15T10:30:00Z" }
}
```

### Error
```json
{
  "error": {
    "code": "SHORT_CODE_NOT_FOUND",
    "message": "No active URL found for the given short code.",
    "details": []
  },
  "meta": { "request_id": "uuid", "timestamp": "2024-01-15T10:30:00Z" }
}
```

---

## Rate Limiting

| Endpoint       | Limit                | Scope     |
|---------------|----------------------|-----------|
| `POST /urls`  | 100 / hour           | Per user  |
| `GET /{code}` | No limit (public)    | —         |
| All others    | 120 / minute         | Per user  |

---

## Endpoints

---

### POST /urls

**Description:** Creates a new short URL. Returns the short code and full short URL.
Optionally accepts a custom alias and expiry date.

**Request Headers:** `X-Idempotency-Key: <uuid-v4>` *(recommended)*

**Request Body:**

| Field          | Type   | Required | Validation                                    | Example                    |
|----------------|--------|----------|-----------------------------------------------|----------------------------|
| `original_url` | string | Yes      | Valid URL, max 2048 chars, HTTPS preferred     | `"https://example.com/very/long/path"` |
| `alias`        | string | No       | 4–32 chars, a-z A-Z 0-9 hyphens, not reserved | `"my-campaign"`            |
| `expires_at`   | string | No       | ISO8601, must be in the future                 | `"2024-06-01T00:00:00Z"`   |
| `title`        | string | No       | max 128 chars, human label                     | `"Summer Campaign"`        |

**Example Request:**
```json
{
  "original_url": "https://example.com/products/summer-sale-2024?utm_source=email",
  "alias": "summer24",
  "expires_at": "2024-09-01T00:00:00Z",
  "title": "Summer Sale Campaign"
}
```

**Response — 201 Created:**
```json
{
  "data": {
    "id": "url_01j9z3k4m5n6p7q8",
    "short_code": "summer24",
    "short_url": "https://go.short.internal/summer24",
    "original_url": "https://example.com/products/summer-sale-2024?utm_source=email",
    "title": "Summer Sale Campaign",
    "expires_at": "2024-09-01T00:00:00Z",
    "is_active": true,
    "created_at": "2024-01-15T10:30:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                                     |
|------|-----------------------------------------------|
| 201  | Short URL created                             |
| 400  | Invalid URL format or missing field           |
| 401  | Unauthorized                                  |
| 409  | Custom alias already taken                    |
| 422  | Alias is reserved, expiry is in the past      |
| 429  | Rate limit exceeded                           |

---

### GET /{code}

**Description:** Resolves a short code and returns a redirect to the original URL. This
endpoint is public and lives on the redirect subdomain. Click is recorded asynchronously.
Expired or inactive codes return non-redirect status codes.

**Path Parameters:** `code` — Short code or custom alias

**Response — 301 Moved Permanently:**
```
HTTP/1.1 301 Moved Permanently
Location: https://example.com/products/summer-sale-2024?utm_source=email
Cache-Control: public, max-age=600
```

**Status Codes:**

| Code | Condition                             |
|------|---------------------------------------|
| 301  | Active URL found — redirect           |
| 404  | Short code does not exist             |
| 410  | URL is expired or deactivated         |

---

### GET /urls/{code}/stats

**Description:** Returns click analytics for a short URL owned by the authenticated user.

**Path Parameters:** `code` — Short code or custom alias

**Query Parameters:**

| Parameter    | Type   | Default    | Description                       |
|--------------|--------|------------|-----------------------------------|
| `from`       | string | 30 days ago| ISO8601 start date                |
| `to`         | string | now        | ISO8601 end date                  |
| `granularity`| string | `day`      | `hour`, `day`, `week`             |

**Response — 200 OK:**
```json
{
  "data": {
    "short_code": "summer24",
    "total_clicks": 14872,
    "unique_clicks": 12043,
    "clicks_by_period": [
      { "period": "2024-01-14", "clicks": 3241 },
      { "period": "2024-01-15", "clicks": 4122 }
    ],
    "top_referrers": [
      { "referrer": "https://gmail.com", "clicks": 5200 },
      { "referrer": "direct", "clicks": 3100 }
    ]
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                              |
|------|----------------------------------------|
| 200  | Success                                |
| 401  | Unauthorized                           |
| 403  | URL belongs to a different user        |
| 404  | Short code not found                   |

---

### DELETE /urls/{code}

**Description:** Deactivates a short URL. After deactivation, the redirect endpoint
returns 410 Gone. The URL record and analytics are retained.

**Path Parameters:** `code` — Short code or custom alias

**Response — 200 OK:**
```json
{
  "data": {
    "short_code": "summer24",
    "is_active": false,
    "deactivated_at": "2024-01-15T12:00:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                              |
|------|----------------------------------------|
| 200  | URL deactivated                        |
| 401  | Unauthorized                           |
| 403  | URL belongs to a different user        |
| 404  | Short code not found                   |
| 422  | URL already inactive                   |

---

### PATCH /urls/{code}

**Description:** Updates mutable fields on an existing short URL: title, expiry date, and
active status. Short code and original URL cannot be changed after creation.

**Path Parameters:** `code` — Short code or custom alias

**Request Body (all fields optional):**

| Field        | Type    | Validation                  | Example                    |
|--------------|---------|-----------------------------|----------------------------|
| `title`      | string  | max 128 chars               | `"Updated Campaign Title"` |
| `expires_at` | string  | ISO8601, future or null     | `"2024-12-31T00:00:00Z"`   |
| `is_active`  | boolean | —                           | `true`                     |

**Response — 200 OK:**
```json
{
  "data": {
    "id": "url_01j9z3k4m5n6p7q8",
    "short_code": "summer24",
    "title": "Updated Campaign Title",
    "expires_at": "2024-12-31T00:00:00Z",
    "is_active": true,
    "updated_at": "2024-01-15T12:00:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                              |
|------|----------------------------------------|
| 200  | URL updated                            |
| 400  | Invalid field values                   |
| 401  | Unauthorized                           |
| 403  | URL belongs to a different user        |
| 404  | Short code not found                   |
