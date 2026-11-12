using Azure.Identity;
using Serilog;
using CrowdFund.Web.Observability;
using CrowdFund.Web.Backup;
using CrowdFund.Web.Security;
using CrowdFund.Web.Services;
using CrowdFund.Ai.Watchdog;
using CrowdFund.Ai.Concierge;
using CrowdFund.Ai.Orchestration;
using CrowdFund.Ai.Fraud;
using CrowdFund.Ai.Agents;
using CrowdFund.Core.Entities;
using CrowdFund.Core.Interfaces;
using CrowdFund.Core.Services;
using CrowdFund.Infrastructure.Data;
using CrowdFund.Infrastructure.Embeddings;
using CrowdFund.Infrastructure.Llm;
using CrowdFund.Infrastructure.PayPal;
using CrowdFund.Infrastructure.Storage;
using CrowdFund.Web.Components;
using CrowdFund.Web.Endpoints;
using CrowdFund.Web.Hubs;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;


// Load local .env (gitignored) into process env before configuration binds
static void LoadDotEnv(string? path = null)
{
    path ??= Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".env"));
    // also try solution root relative to content root
    var candidates = new[]
    {
        path,
        Path.Combine(Directory.GetCurrentDirectory(), ".env"),
        Path.Combine(Directory.GetCurrentDirectory(), "..", ".env"),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".env"))
    };
    foreach (var candidate in candidates.Distinct())
    {
        if (!File.Exists(candidate)) continue;
        foreach (var raw in File.ReadAllLines(candidate))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var val = line[(idx + 1)..].Trim().Trim('"');
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, val);
        }
        break;
    }
}
LoadDotEnv();

var builder = WebApplication.CreateBuilder(args);

// ---- Secrets: environment variables + optional user-secrets + optional Azure Key Vault ----
// Prefer: env vars (ConnectionStrings__Default, PayPal__ClientSecret, Llm__ApiKey)
// Local: dotnet user-secrets set "PayPal:ClientSecret" "..."
builder.Configuration.AddEnvironmentVariables();
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>(optional: true);
}
var kvUri = builder.Configuration["AzureKeyVault:Uri"];
if (!string.IsNullOrWhiteSpace(kvUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(kvUri), new DefaultAzureCredential());
}

// Structured logging
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "CrowdFundAI")
    .WriteTo.Console()
    .WriteTo.File("App_Data/logs/crowdfund-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
    .CreateLogger();
builder.Host.UseSerilog();


var conn = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=App_Data/crowdfund.db";
builder.Services.AddDbContext<AppDbContext>(opt => opt.UseSqlServer(conn));

builder.Services.AddIdentity<IdentityUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 6;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireLowercase = false;
    options.User.RequireUniqueEmail = true;
    options.SignIn.RequireConfirmedAccount = false;
    options.SignIn.RequireConfirmedEmail = false;
    options.Lockout.AllowedForNewUsers = false;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    // Maintain login sessions: sliding window + long-lived when "Remember me"
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.Name = "CrowdFundAI.Auth";
});

// Core + infrastructure
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("webhooks");
builder.Services.AddHttpClient(nameof(CrowdFund.Infrastructure.Storage.S3CompatibleObjectStore));
var objectProvider = builder.Configuration["ObjectStorage:Provider"] ?? "Local";
if (string.Equals(objectProvider, "S3", StringComparison.OrdinalIgnoreCase)
    || string.Equals(objectProvider, "R2", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IObjectStore, S3CompatibleObjectStore>();
else
    builder.Services.AddSingleton<IObjectStore, LocalObjectStore>();
var clamHost = builder.Configuration["ObjectStorage:ClamAvHost"];
if (!string.IsNullOrWhiteSpace(clamHost))
    builder.Services.AddSingleton<IVirusScanner, ClamAvVirusScanner>();
else
    builder.Services.AddSingleton<IVirusScanner, BasicVirusScanner>();
builder.Services.AddScoped<IFileStore, ScanningFileStore>();
builder.Services.AddSingleton<BackgroundJobQueue>();
builder.Services.AddSingleton<PremiumVerificationService>();
builder.Services.AddSingleton<EnterpriseTransparencyService>();
builder.Services.AddScoped<TenantContext>();
builder.Services.AddHostedService<BackgroundJobWorker>();
builder.Services.AddScoped<ILlmClient, FakeLlmClient>();
builder.Services.AddSingleton<IEmbeddingService, TfIdfEmbeddingService>();
builder.Services.AddSingleton<AllocationEngine>();
builder.Services.AddSingleton<LedgerService>();
builder.Services.AddScoped<DonationService>();
builder.Services.AddScoped<ReceiptIntelligence>();
builder.Services.AddScoped<FollowMyMoneyService>();
builder.Services.AddScoped<VerificationCouncil>();
builder.Services.AddSingleton<FraudEngine>();
builder.Services.AddScoped<VerificationOrchestrator>();
builder.Services.AddSingleton<ConciergeTools>();
builder.Services.AddScoped<DonorConcierge>();
builder.Services.AddScoped<ConciergeStreaming>();
builder.Services.AddSingleton<AnomalyEngine>();
builder.Services.AddSingleton<DocumentForensicsService>();
builder.Services.Configure<PolicyOptions>(builder.Configuration.GetSection(PolicyOptions.Section));
builder.Services.AddSingleton<PolicyEngine>(sp =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PolicyOptions>>().Value;
    return new PolicyEngine(opts);
});
builder.Services.AddSingleton<TransparencyScoreService>();
builder.Services.AddHostedService<WatchdogHostedService>();
builder.Services.AddSingleton<AmlScreeningService>();
builder.Services.AddSingleton<KycService>();
builder.Services.AddSingleton<TaxReceiptService>();
builder.Services.AddSingleton<SecurityAuditService>();
builder.Services.AddSingleton<AppMetrics>();
builder.Services.Configure<AlertOptions>(builder.Configuration.GetSection(AlertOptions.Section));
builder.Services.AddHttpClient<AlertService>();
builder.Services.AddHostedService<MetricsAlertHostedService>();
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection(BackupOptions.Section));
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<RiskGraphService>();
builder.Services.AddSingleton<TransparencyLogService>();
builder.Services.AddScoped<CampaignMatchingService>();
builder.Services.AddScoped<ImpactReportService>();
builder.Services.AddScoped<MultimodalProofService>();
builder.Services.Configure<FeeOptions>(builder.Configuration.GetSection(FeeOptions.Section));
builder.Services.AddSingleton<FeeTransparencyService>(sp =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<FeeOptions>>().Value;
    return new FeeTransparencyService(o);
});
builder.Services.AddSingleton<MatchingGiftService>();
builder.Services.Configure<CurrencyOptions>(builder.Configuration.GetSection(CurrencyOptions.Section));
builder.Services.AddSingleton<CurrencyService>(sp =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CurrencyOptions>>().Value;
    return new CurrencyService(o);
});
builder.Services.AddSingleton<CampaignTemplateService>();
builder.Services.AddSingleton<RefundDisputeService>();
builder.Services.AddHostedService<ReverificationHostedService>();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database", tags: new[] { "db" });
builder.Services.AddCrowdFundRateLimiting(builder.Configuration);
builder.Services.AddHttpClient<PayPalPayoutClient>();

// PayPal
builder.Services.Configure<PayPalOptions>(builder.Configuration.GetSection(PayPalOptions.SectionName));
builder.Services.Configure<PayPalCheckoutOptions>(builder.Configuration.GetSection(PayPalCheckoutOptions.SectionName));
builder.Services.AddHttpClient<PayPalAccountService>();
builder.Services.AddHttpClient<PayPalClient>();
builder.Services.AddHttpClient<PayPalSubscriptionsClient>();
builder.Services.AddHttpClient<PayPalVaultClient>();
builder.Services.AddHttpClient<PayPalInvoicingClient>();
builder.Services.AddScoped<PayPalWebhookService>();
builder.Services.AddScoped<PayPalWebhookRouter>();
builder.Services.AddHttpClient();

builder.Services.AddSignalR();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();

var app = builder.Build();
{
    var pp = app.Configuration.GetSection("PayPal");
    var sim = pp.GetValue<bool>("Simulate") || string.IsNullOrWhiteSpace(pp["ClientId"]);
    app.Logger.LogInformation(
        "PayPal gateway: env={Mode}, simulate={Simulate}, clientId={HasId}, clientSecret={HasSecret}, merchants={MerchantCount}",
        pp["Mode"] ?? "sandbox",
        sim,
        !string.IsNullOrWhiteSpace(pp["ClientId"]),
        !string.IsNullOrWhiteSpace(pp["ClientSecret"]),
        pp.GetSection("Merchants").GetChildren().Count());
}


using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    Directory.CreateDirectory("App_Data");
    db.Database.EnsureCreated();

    var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    foreach (var role in new[] { "Creator", "Donor", "Admin", "Auditor" })
    {
        if (!await roleMgr.RoleExistsAsync(role))
            await roleMgr.CreateAsync(new IdentityRole(role));
    }

    var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
    // Always (re)create / reset demo accounts so published credentials work every run
    await CrowdFund.Web.Data.DemoAccounts.EnsureAsync(userMgr, roleMgr, db, app.Logger);

    // 50 campaigns + donations, expenses, customers, covers, matching, flags, ledger, FX
    await CrowdFund.Web.Data.DummyDataSeeder.SeedAsync(db, userMgr, app.Logger);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseCrowdFundStaticWithCdnHeaders();
app.UseMiddleware<TenantMiddleware>();
app.UseMiddleware<CrawlerSsrMiddleware>();
app.UseAntiforgery();
app.UseSerilogRequestLogging();
app.UseCrowdFundSecurityHeaders();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHub<CampaignHub>("/hubs/campaign");
app.MapDonationEndpoints();
app.MapExpenseEndpoints();
app.MapAdminEndpoints();
app.MapChatEndpoints();
app.MapKycPayoutEndpoints();
app.MapAuditEndpoints();
app.MapOpsEndpoints();
app.MapTrustEndpoints();
app.MapRiskTrustEndpoints();
app.MapIntelligenceEndpoints();
app.MapPolicyForensicsEndpoints();
app.MapMoneyFlowEndpoints();
app.MapOrgCurrencyRefundEndpoints();
app.MapGrowthEndpoints();
app.MapPlatformScaleEndpoints();
app.MapPremiumVerificationEndpoints();
app.MapWhiteLabelTrustEndpoints();
app.MapSponsoredMatchingEndpoints();
app.MapEnterpriseTaasEndpoints();
app.MapSeoEndpoints();
app.MapGeoEndpoints();
app.MapPayPalGatewayEndpoints();
app.MapPaymentsDashboardEndpoints();
app.MapPayPalPaymentMethodEndpoints();
app.MapPaymentProcessingEndpoints();
app.MapRefundManagementEndpoints();
app.MapSubscriptionManagementEndpoints();
app.MapPayPalWebhookEndpoints();
app.MapTransactionManagementEndpoints();
app.MapCustomerManagementEndpoints();
app.MapInvoicingEndpoints();
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("db")
});


app.MapPost("/Account/PerformLogin", async (HttpContext http, SignInManager<IdentityUser> signIn, UserManager<IdentityUser> users) =>
{
    var form = await http.Request.ReadFormAsync();
    var email = (form["email"].ToString() ?? "").Trim();
    var password = form["password"].ToString() ?? "";
    var returnUrl = form["returnUrl"].ToString();
    if (string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith("/") || returnUrl == "/")
        returnUrl = "/dashboard";
    var remember = string.Equals(form["remember"], "true", StringComparison.OrdinalIgnoreCase);

    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        return Results.Redirect("/Account/Login?error=" + Uri.EscapeDataString("Email and password are required."));

    var user = await users.FindByEmailAsync(email) ?? await users.FindByNameAsync(email);
    if (user == null)
        return Results.Redirect("/Account/Login?error=" + Uri.EscapeDataString("No account found for that email.") + "&email=" + Uri.EscapeDataString(email));

    var check = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: false);
    if (!check.Succeeded)
        return Results.Redirect("/Account/Login?error=" + Uri.EscapeDataString("Wrong password. Demo: Admin123! / Creator123! / Donor123!") + "&email=" + Uri.EscapeDataString(email));

    await signIn.SignInAsync(user, isPersistent: remember);
    return Results.Redirect(returnUrl);
}).DisableAntiforgery();

app.MapPost("/Account/PerformRegister", async (HttpContext http, SignInManager<IdentityUser> signIn, UserManager<IdentityUser> users, AppDbContext db) =>
{
    var form = await http.Request.ReadFormAsync();
    var email = (form["email"].ToString() ?? "").Trim();
    var password = form["password"].ToString() ?? "";
    var display = (form["displayName"].ToString() ?? "").Trim();
    var role = form["role"].ToString();
    if (role is not ("Donor" or "Creator"))
        role = "Donor";

    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password) || password.Length < 6)
        return Results.Redirect("/Account/Register?error=" + Uri.EscapeDataString("Valid email and password (6+ chars) required."));

    var user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
    var create = await users.CreateAsync(user, password);
    if (!create.Succeeded)
        return Results.Redirect("/Account/Register?error=" + Uri.EscapeDataString(string.Join(" ", create.Errors.Select(e => e.Description))));

    await users.AddToRoleAsync(user, role);
    if (!await db.AppUsers.AnyAsync(u => u.Id == user.Id))
    {
        db.AppUsers.Add(new CrowdFund.Core.Entities.ApplicationUser
        {
            Id = user.Id,
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(display) ? email : display,
            Role = role,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    await signIn.SignInAsync(user, isPersistent: true);
    return Results.Redirect("/dashboard");
}).DisableAntiforgery();

app.MapPost("/Account/Logout", async (SignInManager<IdentityUser> signInManager) =>
{
    await signInManager.SignOutAsync();
    return Results.Redirect("/");
}).DisableAntiforgery();

try { app.Run(); } finally { Log.CloseAndFlush(); }
