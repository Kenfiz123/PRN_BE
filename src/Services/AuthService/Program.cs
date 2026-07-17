using System.Security.Claims;
using System.Threading.RateLimiting;
using AuthService.Contracts;
using AuthService.Data;
using AuthService.Models;
using AuthService.Services;
using ClubReportHub.Shared.Auth;
using ClubReportHub.Shared.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AuthDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<RefreshTokenService>();
builder.Services.AddClubReportJwt(builder.Configuration);

// Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Strict rate limit for login attempts - prevent brute force
    options.AddPolicy("loginLimit", context =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 5,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    // Moderate limit for registration
    options.AddPolicy("registerLimit", context =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 3,
                Window = TimeSpan.FromMinutes(5),
                SegmentsPerWindow = 5,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    // Refresh token limit - per user to prevent token stealing abuse
    options.AddPolicy("refreshLimit", context =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 5,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
});
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
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapGet("/error", () => Results.Problem("An unexpected error occurred.")).AllowAnonymous();
app.MapGet("/", () => Results.Ok(new { service = "Auth Service", status = "running" }));

var auth = app.MapGroup("/api/auth").WithTags("Authentication");
auth.MapPost("/login", async (
    LoginRequest request,
    AuthDbContext db,
    IPasswordHasher<User> passwordHasher,
    RefreshTokenService refreshTokenService) =>
{
    var user = await db.Users
        .Include(x => x.UserRoles)
        .ThenInclude(x => x.Role)
        .FirstOrDefaultAsync(x => x.Username == request.Username || x.Email == request.Username);

    if (user is null || !user.IsActive || user.IsLocked)
    {
        return Results.Unauthorized();
    }

    var passwordResult = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
    if (passwordResult == PasswordVerificationResult.Failed)
    {
        return Results.Unauthorized();
    }

    var refreshToken = await refreshTokenService.CreateRefreshTokenAsync(user.Id);
    return Results.Ok(refreshTokenService.CreateAuthResponse(user, refreshToken));
}).AllowAnonymous().RequireRateLimiting("loginLimit");

auth.MapPost("/register", async (
    RegisterRequest request,
    AuthDbContext db,
    IPasswordHasher<User> passwordHasher,
    RefreshTokenService refreshTokenService) =>
{
    var username = request.Username.Trim();
    var email = request.Email.Trim();
    var fullName = request.FullName.Trim();

    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(fullName))
    {
        return Results.BadRequest(new { message = "Username, full name, email are required." });
    }

    // Password complexity validation
    var password = request.Password;
    if (password.Length < 8)
    {
        return Results.BadRequest(new { message = "Password must be at least 8 characters." });
    }
    var hasUpper = password.Any(char.IsUpper);
    var hasLower = password.Any(char.IsLower);
    var hasDigit = password.Any(char.IsDigit);
    var hasSpecial = password.Any(c => !char.IsLetterOrDigit(c));
    if (!(hasUpper && hasLower && hasDigit && hasSpecial))
    {
        return Results.BadRequest(new { message = "Password must contain uppercase, lowercase, digit, and special character." });
    }

    if (await db.Users.AnyAsync(x => x.Username == username || x.Email == email))
    {
        return Results.Conflict(new { message = "Username or email already exists." });
    }

    var memberRole = await db.Roles.FirstOrDefaultAsync(x => x.Name == AuthRoles.ClubMember);
    if (memberRole is null)
    {
        memberRole = new Role { Name = AuthRoles.ClubMember };
        db.Roles.Add(memberRole);
        await db.SaveChangesAsync();
    }

    var user = new User
    {
        Username = username,
        FullName = fullName,
        Email = email,
        IsActive = true
    };
    user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
    user.UserRoles.Add(new UserRole { User = user, Role = memberRole });

    db.Users.Add(user);
    await db.SaveChangesAsync();

    var refreshToken = await refreshTokenService.CreateRefreshTokenAsync(user.Id);
    return Results.Created($"/api/users/{user.Id}", refreshTokenService.CreateAuthResponse(user, refreshToken, [AuthRoles.ClubMember]));
}).AllowAnonymous().RequireRateLimiting("registerLimit");

auth.MapPost("/refresh", async (
    RefreshTokenRequest request,
    RefreshTokenService refreshTokenService,
    AuthDbContext db) =>
{
    var oldToken = await refreshTokenService.GetRefreshTokenAsync(request.RefreshToken);
    if (oldToken is null)
    {
        return Results.Unauthorized();
    }

    if (!oldToken.IsActive)
    {
        // Token is expired or revoked - revoke entire family for security
        if (oldToken.IsRevoked && !oldToken.IsExpired)
        {
            await refreshTokenService.RevokeFamilyAsync(oldToken.FamilyId, null);
        }
        return Results.Unauthorized();
    }

    var clientIp = "unknown"; // In production, get from HttpContext
    var rotatedToken = await refreshTokenService.RotateRefreshTokenAsync(oldToken, clientIp);
    if (rotatedToken is null)
    {
        return Results.Unauthorized();
    }

    return Results.Ok(refreshTokenService.CreateAuthResponse(oldToken.User, rotatedToken));
}).AllowAnonymous().RequireRateLimiting("refreshLimit");

auth.MapPost("/logout", async (
    RefreshTokenRequest request,
    RefreshTokenService refreshTokenService,
    AuthDbContext db) =>
{
    var token = await refreshTokenService.GetRefreshTokenAsync(request.RefreshToken);
    if (token is not null)
    {
        await refreshTokenService.RevokeFamilyAsync(token.FamilyId, null);
    }
    return Results.NoContent();
}).RequireAuthorization();

var users = app.MapGroup("/api/users").WithTags("Users").RequireAuthorization(policy =>
    policy.RequireRole(AuthRoles.Admin, AuthRoles.SystemAdmin));
users.MapGet("/", async (AuthDbContext db) =>
{
    var usersResult = await db.Users
        .Include(x => x.UserRoles)
        .ThenInclude(x => x.Role)
        .OrderBy(x => x.FullName)
        .ToListAsync();
    return Results.Ok(usersResult.Select(x => ToSummary(x)));
});

users.MapPost("/", async (
    CreateUserRequest request,
    AuthDbContext db,
    IPasswordHasher<User> passwordHasher) =>
{
    if (await db.Users.AnyAsync(x => x.Username == request.Username || x.Email == request.Email))
    {
        return Results.Conflict(new { message = "Username or email already exists." });
    }

    var roles = await db.Roles.Where(x => request.Roles.Contains(x.Name)).ToListAsync();
    if (roles.Count == 0)
    {
        return Results.BadRequest(new { message = "At least one valid role is required." });
    }

    var user = new User
    {
        Username = request.Username,
        FullName = request.FullName,
        Email = request.Email,
        IsActive = true
    };
    user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
    db.Users.Add(user);
    await db.SaveChangesAsync();

    db.UserRoles.AddRange(roles.Select(role => new UserRole { UserId = user.Id, RoleId = role.Id }));
    await db.SaveChangesAsync();

    return Results.Created($"/api/users/{user.Id}", ToSummary(user, withRoles: roles.Select(x => x.Name)));
});

users.MapPut("/{id:int}", async (int id, UpdateUserRequest request, AuthDbContext db, ClaimsPrincipal actor) =>
{
    var user = await db.Users.Include(x => x.UserRoles).ThenInclude(x => x.Role).FirstOrDefaultAsync(x => x.Id == id);
    if (user is null)
    {
        return Results.NotFound();
    }

    if (string.IsNullOrWhiteSpace(request.FullName) || string.IsNullOrWhiteSpace(request.Email) || request.Roles.Count == 0)
    {
        return Results.BadRequest(new { message = "Full name, email, and at least one role are required." });
    }

    if (await db.Users.AnyAsync(x => x.Id != id && x.Email == request.Email.Trim()))
    {
        return Results.Conflict(new { message = "Email already belongs to another account." });
    }

    var requestedRoleNames = request.Roles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var roles = await db.Roles.Where(x => requestedRoleNames.Contains(x.Name)).ToListAsync();
    if (roles.Count != requestedRoleNames.Length)
    {
        return Results.BadRequest(new { message = "One or more requested roles are invalid." });
    }

    var keepsAdministrativeRole = roles.Any(x => x.Name == AuthRoles.Admin || x.Name == AuthRoles.SystemAdmin);
    if (id == actor.GetUserId() && (!request.IsActive || !keepsAdministrativeRole))
    {
        return Results.BadRequest(new { message = "You cannot deactivate your own account or remove your own administrative access." });
    }

    var currentlyAdministrative = user.UserRoles.Any(x => x.Role.Name == AuthRoles.Admin || x.Role.Name == AuthRoles.SystemAdmin);
    if (currentlyAdministrative && (!request.IsActive || !keepsAdministrativeRole))
    {
        var otherActiveAdministrators = await db.Users.CountAsync(x =>
            x.Id != id
            && x.IsActive
            && !x.IsLocked
            && x.UserRoles.Any(ur => ur.Role.Name == AuthRoles.Admin || ur.Role.Name == AuthRoles.SystemAdmin));
        if (otherActiveAdministrators == 0)
        {
            return Results.Conflict(new { message = "The final active administrator cannot be deactivated or demoted." });
        }
    }

    user.FullName = request.FullName.Trim();
    user.Email = request.Email.Trim();
    user.IsActive = request.IsActive;

    db.UserRoles.RemoveRange(user.UserRoles);
    db.UserRoles.AddRange(roles.Select(role => new UserRole { UserId = user.Id, RoleId = role.Id }));
    await db.SaveChangesAsync();
    return Results.Ok(ToSummary(user, withRoles: roles.Select(x => x.Name)));
});

users.MapPatch("/{id:int}/lock", async (int id, AuthDbContext db) =>
{
    var user = await db.Users.FindAsync(id);
    if (user is null)
    {
        return Results.NotFound();
    }

    user.IsLocked = true;
    await db.SaveChangesAsync();
    return Results.NoContent();
});

users.MapPatch("/{id:int}/unlock", async (int id, AuthDbContext db) =>
{
    var user = await db.Users.FindAsync(id);
    if (user is null)
    {
        return Results.NotFound();
    }

    user.IsLocked = false;
    await db.SaveChangesAsync();
    return Results.NoContent();
});

var rolesGroup = app.MapGroup("/api/roles").WithTags("Roles").RequireAuthorization(AuthPolicies.AdminOnly);
rolesGroup.MapGet("/", async (AuthDbContext db) => Results.Ok(await db.Roles.OrderBy(x => x.Name).ToListAsync()));
rolesGroup.MapPost("/", async (CreateRoleRequest request, AuthDbContext db) =>
{
    var roleName = request.Name.Trim().ToUpperInvariant();
    if (await db.Roles.AnyAsync(x => x.Name == roleName))
    {
        return Results.Conflict(new { message = "Role already exists." });
    }

    var role = new Role { Name = roleName };
    db.Roles.Add(role);
    await db.SaveChangesAsync();
    return Results.Created($"/api/roles/{role.Id}", role);
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseStartup");
    await db.ApplyMigrationsWithRetryAsync(logger);
    await AuthSeeder.SeedAsync(
        db,
        scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>(),
        builder.Configuration);
}

app.Run();

static UserSummary ToSummary(User user, IEnumerable<string>? withRoles = null)
{
    var roles = withRoles?.ToArray() ?? user.UserRoles.Select(x => x.Role.Name).OrderBy(x => x).ToArray();
    return new UserSummary(user.Id, user.Username, user.FullName, user.Email, roles, user.IsActive, user.IsLocked);
}
