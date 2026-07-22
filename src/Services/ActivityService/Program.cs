using System.Security.Claims;
using ActivityService.Contracts;
using ActivityService.Data;
using ActivityService.Infrastructure;
using ActivityService.Models;
using ActivityService.Services;
using ClubReportHub.Shared.Auth;
using ClubReportHub.Shared.Data;
using ClubReportHub.Shared.Events;
using ClubReportHub.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ActivityDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddClubReportJwt(builder.Configuration);
builder.Services.AddClubAccessClient(builder.Configuration);
builder.Services.AddScoped<MemberActivityStatisticsService>();
builder.Services.AddHttpClient<ClubMemberRosterClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Services:ClubService:BaseUrl"] ?? "http://localhost:5102/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
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
    var query = db.Activities
        .Include(x => x.Participants)
        .Include(x => x.Attendances)
        .AsQueryable();
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
        .Include(x => x.Attendances)
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
        .Include(x => x.Attendances)
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
    var meetingDays = NormalizeMeetingDays(request.MeetingDays);
    if (meetingDays.Count == 0 && (!request.StartTimeUtc.HasValue || !request.EndTimeUtc.HasValue))
    {
        return Results.BadRequest(new { message = "Select at least one weekly meeting day." });
    }

    if (request.StartTimeUtc.HasValue && request.EndTimeUtc.HasValue
        && request.EndTimeUtc <= request.StartTimeUtc)
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

    var anchorStart = request.StartTimeUtc ?? DateTimeOffset.UtcNow;
    var anchorEnd = request.EndTimeUtc ?? anchorStart.AddHours(1);
    var activity = new ClubActivity
    {
        ClubId = request.ClubId,
        ClubName = access.ClubName,
        Title = request.Title.Trim(),
        Description = request.Description?.Trim() ?? string.Empty,
        StartTimeUtc = anchorStart,
        EndTimeUtc = anchorEnd,
        MeetingDaysCsv = string.Join(',', meetingDays),
        Location = request.Location.Trim(),
        CreatedByUserId = user.GetUserId()
    };

    db.Activities.Add(activity);
    await db.SaveChangesAsync(cancellationToken);

    var recipientUserIds = access.ManagerUserIds
        .Concat(access.MemberUserIds ?? [])
        .Append(user.GetUserId())
        .Where(id => id > 0)
        .Distinct()
        .ToArray();

    await eventBus.PublishAsync(new ActivityCreatedEvent(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        activity.Id,
        activity.ClubId,
        activity.ClubName,
        activity.Title,
        activity.StartTimeUtc,
        recipientUserIds), EventRoutingKeys.ActivityCreated, cancellationToken);

    return Results.Created($"/api/activities/{activity.Id}", ToResponse(activity));
});

activities.MapPost("/from-approved-report", async (
    CreateActivityFromApprovedReportRequest request,
    ActivityDbContext db,
    IEventBus eventBus,
    ClubMemberRosterClient rosterClient,
    ClaimsPrincipal user,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    if (request.ReportId <= 0 || request.ReportDetailId <= 0 || request.ClubId <= 0
        || string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Location))
    {
        return Results.BadRequest(new { message = "The approved report does not contain valid event information." });
    }

    var existing = await db.Activities
        .Include(x => x.Participants)
        .Include(x => x.Attendances)
        .FirstOrDefaultAsync(x => x.SourceReportId == request.ReportId, cancellationToken);
    if (existing is not null)
    {
        return Results.Ok(ToResponse(existing));
    }

    var localStart = new DateTimeOffset(
        request.ActivityDate.ToDateTime(new TimeOnly(9, 0)),
        WeeklyAttendanceRules.VietnamOffset);
    var activity = new ClubActivity
    {
        SourceReportId = request.ReportId,
        SourceReportDetailId = request.ReportDetailId,
        ClubId = request.ClubId,
        ClubName = request.ClubName.Trim(),
        Title = request.Title.Trim(),
        Description = request.Description?.Trim() ?? string.Empty,
        StartTimeUtc = localStart.ToUniversalTime(),
        EndTimeUtc = localStart.AddHours(2).ToUniversalTime(),
        MeetingDaysCsv = string.Empty,
        Location = request.Location.Trim(),
        CreatedByUserId = user.GetUserId()
    };

    db.Activities.Add(activity);
    await db.SaveChangesAsync(cancellationToken);

    IReadOnlyCollection<int> recipientUserIds = [];
    try
    {
        var roster = await rosterClient.GetAsync(
            activity.ClubId,
            activity.StartTimeUtc,
            null,
            1,
            100,
            httpContext.GetBearerToken(),
            cancellationToken);
        recipientUserIds = roster.Items.Select(x => x.UserId).Where(x => x > 0).Distinct().ToArray();
    }
    catch (HttpRequestException)
    {
        // Publishing the approved event must not fail only because notification recipients could not be resolved.
    }

    if (recipientUserIds.Count > 0)
    {
        await eventBus.PublishAsync(new ActivityCreatedEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            activity.Id,
            activity.ClubId,
            activity.ClubName,
            activity.Title,
            activity.StartTimeUtc,
            recipientUserIds), EventRoutingKeys.ActivityCreated, cancellationToken);
    }

    return Results.Created($"/api/activities/{activity.Id}", ToResponse(activity));
}).RequireAuthorization(AuthPolicies.StudentAffairsAdministration);

activities.MapPut("/{id:int}", async (
    int id,
    UpdateActivityRequest request,
    ActivityDbContext db,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var activity = await db.Activities
        .Include(x => x.Participants)
        .Include(x => x.Attendances)
        .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (activity is null)
    {
        return Results.NotFound();
    }

    if (!await CanManageClubAsync(activity.ClubId, clubAccess, httpContext, cancellationToken))
    {
        return Results.Forbid();
    }

    var meetingDays = NormalizeMeetingDays(request.MeetingDays);
    if (meetingDays.Count == 0 && (!request.StartTimeUtc.HasValue || !request.EndTimeUtc.HasValue))
    {
        return Results.BadRequest(new { message = "Select at least one weekly meeting day." });
    }

    if (request.StartTimeUtc.HasValue && request.EndTimeUtc.HasValue
        && request.EndTimeUtc <= request.StartTimeUtc)
    {
        return Results.BadRequest(new { message = "End time must be after start time." });
    }

    activity.Title = request.Title.Trim();
    activity.Description = request.Description?.Trim() ?? string.Empty;
    activity.StartTimeUtc = request.StartTimeUtc ?? activity.StartTimeUtc;
    activity.EndTimeUtc = request.EndTimeUtc ?? activity.EndTimeUtc;
    activity.MeetingDaysCsv = string.Join(',', meetingDays);
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
    var activity = await db.Activities
        .Include(x => x.Participants)
        .Include(x => x.Attendances)
        .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
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

activities.MapPost("/{id:int}/check-in", async (
    int id,
    ActivityDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var activity = await db.Activities
        .Include(x => x.Participants)
        .Include(x => x.Attendances)
        .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (activity is null)
    {
        return Results.NotFound();
    }

    if (activity.Status is ActivityStatuses.Completed or ActivityStatuses.Cancelled)
    {
        return Results.BadRequest(new { message = "This activity is no longer accepting attendance." });
    }

    var access = (await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken))
        .FirstOrDefault(x => x.ClubId == activity.ClubId && x.CanView);
    if (access is null)
    {
        return Results.Forbid();
    }

    var meetingDays = GetMeetingDays(activity.MeetingDaysCsv);
    if (meetingDays.Count == 0)
    {
        return Results.BadRequest(new { message = "This activity does not have a weekly attendance schedule." });
    }

    var userId = user.GetUserId();
    var attendanceDate = WeeklyAttendanceRules.GetVietnamDate(DateTimeOffset.UtcNow);
    var checkInError = WeeklyAttendanceRules.ValidateCheckIn(
        meetingDays,
        attendanceDate,
        activity.Attendances.Where(x => x.UserId == userId).Select(x => x.AttendanceDate));
    if (checkInError is not null)
    {
        return checkInError.Contains("already", StringComparison.OrdinalIgnoreCase)
            ? Results.Conflict(new { message = checkInError })
            : Results.BadRequest(new { message = checkInError });
    }

    var fullName = user.GetDisplayName();
    activity.Attendances.Add(new ActivityAttendance
    {
        UserId = userId,
        FullName = fullName,
        AttendanceDate = attendanceDate,
        Status = AttendanceStatuses.Present,
        CheckedInAtUtc = DateTimeOffset.UtcNow,
        CheckedInByUserId = userId
    });

    var participant = activity.Participants.FirstOrDefault(x => x.UserId == userId);
    if (participant is null)
    {
        activity.Participants.Add(new ActivityParticipant
        {
            UserId = userId,
            FullName = fullName,
            AttendanceStatus = AttendanceStatuses.Attended
        });
    }
    else
    {
        participant.AttendanceStatus = AttendanceStatuses.Attended;
    }

    try
    {
        await db.SaveChangesAsync(cancellationToken);
    }
    catch (DbUpdateException)
    {
        if (await db.ActivityAttendances.AsNoTracking()
            .AnyAsync(x => x.ActivityId == id && x.UserId == userId && x.AttendanceDate == attendanceDate, cancellationToken))
        {
            return Results.Conflict(new { message = "You have already checked in for this activity today." });
        }
        throw;
    }
    return Results.Ok(ToResponse(activity));
});

activities.MapGet("/{id:int}/my-attendance", async (
    int id,
    int page,
    int pageSize,
    ActivityDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var activity = await db.Activities.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (activity is null) return Results.NotFound();

    var access = (await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken))
        .FirstOrDefault(x => x.ClubId == activity.ClubId && x.CanView);
    if (access is null && !CanReviewAllActivities(user)) return Results.Forbid();

    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 100);
    var userId = user.GetUserId();
    var meetingDays = GetMeetingDays(activity.MeetingDaysCsv);
    var vietnamDate = WeeklyAttendanceRules.GetVietnamDate(DateTimeOffset.UtcNow);
    var scheduleStart = DateOnly.FromDateTime(activity.StartTimeUtc.ToOffset(WeeklyAttendanceRules.VietnamOffset).DateTime);
    var scheduledDays = WeeklyAttendanceRules.CountScheduledDays(meetingDays, scheduleStart, vietnamDate);
    var query = db.ActivityAttendances.AsNoTracking()
        .Where(x => x.ActivityId == id && x.UserId == userId)
        .OrderByDescending(x => x.AttendanceDate)
        .ThenByDescending(x => x.CheckedInAtUtc);
    var totalItems = await query.CountAsync(cancellationToken);
    var attendedDays = await db.ActivityAttendances.AsNoTracking()
        .Where(x => x.ActivityId == id && x.UserId == userId && x.Status == AttendanceStatuses.Present)
        .Select(x => x.AttendanceDate)
        .Distinct()
        .CountAsync(cancellationToken);
    var history = await query.Skip((page - 1) * pageSize).Take(pageSize)
        .Select(x => new ActivityAttendanceResponse(
            x.Id, x.UserId, x.FullName, x.AttendanceDate, x.Status, x.Note, x.CheckedInAtUtc, x.CheckedInByUserId))
        .ToListAsync(cancellationToken);
    var alreadyCheckedIn = await db.ActivityAttendances.AsNoTracking()
        .AnyAsync(x => x.ActivityId == id && x.UserId == userId && x.AttendanceDate == vietnamDate, cancellationToken);
    var scheduledToday = WeeklyAttendanceRules.IsScheduledDay(meetingDays, vietnamDate);
    var canCheckIn = scheduledToday
        && !alreadyCheckedIn
        && activity.Status is not ActivityStatuses.Completed and not ActivityStatuses.Cancelled;

    return Results.Ok(new MyWeeklyAttendanceResponse(
        activity.Id, activity.Title, meetingDays, vietnamDate, scheduledToday, alreadyCheckedIn, canCheckIn,
        scheduledDays, attendedDays, WeeklyAttendanceRules.CalculateRate(attendedDays, scheduledDays),
        history, page, pageSize, totalItems,
        totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)pageSize)));
});

app.MapPost("/api/activities/clubs/{clubId:int}/member-statistics", async (
    int clubId,
    MemberStatisticsQuery request,
    MemberActivityStatisticsService statistics,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    if (!await CanManageClubOrReviewAllAsync(clubId, user, clubAccess, httpContext, cancellationToken))
    {
        return Results.Forbid();
    }

    if (request.Members is null || request.Members.Count > 500 || request.Members.Any(x => x.UserId <= 0))
    {
        return Results.BadRequest(new { message = "Provide between 0 and 500 valid members." });
    }

    var result = await statistics.GetBatchAsync(clubId, request.Members, DateTimeOffset.UtcNow, cancellationToken);
    return Results.Ok(result.Values.OrderBy(x => x.UserId));
}).RequireAuthorization(AuthPolicies.BusinessAccess).WithTags("Member Activity Statistics");

app.MapPost("/api/activities/clubs/{clubId:int}/member-statistics/detail", async (
    int clubId,
    MemberStatisticsDetailQuery request,
    MemberActivityStatisticsService statistics,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    if (!await CanManageClubOrReviewAllAsync(clubId, user, clubAccess, httpContext, cancellationToken))
    {
        return Results.Forbid();
    }

    if (request.UserId <= 0 || request.Page < 1 || request.PageSize is < 1 or > 100)
    {
        return Results.BadRequest(new { message = "The member and pagination values are invalid." });
    }

    return Results.Ok(await statistics.GetDetailAsync(clubId, request, DateTimeOffset.UtcNow, cancellationToken));
}).RequireAuthorization(AuthPolicies.BusinessAccess).WithTags("Member Activity Statistics");

app.MapGet("/api/clubs/{clubId:int}/activities/{activityId:int}/attendance", async (
    int clubId,
    int activityId,
    string? search,
    int page,
    int pageSize,
    ActivityDbContext db,
    ClubMemberRosterClient rosterClient,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    if (!await CanManageClubOrReviewAllAsync(clubId, user, clubAccess, httpContext, cancellationToken))
    {
        return Results.Forbid();
    }

    var activity = await db.Activities.AsNoTracking()
        .FirstOrDefaultAsync(x => x.Id == activityId && x.ClubId == clubId, cancellationToken);
    if (activity is null) return Results.NotFound(new { message = "Activity not found in this club." });

    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 100);
    ClubMemberRosterPage roster;
    try
    {
        roster = await rosterClient.GetAsync(clubId, activity.StartTimeUtc, search, page, pageSize, httpContext.GetBearerToken(), cancellationToken);
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var pageUserIds = roster.Items.Select(x => x.UserId).ToArray();
    var attendanceRows = await db.ActivityAttendances.AsNoTracking()
        .Where(x => x.ActivityId == activityId && pageUserIds.Contains(x.UserId))
        .OrderByDescending(x => x.UpdatedAtUtc ?? x.CreatedAtUtc)
        .ToListAsync(cancellationToken);
    var attendanceByUser = attendanceRows.GroupBy(x => x.UserId).ToDictionary(x => x.Key, x => x.First());

    var allStatuses = await db.ActivityAttendances.AsNoTracking()
        .Where(x => x.ActivityId == activityId)
        .GroupBy(x => x.Status)
        .Select(x => new { Status = x.Key, Count = x.Select(a => a.UserId).Distinct().Count() })
        .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken);

    var items = roster.Items.Select(member =>
    {
        attendanceByUser.TryGetValue(member.UserId, out var attendance);
        return new AttendanceMemberResponse(
            member.Id, member.UserId, member.FullName, member.Email, member.PhoneNumber, member.Role, member.JoinedAtUtc,
            attendance?.Status ?? AttendanceStatuses.NotMarked,
            attendance?.Note, attendance?.CheckedInAtUtc, attendance?.CheckedInByUserId);
    }).ToArray();
    var present = allStatuses.GetValueOrDefault(AttendanceStatuses.Present);
    var absent = allStatuses.GetValueOrDefault(AttendanceStatuses.Absent);
    var excused = allStatuses.GetValueOrDefault(AttendanceStatuses.Excused);
    var late = allStatuses.GetValueOrDefault(AttendanceStatuses.Late);
    var notMarked = Math.Max(0, roster.TotalItems - present - absent - excused - late);
    return Results.Ok(new ActivityAttendanceManagementResponse(
        activity.Id, activity.ClubId, activity.Title, activity.StartTimeUtc, activity.Status,
        items, roster.Page, roster.PageSize, roster.TotalItems, roster.TotalPages,
        present, absent, excused, late, notMarked));
}).RequireAuthorization(AuthPolicies.BusinessAccess).WithTags("Activity Attendance");

app.MapPut("/api/clubs/{clubId:int}/activities/{activityId:int}/attendance/{memberId:int}", async (
    int clubId,
    int activityId,
    int memberId,
    UpdateMemberAttendanceRequest request,
    ActivityDbContext db,
    ClubMemberRosterClient rosterClient,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var validation = ValidateAttendance(request.Status, request.Note);
    if (validation is not null) return Results.BadRequest(new { message = validation });
    if (!await CanManageClubOrReviewAllAsync(clubId, user, clubAccess, httpContext, cancellationToken)) return Results.Forbid();

    var activity = await db.Activities.FirstOrDefaultAsync(x => x.Id == activityId && x.ClubId == clubId, cancellationToken);
    if (activity is null) return Results.NotFound(new { message = "Activity not found in this club." });
    if (activity.Status == ActivityStatuses.Cancelled) return Results.BadRequest(new { message = "Attendance cannot be recorded for a cancelled activity." });

    IReadOnlyCollection<ClubMemberRosterItem> members;
    try
    {
        members = await rosterClient.ResolveAsync(clubId, [memberId], activity.StartTimeUtc, httpContext.GetBearerToken(), cancellationToken);
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    var member = members.SingleOrDefault();
    if (member is null) return Results.BadRequest(new { message = "The member is not eligible for this activity." });

    var status = AttendanceStatuses.Normalize(request.Status);
    var row = await db.ActivityAttendances
        .Where(x => x.ActivityId == activityId && x.UserId == member.UserId)
        .OrderBy(x => x.Id)
        .FirstOrDefaultAsync(cancellationToken);
    row ??= new ActivityAttendance
    {
        ActivityId = activityId,
        UserId = member.UserId,
        FullName = member.FullName,
        AttendanceDate = DateOnly.FromDateTime(activity.StartTimeUtc.UtcDateTime)
    };
    if (row.Id == 0) db.ActivityAttendances.Add(row);
    ApplyAttendance(row, status, request.Note, user.GetUserId(), activity.StartTimeUtc);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new AttendanceMemberResponse(member.Id, member.UserId, member.FullName, member.Email, member.PhoneNumber, member.Role, member.JoinedAtUtc, row.Status, row.Note, row.CheckedInAtUtc, row.CheckedInByUserId));
}).RequireAuthorization(AuthPolicies.BusinessAccess).WithTags("Activity Attendance");

app.MapPut("/api/clubs/{clubId:int}/activities/{activityId:int}/attendance", async (
    int clubId,
    int activityId,
    BulkUpdateAttendanceRequest request,
    ActivityDbContext db,
    ClubMemberRosterClient rosterClient,
    ClaimsPrincipal user,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var invalid = AttendanceManagementRules.ValidateBulk(request.Items);
    if (invalid is not null) return Results.BadRequest(new { message = invalid });
    if (!await CanManageClubOrReviewAllAsync(clubId, user, clubAccess, httpContext, cancellationToken)) return Results.Forbid();

    var activity = await db.Activities.FirstOrDefaultAsync(x => x.Id == activityId && x.ClubId == clubId, cancellationToken);
    if (activity is null) return Results.NotFound(new { message = "Activity not found in this club." });
    if (activity.Status == ActivityStatuses.Cancelled) return Results.BadRequest(new { message = "Attendance cannot be recorded for a cancelled activity." });

    IReadOnlyCollection<ClubMemberRosterItem> members;
    try
    {
        members = await rosterClient.ResolveAsync(clubId, request.Items.Select(x => x.MemberId).ToArray(), activity.StartTimeUtc, httpContext.GetBearerToken(), cancellationToken);
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    if (members.Count != request.Items.Count)
        return Results.BadRequest(new { message = "One or more members do not belong to this club or joined after the activity started." });

    IDbContextTransaction? transaction = null;
    try
    {
        if (db.Database.IsRelational()) transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var userIds = members.Select(x => x.UserId).ToArray();
        var existing = await db.ActivityAttendances
            .Where(x => x.ActivityId == activityId && userIds.Contains(x.UserId))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var rowsByUser = existing.GroupBy(x => x.UserId).ToDictionary(x => x.Key, x => x.First());
        var requestByMember = request.Items.ToDictionary(x => x.MemberId);
        foreach (var member in members)
        {
            if (!rowsByUser.TryGetValue(member.UserId, out var row))
            {
                row = new ActivityAttendance
                {
                    ActivityId = activityId,
                    UserId = member.UserId,
                    FullName = member.FullName,
                    AttendanceDate = DateOnly.FromDateTime(activity.StartTimeUtc.UtcDateTime)
                };
                db.ActivityAttendances.Add(row);
            }
            var item = requestByMember[member.Id];
            ApplyAttendance(row, AttendanceStatuses.Normalize(item.Status), item.Note, user.GetUserId(), activity.StartTimeUtc);
        }
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Attendance updated for {Count} members in activity {ActivityId}, club {ClubId}, by user {UserId}", members.Count, activityId, clubId, user.GetUserId());
        return Results.Ok(new { updated = members.Count });
    }
    catch
    {
        if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
        throw;
    }
    finally
    {
        if (transaction is not null) await transaction.DisposeAsync();
    }
}).RequireAuthorization(AuthPolicies.BusinessAccess).WithTags("Activity Attendance");

activities.MapPatch("/{id:int}/complete", async (
    int id,
    ActivityDbContext db,
    HttpContext httpContext,
    ClubAccessClient clubAccess,
    CancellationToken cancellationToken) =>
{
    var activity = await db.Activities
        .Include(x => x.Participants)
        .Include(x => x.Attendances)
        .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
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
    await ActivitySchemaUpgrader.ApplyAsync(db);
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
    GetMeetingDays(activity.MeetingDaysCsv),
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
        .ToArray(),
    activity.Attendances
        .OrderByDescending(x => x.AttendanceDate)
        .ThenBy(x => x.FullName)
        .Select(x => new ActivityAttendanceResponse(
            x.Id,
            x.UserId,
            x.FullName,
            x.AttendanceDate,
            x.Status,
            x.Note,
            x.CheckedInAtUtc,
            x.CheckedInByUserId))
        .ToArray());

static IReadOnlyList<int> NormalizeMeetingDays(IEnumerable<int>? meetingDays) =>
    meetingDays?
        .Where(day => day is >= 1 and <= 7)
        .Distinct()
        .OrderBy(day => day)
        .ToArray()
    ?? [];

static IReadOnlyList<int> GetMeetingDays(string? meetingDaysCsv) =>
    string.IsNullOrWhiteSpace(meetingDaysCsv)
        ? []
        : NormalizeMeetingDays(meetingDaysCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var day) ? day : 0));

static async Task<bool> CanManageClubAsync(
    int clubId,
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken)
{
    var access = await clubAccess.GetMyAccessAsync(httpContext.GetBearerToken(), cancellationToken);
    return access.Any(x => x.ClubId == clubId && x.CanManage);
}

static async Task<bool> CanManageClubOrReviewAllAsync(
    int clubId,
    ClaimsPrincipal user,
    ClubAccessClient clubAccess,
    HttpContext httpContext,
    CancellationToken cancellationToken)
{
    return CanReviewAllActivities(user)
        || await CanManageClubAsync(clubId, clubAccess, httpContext, cancellationToken);
}

static string? ValidateAttendance(string? status, string? note)
{
    return AttendanceManagementRules.ValidateEntry(status, note);
}

static void ApplyAttendance(
    ActivityAttendance row,
    string status,
    string? note,
    int checkedByUserId,
    DateTimeOffset activityStartTimeUtc)
{
    row.Status = status;
    row.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    row.UpdatedAtUtc = DateTimeOffset.UtcNow;
    if (status == AttendanceStatuses.NotMarked)
    {
        row.CheckedInAtUtc = null;
        row.CheckedInByUserId = null;
    }
    else
    {
        row.CheckedInByUserId = checkedByUserId;
        row.CheckedInAtUtc = DateTimeOffset.UtcNow >= activityStartTimeUtc ? DateTimeOffset.UtcNow : null;
    }
}
