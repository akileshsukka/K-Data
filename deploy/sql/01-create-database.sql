/*
  PresApp — create the database and the login of the Windows service.
  Run ONCE on EM-L-5236 in SSMS, connected as a sysadmin.
  The tables are created by PresApp itself on first start (migrations), so the database starts empty.
*/
USE [master];
GO

IF DB_ID(N'PresApp') IS NULL
BEGIN
    CREATE DATABASE [PresApp];
    ALTER DATABASE [PresApp] SET RECOVERY SIMPLE;   -- daily full + differential backups (02-backup-jobs.sql)
END
GO

-- The service runs as the virtual account NT SERVICE\PresApp (created by install-service.ps1).
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'NT SERVICE\PresApp')
    CREATE LOGIN [NT SERVICE\PresApp] FROM WINDOWS WITH DEFAULT_DATABASE = [PresApp];
GO

USE [PresApp];
GO
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'NT SERVICE\PresApp')
    CREATE USER [NT SERVICE\PresApp] FOR LOGIN [NT SERVICE\PresApp];
ALTER ROLE [db_owner] ADD MEMBER [NT SERVICE\PresApp];
GO

/*
  Access to Simpro (database Preservation) is granted in Phase 3, limited to the tables the
  sync writes. Nothing here touches Preservation.
*/
