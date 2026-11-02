/*
  WebText — SQL Server schema.

  You only need this script if your DBA prefers to create the schema by hand (run the installer with -SkipSqlSetup).
  Otherwise tools\Install-Server.ps1 builds the database itself, using the app's own data model.

  Run against an empty database, e.g.:
      CREATE DATABASE WebText;
      GO
      USE WebText;
      GO
  then this script, then grant the IIS app pool account access (bottom of file).
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE TABLE [Clients] (
    [Id]               int            IDENTITY(1,1) NOT NULL,
    [FirstName]        nvarchar(100)  NOT NULL,
    [LastName]         nvarchar(100)  NOT NULL,
    [PhoneE164]        nvarchar(20)   NOT NULL,
    [Email]            nvarchar(200)  NULL,
    [AccountNumber]    nvarchar(50)   NULL,
    [Notes]            nvarchar(2000) NULL,
    [OptedOut]         bit            NOT NULL,
    [OptedOutAtUtc]    datetime2      NULL,
    [IsAutoCreated]    bit            NOT NULL,
    [CreatedAtUtc]     datetime2      NOT NULL,
    [CreatedBy]        nvarchar(256)  NOT NULL,
    [UpdatedAtUtc]     datetime2      NULL,
    [UpdatedBy]        nvarchar(256)  NULL,
    [LastMessageAtUtc] datetime2      NULL,
    CONSTRAINT [PK_Clients] PRIMARY KEY ([Id])
);
CREATE INDEX [IX_Clients_PhoneE164]          ON [Clients] ([PhoneE164]);
CREATE INDEX [IX_Clients_LastMessageAtUtc]   ON [Clients] ([LastMessageAtUtc]);
CREATE INDEX [IX_Clients_LastName_FirstName] ON [Clients] ([LastName], [FirstName]);
CREATE INDEX [IX_Clients_AccountNumber]      ON [Clients] ([AccountNumber]);
GO

CREATE TABLE [Messages] (
    [Id]                 bigint           IDENTITY(1,1) NOT NULL,
    [ClientId]           int              NOT NULL,
    [Direction]          int              NOT NULL,   -- 0 = outbound, 1 = inbound, 2 = internal note
    [Body]               nvarchar(4000)   NOT NULL,
    [FromNumber]         nvarchar(20)     NULL,
    [ToNumber]           nvarchar(20)     NULL,
    [TelnyxMessageId]    nvarchar(64)     NULL,
    [Status]             nvarchar(40)     NOT NULL,
    [ErrorDetail]        nvarchar(1000)   NULL,
    [SentBy]             nvarchar(256)    NULL,       -- DOMAIN\user
    [BatchId]            uniqueidentifier NULL,
    [Segments]           int              NOT NULL,
    [CreatedAtUtc]       datetime2        NOT NULL,
    [SentAtUtc]          datetime2        NULL,
    [StatusUpdatedAtUtc] datetime2        NULL,
    [IsRead]             bit              NOT NULL,
    [ReadBy]             nvarchar(256)    NULL,
    [ReadAtUtc]          datetime2        NULL,
    [SendAttempts]       int              NOT NULL,
    CONSTRAINT [PK_Messages] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Messages_Clients_ClientId] FOREIGN KEY ([ClientId]) REFERENCES [Clients] ([Id]) ON DELETE CASCADE
);
CREATE INDEX [IX_Messages_ClientId_CreatedAtUtc] ON [Messages] ([ClientId], [CreatedAtUtc]);
CREATE INDEX [IX_Messages_TelnyxMessageId]       ON [Messages] ([TelnyxMessageId]);
CREATE INDEX [IX_Messages_Status]                ON [Messages] ([Status]);
CREATE INDEX [IX_Messages_SentBy]                ON [Messages] ([SentBy]);
CREATE INDEX [IX_Messages_CreatedAtUtc]          ON [Messages] ([CreatedAtUtc]);
CREATE INDEX [IX_Messages_Direction_IsRead]      ON [Messages] ([Direction], [IsRead]);
GO

CREATE TABLE [MessageMedia] (
    [Id]          bigint         IDENTITY(1,1) NOT NULL,
    [MessageId]   bigint         NOT NULL,
    [Url]         nvarchar(2000) NOT NULL,
    [ContentType] nvarchar(100)  NULL,
    [SizeBytes]   bigint         NULL,
    CONSTRAINT [PK_MessageMedia] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_MessageMedia_Messages_MessageId] FOREIGN KEY ([MessageId]) REFERENCES [Messages] ([Id]) ON DELETE CASCADE
);
CREATE INDEX [IX_MessageMedia_MessageId] ON [MessageMedia] ([MessageId]);
GO

CREATE TABLE [MessageTemplates] (
    [Id]           int            IDENTITY(1,1) NOT NULL,
    [Name]         nvarchar(100)  NOT NULL,
    [Body]         nvarchar(1600) NOT NULL,
    [IsActive]     bit            NOT NULL,
    [CreatedAtUtc] datetime2      NOT NULL,
    [CreatedBy]    nvarchar(256)  NOT NULL,
    CONSTRAINT [PK_MessageTemplates] PRIMARY KEY ([Id])
);
CREATE INDEX [IX_MessageTemplates_Name] ON [MessageTemplates] ([Name]);
GO

CREATE TABLE [WebhookEvents] (
    [Id]              bigint         IDENTITY(1,1) NOT NULL,
    [EventId]         nvarchar(64)   NOT NULL,
    [EventType]       nvarchar(64)   NOT NULL,
    [OccurredAtUtc]   datetime2      NULL,
    [ReceivedAtUtc]   datetime2      NOT NULL,
    [RawJson]         nvarchar(max)  NOT NULL,
    [Processed]       bit            NOT NULL,
    [ProcessingError] nvarchar(1000) NULL,
    CONSTRAINT [PK_WebhookEvents] PRIMARY KEY ([Id])
);
CREATE UNIQUE INDEX [IX_WebhookEvents_EventId]       ON [WebhookEvents] ([EventId]);
CREATE INDEX        [IX_WebhookEvents_ReceivedAtUtc] ON [WebhookEvents] ([ReceivedAtUtc]);
GO

CREATE TABLE [ClientGroups] (
    [Id]           int           IDENTITY(1,1) NOT NULL,
    [Name]         nvarchar(100) NOT NULL,
    [Description]  nvarchar(500) NULL,
    [CreatedAtUtc] datetime2     NOT NULL,
    [CreatedBy]    nvarchar(256) NOT NULL,
    CONSTRAINT [PK_ClientGroups] PRIMARY KEY ([Id])
);
CREATE UNIQUE INDEX [IX_ClientGroups_Name] ON [ClientGroups] ([Name]);
GO

CREATE TABLE [ClientGroupMembers] (
    [GroupId]    int           NOT NULL,
    [ClientId]   int           NOT NULL,
    [AddedAtUtc] datetime2     NOT NULL,
    [AddedBy]    nvarchar(256) NOT NULL,
    CONSTRAINT [PK_ClientGroupMembers] PRIMARY KEY ([GroupId], [ClientId]),
    CONSTRAINT [FK_ClientGroupMembers_ClientGroups_GroupId] FOREIGN KEY ([GroupId]) REFERENCES [ClientGroups] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_ClientGroupMembers_Clients_ClientId] FOREIGN KEY ([ClientId]) REFERENCES [Clients] ([Id]) ON DELETE CASCADE
);
CREATE INDEX [IX_ClientGroupMembers_ClientId] ON [ClientGroupMembers] ([ClientId]);
GO

CREATE TABLE [AppSettings] (
    [Name]         nvarchar(100) NOT NULL,
    [Value]        nvarchar(max) NULL,       -- secrets are stored encrypted (ASP.NET Core Data Protection)
    [IsSecret]     bit           NOT NULL,
    [UpdatedAtUtc] datetime2     NOT NULL,
    [UpdatedBy]    nvarchar(256) NOT NULL,
    CONSTRAINT [PK_AppSettings] PRIMARY KEY ([Name])
);
GO

CREATE TABLE [AppSettingChanges] (
    [Id]              bigint        IDENTITY(1,1) NOT NULL,
    [Name]            nvarchar(100) NOT NULL,
    [NewValueDisplay] nvarchar(500) NULL,    -- never contains secret values
    [ChangedBy]       nvarchar(256) NOT NULL,
    [ChangedAtUtc]    datetime2     NOT NULL,
    CONSTRAINT [PK_AppSettingChanges] PRIMARY KEY ([Id])
);
CREATE INDEX [IX_AppSettingChanges_ChangedAtUtc] ON [AppSettingChanges] ([ChangedAtUtc]);
GO

CREATE TABLE [DataProtectionKeys] (
    [Id]           int           IDENTITY(1,1) NOT NULL,
    [FriendlyName] nvarchar(200) NULL,
    [Xml]          nvarchar(max) NOT NULL,   -- DPAPI-encrypted with the web server's machine key on Windows
    CONSTRAINT [PK_DataProtectionKeys] PRIMARY KEY ([Id])
);
GO

CREATE TABLE [AutoResponders] (
    [Id]              int            IDENTITY(1,1) NOT NULL,
    [Keyword]         nvarchar(50)   NOT NULL,   -- upper-case, letters/digits/spaces only, e.g. JOKE
    [Match]           int            NOT NULL,   -- 0 = whole text, 1 = starts with, 2 = anywhere
    [Reply]           nvarchar(1600) NOT NULL,
    [IsActive]        bit            NOT NULL,
    [MarkHandled]     bit            NOT NULL,
    [CooldownMinutes] int            NOT NULL,
    [Notes]           nvarchar(500)  NULL,
    [TimesUsed]       int            NOT NULL,
    [LastUsedAtUtc]   datetime2      NULL,
    [CreatedAtUtc]    datetime2      NOT NULL,
    [CreatedBy]       nvarchar(256)  NOT NULL,
    [UpdatedAtUtc]    datetime2      NULL,
    [UpdatedBy]       nvarchar(256)  NULL,
    CONSTRAINT [PK_AutoResponders] PRIMARY KEY ([Id])
);
CREATE UNIQUE INDEX [IX_AutoResponders_Keyword] ON [AutoResponders] ([Keyword]);
GO

CREATE TABLE [AutoResponderChanges] (
    [Id]              bigint         IDENTITY(1,1) NOT NULL,
    [AutoResponderId] int            NOT NULL,   -- no foreign key: history is kept after a keyword is deleted
    [Keyword]         nvarchar(50)   NOT NULL,
    [Action]          int            NOT NULL,   -- 0 added, 1 changed, 2 turned on, 3 turned off, 4 deleted
    [Details]         nvarchar(max)  NULL,       -- what changed ("Reply: old -> new"); full settings when added/deleted
    [ChangedBy]       nvarchar(256)  NOT NULL,   -- Windows login, DOMAIN\user
    [ChangedAtUtc]    datetime2      NOT NULL,
    CONSTRAINT [PK_AutoResponderChanges] PRIMARY KEY ([Id])
);
CREATE INDEX [IX_AutoResponderChanges_ChangedAtUtc] ON [AutoResponderChanges] ([ChangedAtUtc]);
CREATE INDEX [IX_AutoResponderChanges_AutoResponderId] ON [AutoResponderChanges] ([AutoResponderId]);
GO

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
GO

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
GO

/*
  Grant the IIS application pool identity access. Replace the login with your app pool account:
    - built-in identity on the same server:   [IIS APPPOOL\WebText]
    - app pool on a different server:          [YOURDOMAIN\WEBSERVER$]   (the web server's computer account)
    - a dedicated service account:             [YOURDOMAIN\svc-webtext]
*/
-- CREATE LOGIN [YOURDOMAIN\WEBSERVER$] FROM WINDOWS;
-- CREATE USER  [YOURDOMAIN\WEBSERVER$] FOR LOGIN [YOURDOMAIN\WEBSERVER$];
-- ALTER ROLE db_datareader ADD MEMBER [YOURDOMAIN\WEBSERVER$];
-- ALTER ROLE db_datawriter ADD MEMBER [YOURDOMAIN\WEBSERVER$];
GO

/* Optional housekeeping: raw webhook copies are only needed for troubleshooting. Schedule as a SQL Agent job. */
-- DELETE FROM [WebhookEvents] WHERE [ReceivedAtUtc] < DATEADD(day, -90, SYSUTCDATETIME());
