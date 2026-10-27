/*
  PresApp — create the empty database.
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

-- The login of the service (NT SERVICE\PresApp) is created by deploy\install-service.ps1:
-- that Windows account only exists once the "PresApp" service is installed.

/*
  Access to Simpro (database Preservation) is granted later with 03-simpro-access.sql,
  limited to the tables the sync writes. Nothing here touches Preservation.
*/
