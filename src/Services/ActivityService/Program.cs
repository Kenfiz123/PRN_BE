using System.Security.Claims;
using ActivityService.Contracts;
using ActivityService.Data;
using ActivityService.Models;
using ClubReportHub.Shared.Auth;
using ClubReportHub.Shared.Data;
using ClubReportHub.Shared.Events;
using ClubReportHub.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ActivityDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddClubReportJwt(builder.Configuration);
builder.Services.AddClubAccessClient(builder.Configuration);
builder.Services.AddRedisStreamEventBus(builder.Configuration);
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
app.MapGet("/", () => Results.Ok(new { service = "Activity Service", status = "running" }));

var activities = app.MapGroup("/api/activities")
    .WithTags("Activities")
    .RequireAuthorization(AuthPolicies.BusinessAccess);

activities.MapGet("/", async (
    int? clubId,
    string? status,
    DateTimeOffset? from,
    DateTimeOffset? to,
    ActivityDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var query = db.Activities.Include(x => x.Participants).AsQueryable();
    if (!CanReviewAllActivities(user))
    {
        var access = await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken);
        var visibleClubIds = access.Where(x => x.CanView).Select(x => x.ClubId).ToHashSet();
        query = query.Where(x => visibleClubIds.Contains(x.ClubId));
    }

    if (clubId.HasValue)
    {
        query = query.Where(x => x.ClubId == clubId);
    }

    if (!string.IsNullOrWhiteSpace(status))
    {
        query = query.Where(x => x.Status == status);
    }

    if (from.HasValue)
    {
        query = query.Where(x => x.StartTimeUtc >= from.Value);
    }

    if (to.HasValue)
    {
        query = query.Where(x => x.StartTimeUtc <= to.Value);
    }

    var rows = await query.OrderBy(x => x.StartTimeUtc).ToListAsync(cancellationToken);
    return Results.Ok(rows.Select(ToResponse));
});

activities.MapGet("/{id:int}", async (
    int id,
    ActivityDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var activity = await db.Activities
        .Include(x => x.Participants)
        .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (activity is null)
    {
        return Results.NotFound();
    }

    if (!CanReviewAllActivities(user))
    {
        var access = (await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken))
            .FirstOrDefault(x => x.ClubId == activity.ClubId && x.CanView);
        if (access is null)
        {
            return Results.Forbid();
        }
    }

    return Results.Ok(ToResponse(activity));
});

app.MapGet("/api/clubs/{clubId:int}/activities", async (
    int clubId,
    ActivityDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    if (!CanReviewAllActivities(user))
    {
        var access = (await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken))
            .FirstOrDefault(x => x.ClubId == clubId && x.CanView);
        if (access is null)
        {
            return Results.Forbid();
        }
    }

    var rows = await db.Activities
        .Include(x => x.Participants)
        .Where(x => x.ClubId == clubId)
        .OrderBy(x => x.StartTimeUtc)
        .ToListAsync(cancellationToken);
    return Results.Ok(rows.Select(ToResponse));
}).RequireAuthorization(AuthPolicies.BusinessAccess).WithTags("Activities");

activities.MapPost("/", async (
    CreateActivityRequest request,
    ActivityDbContext db,
    IEventBus eventBus,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    if (request.EndTimeUtc <= request.StartTimeUtc)
    {
        return Results.BadRequest(new { message = "End time must be after start time." });
    }

    var access = (await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken))
        .FirstOrDefault(x => x.ClubId == request.ClubId && x.CanManage);
    if (access is null)
    {
        return Results.Forbid();
    }

    if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Location))
    {
        return Results.BadRequest(new { message = "Activity title and location are required." });
    }

    var activity = new ClubActivity
    {
        ClubId = request.ClubId,
        ClubName = access.ClubName,
        Title = request.Title.Trim(),
        Description = request.Description.Trim(),
        StartTimeUtc = request.StartTimeUtc,
        EndTimeUtc = request.EndTimeUtc,
        Location = request.Location.Trim(),
        CreatedByUserId = user.GetUserId()
    };

    db.Activities.Add(activity);
    await db.SaveChangesAsync(cancellationToken);

    await eventBus.PublishAsync(new ActivityCreatedEvent(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        activity.Id,
        activity.ClubId,
        activity.ClubName,
        activity.Title,
        activity.StartTimeUtc), EventRoutingKeys.ActivityCreated, cancellationToken);

    return Results.Created($"/api/activities/{activity.Id}", ToResponse(activity));
});

activities.MapPut("/{id:int}", async (
    int id,
    UpdateActivityRequest request,
    ActivityDbContext db,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var activity = await db.Activities.Include(x => x.Participants).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (activity is null)
    {
        return Results.NotFound();
    }

    if (!await CanManageClubAsync(activity.ClubId, clubAccess, httpContext, cancellationToken))
    {
        return Results.Forbid();
    }

    if (request.EndTimeUtc <= request.StartTimeUtc)
    {
        return Results.BadRequest(new { message = "End time must be after start time." });
    }

    activity.Title = request.Title.Trim();
    activity.Description = request.Description.Trim();
    activity.StartTimeUtc = request.StartTimeUtc;
    activity.EndTimeUtc = request.EndTimeUtc;
    activity.Location = request.Location.Trim();
    activity.Status = request.Status.Trim();
    activity.UpdatedAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(ToResponse(activity));
});

activities.MapPost("/{id:int}/participants", async (
    int id,
    RegisterActivityParticipantRequest request,
    ActivityDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var activity = await db.Activities.Include(x => x.Participants).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (activity is null)
    {
        return Results.NotFound();
    }

    var userId = request.UserId ?? user.GetUserId();
    var access = (await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken))
        .FirstOrDefault(x => x.ClubId == activity.ClubId && x.CanView);
    if (access is null || (userId != user.GetUserId() && !access.CanManage))
    {
        return Results.Forbid();
    }

    if (activity.Status == ActivityStatuses.Completed)
    {
        return Results.BadRequest(new { message = "Completed activities no longer accept participants." });
    }

    if (activity.Participants.Any(x => x.UserId == userId))
    {
        return Results.Conflict(new { message = "Participant is already registered for this activity." });
    }

    activity.Participants.Add(new ActivityParticipant
    {
        UserId = userId,
        FullName = string.IsNullOrWhiteSpace(request.FullName) ? user.GetDisplayName() : request.FullName.Trim()
    });
    await db.SaveChangesAsync();
    return Results.Ok(ToResponse(activity));
});

activities.MapPatch("/{id:int}/complete", async (
    int id,
    ActivityDbContext db,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var activity = await db.Activities.Include(x => x.Participants).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (activity is null)
    {
        return Results.NotFound();
    }

    if (!await CanManageClubAsync(activity.ClubId, clubAccess, httpContext, cancellationToken))
    {
        return Results.Forbid();
    }

    activity.Status = ActivityStatuses.Completed;
    activity.UpdatedAtUtc = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(ToResponse(activity));
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ActivityDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseStartup");
    await db.EnsureCreatedWithRetryAsync(logger);
    await ActivitySeeder.SeedAsync(db);
}

app.Run();

static bool CanReviewAllActivities(ClaimsPrincipal user)
{
    return user.IsInRole(AuthRoles.Admin)
        || user.IsInRole(AuthRoles.StudentAffairsAdmin);
}

static ActivityResponse ToResponse(ClubActivity activity) => new(
    activity.Id,
    activity.ClubId,
    activity.ClubName,
    activity.Title,
    activity.Description,
    activity.StartTimeUtc,
    activity.EndTimeUtc,
    activity.Location,
    activity.Status,
    activity.CreatedByUserId,
    activity.CreatedAtUtc,
    activity.Participants
        .OrderBy(x => x.RegisteredAtUtc)
        .Select(x => new ActivityParticipantResponse(
            x.Id,
            x.UserId,
            x.FullName,
            x.AttendanceStatus,
            x.RegisteredAtUtc))
        .ToArray());

static async Task<bool> CanManageClubAsync(
    int clubId,
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken)
{
    var access = await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken);
    return access.Any(x => x.ClubId == clubId && x.CanManage);
}
