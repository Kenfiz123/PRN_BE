using Microsoft.EntityFrameworkCore;

namespace FinanceService.Data;

public static class FinanceSchemaUpgrader
{
    public static Task ApplyAsync(FinanceDbContext db, CancellationToken cancellationToken = default)
    {
        const string sql = """
            IF COL_LENGTH(N'dbo.BudgetProposals', N'ManagerReviewedByUserId') IS NULL
                ALTER TABLE [dbo].[BudgetProposals] ADD [ManagerReviewedByUserId] int NULL;

            IF COL_LENGTH(N'dbo.BudgetProposals', N'ManagerReviewedAtUtc') IS NULL
                ALTER TABLE [dbo].[BudgetProposals] ADD [ManagerReviewedAtUtc] datetimeoffset NULL;

            IF COL_LENGTH(N'dbo.BudgetProposals', N'ManagerReviewNote') IS NULL
                ALTER TABLE [dbo].[BudgetProposals] ADD [ManagerReviewNote] nvarchar(1000) NULL;

            IF COL_LENGTH(N'dbo.BudgetProposals', N'SourceReportId') IS NULL
                ALTER TABLE [dbo].[BudgetProposals] ADD [SourceReportId] int NULL;

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'[dbo].[BudgetProposals]')
                  AND name = N'IX_BudgetProposals_SourceReportId')
                EXEC(N'CREATE UNIQUE INDEX [IX_BudgetProposals_SourceReportId]
                    ON [dbo].[BudgetProposals] ([SourceReportId])
                    WHERE [SourceReportId] IS NOT NULL');
            """;

        return db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }
}
