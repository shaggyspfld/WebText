/*
  Upgrade for databases created before settings moved into the database.
  The app only creates tables on an EMPTY database, so run this once against an existing WebText database.
  Safe to run more than once. (Run upgrade-001 first if you haven't.)

  After upgrading, open the app as a member of txtAppAdmin → Settings, and re-enter the Telnyx values that used
  to be in appsettings.json. Then remove them from appsettings.json.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
IF OBJECT_ID(N'[AppSettings]') IS NULL
BEGIN
    CREATE TABLE [AppSettings] (
        [Name]         nvarchar(100) NOT NULL,
        [Value]        nvarchar(max) NULL,       -- secrets are stored encrypted (ASP.NET Core Data Protection)
        [IsSecret]     bit           NOT NULL,
        [UpdatedAtUtc] datetime2     NOT NULL,
        [UpdatedBy]    nvarchar(256) NOT NULL,
        CONSTRAINT [PK_AppSettings] PRIMARY KEY ([Name])
    );
    
    CREATE TABLE [AppSettingChanges] (
        [Id]              bigint        IDENTITY(1,1) NOT NULL,
        [Name]            nvarchar(100) NOT NULL,
        [NewValueDisplay] nvarchar(500) NULL,    -- never contains secret values
        [ChangedBy]       nvarchar(256) NOT NULL,
        [ChangedAtUtc]    datetime2     NOT NULL,
        CONSTRAINT [PK_AppSettingChanges] PRIMARY KEY ([Id])
    );
    CREATE INDEX [IX_AppSettingChanges_ChangedAtUtc] ON [AppSettingChanges] ([ChangedAtUtc]);
    
    CREATE TABLE [DataProtectionKeys] (
        [Id]           int           IDENTITY(1,1) NOT NULL,
        [FriendlyName] nvarchar(200) NULL,
        [Xml]          nvarchar(max) NOT NULL,   -- DPAPI-encrypted with the web server's machine key on Windows
        CONSTRAINT [PK_DataProtectionKeys] PRIMARY KEY ([Id])
    );
END
GO
