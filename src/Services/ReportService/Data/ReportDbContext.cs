using Microsoft.EntityFrameworkCore;
using ReportService.Models;

namespace ReportService.Data;

public sealed class ReportDbContext(DbContextOptions<ReportDbContext> options) : DbContext(options)
{
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<ReportDetail> ReportDetails => Set<ReportDetail>();
    public DbSet<ReportAttachment> ReportAttachments => Set<ReportAttachment>();
    public DbSet<ReportFeedback> ReportFeedback => Set<ReportFeedback>();
    public DbSet<ReportingDeadline> ReportingDeadlines => Set<ReportingDeadline>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Report>(entity =>
        {
            entity.HasIndex(x => new { x.ClubId, x.Period, x.Tag }).IsUnique();
            entity.HasIndex(x => x.Status);
            entity.Property(x => x.ClubName).HasMaxLength(200);
            entity.Property(x => x.Period).HasMaxLength(40);
            entity.Property(x => x.ReportType).HasMaxLength(80);
            entity.Property(x => x.Tag).HasMaxLength(80);
            entity.Property(x => x.Status).HasMaxLength(40);
            entity.HasMany(x => x.Details).WithOne(x => x.Report).HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.Attachments).WithOne(x => x.Report).HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.Feedback).WithOne(x => x.Report).HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReportDetail>(entity =>
        {
            entity.Property(x => x.ActivityName).HasMaxLength(200);
            entity.Property(x => x.Description).HasMaxLength(2000);
            entity.Property(x => x.Outcome).HasMaxLength(1000);
        });

        modelBuilder.Entity<ReportAttachment>(entity =>
        {
            entity.Property(x => x.FileName).HasMaxLength(260);
            entity.Property(x => x.ContentType).HasMaxLength(120);
            entity.Property(x => x.StoragePath).HasMaxLength(500);
        });

        modelBuilder.Entity<ReportFeedback>(entity =>
        {
            entity.Property(x => x.ReviewerName).HasMaxLength(200);
            entity.Property(x => x.Decision).HasMaxLength(40);
            entity.Property(x => x.Message).HasMaxLength(2000);
        });

        modelBuilder.Entity<ReportingDeadline>(entity =>
        {
            entity.HasIndex(x => x.Period).IsUnique();
            entity.Property(x => x.Period).HasMaxLength(40);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.Property(x => x.Action).HasMaxLength(80);
            entity.Property(x => x.Description).HasMaxLength(1000);
        });
    }
}
