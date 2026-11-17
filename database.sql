CREATE TABLE [DemoRuntime] (
    [Id] int NOT NULL,
    [RunId] nvarchar(32) NOT NULL,
    CONSTRAINT [PK_DemoRuntime] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [Workspaces] (
    [Id] nvarchar(64) NOT NULL,
    [CreatedUtc] datetime2 NOT NULL,
    CONSTRAINT [PK_Workspaces] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [Modules] (
    [Id] uniqueidentifier NOT NULL,
    [SessionId] nvarchar(64) NOT NULL,
    [Title] nvarchar(500) NOT NULL,
    [CreatedUtc] datetime2 NOT NULL,
    [Revision] int NOT NULL,
    [SnapshotJson] nvarchar(max) NOT NULL,
    CONSTRAINT [PK_Modules] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Modules_Workspaces_SessionId] FOREIGN KEY ([SessionId]) REFERENCES [Workspaces] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [DocumentFingerprints] (
    [ModuleId] uniqueidentifier NOT NULL,
    [TextHash] char(64) NOT NULL,
    CONSTRAINT [PK_DocumentFingerprints] PRIMARY KEY ([ModuleId]),
    CONSTRAINT [FK_DocumentFingerprints_Modules_ModuleId] FOREIGN KEY ([ModuleId]) REFERENCES [Modules] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [Requirements] (
    [ModuleId] uniqueidentifier NOT NULL,
    [Id] nvarchar(100) NOT NULL,
    [Title] nvarchar(max) NOT NULL,
    [Kind] nvarchar(30) NOT NULL,
    [Mandatory] bit NOT NULL,
    [Count] int NOT NULL,
    [EvidenceJson] nvarchar(max) NOT NULL,
    CONSTRAINT [PK_Requirements] PRIMARY KEY ([ModuleId], [Id]),
    CONSTRAINT [FK_Requirements_Modules_ModuleId] FOREIGN KEY ([ModuleId]) REFERENCES [Modules] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [Routes] (
    [Id] nvarchar(32) NOT NULL,
    [ModuleId] uniqueidentifier NOT NULL,
    [Name] nvarchar(max) NOT NULL,
    [Approved] bit NOT NULL,
    CONSTRAINT [PK_Routes] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Routes_Modules_ModuleId] FOREIGN KEY ([ModuleId]) REFERENCES [Modules] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [Milestones] (
    [Id] nvarchar(32) NOT NULL,
    [RouteId] nvarchar(32) NOT NULL,
    [Title] nvarchar(max) NOT NULL,
    [Complete] bit NOT NULL,
    [Optional] bit NOT NULL,
    [Notes] nvarchar(max) NOT NULL,
    CONSTRAINT [PK_Milestones] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Milestones_Routes_RouteId] FOREIGN KEY ([RouteId]) REFERENCES [Routes] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [MilestoneDependencies] (
    [MilestoneId] nvarchar(32) NOT NULL,
    [PrerequisiteId] nvarchar(32) NOT NULL,
    CONSTRAINT [PK_MilestoneDependencies] PRIMARY KEY ([MilestoneId], [PrerequisiteId]),
    CONSTRAINT [FK_MilestoneDependencies_Milestones_MilestoneId] FOREIGN KEY ([MilestoneId]) REFERENCES [Milestones] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_MilestoneDependencies_Milestones_PrerequisiteId] FOREIGN KEY ([PrerequisiteId]) REFERENCES [Milestones] ([Id])
);
GO


CREATE INDEX [IX_DocumentFingerprints_TextHash] ON [DocumentFingerprints] ([TextHash]);
GO


CREATE INDEX [IX_MilestoneDependencies_PrerequisiteId] ON [MilestoneDependencies] ([PrerequisiteId]);
GO


CREATE INDEX [IX_Milestones_RouteId] ON [Milestones] ([RouteId]);
GO


CREATE INDEX [IX_Modules_SessionId] ON [Modules] ([SessionId]);
GO


CREATE INDEX [IX_Routes_ModuleId] ON [Routes] ([ModuleId]);
GO



