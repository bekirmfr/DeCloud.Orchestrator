using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using Orchestrator.Background;
using Orchestrator.Services;
using Orchestrator.Extensions;
using Orchestrator.Hubs;
using Orchestrator.Infrastructure;
using Orchestrator.Interfaces.Blockchain;
using Orchestrator.Middleware;
using Orchestrator.Models;
using Orchestrator.Persistence;
using Orchestrator.Services.VmScheduling;
using Serilog;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/orchestrator-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

// Use Serilog
builder.Host.UseSerilog();

// =====================================================
// MongoDB Configuration
// =====================================================
var mongoConnectionString = builder.Configuration.GetConnectionString("MongoDB");
IMongoDatabase? mongoDatabase = null;

if (!string.IsNullOrEmpty(mongoConnectionString))
{
    try
    {
        builder.Services.AddSingleton<IMongoClient>(sp =>
        {
            var settings = MongoClientSettings.FromConnectionString(mongoConnectionString);
            settings.ServerApi = new ServerApi(ServerApiVersion.V1);
            settings.ConnectTimeout = TimeSpan.FromSeconds(10);
            settings.SocketTimeout = TimeSpan.FromSeconds(10);

            return new MongoClient(settings);
        });

        builder.Services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<IMongoClient>();
            var databaseName = MongoUrl.Create(mongoConnectionString).DatabaseName ?? "decloud";
            return client.GetDatabase(databaseName);
        });

        // Test connection during startup
        var tempClient = new MongoClient(mongoConnectionString);
        var databaseName = MongoUrl.Create(mongoConnectionString).DatabaseName ?? "decloud";
        mongoDatabase = tempClient.GetDatabase(databaseName);

        // Ping to verify connection
        mongoDatabase.RunCommand<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1));

        Log.Information("✓ MongoDB connected successfully: {Database}", databaseName);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "MongoDB connection failed - falling back to in-memory storage");
        mongoDatabase = null;
    }
}
else
{
    Log.Warning("⚠ MongoDB not configured - using in-memory storage (data will not persist!)");
}

// =====================================================
// Core Services
// =====================================================
builder.Services.AddSingleton(sp =>
{
    var database = sp.GetService<IMongoDatabase>();
    var logger = sp.GetRequiredService<ILogger<DataStore>>();
    return new DataStore(database, logger);
});
builder.Services.AddSingleton<ISchedulingConfigService, SchedulingConfigService>();
builder.Services.AddScoped<NodePerformanceEvaluator>();
builder.Services.AddScoped<NodeCapacityCalculator>();
builder.Services.AddSingleton<IVmSchedulingService, VmSchedulingService>();
builder.Services.AddSingleton<IRelayNodeService, RelayNodeService>();
builder.Services.AddSingleton<IWireGuardManager, WireGuardManager>();
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<INodeService, NodeService>();
builder.Services.AddSingleton<IVmService, VmService>();
// Command delivery service (hybrid push-pull)
builder.Services.AddSingleton<INodeCommandService, NodeCommandService>();

// HttpClient for push commands
builder.Services.AddHttpClient<NodeCommandService>()
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(5);  // Fast timeout for push attempts
    });
// UserService needs IWebHostEnvironment for dev mode signature validation
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddSingleton<IEventService, EventService>();
builder.Services.AddHttpClient<ITerminalService, TerminalService>();
builder.Services.AddSingleton<IWalletSshKeyService, WalletSshKeyService>();
builder.Services.AddSingleton<ISshCertificateService, SshCertificateService>();
// Node Marketplace Service (for node discovery and search)
builder.Services.AddSingleton<INodeMarketplaceService, NodeMarketplaceService>();
// Node Reputation Service (for uptime tracking and VM metrics)
builder.Services.AddSingleton<INodeReputationService, NodeReputationService>();
// Central Ingress Gateway (optional - for *.vms.decloud.io routing)
builder.Services.Configure<CentralIngressOptions>(builder.Configuration.GetSection("CentralIngress"));
builder.Services.AddHttpClient<ICentralCaddyManager, CentralCaddyManager>();
builder.Services.AddSingleton<ICentralIngressService, CentralIngressService>();
builder.Services.AddSingleton<IBlockchainService, BlockchainService>();
builder.Services.AddHttpClient("SubdomainProxy")
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(60);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    });
builder.Services.AddSingleton<INetworkLatencyTracker, NetworkLatencyTracker>();
// Configure HTTP client for latency tracker
builder.Services.AddHttpClient("latency-tracker")
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(5);
    });
// Add attestation services
builder.Services.AddAttestationServices(builder.Configuration);
builder.Services.AddPaymentServices(builder.Configuration);
// Configure JSON serialization for all HttpClient JSON extension methods
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// =====================================================
// Background Services
// =====================================================
builder.Services.AddHostedService<NodeHealthMonitorService>();
builder.Services.AddHostedService<RelayHealthMonitor>();
builder.Services.AddHostedService<VmSchedulerService>();
builder.Services.AddHostedService<CleanupService>();
builder.Services.AddHostedService<StatePruningService>();
builder.Services.AddHostedService<NodeReputationMaintenanceService>();

// Add MongoDB sync service if MongoDB is configured
if (mongoDatabase != null)
{
    builder.Services.AddHostedService<MongoDBSyncService>();
}

// =====================================================
// API & Authentication
// =====================================================
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Use camelCase for all JSON properties
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true; // Accept any casing
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "DeCloud Orchestrator API",
        Version = "v1",
        Description = "Decentralized cloud computing orchestrator"
    });

    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme",
        Name = "Authorization",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// JWT Authentication
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? "default-dev-key-change-in-production-min-32-chars!";
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "orchestrator";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "orchestrator-client";

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtIssuer,
        ValidAudience = jwtAudience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
    };

    // Allow SignalR to use token from query string
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;

            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hub"))
            {
                context.Token = accessToken;
            }

            return Task.CompletedTask;
        }
    };
})
.AddApiKey();

builder.Services.AddAuthorization();

// SignalR
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
});

// =====================================================
// CORS Configuration (Updated for Vite)
// =====================================================
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            // Development: Allow Vite dev server
            policy
                .WithOrigins(
                    "http://localhost:3000",      // Vite default port
                    "http://localhost:5173",      // Alternative Vite port
                    "http://127.0.0.1:3000",
                    "http://127.0.0.1:5173"
                )
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();

            Log.Information("CORS configured for development: Vite dev server (localhost:3000)");
        }
        else
        {
            // Production: Same-origin (no CORS needed), but allow configured origins
            var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
                ?? Array.Empty<string>();

            if (allowedOrigins.Length > 0)
            {
                policy
                    .WithOrigins(allowedOrigins)
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                    .AllowCredentials();

                Log.Information("CORS configured for production with origins: {Origins}",
                    string.Join(", ", allowedOrigins));
            }
            else
            {
                // No CORS needed - same origin
                policy
                    .AllowAnyOrigin()
                    .AllowAnyMethod()
                    .AllowAnyHeader();

                Log.Information("CORS configured: Allow any origin (same-origin deployment)");
            }
        }
    });
});

// Health checks
builder.Services.AddHealthChecks()
    .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy())
    .AddCheck("mongodb", () =>
    {
        if (mongoDatabase == null)
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Degraded(
                "MongoDB not configured - using in-memory storage");

        try
        {
            mongoDatabase.RunCommand<MongoDB.Bson.BsonDocument>(
                new MongoDB.Bson.BsonDocument("ping", 1));
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("MongoDB connected");
        }
        catch (Exception ex)
        {
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy(
                "MongoDB connection failed", ex);
        }
    });

var app = builder.Build();

// =====================================================
// CRITICAL: Load State from MongoDB on Startup
// =====================================================
if (mongoDatabase != null)
{
    using (var scope = app.Services.CreateScope())
    {
        var dataStore = scope.ServiceProvider.GetRequiredService<DataStore>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        logger.LogInformation("⏳ Loading state from MongoDB...");

        try
        {
            await dataStore.LoadStateFromDatabaseAsync();
            logger.LogInformation("✓ State loaded successfully from MongoDB");

            var ingressService = scope.ServiceProvider.GetRequiredService<ICentralIngressService>();
            if (ingressService.IsEnabled)
            {
                logger.LogInformation("🔄 Initializing CentralIngress for running VMs...");

                var runningVms = dataStore.GetActiveVMs()
                    .Where(vm => vm.Status == VmStatus.Running)
                    .ToList();

                logger.LogInformation("Found {Count} running VMs to register", runningVms.Count);

                foreach (var vm in runningVms)
                {
                    try
                    {
                        await ingressService.OnVmStartedAsync(vm.Id);
                        logger.LogInformation("✓ Registered VM {VmId} ({Name}) with CentralIngress",
                            vm.Id, vm.Name);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to register VM {VmId} with CentralIngress", vm.Id);
                    }
                }

                logger.LogInformation("✓ CentralIngress initialization complete");
            }
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "❌ CRITICAL: Failed to load state from MongoDB");

            // In production, this should probably fail-fast
            var isProduction = builder.Environment.IsProduction();
            if (isProduction)
            {
                logger.LogCritical("Production environment detected - cannot start without persistent state");
                throw;  // Crash the application
            }
            else
            {
                logger.LogWarning("Development environment - continuing with empty state");
            }
        }
    }
}

// ==================== Seed Default Configuration ====================
using (var scope = app.Services.CreateScope())
{
    var configService = scope.ServiceProvider.GetRequiredService<ISchedulingConfigService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        // This will create default config if it doesn't exist
        var config = await configService.GetConfigAsync();
        logger.LogInformation(
            "Scheduling configuration loaded: v{Version}, Baseline: {Baseline}",
            config.Version, config.BaselineBenchmark);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to load scheduling configuration on startup");
        // Don't fail startup, service will retry on first request
    }
}

// =====================================================
// Middleware Pipeline
// =====================================================

// =====================================================
// Static Files Configuration (Vite Integration)
// =====================================================
if (app.Environment.IsDevelopment())
{
    // ===================================
    // DEVELOPMENT MODE
    // ===================================
    // In development, Vite dev server (port 3000) handles frontend
    // .NET only serves API endpoints
    // No static file serving needed

    Log.Information("╔══════════════════════════════════════════════════════════╗");
    Log.Information("║  🔧 DEVELOPMENT MODE                                     ║");
    Log.Information("╚══════════════════════════════════════════════════════════╝");
    Log.Information("");
    Log.Information("  Frontend:      http://localhost:3000 (Vite dev server)");
    Log.Information("  Backend API:   {Url}", app.Urls.FirstOrDefault() ?? "http://localhost:5050");
    Log.Information("  Swagger UI:    {Url}/swagger", app.Urls.FirstOrDefault() ?? "http://localhost:5050");
    Log.Information("");
    Log.Information("  📝 To start frontend:");
    Log.Information("     cd wwwroot");
    Log.Information("     npm run dev");
    Log.Information("");
}
else
{
    // ===================================
    // PRODUCTION MODE
    // ===================================
    // Serve Vite production build from wwwroot/dist/

    var distPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "dist");

    Log.Information("╔══════════════════════════════════════════════════════════╗");
    Log.Information("║  🚀 PRODUCTION MODE                                      ║");
    Log.Information("╚══════════════════════════════════════════════════════════╝");
    Log.Information("");

    // Check if dist/ exists
    if (Directory.Exists(distPath))
    {
        Log.Information("  ✓ Frontend build found: wwwroot/dist/");

        // Serve index.html by default
        app.UseDefaultFiles(new DefaultFilesOptions
        {
            DefaultFileNames = new List<string> { "index.html" },
            FileProvider = new PhysicalFileProvider(distPath),
            RequestPath = ""
        });

        // Serve static files from dist/
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(distPath),
            RequestPath = "",
            OnPrepareResponse = ctx =>
            {
                // Cache static assets with hashes for 1 year
                var file = ctx.File.Name;
                if (file.Contains("-") && (file.EndsWith(".js") || file.EndsWith(".css")))
                {
                    // Hashed files (e.g., index-abc123.js) - cache forever
                    ctx.Context.Response.Headers.Append(
                        "Cache-Control",
                        "public,max-age=31536000,immutable"
                    );
                }
                else if (file.EndsWith(".woff") || file.EndsWith(".woff2"))
                {
                    // Fonts - cache for 1 year
                    ctx.Context.Response.Headers.Append(
                        "Cache-Control",
                        "public,max-age=31536000,immutable"
                    );
                }
                else
                {
                    // Other files (index.html, etc.) - no cache
                    ctx.Context.Response.Headers.Append(
                        "Cache-Control",
                        "no-cache,no-store,must-revalidate"
                    );
                }
            }
        });

        Log.Information("  ✓ Static files configured: Serving from wwwroot/dist/");
    }
    else
    {
        Log.Warning("  ⚠ Frontend build NOT found!");
        Log.Warning("     Expected: {DistPath}", distPath);
        Log.Warning("     Run: cd wwwroot && npm run build");
        Log.Warning("");
        Log.Warning("  → Frontend will not be available until built");
    }

    Log.Information("  Application:   {Url}", app.Urls.FirstOrDefault() ?? "http://localhost:5050");
    Log.Information("  Swagger UI:    {Url}/swagger", app.Urls.FirstOrDefault() ?? "http://localhost:5050");
    Log.Information("");
}

// Swagger enabled in all environments
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Orchestrator API v1");
    c.RoutePrefix = "swagger";
});

app.UseRequestLogging();
app.UseErrorHandling();

// WebSocket terminal/sftp proxy (MUST be before CORS)
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});
app.UseWebSocketProxy();
app.UseSubdomainProxy();

app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<OrchestratorHub>("/hub/orchestrator");
app.MapHealthChecks("/health");
//app.MapHealthChecks("/health/wallet-ssh");

// =====================================================
// SPA Fallback (Production Only)
// =====================================================
// For client-side routing - return index.html for non-API routes
if (!app.Environment.IsDevelopment())
{
    app.MapFallback(async context =>
    {
        // Don't intercept API routes, SignalR hubs, Swagger, or health checks
        if (context.Request.Path.StartsWithSegments("/api") ||
            context.Request.Path.StartsWithSegments("/hub") ||
            context.Request.Path.StartsWithSegments("/swagger") ||
            context.Request.Path.StartsWithSegments("/health"))
        {
            context.Response.StatusCode = 404;
            return;
        }

        // Serve index.html for all other routes (SPA routing)
        var distPath = Path.Combine(
            app.Environment.ContentRootPath,
            "wwwroot",
            "dist"
        );
        var indexPath = Path.Combine(distPath, "index.html");

        if (File.Exists(indexPath))
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.Append("Cache-Control", "no-cache,no-store,must-revalidate");
            await context.Response.SendFileAsync(indexPath);
        }
        else
        {
            context.Response.StatusCode = 503;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(@"
<!DOCTYPE html>
<html>
<head>
    <title>DeCloud - Not Built</title>
    <style>
        body { 
            font-family: system-ui, -apple-system, sans-serif; 
            max-width: 600px; 
            margin: 100px auto; 
            padding: 20px;
            background: #1a1a1a;
            color: #fff;
        }
        .error { 
            background: #ff4444; 
            padding: 20px; 
            border-radius: 8px; 
            margin-bottom: 20px;
        }
        .instructions { 
            background: #333; 
            padding: 20px; 
            border-radius: 8px;
            font-family: monospace;
        }
        h1 { color: #ff4444; }
        code { 
            background: #000; 
            padding: 2px 6px; 
            border-radius: 3px; 
        }
    </style>
</head>
<body>
    <div class='error'>
        <h1>⚠️ Frontend Not Built</h1>
        <p>The frontend application has not been built yet.</p>
    </div>
    <div class='instructions'>
        <p><strong>To build the frontend:</strong></p>
        <p>1. Navigate to frontend directory:</p>
        <p><code>cd wwwroot</code></p>
        <p>2. Install dependencies (first time only):</p>
        <p><code>npm install</code></p>
        <p>3. Build production bundle:</p>
        <p><code>npm run build</code></p>
        <p>4. Restart this server</p>
    </div>
    <p style='margin-top: 20px; color: #888;'>
        API is still available at <a href='/swagger' style='color: #10b981;'>/swagger</a>
    </p>
</body>
</html>
");
        }
    });
}

// =====================================================
// Startup Banner
// =====================================================
if (app.Environment.IsDevelopment())
{
    Log.Information("  SignalR Hub:   {Url}/hub/orchestrator", app.Urls.FirstOrDefault() ?? "http://localhost:5050");
    Log.Information("  Health Check:  {Url}/health", app.Urls.FirstOrDefault() ?? "http://localhost:5050");
    Log.Information("");
}
else
{
    Log.Information("  SignalR Hub:   {Url}/hub/orchestrator", app.Urls.FirstOrDefault() ?? "http://localhost:5050");
    Log.Information("  Health Check:  {Url}/health", app.Urls.FirstOrDefault() ?? "http://localhost:5050");
    Log.Information("");
}

if (mongoDatabase != null)
{
    Log.Information("  Database:      MongoDB (persistent)");
}
else
{
    Log.Warning("  Database:      In-Memory (NON-PERSISTENT!)");
}

Log.Information("");
Log.Information("═══════════════════════════════════════════════════════════");

app.Run();