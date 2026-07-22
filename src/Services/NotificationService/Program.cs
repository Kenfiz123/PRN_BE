using ClubReportHub.Shared.Auth;
using ClubReportHub.Shared.Data;
using ClubReportHub.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using NotificationService.Consumers;
using NotificationService.Contracts;
using NotificationService.Data;
using NotificationService.Models;
using StackExchange.Redis;
using System.Security.Claims;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<NotificationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.Configure<RedisStreamOptions>(
    builder.Configuration.GetSection(RedisStreamOptions.SectionName));
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RedisStreamOptions>>().Value;
    var config = ConfigurationOptions.Parse(options.ConnectionString);
    config.AbortOnConnectFail = false;
    config.ConnectRetry = 3;
    config.ConnectTimeout = 5000;
    return ConnectionMultiplexer.Connect(config);
});
builder.Services.AddHostedService<RedisStreamNotificationConsumer>();
builder.Services.AddClubAccessClient(builder.Configuration);
builder.Services.AddClubReportJwt(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
    {
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? ["http://localhost:3000", "http://localhost:5173"];
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});
builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/error");
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("frontend");
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapGet("/error", () => Results.Problem("An unexpected error occurred.")).AllowAnonymous();
app.MapGet("/", () => Results.Ok(new { service = "Notification Service", status = "running" }));

var notifications = app.MapGroup("/api/notifications")
    .WithTags("Notifications")
    .RequireAuthorization(AuthPolicies.AllActors);

notifications.MapGet("/", async (
    int? recipientUserId,
    string? recipientRole,
    bool? unreadOnly,
    NotificationDbContext db,
    ClaimsPrincipal user,
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var recipientRoles = await GetRecipientRolesAsync(user, clubAccess, httpContext, cancellationToken);
    var query = ScopeNotificationQuery(db.Notifications.AsQueryable(), user, recipientRoles, recipientUserId, recipientRole);
    if (query is null)
    {
        return Results.Forbid();
    }

    if (unreadOnly == true)
    {
        query = query.Where(x => !x.IsRead);
    }

    var rows = await query.OrderByDescending(x => x.CreatedAtUtc).Take(100).ToListAsync();
    return Results.Ok(rows.Select(ToResponse));
});

notifications.MapPut("/{id:int}/read", async (
    int id,
    NotificationDbContext db,
    ClaimsPrincipal user,
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var notification = await db.Notifications.FindAsync([id], cancellationToken);
    if (notification is null)
    {
        return Results.NotFound();
    }

    var recipientRoles = await GetRecipientRolesAsync(user, clubAccess, httpContext, cancellationToken);
    if (!CanAccessNotification(user, recipientRoles, notification))
    {
        return Results.Forbid();
    }

    notification.IsRead = true;
    await db.SaveChangesAsync();
    return Results.NoContent();
});

notifications.MapPut("/read-all", async (
    int? recipientUserId,
    string? recipientRole,
    NotificationDbContext db,
    ClaimsPrincipal user,
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var recipientRoles = await GetRecipientRolesAsync(user, clubAccess, httpContext, cancellationToken);
    var query = ScopeNotificationQuery(db.Notifications.Where(x => !x.IsRead), user, recipientRoles, recipientUserId, recipientRole);
    if (query is null)
    {
        return Results.Forbid();
    }

    await query.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.IsRead, true));
    return Results.NoContent();
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseStartup");
    await db.EnsureCreatedWithRetryAsync(logger);
    await NotificationSeeder.SeedAsync(db);
}

app.Run();

static NotificationResponse ToResponse(Notification notification)
{
    var (title, message) = NormalizeLegacyNotification(notification);
    return new NotificationResponse(
        notification.Id,
        notification.RecipientUserId,
        notification.RecipientRole,
        notification.EventType,
        title,
        message,
        notification.IsRead,
        notification.CreatedAtUtc);
}

static (string Title, string Message) NormalizeLegacyNotification(Notification notification)
{
    var eventType = notification.EventType.ToLowerInvariant();
    var message = notification.Message;

    return eventType switch
    {
        "club.created" => (
            "New club created",
            Regex.Replace(message, @"^(.*?) \((.*?)\) .*?$", "$1 ($2) has been added to the system.")),
        "user.registered" => (
            "Welcome to FPTU Club Hub",
            message.StartsWith("Hello ", StringComparison.Ordinal)
                ? message
                : Regex.Replace(message, @"^Xin ch\u00E0o (.*?)\..*$", "Hello $1. Your member account is ready.")),
        "activity.created" => (
            "New activity",
            Regex.Replace(message, @"^(.*?) \u0111\u00E3 l\u00EAn l\u1ECBch ho\u1EA1t \u0111\u1ED9ng (.*?)\.$", "$1 has scheduled the activity $2.")),
        "report.submitted" => (
            notification.Title.Contains("final", StringComparison.OrdinalIgnoreCase)
                || notification.Title == "B\u00E1o c\u00E1o ch\u1EDD ph\u00EA duy\u1EC7t"
                ? "Report awaiting final approval"
                : "Report awaiting club manager review",
            Regex.Replace(message, @"^(.*?) \u0111\u00E3 g\u1EEDi b\u00E1o c\u00E1o k\u1EF3 (.*?)\.$", "$1 submitted the report for period $2.")),
        "report.approved" => (
            "Report approved",
            Regex.Replace(message, @"^B\u00E1o c\u00E1o k\u1EF3 (.*?) c\u1EE7a (.*?) \u0111\u00E3 \u0111\u01B0\u1EE3c ph\u00EA duy\u1EC7t\.$", "The $1 report for $2 has been approved.")),
        "report.rejected" => (
            "Report requires revision",
            Regex.Replace(message, @"^B\u00E1o c\u00E1o k\u1EF3 (.*?) c\u1EE7a (.*?) b\u1ECB t\u1EEB ch\u1ED1i: (.*)$", "The $1 report for $2 was rejected: $3")),
        "kpi.calculated" => (
            "KPI updated",
            Regex.Replace(message, @"^KPI k\u1EF3 (.*?) c\u1EE7a (.*?) l\u00E0 (.*?) \u0111i\u1EC3m\.$", "The KPI score for $2 in period $1 is $3.")),
        "budget.proposal.submitted" => (
            "New budget proposal",
            Regex.Replace(message, @"^(.*?) \u0111\u1EC1 xu\u1EA5t ng\u00E2n s\u00E1ch (.*?) VN\u0110\.$", "$1 requested a budget of $2 VND.")),
        "budget.approved" => (
            "Budget approved",
            Regex.Replace(message, @"^Ng\u00E2n s\u00E1ch c\u1EE7a (.*?) \u0111\u01B0\u1EE3c duy\u1EC7t v\u1EDBi s\u1ED1 ti\u1EC1n (.*?) VN\u0110\.$", "The budget for $1 was approved for $2 VND.")),
        "settlement.overdue" => (
            "Overdue settlement",
            Regex.Replace(message, @"^(.*?) c\u00F3 quy\u1EBFt to\u00E1n qu\u00E1 h\u1EA1n cho \u0111\u1EC1 xu\u1EA5t #(.*?)\.$", "$1 has an overdue settlement for proposal #$2.")),
        "export.completed" => (
            "Export ready",
            Regex.Replace(message, @"^File (.*?) \u0111\u00E3 t\u1EA1o xong: (.*?)\.$", "The $1 export is ready: $2.")),
        "report.deadline.reminder" => (
            "Report deadline reminder",
            Regex.Replace(message, @"^K\u1EF3 (.*?) v\u1EABn c\u00F2n c\u00E2u l\u1EA1c b\u1ED9 ch\u01B0a n\u1ED9p b\u00E1o c\u00E1o\.$", "Some clubs have not yet submitted their reports for period $1.")),
        _ => (
            notification.Title == "S\u1EF1 ki\u1EC7n h\u1EC7 th\u1ED1ng" ? "System event" : notification.Title,
            message == "H\u1EC7 th\u1ED1ng v\u1EEBa ghi nh\u1EADn m\u1ED9t s\u1EF1 ki\u1EC7n m\u1EDBi."
                ? "The system recorded a new event."
                : message)
    };
}

static IQueryable<Notification>? ScopeNotificationQuery(
    IQueryable<Notification> query,
    ClaimsPrincipal user,
    IReadOnlyCollection<string> roles,
    int? recipientUserId,
    string? recipientRole)
{
    var normalizedRole = NormalizeRole(recipientRole);
    if (IsNotificationAdmin(user))
    {
        return ApplyRequestedRecipientFilters(query, recipientUserId, normalizedRole);
    }

    var userId = user.GetUserId();
    if (recipientUserId.HasValue && recipientUserId.Value != userId)
    {
        return null;
    }

    if (normalizedRole is not null && !roles.Contains(normalizedRole))
    {
        return null;
    }

    query = query.Where(x => x.RecipientUserId == userId || (x.RecipientRole != null && roles.Contains(x.RecipientRole)));
    return ApplyRequestedRecipientFilters(query, recipientUserId, normalizedRole);
}

static IQueryable<Notification> ApplyRequestedRecipientFilters(
    IQueryable<Notification> query,
    int? recipientUserId,
    string? recipientRole)
{
    if (recipientUserId.HasValue && recipientRole is not null)
    {
        return query.Where(x => x.RecipientUserId == recipientUserId || x.RecipientRole == recipientRole);
    }

    if (recipientUserId.HasValue)
    {
        return query.Where(x => x.RecipientUserId == recipientUserId);
    }

    if (recipientRole is not null)
    {
        return query.Where(x => x.RecipientRole == recipientRole);
    }

    return query;
}

static bool CanAccessNotification(
    ClaimsPrincipal user,
    IReadOnlyCollection<string> roles,
    Notification notification)
{
    if (IsNotificationAdmin(user))
    {
        return true;
    }

    var userId = user.GetUserId();
    return notification.RecipientUserId == userId
        || (notification.RecipientRole is not null && roles.Contains(notification.RecipientRole));
}

static bool IsNotificationAdmin(ClaimsPrincipal user)
{
    return user.IsInRole(AuthRoles.Admin);
}

static async Task<string[]> GetRecipientRolesAsync(
    ClaimsPrincipal user,
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken)
{
    var roles = user.Claims
        .Where(x => x.Type == ClaimTypes.Role || x.Type == "role")
        .Select(x => x.Value)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var access = await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken);
    if (access.Any(item => item.IsManager)) roles.Add(AuthRoles.ClubManager);
    if (access.Any(item => item.IsTreasurer)) roles.Add(AuthRoles.Treasurer);
    if (access.Any(item => item.IsApprovedMember || item.IsManager)) roles.Add(AuthRoles.ClubMember);

    return roles.ToArray();
}

static string? NormalizeRole(string? role)
{
    return string.IsNullOrWhiteSpace(role) ? null : role.Trim();
}
