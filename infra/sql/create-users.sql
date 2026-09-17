-- Contained database users for the MLCP workload identities (ADR-019, ADR-026).
--
-- Run by the "<prefix>-<env>-<region>-create-users" Container Apps job as the migrator identity,
-- which is a member of the SQL admin Entra group. Run it after the migrate job has created the
-- roles:
--
--   sqlcmd -S tcp:<server>,1433 -d mlcp --authentication-method ActiveDirectoryManagedIdentity \
--          -U <migrator client id> -b -i /app/sql/create-users.sql \
--          -v WebUserName=mlcp_web_user WebClientId=<web identity client id> \
--             WorkerUserName=mlcp_worker_user WorkerClientId=<worker identity client id>
--
-- Users are created WITH SID = <client id>, TYPE = E, not FROM EXTERNAL PROVIDER. When the caller
-- is a service principal (the migrator), FROM EXTERNAL PROVIDER can only resolve the name if the
-- SQL server's own identity holds the Entra "Directory Readers" role. The SID form needs no
-- directory lookup. Azure SQL maps a managed identity to the user whose SID is the identity's
-- client (application) id, so we are free to choose the user name.
-- Source: https://learn.microsoft.com/en-us/sql/t-sql/statements/create-user-transact-sql
-- (example K, "without validation").
--
-- The script is idempotent: running it again changes nothing.

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DATABASE_PRINCIPAL_ID(N'mlcp_web') IS NULL
   OR DATABASE_PRINCIPAL_ID(N'mlcp_worker') IS NULL
   OR DATABASE_PRINCIPAL_ID(N'mlcp_system') IS NULL
    THROW 50001, 'Database roles mlcp_web, mlcp_worker and mlcp_system are missing. Run the migrate job first.', 1;

DECLARE @users TABLE (UserName sysname NOT NULL, ClientId uniqueidentifier NOT NULL, Roles nvarchar(200) NOT NULL);
INSERT INTO @users (UserName, ClientId, Roles) VALUES
    (N'$(WebUserName)',    CONVERT(uniqueidentifier, N'$(WebClientId)'),    N'mlcp_web'),
    (N'$(WorkerUserName)', CONVERT(uniqueidentifier, N'$(WorkerClientId)'), N'mlcp_worker,mlcp_system');

DECLARE @name sysname, @clientId uniqueidentifier, @roles nvarchar(200), @sid varbinary(16),
        @role sysname, @sql nvarchar(max);

DECLARE user_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT UserName, ClientId, Roles FROM @users;
OPEN user_cursor;
FETCH NEXT FROM user_cursor INTO @name, @clientId, @roles;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sid = CONVERT(varbinary(16), @clientId);

    IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @name AND sid <> @sid)
        THROW 50002, 'A database user with this name exists for a different identity. Drop it deliberately, then run again.', 1;

    IF DATABASE_PRINCIPAL_ID(@name) IS NULL
    BEGIN
        SET @sql = N'CREATE USER ' + QUOTENAME(@name)
                 + N' WITH SID = ' + CONVERT(nvarchar(64), @sid, 1) + N', TYPE = E;';
        EXEC (@sql);
        PRINT N'Created user ' + @name;
    END;

    -- Workload users get only their MLCP role(s): no db_datareader, no db_datawriter, no DDL.
    DECLARE role_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT LTRIM(RTRIM(value)) FROM STRING_SPLIT(@roles, N',');
    OPEN role_cursor;
    FETCH NEXT FROM role_cursor INTO @role;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF IS_ROLEMEMBER(@role, @name) = 0
        BEGIN
            SET @sql = N'ALTER ROLE ' + QUOTENAME(@role) + N' ADD MEMBER ' + QUOTENAME(@name) + N';';
            EXEC (@sql);
            PRINT N'Added ' + @name + N' to ' + @role;
        END;
        FETCH NEXT FROM role_cursor INTO @role;
    END;
    CLOSE role_cursor;
    DEALLOCATE role_cursor;

    FETCH NEXT FROM user_cursor INTO @name, @clientId, @roles;
END;

CLOSE user_cursor;
DEALLOCATE user_cursor;

-- The web user must never be in the system role, because the RLS system clause trusts
-- IS_MEMBER('mlcp_system').
IF IS_ROLEMEMBER(N'mlcp_system', N'$(WebUserName)') = 1
    THROW 50003, 'The web user is a member of mlcp_system. Remove it: this breaks tenant isolation.', 1;

SELECT dp.name AS UserName, r.name AS RoleName
FROM sys.database_role_members AS m
JOIN sys.database_principals AS dp ON dp.principal_id = m.member_principal_id
JOIN sys.database_principals AS r  ON r.principal_id  = m.role_principal_id
WHERE dp.name IN (N'$(WebUserName)', N'$(WorkerUserName)')
ORDER BY dp.name, r.name;
