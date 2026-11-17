-- Run once as administrator in Ascent, with the Ascent application pool STOPPED.
-- This demo upgrade intentionally deletes all previous projects and progress.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DELETE FROM [MilestoneDependencies];
DELETE FROM [Milestones];
DELETE FROM [Routes];
DELETE FROM [Requirements];
DELETE FROM [DocumentFingerprints];
DELETE FROM [Modules];
IF OBJECT_ID(N'Workspaces',N'U') IS NULL
    CREATE TABLE [Workspaces] ([Id] nvarchar(64) NOT NULL CONSTRAINT [PK_Workspaces] PRIMARY KEY,[CreatedUtc] datetime2 NOT NULL);
IF OBJECT_ID(N'DemoRuntime',N'U') IS NULL
    CREATE TABLE [DemoRuntime] ([Id] int NOT NULL CONSTRAINT [PK_DemoRuntime] PRIMARY KEY,[RunId] nvarchar(32) NOT NULL);
DELETE FROM [Workspaces];
DELETE FROM [DemoRuntime];
IF COL_LENGTH('Modules','SessionId') IS NULL
    ALTER TABLE [Modules] ADD [SessionId] nvarchar(64) NOT NULL;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'Modules') AND name=N'IX_Modules_SessionId')
    EXEC(N'CREATE INDEX [IX_Modules_SessionId] ON [Modules]([SessionId]);');
IF NOT EXISTS(SELECT 1 FROM sys.foreign_keys WHERE name=N'FK_Modules_Workspaces_SessionId')
    EXEC(N'ALTER TABLE [Modules] ADD CONSTRAINT [FK_Modules_Workspaces_SessionId] FOREIGN KEY ([SessionId]) REFERENCES [Workspaces]([Id]) ON DELETE CASCADE;');
COMMIT TRANSACTION;
