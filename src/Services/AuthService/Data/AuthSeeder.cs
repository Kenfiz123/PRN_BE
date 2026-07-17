using AuthService.Models;
using ClubReportHub.Shared.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AuthService.Data;

public static class AuthSeeder
{
    public static async Task SeedAsync(AuthDbContext db, IPasswordHasher<User> passwordHasher, IConfiguration? configuration = null)
    {
        await EnsureRoleAsync(db, AuthRoles.Admin);
        await EnsureRoleAsync(db, AuthRoles.SystemAdmin);
        await EnsureRoleAsync(db, AuthRoles.StudentAffairsAdmin);
        await EnsureRoleAsync(db, AuthRoles.ClubManager);
        await EnsureRoleAsync(db, AuthRoles.Treasurer);
        await EnsureRoleAsync(db, AuthRoles.ClubMember);
        await db.SaveChangesAsync();

        // Read default passwords from configuration, fallback to environment variable or throw
        var adminPassword = GetSeedPassword(configuration, "SeedPasswords:Admin", "ADMIN_SEED_PASSWORD")
            ?? throw new InvalidOperationException("Admin seed password must be configured via SeedPasswords:Admin or ADMIN_SEED_PASSWORD environment variable");
        var managerPassword = GetSeedPassword(configuration, "SeedPasswords:Manager", "MANAGER_SEED_PASSWORD")
            ?? throw new InvalidOperationException("Manager seed password must be configured via SeedPasswords:Manager or MANAGER_SEED_PASSWORD environment variable");
        var studentAffairsPassword = GetSeedPassword(configuration, "SeedPasswords:StudentAffairs", "STUDENT_AFFAIRS_SEED_PASSWORD")
            ?? throw new InvalidOperationException("Student Affairs seed password must be configured via SeedPasswords:StudentAffairs or STUDENT_AFFAIRS_SEED_PASSWORD environment variable");
        var treasurerPassword = GetSeedPassword(configuration, "SeedPasswords:Treasurer", "TREASURER_SEED_PASSWORD")
            ?? throw new InvalidOperationException("Treasurer seed password must be configured via SeedPasswords:Treasurer or TREASURER_SEED_PASSWORD environment variable");
        var studentPassword = GetSeedPassword(configuration, "SeedPasswords:Student", "STUDENT_SEED_PASSWORD")
            ?? throw new InvalidOperationException("Student seed password must be configured via SeedPasswords:Student or STUDENT_SEED_PASSWORD environment variable");

        await EnsureUserAsync(
            db,
            passwordHasher,
            username: "admin@club.local",
            fullName: "Quản trị hệ thống",
            email: "admin@club.local",
            password: adminPassword,
            roles: [AuthRoles.Admin]);

        await EnsureUserAsync(
            db,
            passwordHasher,
            username: "manager@club.local",
            fullName: "Chủ nhiệm câu lạc bộ",
            email: "manager@club.local",
            password: managerPassword,
            roles: [AuthRoles.ClubManager]);

        await EnsureUserAsync(
            db,
            passwordHasher,
            username: "studentaffairs@club.local",
            fullName: "Quản trị công tác sinh viên",
            email: "studentaffairs@club.local",
            password: studentAffairsPassword,
            roles: [AuthRoles.StudentAffairsAdmin]);

        await EnsureUserAsync(
            db,
            passwordHasher,
            username: "treasurer@club.local",
            fullName: "Thủ quỹ câu lạc bộ",
            email: "treasurer@club.local",
            password: treasurerPassword,
            roles: [AuthRoles.Treasurer]);

        await EnsureUserAsync(
            db,
            passwordHasher,
            username: "student@club.local",
            fullName: "Thành viên câu lạc bộ",
            email: "student@club.local",
            password: studentPassword,
            roles: [AuthRoles.ClubMember]);

        await db.SaveChangesAsync();
    }

    private static string? GetSeedPassword(IConfiguration? configuration, string configKey, string envVar)
    {
        var value = configuration?[configKey] ?? Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static async Task EnsureRoleAsync(AuthDbContext db, string roleName)
    {
        if (!await db.Roles.AnyAsync(x => x.Name == roleName))
        {
            db.Roles.Add(new Role { Name = roleName });
        }
    }

    private static async Task EnsureUserAsync(
        AuthDbContext db,
        IPasswordHasher<User> passwordHasher,
        string username,
        string fullName,
        string email,
        string password,
        IReadOnlyCollection<string> roles)
    {
        var existing = await db.Users.Include(x => x.UserRoles).ThenInclude(x => x.Role)
            .FirstOrDefaultAsync(x => x.Username == username);
        if (existing is not null)
        {
            existing.FullName = fullName;
            existing.Email = email;
            existing.IsActive = true;

            var existingRoleNames = existing.UserRoles.Select(x => x.Role.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingRoleNames = roles.Where(role => !existingRoleNames.Contains(role)).ToArray();
            if (missingRoleNames.Length > 0)
            {
                var missingRoles = await db.Roles.Where(x => missingRoleNames.Contains(x.Name)).ToListAsync();
                foreach (var role in missingRoles)
                {
                    db.UserRoles.Add(new UserRole { UserId = existing.Id, RoleId = role.Id });
                }
            }

            return;
        }

        var user = new User
        {
            Username = username,
            FullName = fullName,
            Email = email,
            IsActive = true
        };
        user.PasswordHash = passwordHasher.HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var roleEntities = await db.Roles.Where(x => roles.Contains(x.Name)).ToListAsync();
        foreach (var role in roleEntities)
        {
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        }
    }
}
