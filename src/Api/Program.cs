using UrlShortener.Infrastructure.Cache;
using UrlShortener.Infrastructure.Messaging;
using StackExchange.Redis;
using Shared.Infrastructure.RateLimit;
using Shared.Api.Controllers;
using Microsoft.Extensions.Diagnostics.HealthChecks;

builder.Services.AddSingleton<IEnumerable<RateLimitRule>>(
    _ => RateLimitPolicies.UrlPolicies());

builder.Services.AddHealthChecks()
    .AddCheck<RedisHealthCheck>("redis",       failureStatus: HealthStatus.Degraded,  tags: ["cache"])
    .AddCheck<RabbitMqHealthCheck>("rabbitmq", failureStatus: HealthStatus.Degraded,  tags: ["messaging"])
    .AddCheck<PostgreSqlHealthCheck>("postgresql", failureStatus: HealthStatus.Unhealthy, tags: ["database"]);

builder.Services.AddTransient<RedisHealthCheck>();
builder.Services.AddTransient(_ => new PostgreSqlHealthCheck(builder.Configuration.GetConnectionString("PostgreSQL")!));
builder.Services.AddTransient(_ => new RabbitMqHealthCheck(builder.Configuration.GetConnectionString("RabbitMQ") ?? "amqp://devuser:devpass@localhost:5672/"));

// ── Middleware pipeline ──
// app.UseAuthentication();
// app.UseAuthorization();
// app.UseMiddleware<RedisRateLimitMiddleware>();
// app.MapControllers();
// app.MapHealthEndpoints();

// Redis
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379"));

// Use Phase 5 extended cache with stampede protection
builder.Services.AddSingleton<UrlCacheService>();         // Phase 4 base
builder.Services.AddSingleton<UrlCacheServiceV2>();       // Phase 5 with stampede protection

// RabbitMQ click publisher (Phase 4) + analytics consumer (Phase 5)
builder.Services.AddSingleton<ClickEventPublisher>();
builder.Services.AddSingleton<ClickAnalyticsConsumer>();
builder.Services.AddHostedService<ClickAnalyticsConsumerWorker>();

// Repositories and services
builder.Services.AddScoped<UrlRepository>();
builder.Services.AddScoped<IUrlService, UrlService>();
builder.Services.AddScoped<IRedirectService, RedirectService>();
builder.Services.AddScoped<IAnalyticsService, AnalyticsService>();

// Health checks
builder.Services.AddHealthChecks()
    .AddNpgsql(builder.Configuration.GetConnectionString("PostgreSQL")!)
    .AddRedis(builder.Configuration.GetConnectionString("Redis")!);