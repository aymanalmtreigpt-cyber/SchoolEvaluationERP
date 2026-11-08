using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;

namespace SchoolEvaluationERP.Services
{
    public static class DatabaseSchemaService
    {
        private static readonly SemaphoreSlim _schemaLock = new(1, 1);
        private static bool _isEnsured;

        public static async Task ForceReverifySchemaAsync(SqlConnection conn)
        {
            _isEnsured = false;
            await EnsureSchemaV2Async(conn);
        }

        public static async Task EnsureSchemaV2Async(SqlConnection conn)
        {
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync();
            }

            if (_isEnsured) return;

            await _schemaLock.WaitAsync();
            try
            {
                if (_isEnsured) return;

                int isV2Ready = await conn.ExecuteScalarAsync<int>(@"
                    SELECT CASE WHEN
                        OBJECT_ID('dbo.Users', 'U') IS NOT NULL
                        AND OBJECT_ID('dbo.Branches', 'U') IS NOT NULL
                        AND OBJECT_ID('dbo.Categories', 'U') IS NOT NULL
                        AND OBJECT_ID('dbo.Jobs', 'U') IS NOT NULL
                        AND OBJECT_ID('dbo.JobTypes', 'U') IS NOT NULL
                        AND OBJECT_ID('dbo.EvaluatorGroups', 'U') IS NOT NULL
                        AND OBJECT_ID('dbo.Evaluators', 'U') IS NOT NULL
                        AND OBJECT_ID('dbo.CategoryEvaluatorGroups', 'U') IS NOT NULL
                        AND OBJECT_ID('dbo.Employees', 'U') IS NOT NULL
                        AND OBJECT_ID('dbo.EmployeeEvaluators', 'U') IS NOT NULL
                        AND COL_LENGTH('dbo.Employees', 'HasIncentive') IS NOT NULL
                        AND COL_LENGTH('dbo.Employees', 'IsContinuing') IS NOT NULL
                        AND COL_LENGTH('dbo.Employees', 'JobTypeID') IS NOT NULL
                        AND COL_LENGTH('dbo.TemplateSheets', 'TargetBranchID') IS NOT NULL
                        AND COL_LENGTH('dbo.EvaluationRecords', 'WeekNumber') IS NULL
                    THEN 1 ELSE 0 END");

                if (isV2Ready == 0)
                {
                    await UpgradeToV2SchemaInternalAsync(conn);
                }

                await EnsureIncentiveAndBalancingExtensionsAsync(conn);

                _isEnsured = true;
            }
            finally
            {
                _schemaLock.Release();
            }
        }

        private static async Task EnsureIncentiveAndBalancingExtensionsAsync(SqlConnection conn)
        {
            await conn.ExecuteAsync(@"
                UPDATE dbo.UserPermissions
                SET ModuleArabicName = N'إدخال التقييمات الشهرية'
                WHERE ModuleCode = 'ScoreEntry' AND ModuleArabicName <> N'إدخال التقييمات الشهرية';

                IF COL_LENGTH('dbo.Categories', 'DepartmentID') IS NOT NULL
                BEGIN
                    IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Categories_Departments')
                        ALTER TABLE dbo.Categories DROP CONSTRAINT FK_Categories_Departments;
                    ALTER TABLE dbo.Categories ALTER COLUMN DepartmentID INT NULL;
                END

                IF COL_LENGTH('dbo.Employees', 'DepartmentID') IS NOT NULL
                BEGIN
                    IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Employees_Departments')
                        ALTER TABLE dbo.Employees DROP CONSTRAINT FK_Employees_Departments;
                    ALTER TABLE dbo.Employees ALTER COLUMN DepartmentID INT NULL;
                END

                DECLARE @OldUniqueName NVARCHAR(200);
                SELECT TOP 1 @OldUniqueName = kc.name
                FROM sys.key_constraints kc
                INNER JOIN sys.index_columns ic ON kc.parent_object_id = ic.object_id AND kc.unique_index_id = ic.index_id
                WHERE kc.parent_object_id = OBJECT_ID('dbo.Evaluators')
                  AND kc.type = 'UQ'
                GROUP BY kc.name
                HAVING COUNT(1) = 1;

                IF @OldUniqueName IS NOT NULL
                BEGIN
                    EXEC(N'ALTER TABLE dbo.Evaluators DROP CONSTRAINT [' + @OldUniqueName + N']');
                END

                IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_Group_EvaluatorName' AND parent_object_id = OBJECT_ID('dbo.Evaluators'))
                BEGIN
                    ALTER TABLE dbo.Evaluators ADD CONSTRAINT UQ_Group_EvaluatorName UNIQUE (GroupID, EvaluatorName);
                END

                IF COL_LENGTH('dbo.Categories', 'MaxIncentiveAmount') IS NULL
                BEGIN
                    ALTER TABLE dbo.Categories ADD MaxIncentiveAmount DECIMAL(10,2) NOT NULL CONSTRAINT DF_Categories_MaxInc DEFAULT (7000);
                END

                IF COL_LENGTH('dbo.Categories', 'MinIncentiveAmount') IS NULL
                BEGIN
                    ALTER TABLE dbo.Categories ADD MinIncentiveAmount DECIMAL(10,2) NOT NULL CONSTRAINT DF_Categories_MinInc DEFAULT (1500);
                END

                IF OBJECT_ID('dbo.EvaluationEmployeeAdjustments', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.EvaluationEmployeeAdjustments (
                        AdjustmentID INT IDENTITY(1,1) PRIMARY KEY,
                        RecordID INT NOT NULL,
                        SheetID INT NOT NULL,
                        EmployeeID INT NOT NULL,
                        BalancingDelta DECIMAL(6,2) NOT NULL DEFAULT 0,
                        CONSTRAINT FK_Adjustments_Records FOREIGN KEY (RecordID) REFERENCES dbo.EvaluationRecords(RecordID) ON DELETE CASCADE,
                        CONSTRAINT UQ_Adjustment_Rec_Sheet_Emp UNIQUE (RecordID, SheetID, EmployeeID)
                    );
                END

                INSERT INTO dbo.UserPermissions (UserID, ModuleCode, ModuleArabicName, CanRead, CanInsert, CanUpdate, CanDelete)
                SELECT u.UserID, 'IncentiveBasics', N'أساسيات الحوافز للفئات', u.IsAdmin, u.IsAdmin, u.IsAdmin, u.IsAdmin
                FROM dbo.Users u
                WHERE NOT EXISTS (
                    SELECT 1 FROM dbo.UserPermissions up WHERE up.UserID = u.UserID AND up.ModuleCode = 'IncentiveBasics'
                );

                INSERT INTO dbo.UserPermissions (UserID, ModuleCode, ModuleArabicName, CanRead, CanInsert, CanUpdate, CanDelete)
                SELECT u.UserID, 'ScoreBalancing', N'نسب الموازنة للتقييمات', u.IsAdmin, u.IsAdmin, u.IsAdmin, u.IsAdmin
                FROM dbo.Users u
                WHERE NOT EXISTS (
                    SELECT 1 FROM dbo.UserPermissions up WHERE up.UserID = u.UserID AND up.ModuleCode = 'ScoreBalancing'
                );
            ");
        }

        private static async Task UpgradeToV2SchemaInternalAsync(SqlConnection conn)
        {
            await conn.ExecuteAsync(@"
                IF OBJECT_ID('dbo.Users', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.Users (
                        UserID INT IDENTITY(1,1) PRIMARY KEY,
                        Username NVARCHAR(100) NOT NULL UNIQUE,
                        PasswordHash NVARCHAR(256) NOT NULL,
                        PasswordSalt NVARCHAR(128) NOT NULL,
                        FullName NVARCHAR(150) NOT NULL,
                        IsAdmin BIT NOT NULL DEFAULT 0,
                        MustChangePassword BIT NOT NULL DEFAULT 1,
                        IsActive BIT NOT NULL DEFAULT 1,
                        CreatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME()
                    );
                END

                IF OBJECT_ID('dbo.UserPermissions', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.UserPermissions (
                        PermissionID INT IDENTITY(1,1) PRIMARY KEY,
                        UserID INT NOT NULL,
                        ModuleCode VARCHAR(50) NOT NULL,
                        ModuleArabicName NVARCHAR(100) NOT NULL,
                        CanRead BIT NOT NULL DEFAULT 0,
                        CanInsert BIT NOT NULL DEFAULT 0,
                        CanUpdate BIT NOT NULL DEFAULT 0,
                        CanDelete BIT NOT NULL DEFAULT 0,
                        CONSTRAINT FK_UserPermissions_Users FOREIGN KEY (UserID) REFERENCES dbo.Users(UserID) ON DELETE CASCADE,
                        CONSTRAINT UQ_User_Module UNIQUE (UserID, ModuleCode)
                    );
                END

                IF OBJECT_ID('dbo.Branches', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.Branches (
                        BranchID INT IDENTITY(1,1) PRIMARY KEY,
                        BranchName NVARCHAR(150) NOT NULL UNIQUE,
                        IsActive BIT NOT NULL DEFAULT 1
                    );
                END");
        }
    }
}

