/*
  Adds app-hosted sign-in: local users, local groups, and the security log (Settings → Sign-in → Local).
  The installer's database step runs this automatically. Safe to run more than once.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
IF OBJECT_ID(N'[LocalUsers]') IS NULL
BEGIN
    CREATE TABLE [LocalUsers] (
        [Id]                    int            IDENTITY(1,1) NOT NULL,
        [UserName]              nvarchar(64)   NOT NULL,   -- lower-case; recorded as the sender of their texts
        [DisplayName]           nvarchar(200)  NULL,
        [Email]                 nvarchar(256)  NULL,
        [PasswordHash]          nvarchar(500)  NOT NULL,   -- salted PBKDF2 hash (ASP.NET Core Identity format), never the password
        [MustChangePassword]    bit            NOT NULL,
        [PasswordChangedAtUtc]  datetime2      NULL,
        [TotpSecretProtected]   nvarchar(1000) NULL,       -- authenticator secret, encrypted with Data Protection
        [TotpLastStep]          bigint         NOT NULL,   -- stops a code being used twice
        [IsActive]              bit            NOT NULL,
        [FailedAttempts]        int            NOT NULL,
        [LockedUntilUtc]        datetime2      NULL,
        [SecurityStamp]         nvarchar(64)   NOT NULL,   -- changes on password/authenticator reset, ending existing sessions
        [LastSignInAtUtc]       datetime2      NULL,
        [CreatedAtUtc]          datetime2      NOT NULL,
        [CreatedBy]             nvarchar(256)  NOT NULL,
        [UpdatedAtUtc]          datetime2      NULL,
        [UpdatedBy]             nvarchar(256)  NULL,
        CONSTRAINT [PK_LocalUsers] PRIMARY KEY ([Id])
    );
    CREATE UNIQUE INDEX [IX_LocalUsers_UserName] ON [LocalUsers] ([UserName]);

    CREATE TABLE [LocalGroups] (
        [Id]           int           IDENTITY(1,1) NOT NULL,
        [Name]         nvarchar(128) NOT NULL,
        [Description]  nvarchar(500) NULL,
        [CreatedAtUtc] datetime2     NOT NULL,
        [CreatedBy]    nvarchar(256) NOT NULL,
        CONSTRAINT [PK_LocalGroups] PRIMARY KEY ([Id])
    );
    CREATE UNIQUE INDEX [IX_LocalGroups_Name] ON [LocalGroups] ([Name]);

    CREATE TABLE [LocalGroupMembers] (
        [GroupId] int NOT NULL,
        [UserId]  int NOT NULL,
        CONSTRAINT [PK_LocalGroupMembers] PRIMARY KEY ([GroupId], [UserId]),
        CONSTRAINT [FK_LocalGroupMembers_LocalGroups_GroupId] FOREIGN KEY ([GroupId]) REFERENCES [LocalGroups] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_LocalGroupMembers_LocalUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [LocalUsers] ([Id]) ON DELETE CASCADE
    );
    CREATE INDEX [IX_LocalGroupMembers_UserId] ON [LocalGroupMembers] ([UserId]);

    CREATE TABLE [SecurityEvents] (
        [Id]        bigint        IDENTITY(1,1) NOT NULL,
        [AtUtc]     datetime2     NOT NULL,
        [Event]     nvarchar(50)  NOT NULL,   -- SignIn, SignInFailed, LockedOut, PasswordChanged, UserCreated, ...
        [UserName]  nvarchar(256) NULL,       -- the account it's about
        [By]        nvarchar(256) NULL,       -- who did it
        [IpAddress] nvarchar(64)  NULL,
        [Details]   nvarchar(max) NULL,
        CONSTRAINT [PK_SecurityEvents] PRIMARY KEY ([Id])
    );
    CREATE INDEX [IX_SecurityEvents_AtUtc] ON [SecurityEvents] ([AtUtc]);
    CREATE INDEX [IX_SecurityEvents_UserName] ON [SecurityEvents] ([UserName]);
END
GO
