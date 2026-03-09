# Architecture — URL Shortener Service

---

## Overview

The URL Shortener is one of the most read-skewed systems in this lab: a single URL created
once may be redirected millions of times. This fundamental asymmetry drives all architectural
decisions. The write path is conventional and can be slow relative to the read path. The read
path — `GET /{code}` — must be as fast as physically possible, which means the database must
never be on the critical path for the vast majority of redirect requests. Redis cache absorbs
nearly all redirect lookups. Click analytics are recorded asynchronously, completely off the
redirect path. The result is a system that can sustain hundreds of millions of redirects per
day on modest infrastructure.

---

## Architecture Diagram

```
┌──────────────────────────────────────────────────────────────┐
│                    Clients (Browsers / Apps)                 │
└──────────────────────────┬───────────────────────────────────┘
                           │ HTTPS
┌──────────────────────────▼───────────────────────────────────┐
│                     Load Balancer                            │
│          (Round-Robin, TLS Termination)                      │
└──────────────────────────┬───────────────────────────────────┘
                           │
          ┌────────────────┴────────────────┐
          │                                 │
┌─────────▼────────────┐      ┌────────────▼────────────────┐
│    Write API          │      │    Redirect Service          │
│  POST /urls           │      │    GET /{code}               │
│  DELETE, PATCH /urls  │      │    (read-optimised path)     │
└─────────┬────────────┘      └────────────┬────────────────┘
          │                                │
┌─────────▼─────────┐         ┌────────────▼────────────────┐
│   UrlService       │         │         Redis                │
│   (encode, store)  │◄───────►│   (short_code → URL cache)  │
└─────────┬─────────┘         └────────────┬────────────────┘
          │                                │ (on miss)
┌─────────▼──────────────────────────────▼─────────────────┐
│                       PostgreSQL                           │
│          (urls · url_clicks · custom_aliases)             │
└──────────────────────────────┬────────────────────────────┘
                               │
                   ┌───────────▼──────────────┐
                   │  RabbitMQ: click.events   │
                   │  (async analytics queue)  │
                   └───────────┬──────────────┘
                               │
                   ┌───────────▼──────────────┐
                   │    Analytics Consumer     │
                   │  (writes url_clicks rows) │
                   └──────────────────────────┘
```

---

## Layer-by-Layer Description

### Load Balancer

The load balancer handles two fundamentally different traffic patterns on the same domain:
write operations (`POST /urls`, management endpoints) and redirect lookups (`GET /{code}`).
Both are served by stateless services and use round-robin distribution. TLS is terminated
at the load balancer. Health checks monitor both the Write API and the Redirect Service
independently, allowing one to be removed from rotation without affecting the other.

### Write API

The Write API handles URL creation, deactivation, and updates. It is not on any performance-
critical path — a URL creation that takes 100ms is perfectly acceptable. The Write API:

1. Validates the submitted URL (format, length, blocked domain list).
2. Generates a short code via the encoding service (see UrlService).
3. Checks for custom alias conflicts atomically (using a PostgreSQL unique index).
4. Writes the URL record to PostgreSQL.
5. Performs a write-through cache population: immediately writes the new mapping to Redis
   so the Redirect Service can serve it without a database read on first access.

### Redirect Service

The Redirect Service handles `GET /{code}` requests. It is the highest-throughput component
and is optimised accordingly. Its sole job is to resolve a short code to a URL and return
a redirect response. For every request:

1. Look up `url:{code}` in Redis. If found and not expired, return 301 immediately.
2. On cache miss: query PostgreSQL for the URL. If found, write to Redis with a 10-minute TTL,
   return 301. If not found, return 404.
3. After returning the response (not before, not synchronously with): publish a click event
   to RabbitMQ for async analytics recording. The publish is fire-and-forget — a publish
   failure is logged but does not affect the redirect response.

Expired URLs are a special case: the Redis cache must not serve an expired URL after its
`expires_at`. The solution is that the cache TTL is set to `min(10 minutes, time_until_expiry)`.
After the URL expires, the cache key also expires, and the Redirect Service falls through to
the database, which returns the record with an `is_active` check that returns 410 Gone.

### URL Service — Short Code Generation

Short codes are generated using base62 encoding (characters: a-z, A-Z, 0-9), producing
8-character codes with ~218 trillion unique combinations. Generation follows this logic:

1. Generate a random 8-character base62 string.
2. Attempt to insert into the `urls` table. The `short_code` column has a unique index.
3. On unique constraint violation (collision): regenerate and retry (up to 3 times).
4. At scale (>1B URLs), switch to a pre-generated code pool: a background job generates
   codes in batches of 100,000 and stores them in a `code_pool` table. The write path
   claims one atomically using `SELECT FOR UPDATE SKIP LOCKED`, eliminating real-time
   collision resolution.

### Cache — Redis

Redis stores the `url:{code}` → `{original_url, expires_at, is_active}` mapping. Cache
writes happen in two places: on URL creation (write-through) and on redirect cache miss
(cache-aside). The TTL is 10 minutes for standard URLs and `time_until_expiry` for URLs
with an expiry date.

There is no cache for write operations. The PostgreSQL unique index is the collision
check authority. Redis stores only the data the Redirect Service needs to serve a response —
nothing more.

### Analytics Pipeline — RabbitMQ

Click events are published to a RabbitMQ queue after each redirect. The Analytics Consumer
processes these events and writes `url_clicks` records to PostgreSQL. This is entirely
asynchronous: the consumer can be scaled independently, can fall behind under spike load
(click events buffer in RabbitMQ), and a consumer failure does not affect redirect throughput.

Click analytics accuracy is best-effort: if the RabbitMQ publish fails on a given redirect,
that click is not recorded. This is an explicit tradeoff — redirect latency is never
sacrificed for click count accuracy.

### Database — PostgreSQL

PostgreSQL stores URL records, click analytics, and custom alias mappings. The `urls` table
has a unique index on `short_code`. The `url_clicks` table is high-write and should be
partitioned by day or week for efficient range queries in analytics. The `custom_aliases`
table has a unique index on `alias` to enforce conflict-free alias creation.

---

## Component Responsibilities Summary

| Component           | Responsibility                                          | Communicates Via        |
|---------------------|---------------------------------------------------------|-------------------------|
| Load Balancer       | TLS termination, routing, health checks                 | HTTPS                   |
| Write API           | URL creation, deactivation, alias management            | HTTP (internal)         |
| Redirect Service    | Short code resolution, 301 response, click fire-and-forget | HTTP (external) + Redis |
| UrlService          | Code generation, collision handling, write-through cache| PostgreSQL + Redis      |
| Redis               | Short code → URL cache (read acceleration)              | In-memory               |
| PostgreSQL          | URL records, analytics, aliases (source of truth)       | TCP                     |
| RabbitMQ            | Click event queue for async analytics                   | AMQP protocol           |
| Analytics Consumer  | Consume click events, write url_clicks records          | RabbitMQ + PostgreSQL   |
