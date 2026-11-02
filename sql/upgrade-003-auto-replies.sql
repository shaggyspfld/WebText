/*
  Adds keyword auto-replies (the Auto-replies page) and their change history.
  The installer's database step runs this automatically. To apply it by hand, run it against an existing WebText
  database as a login that can create tables. Safe to run more than once.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
IF OBJECT_ID(N'[AutoResponders]') IS NULL
BEGIN
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
END
GO
IF OBJECT_ID(N'[AutoResponderChanges]') IS NULL
BEGIN
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
END
GO
