/*
  K Data — access of the Windows service to Simpro (database Preservation).
  Run ONCE on EM-L-5236 in SSMS by someone with rights to manage users in Preservation (IT / DBA),
  AFTER deploy\install-service.ps1 (it creates the login NT SERVICE\PresApp).

  Nothing in Preservation changes except one database user and its permissions:
  no tables, columns or triggers are added. The account can only read the catalogs and
  read/write the tables the sync writes (DELETE is used only by "Undo" of the app's own rows).
*/
USE [Preservation];
GO
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'NT SERVICE\PresApp')
    CREATE USER [NT SERVICE\PresApp] FOR LOGIN [NT SERVICE\PresApp];
GO
GRANT SELECT ON dbo.tbl_flangeCustomField              TO [NT SERVICE\PresApp];
GRANT SELECT ON dbo.tbl_installationCustomFlangeFields TO [NT SERVICE\PresApp];
GRANT SELECT ON dbo.tbl_locationBreakdown              TO [NT SERVICE\PresApp];
GRANT SELECT ON dbo.tbl_flangeCriticality              TO [NT SERVICE\PresApp];

GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.tbl_flange                  TO [NT SERVICE\PresApp];
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.tbl_flangeCustomFieldValue  TO [NT SERVICE\PresApp];
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.tbl_flangeHistory           TO [NT SERVICE\PresApp];
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.tbl_flangeChanges           TO [NT SERVICE\PresApp];
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.tbl_flangeCustomFieldChange TO [NT SERVICE\PresApp];
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.tbl_workpackItem            TO [NT SERVICE\PresApp];
GO
-- Check: lists what the account may do
SELECT p.permission_name, o.name AS table_name
FROM sys.database_permissions p
JOIN sys.objects o ON o.object_id = p.major_id
WHERE p.grantee_principal_id = DATABASE_PRINCIPAL_ID(N'NT SERVICE\PresApp')
ORDER BY o.name, p.permission_name;
