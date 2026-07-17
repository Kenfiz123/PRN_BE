using ClubReportHub.Shared.Auth;
using ClubReportHub.Shared.Data;
using ClubReportHub.Shared.Events;
using ClubReportHub.Shared.Messaging;
using ClubService.Contracts;
using ClubService.Data;
using ClubService.Models;
using Microsoft.EntityFrameworkCore;
using System.Net.Mail;
using System.Security.Claims;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ClubDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddClubReportJwt(builder.Configuration);
builder.Services.AddRabbitMqEventBus(builder.Configuration);
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
app.MapGet("/", () => Results.Ok(new { service = "Club Service", status = "running" }));

var clubs = app.MapGroup("/api/clubs").WithTags("Clubs").RequireAuthorization(AuthPolicies.AdminOrClubManagerOrMember);

clubs.MapGet("/", async (string? search, bool? active, ClubDbContext db, ClaimsPrincipal user) =>
{
    var query = db.Clubs
        .Include(x => x.ManagerAssignments)
        .Include(x => x.Memberships)
        .AsQueryable();
    if (!string.IsNullOrWhiteSpace(search))
    {
        query = query.Where(x => x.Code.Contains(search) || x.Name.Contains(search));
    }

    if (active.HasValue)
    {
        query = query.Where(x => x.IsActive == active);
    }

    var result = await query.OrderBy(x => x.Name).ToListAsync();
    if (IsAdmin(user))
    {
        return Results.Ok(result.Select(ToResponse));
    }

    var userId = user.GetUserId();
    var managedClubIds = result
        .Where(x => x.ManagerAssignments.Any(m => m.ManagerUserId == userId && m.IsActive))
        .Select(x => x.Id)
        .ToHashSet();
    return Results.Ok(result.Select(club => managedClubIds.Contains(club.Id) ? ToResponse(club) : ToDirectoryResponse(club)));
});

clubs.MapGet("/me/managed", async (ClaimsPrincipal user, ClubDbContext db) =>
{
    var userId = user.GetUserId();
    var clubsForManager = await db.Clubs
        .Include(x => x.ManagerAssignments)
        .Include(x => x.Memberships)
        .Where(x => x.ManagerAssignments.Any(m => m.ManagerUserId == userId && m.IsActive))
        .OrderBy(x => x.Name)
        .ToListAsync();
    return Results.Ok(clubsForManager.Select(ToResponse));
});

clubs.MapGet("/me/memberships", async (ClaimsPrincipal user, ClubDbContext db) =>
{
    var userId = user.GetUserId();
    var memberships = await db.ClubMemberships
        .Include(x => x.Club)
        .Where(x => x.UserId == userId)
        .OrderByDescending(x => x.RequestedAtUtc)
        .ToListAsync();
    return Results.Ok(memberships.Select(ToMembershipResponse));
});

clubs.MapGet("/me/access", async (ClaimsPrincipal user, ClubDbContext db) =>
{
    var userId = user.GetUserId();
    var managedClubs = await db.ClubManagerAssignments
        .AsNoTracking()
        .Where(x => x.ManagerUserId == userId && x.IsActive)
        .Select(x => new { x.ClubId, x.Club.Name })
        .ToListAsync();
    var memberships = await db.ClubMemberships
        .AsNoTracking()
        .Where(x => x.UserId == userId && x.Status == ClubMembershipStatuses.Approved)
        .Select(x => new { x.ClubId, x.Club.Name, x.Role })
        .ToListAsync();

    var clubIds = managedClubs.Select(x => x.ClubId)
        .Concat(memberships.Select(x => x.ClubId))
        .Distinct()
        .OrderBy(x => x)
        .ToArray();
    var activeManagers = await db.ClubManagerAssignments
        .AsNoTracking()
        .Where(x => clubIds.Contains(x.ClubId) && x.IsActive)
        .Select(x => new { x.ClubId, x.ManagerUserId })
        .ToListAsync();
    var access = clubIds.Select(clubId =>
    {
        var managed = managedClubs.FirstOrDefault(x => x.ClubId == clubId);
        var membership = memberships.FirstOrDefault(x => x.ClubId == clubId);
        return new ClubAccessResponse(
            clubId,
            managed?.Name ?? membership?.Name ?? string.Empty,
            managed is not null,
            string.Equals(membership?.Role, ClubMemberRoles.Treasurer, StringComparison.OrdinalIgnoreCase),
            membership is not null,
            activeManagers.Where(x => x.ClubId == clubId).Select(x => x.ManagerUserId).Distinct().ToArray());
    });

    return Results.Ok(access);
});

clubs.MapGet("/applications", async (ClubDbContext db) =>
{
    var applications = await db.ClubCreationApplications
        .OrderByDescending(x => x.SubmittedAtUtc)
        .ToListAsync();
    return Results.Ok(applications.Select(ToApplicationResponse));
}).RequireAuthorization(AuthPolicies.AdminOnly);

clubs.MapPost("/applications", async (
    CreateClubApplicationRequest request,
    ClaimsPrincipal user,
    ClubDbContext db) =>
{
    var userId = user.GetUserId();
    var code = request.Code.Trim().ToUpperInvariant();

    var validationError = ValidateClubApplication(request, code);
    if (validationError is not null)
    {
        return Results.BadRequest(new { message = validationError });
    }

    if (await db.ClubManagerAssignments.AnyAsync(x => x.ManagerUserId == userId && x.IsActive))
    {
        return Results.Conflict(new { message = "Each club owner can manage one club only." });
    }

    if (await db.Clubs.AnyAsync(x => x.Code == code))
    {
        return Results.Conflict(new { message = "Club code already exists." });
    }

    if (await db.ClubCreationApplications.AnyAsync(x => x.RequesterUserId == userId && x.Status == ClubApplicationStatuses.Submitted))
    {
        return Results.Conflict(new { message = "You already have a pending club creation application." });
    }

    var application = new ClubCreationApplication
    {
        RequesterUserId = userId,
        RequesterName = user.GetDisplayName(),
        Code = code,
        Name = request.Name.Trim(),
        Description = request.Description.Trim(),
        Purpose = request.Purpose.Trim(),
        Reason = request.Reason.Trim(),
        ContactEmail = request.ContactEmail.Trim(),
        ContactPhone = request.ContactPhone.Trim()
    };

    db.ClubCreationApplications.Add(application);
    await db.SaveChangesAsync();
    return Results.Created($"/api/clubs/applications/{application.Id}", ToApplicationResponse(application));
});

clubs.MapPost("/applications/{applicationId:int}/approve", async (
    int applicationId,
    ReviewClubApplicationRequest request,
    ClaimsPrincipal user,
    ClubDbContext db,
    IEventBus eventBus,
    CancellationToken cancellationToken) =>
{
    if (request.Note?.Trim().Length > 1000)
    {
        return Results.BadRequest(new { message = "Ghi chú xét duyệt không được vượt quá 1.000 ký tự." });
    }

    var application = await db.ClubCreationApplications.FirstOrDefaultAsync(x => x.Id == applicationId, cancellationToken);
    if (application is null)
    {
        return Results.NotFound();
    }

    if (application.Status != ClubApplicationStatuses.Submitted)
    {
        return Results.Conflict(new { message = "Application was already reviewed." });
    }

    if (await db.ClubManagerAssignments.AnyAsync(x => x.ManagerUserId == application.RequesterUserId && x.IsActive, cancellationToken))
    {
        return Results.Conflict(new { message = "Requester already owns a club." });
    }

    if (await db.Clubs.AnyAsync(x => x.Code == application.Code, cancellationToken))
    {
        return Results.Conflict(new { message = "Club code already exists." });
    }

    var club = new Club
    {
        Code = application.Code,
        Name = application.Name,
        Description = application.Description,
        ContactEmail = application.ContactEmail,
        ContactPhone = application.ContactPhone
    };
    club.ManagerAssignments.Add(new ClubManagerAssignment
    {
        ManagerUserId = application.RequesterUserId,
        ManagerName = application.RequesterName,
        IsActive = true
    });
    club.Memberships.Add(new ClubMembership
    {
        UserId = application.RequesterUserId,
        FullName = application.RequesterName,
        Role = ClubMemberRoles.Member,
        Status = ClubMembershipStatuses.Approved,
        ReviewedAtUtc = DateTimeOffset.UtcNow,
        ReviewedByUserId = user.GetUserId()
    });

    db.Clubs.Add(club);
    await db.SaveChangesAsync(cancellationToken);

    application.Status = ClubApplicationStatuses.Approved;
    application.ReviewNote = request.Note?.Trim();
    application.CreatedClubId = club.Id;
    application.ReviewedAtUtc = DateTimeOffset.UtcNow;
    application.ReviewedByUserId = user.GetUserId();
    await db.SaveChangesAsync(cancellationToken);

    await eventBus.PublishAsync(new ClubCreatedEvent(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        club.Id,
        club.Code,
        club.Name), EventRoutingKeys.ClubCreated, cancellationToken);

    return Results.Ok(ToApplicationResponse(application));
}).RequireAuthorization(AuthPolicies.AdminOnly);

clubs.MapPost("/applications/{applicationId:int}/reject", async (
    int applicationId,
    ReviewClubApplicationRequest request,
    ClaimsPrincipal user,
    ClubDbContext db) =>
{
    if (request.Note?.Trim().Length > 1000)
    {
        return Results.BadRequest(new { message = "Ghi chú xét duyệt không được vượt quá 1.000 ký tự." });
    }

    var application = await db.ClubCreationApplications.FirstOrDefaultAsync(x => x.Id == applicationId);
    if (application is null)
    {
        return Results.NotFound();
    }

    if (application.Status != ClubApplicationStatuses.Submitted)
    {
        return Results.Conflict(new { message = "Application was already reviewed." });
    }

    application.Status = ClubApplicationStatuses.Rejected;
    application.ReviewNote = request.Note?.Trim();
    application.ReviewedAtUtc = DateTimeOffset.UtcNow;
    application.ReviewedByUserId = user.GetUserId();
    await db.SaveChangesAsync();
    return Results.Ok(ToApplicationResponse(application));
}).RequireAuthorization(AuthPolicies.AdminOnly);

clubs.MapGet("/{id:int}", async (int id, ClubDbContext db, ClaimsPrincipal user) =>
{
    var club = await db.Clubs
        .Include(x => x.ManagerAssignments)
        .Include(x => x.Memberships)
        .FirstOrDefaultAsync(x => x.Id == id);
    if (club is null)
    {
        return Results.NotFound();
    }

    var canViewPrivateDetails = IsAdmin(user)
        || club.ManagerAssignments.Any(x => x.ManagerUserId == user.GetUserId() && x.IsActive);
    return Results.Ok(canViewPrivateDetails ? ToResponse(club) : ToDirectoryResponse(club));
});

clubs.MapGet("/manager/{managerUserId:int}", async (int managerUserId, ClubDbContext db, ClaimsPrincipal user) =>
{
    if (!IsAdmin(user) && managerUserId != user.GetUserId())
    {
        return Results.Forbid();
    }

    var clubsForManager = await db.Clubs
        .Include(x => x.ManagerAssignments)
        .Include(x => x.Memberships)
        .Where(x => x.ManagerAssignments.Any(m => m.ManagerUserId == managerUserId && m.IsActive))
        .OrderBy(x => x.Name)
        .ToListAsync();
    return Results.Ok(clubsForManager.Select(ToResponse));
});

clubs.MapPost("/", async (
    CreateClubRequest request,
    ClubDbContext db,
    IEventBus eventBus,
    CancellationToken cancellationToken) =>
{
    var code = request.Code.Trim().ToUpperInvariant();
    if (await db.Clubs.AnyAsync(x => x.Code == code, cancellationToken))
    {
        return Results.Conflict(new { message = "Club code already exists." });
    }

    var club = new Club
    {
        Code = code,
        Name = request.Name.Trim(),
        Description = request.Description.Trim(),
        ContactEmail = request.ContactEmail.Trim(),
        ContactPhone = request.ContactPhone.Trim()
    };
    db.Clubs.Add(club);
    await db.SaveChangesAsync(cancellationToken);

    await eventBus.PublishAsync(new ClubCreatedEvent(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        club.Id,
        club.Code,
        club.Name), EventRoutingKeys.ClubCreated, cancellationToken);

    return Results.Created($"/api/clubs/{club.Id}", ToResponse(club));
}).RequireAuthorization(AuthPolicies.AdminOnly);

clubs.MapPut("/{id:int}", async (int id, UpdateClubRequest request, ClubDbContext db) =>
{
    var club = await db.Clubs
        .Include(x => x.ManagerAssignments)
        .Include(x => x.Memberships)
        .FirstOrDefaultAsync(x => x.Id == id);
    if (club is null)
    {
        return Results.NotFound();
    }

    club.Name = request.Name.Trim();
    club.Description = request.Description.Trim();
    club.ContactEmail = request.ContactEmail.Trim();
    club.ContactPhone = request.ContactPhone.Trim();
    club.IsActive = request.IsActive;
    await db.SaveChangesAsync();
    return Results.Ok(ToResponse(club));
}).RequireAuthorization(AuthPolicies.AdminOnly);

clubs.MapDelete("/{id:int}", async (int id, ClubDbContext db, ClaimsPrincipal user) =>
{
    var club = await db.Clubs.FindAsync(id);
    if (club is null)
    {
        return Results.NotFound();
    }

    if (!IsAdmin(user) && !await UserOwnsClubAsync(db, id, user.GetUserId()))
    {
        return Results.Forbid();
    }

    db.Clubs.Remove(club);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

clubs.MapPost("/{id:int}/managers", async (int id, AssignManagerRequest request, ClubDbContext db) =>
{
    var club = await db.Clubs
        .Include(x => x.ManagerAssignments)
        .Include(x => x.Memberships)
        .FirstOrDefaultAsync(x => x.Id == id);
    if (club is null)
    {
        return Results.NotFound();
    }

    var managesAnotherClub = await db.ClubManagerAssignments
        .AnyAsync(x => x.ManagerUserId == request.ManagerUserId && x.ClubId != id && x.IsActive);
    if (managesAnotherClub)
    {
        return Results.Conflict(new { message = "Each club owner can manage one club only." });
    }

    foreach (var assignment in club.ManagerAssignments.Where(x => x.IsActive))
    {
        assignment.IsActive = false;
        assignment.EndedAtUtc = DateTimeOffset.UtcNow;
    }

    db.ClubManagerAssignments.Add(new ClubManagerAssignment
    {
        ClubId = id,
        ManagerUserId = request.ManagerUserId,
        ManagerName = request.ManagerName.Trim(),
        IsActive = true
    });

    var existingMembership = club.Memberships.FirstOrDefault(x => x.UserId == request.ManagerUserId);
    if (existingMembership is null)
    {
        club.Memberships.Add(new ClubMembership
        {
            ClubId = id,
            UserId = request.ManagerUserId,
            FullName = request.ManagerName.Trim(),
            Role = ClubMemberRoles.Member,
            Status = ClubMembershipStatuses.Approved,
            ReviewedAtUtc = DateTimeOffset.UtcNow
        });
    }
    else
    {
        existingMembership.FullName = request.ManagerName.Trim();
        existingMembership.Status = ClubMembershipStatuses.Approved;
        existingMembership.ReviewedAtUtc = DateTimeOffset.UtcNow;
    }

    await db.SaveChangesAsync();
    var updated = await db.Clubs
        .Include(x => x.ManagerAssignments)
        .Include(x => x.Memberships)
        .FirstAsync(x => x.Id == id);
    return Results.Ok(ToResponse(updated));
}).RequireAuthorization(AuthPolicies.AdminOnly);

clubs.MapPost("/{id:int}/join", async (
    int id,
    JoinClubRequest request,
    ClaimsPrincipal user,
    ClubDbContext db) =>
{
    var club = await db.Clubs
        .Include(x => x.ManagerAssignments)
        .Include(x => x.Memberships)
        .FirstOrDefaultAsync(x => x.Id == id && x.IsActive);
    if (club is null)
    {
        return Results.NotFound();
    }

    var userId = user.GetUserId();
    if (club.ManagerAssignments.Any(x => x.ManagerUserId == userId && x.IsActive))
    {
        return Results.Conflict(new { message = "Club owner is already attached to this club." });
    }

    var validationError = ValidateJoinRequest(request);
    if (validationError is not null)
    {
        return Results.BadRequest(new { message = validationError });
    }

    var existing = club.Memberships.FirstOrDefault(x => x.UserId == userId);
    if (existing is not null)
    {
        if (existing.Status == ClubMembershipStatuses.Rejected)
        {
            existing.Status = ClubMembershipStatuses.Pending;
            existing.Role = ClubMemberRoles.Member;
            existing.RequestMessage = request.Message?.Trim();
            existing.PersonalInfo = request.PersonalInfo.Trim();
            existing.Goals = request.Goals.Trim();
            existing.Reason = request.Reason.Trim();
            existing.ReviewNote = null;
            existing.RequestedAtUtc = DateTimeOffset.UtcNow;
            existing.ReviewedAtUtc = null;
            existing.ReviewedByUserId = null;
            await db.SaveChangesAsync();
            return Results.Ok(ToMembershipResponseWithClub(existing, club));
        }

        return Results.Conflict(new { message = $"Membership request already exists with status {existing.Status}." });
    }

    var membership = new ClubMembership
    {
        ClubId = id,
        UserId = userId,
        FullName = user.GetDisplayName(),
        RequestMessage = request.Message?.Trim(),
        PersonalInfo = request.PersonalInfo.Trim(),
        Goals = request.Goals.Trim(),
        Reason = request.Reason.Trim(),
        Role = ClubMemberRoles.Member,
        Status = ClubMembershipStatuses.Pending
    };
    db.ClubMemberships.Add(membership);
    await db.SaveChangesAsync();

    membership.Club = club;
    return Results.Created($"/api/clubs/memberships/{membership.Id}", ToMembershipResponse(membership));
});

clubs.MapGet("/{id:int}/memberships", async (int id, ClaimsPrincipal user, ClubDbContext db) =>
{
    if (!IsAdmin(user) && !await UserOwnsClubAsync(db, id, user.GetUserId()))
    {
        return Results.Forbid();
    }

    var memberships = await db.ClubMemberships
        .Include(x => x.Club)
        .Where(x => x.ClubId == id)
        .OrderBy(x => x.Status)
        .ThenBy(x => x.FullName)
        .ToListAsync();
    return Results.Ok(memberships.Select(ToMembershipResponse));
});

clubs.MapPost("/memberships/{membershipId:int}/approve", async (
    int membershipId,
    ReviewClubMembershipRequest request,
    ClaimsPrincipal user,
    ClubDbContext db) =>
{
    if (request.Note?.Trim().Length > 1000)
    {
        return Results.BadRequest(new { message = "Ghi chú xét duyệt không được vượt quá 1.000 ký tự." });
    }

    var membership = await db.ClubMemberships
        .Include(x => x.Club)
        .FirstOrDefaultAsync(x => x.Id == membershipId);
    if (membership is null)
    {
        return Results.NotFound();
    }

    if (!IsAdmin(user) && !await UserOwnsClubAsync(db, membership.ClubId, user.GetUserId()))
    {
        return Results.Forbid();
    }

    if (membership.Status != ClubMembershipStatuses.Pending)
    {
        return Results.Conflict(new { message = "Only pending membership requests can be approved." });
    }

    membership.Status = ClubMembershipStatuses.Approved;
    membership.Role = ClubMemberRoles.Member;
    membership.ReviewNote = request.Note?.Trim();
    membership.ReviewedAtUtc = DateTimeOffset.UtcNow;
    membership.ReviewedByUserId = user.GetUserId();
    await db.SaveChangesAsync();
    return Results.Ok(ToMembershipResponse(membership));
});

clubs.MapPost("/memberships/{membershipId:int}/reject", async (
    int membershipId,
    ReviewClubMembershipRequest request,
    ClaimsPrincipal user,
    ClubDbContext db) =>
{
    if (request.Note?.Trim().Length > 1000)
    {
        return Results.BadRequest(new { message = "Ghi chú xét duyệt không được vượt quá 1.000 ký tự." });
    }

    var membership = await db.ClubMemberships
        .Include(x => x.Club)
        .FirstOrDefaultAsync(x => x.Id == membershipId);
    if (membership is null)
    {
        return Results.NotFound();
    }

    if (!IsAdmin(user) && !await UserOwnsClubAsync(db, membership.ClubId, user.GetUserId()))
    {
        return Results.Forbid();
    }

    if (membership.Status != ClubMembershipStatuses.Pending)
    {
        return Results.Conflict(new { message = "Only pending membership requests can be rejected." });
    }

    membership.Status = ClubMembershipStatuses.Rejected;
    membership.Role = ClubMemberRoles.Member;
    membership.ReviewNote = request.Note?.Trim();
    membership.ReviewedAtUtc = DateTimeOffset.UtcNow;
    membership.ReviewedByUserId = user.GetUserId();
    await db.SaveChangesAsync();
    return Results.Ok(ToMembershipResponse(membership));
});

clubs.MapPost("/{id:int}/treasurers", async (
    int id,
    AssignTreasurerRequest request,
    ClaimsPrincipal user,
    ClubDbContext db) =>
{
    if (!IsAdmin(user) && !await UserOwnsClubAsync(db, id, user.GetUserId()))
    {
        return Results.Forbid();
    }

    var club = await db.Clubs
        .Include(x => x.Memberships)
        .FirstOrDefaultAsync(x => x.Id == id);
    if (club is null)
    {
        return Results.NotFound();
    }

    var membership = club.Memberships.FirstOrDefault(x => x.UserId == request.MemberUserId);
    if (membership is null || membership.Status != ClubMembershipStatuses.Approved)
    {
        return Results.BadRequest(new { message = "Treasurer must be an approved member of this club." });
    }

    if (membership.Role != ClubMemberRoles.Treasurer)
    {
        var treasurerCount = club.Memberships.Count(x => x.Role == ClubMemberRoles.Treasurer && x.Status == ClubMembershipStatuses.Approved);
        if (treasurerCount >= 2)
        {
            return Results.Conflict(new { message = "A club can have at most two treasurers." });
        }
    }

    membership.FullName = string.IsNullOrWhiteSpace(request.MemberName) ? membership.FullName : request.MemberName.Trim();
    membership.Role = ClubMemberRoles.Treasurer;
    membership.ReviewedAtUtc = DateTimeOffset.UtcNow;
    membership.ReviewedByUserId = user.GetUserId();
    await db.SaveChangesAsync();

    membership.Club = club;
    return Results.Ok(ToMembershipResponse(membership));
});

clubs.MapPost("/memberships/{membershipId:int}/member", async (
    int membershipId,
    ClaimsPrincipal user,
    ClubDbContext db) =>
{
    var membership = await db.ClubMemberships
        .Include(x => x.Club)
        .FirstOrDefaultAsync(x => x.Id == membershipId);
    if (membership is null)
    {
        return Results.NotFound();
    }

    if (!IsAdmin(user) && !await UserOwnsClubAsync(db, membership.ClubId, user.GetUserId()))
    {
        return Results.Forbid();
    }

    membership.Role = ClubMemberRoles.Member;
    membership.ReviewedAtUtc = DateTimeOffset.UtcNow;
    membership.ReviewedByUserId = user.GetUserId();
    await db.SaveChangesAsync();
    return Results.Ok(ToMembershipResponse(membership));
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseStartup");
    await db.ApplyMigrationsWithRetryAsync(logger);
    await ClubSeeder.SeedAsync(db);
}

app.Run();

static ClubResponse ToResponse(Club club)
{
    var managers = club.ManagerAssignments
        .OrderByDescending(x => x.IsActive)
        .ThenByDescending(x => x.AssignedAtUtc)
        .Select(x => new ManagerAssignmentResponse(
            x.Id,
            x.ManagerUserId,
            x.ManagerName,
            x.AssignedAtUtc,
            x.EndedAtUtc,
            x.IsActive))
        .ToArray();

    var members = club.Memberships
        .OrderBy(x => x.Status)
        .ThenByDescending(x => x.Role == ClubMemberRoles.Treasurer)
        .ThenBy(x => x.FullName)
        .Select(x => ToMembershipResponseWithClub(x, club))
        .ToArray();

    return new ClubResponse(
        club.Id,
        club.Code,
        club.Name,
        club.Description,
        club.ContactEmail,
        club.ContactPhone,
        club.IsActive,
        managers,
        members);
}

static ClubResponse ToDirectoryResponse(Club club)
{
    var response = ToResponse(club);
    var visibleMembers = response.Members
        .Where(x => x.Status == ClubMembershipStatuses.Approved)
        .Select(x => x with
        {
            RequestMessage = null,
            PersonalInfo = string.Empty,
            Goals = string.Empty,
            Reason = string.Empty,
            ReviewNote = null
        })
        .ToArray();
    return response with { Members = visibleMembers };
}

static ClubMembershipResponse ToMembershipResponse(ClubMembership membership)
{
    return ToMembershipResponseWithClub(membership, membership.Club);
}

static ClubMembershipResponse ToMembershipResponseWithClub(ClubMembership membership, Club club)
{
    return new ClubMembershipResponse(
        membership.Id,
        membership.ClubId,
        club.Name,
        membership.UserId,
        membership.FullName,
        membership.Role,
        membership.Status,
        membership.RequestMessage,
        membership.PersonalInfo,
        membership.Goals,
        membership.Reason,
        membership.ReviewNote,
        membership.RequestedAtUtc,
        membership.ReviewedAtUtc,
        membership.ReviewedByUserId);
}

static ClubCreationApplicationResponse ToApplicationResponse(ClubCreationApplication application)
{
    return new ClubCreationApplicationResponse(
        application.Id,
        application.RequesterUserId,
        application.RequesterName,
        application.Code,
        application.Name,
        application.Description,
        application.Purpose,
        application.Reason,
        application.ContactEmail,
        application.ContactPhone,
        application.Status,
        application.ReviewNote,
        application.CreatedClubId,
        application.SubmittedAtUtc,
        application.ReviewedAtUtc,
        application.ReviewedByUserId);
}

static bool IsAdmin(ClaimsPrincipal user)
{
    return user.IsInRole(AuthRoles.Admin)
        || user.IsInRole(AuthRoles.SystemAdmin)
        || user.IsInRole(AuthRoles.StudentAffairsAdmin);
}

static Task<bool> UserOwnsClubAsync(ClubDbContext db, int clubId, int userId)
{
    return db.ClubManagerAssignments.AnyAsync(x => x.ClubId == clubId && x.ManagerUserId == userId && x.IsActive);
}

static string? ValidateClubApplication(CreateClubApplicationRequest request, string normalizedCode)
{
    if (string.IsNullOrWhiteSpace(normalizedCode)
        || string.IsNullOrWhiteSpace(request.Name)
        || string.IsNullOrWhiteSpace(request.Description)
        || string.IsNullOrWhiteSpace(request.Purpose)
        || string.IsNullOrWhiteSpace(request.Reason)
        || string.IsNullOrWhiteSpace(request.ContactEmail)
        || string.IsNullOrWhiteSpace(request.ContactPhone))
    {
        return "Vui lòng nhập đầy đủ mã, tên, mô tả, mục đích, lý do và thông tin liên hệ của câu lạc bộ.";
    }

    if (!Regex.IsMatch(normalizedCode, "^[A-Z0-9_-]{2,20}$"))
    {
        return "Mã câu lạc bộ chỉ gồm 2-20 chữ cái, chữ số, dấu gạch ngang hoặc gạch dưới.";
    }

    if (!MailAddress.TryCreate(request.ContactEmail.Trim(), out var email)
        || !string.Equals(email.Address, request.ContactEmail.Trim(), StringComparison.OrdinalIgnoreCase))
    {
        return "Email liên hệ không hợp lệ.";
    }

    if (!Regex.IsMatch(request.ContactPhone.Trim(), "^\\+?[0-9]{9,15}$"))
    {
        return "Số điện thoại phải có từ 9 đến 15 chữ số.";
    }

    if (request.Name.Trim().Length > 150
        || request.Description.Trim().Length > 1000
        || request.Purpose.Trim().Length > 1000
        || request.Reason.Trim().Length > 1000)
    {
        return "Nội dung đơn đăng ký vượt quá độ dài cho phép.";
    }

    return null;
}

static string? ValidateJoinRequest(JoinClubRequest request)
{
    if (string.IsNullOrWhiteSpace(request.PersonalInfo)
        || string.IsNullOrWhiteSpace(request.Goals)
        || string.IsNullOrWhiteSpace(request.Reason))
    {
        return "Vui lòng nhập thông tin cá nhân, mục tiêu và lý do tham gia câu lạc bộ.";
    }

    if ((request.Message?.Trim().Length ?? 0) > 1000
        || request.PersonalInfo.Trim().Length > 1000
        || request.Goals.Trim().Length > 1000
        || request.Reason.Trim().Length > 1000)
    {
        return "Nội dung đơn tham gia vượt quá độ dài cho phép.";
    }

    return null;
}
