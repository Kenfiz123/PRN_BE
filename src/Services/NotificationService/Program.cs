using ClubReportHub.Shared.Auth;
using ClubReportHub.Shared.Data;
using Microsoft.EntityFrameworkCore;
using NotificationService.Consumers;
using NotificationService.Contracts;
using NotificationService.Data;
using NotificationService.Models;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<NotificationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.Configure<ClubReportHub.Shared.Messaging.RabbitMqOptions>(
    builder.Configuration.GetSection(ClubReportHub.Shared.Messaging.RabbitMqOptions.SectionName));
builder.Services.AddHostedService<RabbitMqNotificationConsumer>();
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

var notifications = app.MapGroup("/api/notifications").WithTags("Notifications").RequireAuthorization(AuthPolicies.AdminOrClubManagerOrMember);

notifications.MapGet("/", async (
    int? recipientUserId,
    string? recipientRole,
    bool? unreadOnly,
    NotificationDbContext db,
    ClaimsPrincipal user) =>
{
    var query = ScopeNotificationQuery(db.Notifications.AsQueryable(), user, recipientUserId, recipientRole);
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

notifications.MapPut("/{id:int}/read", async (int id, NotificationDbContext db, ClaimsPrincipal user) =>
{
    var notification = await db.Notifications.FindAsync(id);
    if (notification is null)
    {
        return Results.NotFound();
    }

    if (!CanAccessNotification(user, notification))
    {
        return Results.Forbid();
    }

    notification.IsRead = true;
    await db.SaveChangesAsync();
    return Results.NoContent();
});

notifications.MapPut("/read-all", async (int? recipientUserId, string? recipientRole, NotificationDbContext db, ClaimsPrincipal user) =>
{
    var query = ScopeNotificationQuery(db.Notifications.Where(x => !x.IsRead), user, recipientUserId, recipientRole);
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
    await db.ApplyMigrationsWithRetryAsync(logger);
    await NotificationSeeder.SeedAsync(db);
}

app.Run();

static NotificationResponse ToResponse(Notification notification) => new(
    notification.Id,
    notification.RecipientUserId,
    notification.RecipientRole,
    notification.EventType,
    notification.Title,
    notification.Message,
    notification.IsRead,
    notification.CreatedAtUtc);

static IQueryable<Notification>? ScopeNotificationQuery(
    IQueryable<Notification> query,
    ClaimsPrincipal user,
    int? recipientUserId,
    string? recipientRole)
{
    var normalizedRole = NormalizeRole(recipientRole);
    if (IsNotificationAdmin(user))
    {
        return ApplyRequestedRecipientFilters(query, recipientUserId, normalizedRole);
    }

    var userId = user.GetUserId();
    var roles = GetRecipientRoles(user);
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

static bool CanAccessNotification(ClaimsPrincipal user, Notification notification)
{
    if (IsNotificationAdmin(user))
    {
        return true;
    }

    var userId = user.GetUserId();
    var roles = GetRecipientRoles(user);
    return notification.RecipientUserId == userId
        || (notification.RecipientRole is not null && roles.Contains(notification.RecipientRole));
}

static bool IsNotificationAdmin(ClaimsPrincipal user)
{
    return user.IsInRole(AuthRoles.Admin) || user.IsInRole(AuthRoles.SystemAdmin);
}

static string[] GetRecipientRoles(ClaimsPrincipal user)
{
    return user.Claims
        .Where(x => x.Type == ClaimTypes.Role || x.Type == "role")
        .Select(x => x.Value)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static string? NormalizeRole(string? role)
{
    return string.IsNullOrWhiteSpace(role) ? null : role.Trim();
}
