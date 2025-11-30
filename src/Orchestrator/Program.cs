using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using Orchestrator.Background;
using Orchestrator.Data;
using Orchestrator.Hubs;
using Orchestrator.Infrastructure;
using Orchestrator.Services;
using Serilog;
using System.Text;
using System.Text.Json;

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

builder.Services.AddScoped<INodeService, NodeService>();
builder.Services.AddScoped<IVmService, VmService>();
// UserService needs IWebHostEnvironment for dev mode signature validation
builder.Services.AddScoped<IUserService>(sp =>
{
    var dataStore = sp.GetRequiredService<DataStore>();
    var logger = sp.GetRequiredService<ILogger<UserService>>();
    var config = sp.GetRequiredService<IConfiguration>();
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    return new UserService(dataStore, logger, config, env);
});
builder.Services.AddScoped<IEventService, EventService>();
builder.Services.AddHttpClient<ITerminalService, TerminalService>();

// =====================================================
// Background Services
// =====================================================
builder.Services.AddHostedService<NodeHealthMonitorService>();
builder.Services.AddHostedService<VmSchedulerService>();
builder.Services.AddHostedService<BillingService>();
builder.Services.AddHostedService<CleanupService>();

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
        // Make JSON deserialization case-insensitive
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;

        // Use camelCase for consistency
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
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

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .SetIsOriginAllowed(_ => true)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
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

// =====================================================
// Middleware Pipeline
// =====================================================

// Serve static files (dashboard)
app.UseDefaultFiles();
app.UseStaticFiles();

// Swagger enabled in all environments
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Orchestrator API v1");
    c.RoutePrefix = "swagger";
});

app.UseRequestLogging();
app.UseErrorHandling();

app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<OrchestratorHub>("/hub/orchestrator");
app.MapHealthChecks("/health");

Log.Information("╔══════════════════════════════════════════════════════════╗");
Log.Information("║       DeCloud Orchestrator Starting...                   ║");
Log.Information("╚══════════════════════════════════════════════════════════╝");
Log.Information("");
Log.Information("  Swagger UI:    {Url}/swagger", app.Urls.FirstOrDefault() ?? "http://localhost:5000");
Log.Information("  SignalR Hub:   {Url}/hub/orchestrator", app.Urls.FirstOrDefault() ?? "http://localhost:5000");
Log.Information("  Health Check:  {Url}/health", app.Urls.FirstOrDefault() ?? "http://localhost:5000");
Log.Information("");

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