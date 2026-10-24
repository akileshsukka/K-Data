/*
  PresApp — automatic backups with SQL Server Agent (SQL Server 2022 Standard on EM-L-5236).
  Run ONCE in SSMS as sysadmin, after 01-create-database.sql.

  Creates job "PresApp - Backups":
    - Full backup every day at 02:00 (Dubai time = server time).
    - Differential backup every 4 hours.
    - Deletes local backup files older than 30 days.
    - Copies the backup folder to a second location (@SecondCopy) with robocopy.

  Before running, set the two folders below. The SQL Server service account needs write access
  to both (for a network share, use a domain account or ask IT).
*/
USE [msdb];
GO

DECLARE @LocalFolder nvarchar(260) = N'D:\PresApp\Backups';            -- local disk of the server
DECLARE @SecondCopy  nvarchar(260) = N'\\NAS\Backups\PresApp';          -- NAS, external disk or synced folder
DECLARE @JobName     sysname       = N'PresApp - Backups';

IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = @JobName)
    EXEC msdb.dbo.sp_delete_job @job_name = @JobName;

EXEC msdb.dbo.sp_add_job @job_name = @JobName, @description = N'Full daily, differential every 4 h, 30-day retention, second copy.';

DECLARE @full nvarchar(max) = N'
DECLARE @f nvarchar(400) = N''' + @LocalFolder + N'\PresApp_FULL_'' + FORMAT(SYSDATETIME(), ''yyyyMMdd_HHmm'') + N''.bak'';
IF DATEPART(HOUR, SYSDATETIME()) = 2
   OR NOT EXISTS (SELECT 1 FROM msdb.dbo.backupset WHERE database_name = N''PresApp'' AND type = ''D'' AND is_copy_only = 0)
    BACKUP DATABASE [PresApp] TO DISK = @f WITH COMPRESSION, CHECKSUM, INIT;
ELSE
BEGIN
    SET @f = REPLACE(@f, N''_FULL_'', N''_DIFF_'');
    BACKUP DATABASE [PresApp] TO DISK = @f WITH DIFFERENTIAL, COMPRESSION, CHECKSUM, INIT;
END';

EXEC msdb.dbo.sp_add_jobstep @job_name = @JobName, @step_name = N'Backup', @subsystem = N'TSQL',
     @database_name = N'master', @command = @full, @on_success_action = 3;

DECLARE @cleanup nvarchar(max) = N'
DECLARE @limit datetime = DATEADD(DAY, -30, GETDATE());
EXEC master.dbo.xp_delete_file 0, N''' + @LocalFolder + N''', N''bak'', @limit, 0;';

EXEC msdb.dbo.sp_add_jobstep @job_name = @JobName, @step_name = N'Delete old files', @subsystem = N'TSQL',
     @database_name = N'master', @command = @cleanup, @on_success_action = 3;

DECLARE @copy nvarchar(max) = N'cmd /c robocopy "' + @LocalFolder + N'" "' + @SecondCopy
     + N'" *.bak /MIR /R:2 /W:30 /NP & if %ERRORLEVEL% LSS 8 exit 0';

EXEC msdb.dbo.sp_add_jobstep @job_name = @JobName, @step_name = N'Second copy', @subsystem = N'CmdExec',
     @command = @copy, @on_success_action = 1;

-- Every 4 hours starting at 02:00 (02, 06, 10, 14, 18, 22): the 02:00 run is the full backup.
EXEC msdb.dbo.sp_add_jobschedule @job_name = @JobName, @name = N'Every 4 hours',
     @freq_type = 4, @freq_interval = 1, @freq_subday_type = 8, @freq_subday_interval = 4,
     @active_start_time = 020000;

EXEC msdb.dbo.sp_add_jobserver @job_name = @JobName, @server_name = N'(local)';
GO

/* First run right away: with no full backup yet, it takes a full one. */
EXEC msdb.dbo.sp_start_job @job_name = N'PresApp - Backups';
GO
