using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using GroupBuy.API;
using GroupBuy.Application;
using GroupBuy.Domain;
using GroupBuy.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;

if (args.Length == 1 && args[0] == "--healthcheck") {
    try { using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(3) }; var port = Environment.GetEnvironmentVariable("PORT") ?? "8080"; if (!int.TryParse(port, out var number) || number is < 1024 or > 65535) throw new InvalidOperationException("Invalid health port."); using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{number}/health/ready"); request.Headers.Host = Environment.GetEnvironmentVariable("HealthChecks__Host") ?? (Environment.GetEnvironmentVariable("AllowedHosts") ?? "localhost").Split(';')[0].Trim(); using var response = await client.SendAsync(request); Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1; }
    catch { Environment.ExitCode = 1; } return;
}
var builder = WebApplication.CreateBuilder(args);
ProductionConfiguration.ConfigurePort(builder);
if (args.Contains("--seed-demo") && !builder.Environment.IsDevelopment()) throw new InvalidOperationException("Demo seed is development-only.");
builder.Host.UseSerilog((_, log) => log.MinimumLevel.Information().MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning).MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Fatal).MinimumLevel.Override("Microsoft.AspNetCore.DataProtection", Serilog.Events.LogEventLevel.Fatal).MinimumLevel.Override("Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler", Serilog.Events.LogEventLevel.Fatal).WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter(renderMessage: true)));
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 64 * 1024);
builder.Services.AddDbContext<MarketplaceDb>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Database") ?? throw new InvalidOperationException("Set ConnectionStrings__Database."), p => p.CommandTimeout(30)));
builder.Services.AddIdentityCore<IdentityAccount>(o => { o.User.RequireUniqueEmail = false; /* Database unique email index remains authoritative; mobile-only customers have no email. */ o.Password.RequiredLength = 12; o.Lockout.MaxFailedAccessAttempts = 5; o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15); }).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<MarketplaceDb>().AddUserStore<ProtectedIdentityStore>().AddDefaultTokenProviders();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IOtpDeliveryProvider, UnconfiguredOtpDelivery>();
builder.Services.AddSingleton<IGoogleSigningKeys, GoogleSigningKeys>();
builder.Services.AddScoped<IGoogleCredentialValidator, GoogleCredentialValidator>();
builder.Services.AddScoped<CustomerAuthenticationService>();
builder.Services.AddHttpClient("GoogleSigningKeys", c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<AuthenticationRateLimitSettings>();
builder.Services.AddScoped<AdminMfaService>(); builder.Services.AddScoped<IAdminMfa>(s => s.GetRequiredService<AdminMfaService>()); builder.Services.AddScoped<AdminProvisioner>();
ProductionConfiguration.Configure(builder);
PilotConfiguration.Validate(builder.Configuration, builder.Environment);
builder.Services.AddSingleton<WorkerHealthState>();
builder.Services.AddSingleton<OperationalMetrics>();
builder.Services.AddScoped<IIdentityService, IdentityService>(); builder.Services.AddScoped<Marketplace>();
builder.Services.AddScoped<IMarketplace>(s => s.GetRequiredService<Marketplace>()); builder.Services.AddScoped<ICampaignExpiryProcessor>(s => s.GetRequiredService<Marketplace>());
builder.Services.AddScoped<IOperations>(s => s.GetRequiredService<Marketplace>());
builder.Services.AddScoped<IAdministration>(s => s.GetRequiredService<Marketplace>());
builder.Services.AddHttpClient("Razorpay");
builder.Services.AddScoped<IPaymentGateway>(PilotConfiguration.Gateway);
builder.Services.AddScoped<IMyOrders>(s => s.GetRequiredService<Marketplace>());
builder.Services.AddScoped<ICheckout>(s => s.GetRequiredService<Marketplace>());
builder.Services.AddScoped<IGroupDeals>(s => s.GetRequiredService<Marketplace>());
builder.Services.AddScoped<IPhaseOne>(s => s.GetRequiredService<Marketplace>());
builder.Services.AddScoped<DiscoveryAreas>();
builder.Services.AddScoped<DiscoveryQueries>();
builder.Services.AddScoped<IDiscovery, DiscoveryService>();
builder.Services.AddHostedService<ExpiryWorker>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => {
    var key = builder.Configuration["Jwt:SigningKey"]!;
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new() { ValidateIssuer = true, ValidIssuer = builder.Configuration["Jwt:Issuer"], ValidateAudience = true, ValidAudience = builder.Configuration["Jwt:Audience"], ValidateLifetime = true, ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), ClockSkew = TimeSpan.FromSeconds(15), NameClaimType = "sub", RoleClaimType = "role", ValidAlgorithms = [SecurityAlgorithms.HmacSha256] };
    o.Events = new JwtBearerEvents { OnTokenValidated = async ctx => {
        var db = ctx.HttpContext.RequestServices.GetRequiredService<MarketplaceDb>();
        if (!Guid.TryParse(ctx.Principal?.FindFirst("sub")?.Value, out var id) || !int.TryParse(ctx.Principal?.FindFirst("ver")?.Value, out var version) || !await db.Users.AnyAsync(x => x.Id == id && x.TokenVersion == version, ctx.HttpContext.RequestAborted) || !await db.UsersProfile.AnyAsync(x => x.Id == id && x.Status == AccountStatus.Active, ctx.HttpContext.RequestAborted)) ctx.Fail("Account unavailable or token revoked.");
        else
        {
            var currentRoles = await (from membership in db.UserRoles
                                      join role in db.Roles on membership.RoleId equals role.Id
                                      where membership.UserId == id
                                      select role.Name).ToListAsync(ctx.HttpContext.RequestAborted);
            if ((ctx.Principal!.IsInRole("Admin") && (ctx.Principal.FindFirst("amr")?.Value != "mfa" || !await db.Users.AnyAsync(u => u.Id == id && u.TwoFactorEnabled, ctx.HttpContext.RequestAborted))) || ctx.Principal.FindAll("role").Any(claim => !currentRoles.Contains(claim.Value)))
                ctx.Fail("Role assignment revoked.");
        }
    }, OnAuthenticationFailed = ctx => {
        if (!ctx.HttpContext.RequestAborted.IsCancellationRequested) {
            var errorType = ctx.Exception.GetType().Name;
            ctx.HttpContext.RequestServices.GetRequiredService<OperationalMetrics>().RecordAuthenticationFailure(errorType);
            ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AuthenticationSecurity").LogWarning("Authentication failed {ErrorType} {TraceId}", errorType, ctx.HttpContext.TraceIdentifier);
        }
        ctx.Fail("Authentication unavailable or invalid."); return Task.CompletedTask;
    } };
});
builder.Services.AddAuthorization(o => { foreach (var role in new[] { "Customer", "Seller", "Admin" }) o.AddPolicy(role, p => p.RequireAuthenticatedUser().RequireRole(role)); o.AddPolicy("Admin", p => p.RequireAuthenticatedUser().RequireRole("Admin").RequireClaim("amr", "mfa")); });
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()); o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow; });
builder.Services.AddProblemDetails(); builder.Services.AddExceptionHandler<Errors>();
builder.Services.AddHttpsRedirection(o => o.HttpsPort = 443);
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((document, context, ct) => {
    document.Info.Title = "Groudeal Marketplace API"; document.Info.Version = "v1";
    document.Info.Description = "Legacy reservations remain non-financial. Phase One campaigns require captured provider payments; final tier differences return to the source payment. Prices are INR per variant unit.";
    document.Components ??= new();
    document.Components.SecuritySchemes ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiSecurityScheme>();
    document.Components.SecuritySchemes["Bearer"] = new Microsoft.OpenApi.OpenApiSecurityScheme { Type = Microsoft.OpenApi.SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" };
    return Task.CompletedTask;
}));
var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
if (origins.Any(x => x.Contains('*') || !Uri.TryCreate(x, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "" || uri.UserInfo != "" || x.EndsWith('/') || (!builder.Environment.IsDevelopment() && uri.Scheme != "https"))) throw new InvalidOperationException("CORS requires explicit valid origins.");
builder.Services.AddCors(o => o.AddDefaultPolicy(p => { if (origins.Length > 0) p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod(); }));
builder.Services.AddRateLimiter(o => {
    o.RejectionStatusCode = 429;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new() { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("auth", c => c.RequestServices.GetRequiredService<AuthenticationRateLimitSettings>().Partition(c.Connection.RemoteIpAddress?.ToString() ?? "unknown"));
    o.AddPolicy("admin", c => RateLimitPartition.GetFixedWindowLimiter(c.User.FindFirst("sub")?.Value ?? c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new() { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("customer-login", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new() { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("customer-auth", c => RateLimitPartition.GetFixedWindowLimiter(c.User.FindFirst("sub")?.Value ?? "unknown", _ => new() { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("mfa", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new() { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("pickup", c => RateLimitPartition.GetFixedWindowLimiter(c.User.FindFirst("sub")?.Value ?? c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new() { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("checkout", c => RateLimitPartition.GetFixedWindowLimiter(c.User.FindFirst("sub")?.Value ?? c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.OnRejected = async (c, ct) => { c.HttpContext.Response.StatusCode = 429; await Results.Problem(statusCode: 429, title: "Rate limit exceeded.", extensions: new Dictionary<string, object?> { ["code"] = "rate_limited" }).ExecuteAsync(c.HttpContext); };
});
var app = builder.Build();
app.ValidateCustomerAuthentication();
try {
    DataProtectionSetup.ValidatePersistedKeyRing(app.Services, requireEncryption: !app.Environment.IsDevelopment());
    var protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Groudeal.StartupProbe.v1");
    if (protector.Unprotect(protector.Protect("startup-probe")) != "startup-probe") throw new InvalidOperationException("Protection probe failed.");
}
catch (Exception ex) { app.Logger.LogError("Data protection initialization failed {ErrorType}", ex.GetType().Name); throw new InvalidOperationException("Data protection is unavailable; verify key storage and certificate configuration."); }
var signingKey = builder.Configuration["Jwt:SigningKey"];
if (string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32) throw new InvalidOperationException("Set Jwt__SigningKey to a random secret of at least 32 bytes.");
if (string.IsNullOrWhiteSpace(builder.Configuration["Jwt:Issuer"]) || string.IsNullOrWhiteSpace(builder.Configuration["Jwt:Audience"])) throw new InvalidOperationException("Configure JWT issuer and audience.");
if (builder.Configuration.GetValue("Jwt:ExpiryMinutes", 15) is < 1 or > 60) throw new InvalidOperationException("JWT expiry must be 1–60 minutes.");
if (builder.Configuration.GetValue("Operations:ConfirmationMinutes", 60) is < 1 or > 1440 || builder.Configuration.GetValue("Operations:PickupHours", 48) is < 1 or > 168) throw new InvalidOperationException("Confirmation must be 1–1440 minutes and pickup 1–168 hours.");
app.UseForwardedHeaders();
app.UseRenderFreeIngress();
app.UseExceptionHandler(); app.UseStatusCodePages(async c => await Results.Problem(statusCode: c.HttpContext.Response.StatusCode).ExecuteAsync(c.HttpContext));
if (!app.Environment.IsDevelopment()) { app.UseHsts(); app.UseWhen(c => c.Request.Path != "/health/live" && c.Request.Path != "/health/ready", branch => branch.UseHttpsRedirection()); }
app.Use(async (c, next) => { c.Response.Headers.XContentTypeOptions = "nosniff"; c.Response.Headers["Referrer-Policy"] = "no-referrer"; c.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'"; c.Response.Headers.CacheControl = "no-store"; await next(); });
app.Use(async (context, next) => {
    var started = System.Diagnostics.Stopwatch.GetTimestamp();
    try { await next(); }
    finally {
        var route = context.GetEndpoint() is Microsoft.AspNetCore.Routing.RouteEndpoint endpoint
            ? endpoint.RoutePattern.RawText ?? "unmatched"
            : "unmatched";
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        context.RequestServices.GetRequiredService<OperationalMetrics>().RecordRequest(route, context.Request.Method, context.Response.StatusCode, elapsed);
    }
});
app.UseCors(); app.UseAuthentication(); app.UseRateLimiter(); app.UseAuthorization();
if (app.Environment.IsDevelopment()) app.MapOpenApi(); else app.MapOpenApi().RequireAuthorization("Admin");
app.MapMarketplace();
app.MapCustomerAuthentication();
app.MapPhaseOne();
app.MapGroupDeals();
app.MapCheckout();
app.MapMyOrders();
app.MapDiscovery();
app.MapOperations();
app.MapAdministration();
app.MapAdminSecurity();
app.MapOperationalHealth();
if (builder.Configuration["admin-operation"] is string operation) {
    if (!Guid.TryParse(builder.Configuration["user-id"], out var userId)) throw new InvalidOperationException("Provide --user-id with an existing operator UUID.");
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AdminProvisioner>().Execute(operation, userId, builder.Configuration["operator-id"] ?? "", builder.Configuration["reference"] ?? "", default);
    app.Logger.LogInformation("Admin operational command completed; inspect the transactional audit using its change reference."); return;
}
if (args.Contains("--seed-demo")) {
    if (!app.Environment.IsDevelopment()) throw new InvalidOperationException("Demo seed is development-only.");
    await using var scope = app.Services.CreateAsyncScope(); await DemoSeed.Run(scope.ServiceProvider); return;
}
app.Run();
public partial class Program;

