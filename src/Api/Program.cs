using UrlShortener.Infrastructure.Cache;
using UrlShortener.Infrastructure.Messaging;
using UrlShortener.Infrastructure.RateLimit;
using UrlShortener.Api.Models.Requests;
using UrlShortener.Api.Models.Responses;
using UrlShortener.Api.Authentication;
using UrlShortener.Application.Interfaces;
using UrlShortener.Application.Services;
using UrlShortener.Infrastructure.Persistence;
using Shared.Infrastructure.RateLimit;
using Shared.Infrastructure.HealthChecks;
using Shared.Api.Controllers;
using StackExchange.Redis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.OpenApi.Models;
using FluentValidation.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// ════════════════════════════════════════════════════════════════════════════
// CONFIGURATION & LOGGING
// ════════════════════════════════════════════════════════════════════════════

var config = builder.Configuration;
builder.Logging.ClearProviders()
    .AddConsole()
    .AddDebug();

// ════════════════════════════════════════════════════════════════════════════
// REDIS CACHE — Required by rate limiter and cache services
// ════════════════════════════════════════════════════════════════════════════

builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var redisUrl = config.GetConnectionString("Redis") ?? "localhost:6379";
    try
    {
        var options = ConfigurationOptions.Parse(redisUrl);
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 5000;
        return ConnectionMultiplexer.Connect(options);
    }
    catch (Exception ex)
    {
        sp.GetRequiredService<ILogger<Program>>().LogError(ex, "Redis connection failed");
        throw;
    }
});

// ════════════════════════════════════════════════════════════════════════════
// CACHE SERVICES — Phase 5 with stampede protection
// ════════════════════════════════════════════════════════════════════════════

builder.Services.AddSingleton<UrlCacheService>();        // Phase 4 base
builder.Services.AddSingleton<UrlCacheServiceV2>();      // Phase 5 with stampede protection

// ════════════════════════════════════════════════════════════════════════════
// MESSAGING — RabbitMQ click publisher & consumer
// ════════════════════════════════════════════════════════════════════════════

builder.Services.AddSingleton<ClickEventPublisher>();
builder.Services.AddSingleton<ClickAnalyticsConsumer>();
builder.Services.AddHostedService<ClickAnalyticsConsumerWorker>();

// ════════════════════════════════════════════════════════════════════════════
// DATA ACCESS LAYER — Repository & Services
// ════════════════════════════════════════════════════════════════════════════

builder.Services.AddScoped<UrlRepository>();
builder.Services.AddScoped<IUrlService, UrlService>();
builder.Services.AddScoped<IRedirectService, RedirectService>();
builder.Services.AddScoped<IAnalyticsService, AnalyticsService>();

// ════════════════════════════════════════════════════════════════════════════
// VALIDATION — FluentValidation for requests
// ════════════════════════════════════════════════════════════════════════════

builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddFluentValidationClientsideAdapters();

// ════════════════════════════════════════════════════════════════════════════
// AUTHENTICATION & AUTHORIZATION
// ════════════════════════════════════════════════════════════════════════════

var jwtOptions = config.GetSection("JwtOptions");
var disableAuth = jwtOptions.GetValue<bool>("DisableAuthentication");

if (!disableAuth && jwtOptions.GetValue<string>("Authority") is not (null or ""))
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = jwtOptions.GetValue<string>("Authority")!;
            options.Audience = jwtOptions.GetValue<string>("Audience");
            options.TokenValidationParameters.ValidateIssuerSigningKey 
                = jwtOptions.GetValue<bool>("ValidateIssuerSigningKey");
            options.TokenValidationParameters.ValidateIssuer 
                = jwtOptions.GetValue<bool>("ValidateIssuer");
            options.TokenValidationParameters.ValidateAudience 
                = jwtOptions.GetValue<bool>("ValidateAudience");
        });
}
else
{
    builder.Services.AddAuthentication("Development")
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>("Development", _ => { });
}

builder.Services.AddAuthorization();

// ════════════════════════════════════════════════════════════════════════════
// RATE LIMITING — Distributed via Redis
// ════════════════════════════════════════════════════════════════════════════

builder.Services.AddSingleton<IEnumerable<RateLimitRule>>(
    _ => UrlShortener.Infrastructure.RateLimit.RateLimitPolicies.UrlPolicies());

// ════════════════════════════════════════════════════════════════════════════
// HEALTH CHECKS — PostgreSQL, Redis, RabbitMQ
// ════════════════════════════════════════════════════════════════════════════

builder.Services.AddHealthChecks()
    .AddCheck<PostgreSqlHealthCheck>("postgresql", 
        failureStatus: HealthStatus.Unhealthy, tags: ["database"], timeout: TimeSpan.FromSeconds(5))
    .AddCheck<RedisHealthCheck>("redis", 
        failureStatus: HealthStatus.Degraded, tags: ["cache"], timeout: TimeSpan.FromSeconds(5))
    .AddCheck<RabbitMqHealthCheck>("rabbitmq", 
        failureStatus: HealthStatus.Degraded, tags: ["messaging"], timeout: TimeSpan.FromSeconds(5));

builder.Services.AddScoped(sp => 
    new PostgreSqlHealthCheck(config.GetConnectionString("PostgreSQL")!));
builder.Services.AddScoped(sp => 
    new RedisHealthCheck(sp.GetRequiredService<IConnectionMultiplexer>()));
builder.Services.AddScoped(sp => 
    new RabbitMqHealthCheck(config.GetConnectionString("RabbitMQ") 
        ?? "amqp://devuser:devpass@localhost:5672/"));

// ════════════════════════════════════════════════════════════════════════════
// API DOCUMENTATION — Swagger/OpenAPI
// ════════════════════════════════════════════════════════════════════════════

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title   = "URL Shortener API",
        Version = "v1",
        Description = "High-throughput URL shortening service with Redis caching and async analytics.",
        TermsOfService = new Uri("https://example.com/terms"),
        Contact = new OpenApiContact { Name = "API Support", Url = new Uri("https://example.com/support") },
        License = new OpenApiLicense { Name = "MIT", Url = new Uri("https://opensource.org/licenses/MIT") }
    });

    if (!disableAuth)
    {
        options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.ApiKey,
            Scheme = "Bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "JWT Authorization header using the Bearer scheme."
        });

        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
                },
                Array.Empty<string>()
            }
        });
    }

    var xmlFile = Path.Combine(AppContext.BaseDirectory, "UrlShortener.Api.xml");
    if (File.Exists(xmlFile)) options.IncludeXmlComments(xmlFile);
});

builder.Services.AddControllers();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

// ════════════════════════════════════════════════════════════════════════════
// BUILD APPLICATION
// ════════════════════════════════════════════════════════════════════════════

var app = builder.Build();

// ════════════════════════════════════════════════════════════════════════════
// MIDDLEWARE PIPELINE
// ════════════════════════════════════════════════════════════════════════════

// Development-only features
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "URL Shortener API v1");
        options.RoutePrefix = "swagger";
    });
}

app.UseHttpsRedirection();
app.UseCors("AllowAll");

// Structured logging
app.Use(async (context, next) =>
{
    context.TraceIdentifier = Guid.NewGuid().ToString("N");
    await next();
});

app.UseRouting();

// Authentication & Authorization
app.UseAuthentication();
app.UseAuthorization();

// Rate limiting (distributed via Redis)
var rateLimitRules = app.Services.GetRequiredService<IEnumerable<RateLimitRule>>();
if (config.GetValue<bool>("RateLimiting:Enabled", true))
{
    app.UseMiddleware<RedisRateLimitMiddleware>();
}

// ════════════════════════════════════════════════════════════════════════════
// ENDPOINT MAPPING
// ════════════════════════════════════════════════════════════════════════════

app.MapControllers();
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/detail", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = HealthCheckEndpoint.WriteResponse
});

// ════════════════════════════════════════════════════════════════════════════
// RUN APPLICATION
// ════════════════════════════════════════════════════════════════════════════

var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Starting URL Shortener API...");
logger.LogInformation("Connected to PostgreSQL: {PostgreSQL}", !string.IsNullOrEmpty(config.GetConnectionString("PostgreSQL")));
logger.LogInformation("Connected to Redis: {Redis}", !string.IsNullOrEmpty(config.GetConnectionString("Redis")));
logger.LogInformation("Connected to RabbitMQ: {RabbitMQ}", !string.IsNullOrEmpty(config.GetConnectionString("RabbitMQ")));

app.Run();
