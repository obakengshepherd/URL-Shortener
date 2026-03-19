using UrlShortener.Infrastructure.Cache;
using UrlShortener.Infrastructure.Messaging;
using StackExchange.Redis;

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