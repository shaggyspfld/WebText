/*
  Upgrade for databases created before recipient lists were added.
  The app only creates tables on an EMPTY database, so run this once against an existing WebText database.
  Safe to run more than once.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
IF OBJECT_ID(N'[ClientGroups]') IS NULL
BEGIN
CREATE TABLE [ClientGroups] (
        [Id]           int           IDENTITY(1,1) NOT NULL,
        [Name]         nvarchar(100) NOT NULL,
        [Description]  nvarchar(500) NULL,
        [CreatedAtUtc] datetime2     NOT NULL,
        [CreatedBy]    nvarchar(256) NOT NULL,
        CONSTRAINT [PK_ClientGroups] PRIMARY KEY ([Id])
    );
    CREATE UNIQUE INDEX [IX_ClientGroups_Name] ON [ClientGroups] ([Name]);
    
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
END
GO
