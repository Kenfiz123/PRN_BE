using ClubService.Models;
using Microsoft.EntityFrameworkCore;

namespace ClubService.Data;

public sealed class ClubDbContext(DbContextOptions<ClubDbContext> options) : DbContext(options)
{
    public DbSet<Club> Clubs => Set<Club>();
    public DbSet<ClubManagerAssignment> ClubManagerAssignments => Set<ClubManagerAssignment>();
    public DbSet<ClubMembership> ClubMemberships => Set<ClubMembership>();
    public DbSet<ClubCreationApplication> ClubCreationApplications => Set<ClubCreationApplication>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Club>(entity =>
        {
            entity.HasIndex(x => x.Code).IsUnique();
            entity.Property(x => x.Code).HasMaxLength(30);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Description).HasMaxLength(1000);
            entity.Property(x => x.ContactEmail).HasMaxLength(200);
            entity.Property(x => x.ContactPhone).HasMaxLength(40);
            entity.HasMany(x => x.Memberships)
                .WithOne(x => x.Club)
                .HasForeignKey(x => x.ClubId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ClubManagerAssignment>(entity =>
        {
            entity.HasIndex(x => new { x.ClubId, x.ManagerUserId, x.IsActive });
            entity.Property(x => x.ManagerName).HasMaxLength(200);
            entity.HasOne(x => x.Club)
                .WithMany(x => x.ManagerAssignments)
                .HasForeignKey(x => x.ClubId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ClubMembership>(entity =>
        {
            entity.HasIndex(x => new { x.ClubId, x.UserId }).IsUnique();
            entity.HasIndex(x => new { x.ClubId, x.Role, x.Status });
            entity.Property(x => x.FullName).HasMaxLength(200);
            entity.Property(x => x.Role).HasMaxLength(40);
            entity.Property(x => x.Status).HasMaxLength(40);
            entity.Property(x => x.RequestMessage).HasMaxLength(1000);
            entity.Property(x => x.PersonalInfo).HasMaxLength(1000);
            entity.Property(x => x.Goals).HasMaxLength(1000);
            entity.Property(x => x.Reason).HasMaxLength(1000);
            entity.Property(x => x.ReviewNote).HasMaxLength(1000);
        });

        modelBuilder.Entity<ClubCreationApplication>(entity =>
        {
            entity.HasIndex(x => new { x.RequesterUserId, x.Status });
            entity.HasIndex(x => new { x.Code, x.Status });
            entity.Property(x => x.RequesterName).HasMaxLength(200);
            entity.Property(x => x.Code).HasMaxLength(30);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Description).HasMaxLength(1000);
            entity.Property(x => x.Purpose).HasMaxLength(1000);
            entity.Property(x => x.Reason).HasMaxLength(1000);
            entity.Property(x => x.ContactEmail).HasMaxLength(200);
            entity.Property(x => x.ContactPhone).HasMaxLength(40);
            entity.Property(x => x.Status).HasMaxLength(40);
            entity.Property(x => x.ReviewNote).HasMaxLength(1000);
        });
    }
}
