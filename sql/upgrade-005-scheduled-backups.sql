/*
  Adds the backup run history used by scheduled backups (Settings → Scheduled backups).
  The installer's database step runs this automatically. Safe to run more than once.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
IF OBJECT_ID(N'[BackupRuns]') IS NULL
BEGIN
    CREATE TABLE [BackupRuns] (
        [Id]              bigint         IDENTITY(1,1) NOT NULL,
        [ScheduledForUtc] datetime2      NULL,       -- the schedule slot; unique so only one web server runs it
        [StartedAtUtc]    datetime2      NOT NULL,
        [FinishedAtUtc]   datetime2      NULL,
        [Server]          nvarchar(100)  NOT NULL,
        [StartedBy]       nvarchar(256)  NOT NULL,
        [Success]         bit            NULL,
        [FilePath]        nvarchar(500)  NULL,
        [SizeBytes]       bigint         NULL,
        [Error]           nvarchar(2000) NULL,
        CONSTRAINT [PK_BackupRuns] PRIMARY KEY ([Id])
    );
    CREATE UNIQUE INDEX [IX_BackupRuns_ScheduledForUtc] ON [BackupRuns] ([ScheduledForUtc]) WHERE [ScheduledForUtc] IS NOT NULL;
    CREATE INDEX [IX_BackupRuns_StartedAtUtc] ON [BackupRuns] ([StartedAtUtc]);
END
GO
