using Microsoft.EntityFrameworkCore;

namespace ActivityService.Data;

public static class ActivitySchemaUpgrader
{
    public static Task ApplyAsync(ActivityDbContext db, CancellationToken cancellationToken = default)
    {
        const string sql = """
            IF COL_LENGTH(N'dbo.Activities', N'MeetingDaysCsv') IS NULL
            BEGIN
                ALTER TABLE [dbo].[Activities]
                ADD [MeetingDaysCsv] nvarchar(32) NOT NULL
                    CONSTRAINT [DF_Activities_MeetingDaysCsv] DEFAULT N'';
            END;

            IF OBJECT_ID(N'[dbo].[ActivityAttendances]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[ActivityAttendances]
                (
                    [Id] int IDENTITY(1,1) NOT NULL,
                    [ActivityId] int NOT NULL,
                    [UserId] int NOT NULL,
                    [FullName] nvarchar(200) NOT NULL,
                    [AttendanceDate] date NOT NULL,
                    [Status] nvarchar(40) NOT NULL CONSTRAINT [DF_ActivityAttendances_Status] DEFAULT N'NotMarked',
                    [Note] nvarchar(1000) NULL,
                    [CheckedInAtUtc] datetimeoffset NULL,
                    [CheckedInByUserId] int NULL,
                    [CreatedAtUtc] datetimeoffset NOT NULL CONSTRAINT [DF_ActivityAttendances_CreatedAtUtc] DEFAULT SYSDATETIMEOFFSET(),
                    [UpdatedAtUtc] datetimeoffset NULL,
                    CONSTRAINT [PK_ActivityAttendances] PRIMARY KEY ([Id]),
                    CONSTRAINT [FK_ActivityAttendances_Activities_ActivityId]
                        FOREIGN KEY ([ActivityId]) REFERENCES [dbo].[Activities] ([Id]) ON DELETE NO ACTION
                );

                CREATE UNIQUE INDEX [IX_ActivityAttendances_ActivityId_UserId_AttendanceDate]
                    ON [dbo].[ActivityAttendances] ([ActivityId], [UserId], [AttendanceDate]);
            END;

            IF COL_LENGTH(N'dbo.ActivityAttendances', N'Status') IS NULL
            BEGIN
                ALTER TABLE [dbo].[ActivityAttendances]
                    ADD [Status] nvarchar(40) NOT NULL
                        CONSTRAINT [DF_ActivityAttendances_Status] DEFAULT N'Present';
            END;

            IF COL_LENGTH(N'dbo.ActivityAttendances', N'Note') IS NULL
                ALTER TABLE [dbo].[ActivityAttendances] ADD [Note] nvarchar(1000) NULL;
            IF COL_LENGTH(N'dbo.ActivityAttendances', N'CheckedInByUserId') IS NULL
                ALTER TABLE [dbo].[ActivityAttendances] ADD [CheckedInByUserId] int NULL;
            IF COL_LENGTH(N'dbo.ActivityAttendances', N'CreatedAtUtc') IS NULL
                ALTER TABLE [dbo].[ActivityAttendances] ADD [CreatedAtUtc] datetimeoffset NOT NULL
                    CONSTRAINT [DF_ActivityAttendances_CreatedAtUtc] DEFAULT SYSDATETIMEOFFSET();
            IF COL_LENGTH(N'dbo.ActivityAttendances', N'UpdatedAtUtc') IS NULL
                ALTER TABLE [dbo].[ActivityAttendances] ADD [UpdatedAtUtc] datetimeoffset NULL;

            IF EXISTS (
                SELECT 1 FROM sys.columns
                WHERE object_id = OBJECT_ID(N'[dbo].[ActivityAttendances]')
                  AND name = N'CheckedInAtUtc' AND is_nullable = 0)
                ALTER TABLE [dbo].[ActivityAttendances] ALTER COLUMN [CheckedInAtUtc] datetimeoffset NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ActivityAttendances_ActivityId_Status' AND object_id = OBJECT_ID(N'[dbo].[ActivityAttendances]'))
                CREATE INDEX [IX_ActivityAttendances_ActivityId_Status]
                    ON [dbo].[ActivityAttendances] ([ActivityId], [Status]);

            IF EXISTS (
                SELECT 1 FROM sys.foreign_keys
                WHERE name = N'FK_ActivityAttendances_Activities_ActivityId'
                  AND delete_referential_action = 1)
            BEGIN
                ALTER TABLE [dbo].[ActivityAttendances]
                    DROP CONSTRAINT [FK_ActivityAttendances_Activities_ActivityId];
                ALTER TABLE [dbo].[ActivityAttendances]
                    ADD CONSTRAINT [FK_ActivityAttendances_Activities_ActivityId]
                    FOREIGN KEY ([ActivityId]) REFERENCES [dbo].[Activities] ([Id]) ON DELETE NO ACTION;
            END;
            """;

        return db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }
}
