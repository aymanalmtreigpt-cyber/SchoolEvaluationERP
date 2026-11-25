using System;
using System.Data;
using System.Diagnostics;
using System.IO;
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

        public static async Task EnsureDatabaseCreatedAsync(string connectionString)
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            string dbName = string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "SchoolEvaluationDB" : builder.InitialCatalog;

            if (builder.DataSource != null && builder.DataSource.Contains("localdb", StringComparison.OrdinalIgnoreCase))
            {
                TryStartLocalDbInstanceSilently();
            }

            var masterBuilder = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = "master",
                Pooling = false,
                ConnectTimeout = Math.Max(builder.ConnectTimeout, 30)
            };

            using var masterConn = new SqlConnection(masterBuilder.ConnectionString);
            await masterConn.OpenAsync();

            int exists = await masterConn.ExecuteScalarAsync<int>(
                "SELECT CASE WHEN DB_ID(@DbName) IS NOT NULL THEN 1 ELSE 0 END",
                new { DbName = dbName });

            if (exists == 0)
            {
                try
                {
                    await masterConn.ExecuteAsync($"CREATE DATABASE [{dbName}];", commandTimeout: 60);
                }
                catch (SqlException)
                {
                    string appDataFolder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "SchoolEvaluationERP_Data");
                    Directory.CreateDirectory(appDataFolder);

                    string mdfPath = Path.Combine(appDataFolder, $"{dbName}.mdf");
                    string ldfPath = Path.Combine(appDataFolder, $"{dbName}_log.ldf");

                    if (File.Exists(mdfPath))
                    {
                        string attachSql = $@"
                            CREATE DATABASE [{dbName}]
                            ON (FILENAME = N'{mdfPath}')
                            FOR ATTACH;";
                        await masterConn.ExecuteAsync(attachSql, commandTimeout: 60);
                    }
                    else
                    {
                        string createCustomPathSql = $@"
                            CREATE DATABASE [{dbName}]
                            ON PRIMARY (NAME = N'{dbName}', FILENAME = N'{mdfPath}')
                            LOG ON (NAME = N'{dbName}_log', FILENAME = N'{ldfPath}');";
                        await masterConn.ExecuteAsync(createCustomPathSql, commandTimeout: 60);
                    }
                }

                for (int i = 0; i < 20; i++)
                {
                    int state = await masterConn.ExecuteScalarAsync<int>(
                        "SELECT ISNULL((SELECT state FROM sys.databases WHERE name = @DbName), -1)",
                        new { DbName = dbName });
                    if (state == 0) break;
                    await Task.Delay(250);
                }
            }
            else
            {
                try
                {
                    await masterConn.ExecuteAsync($@"
                        IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'{dbName}' AND user_access <> 0)
                        BEGIN
                            ALTER DATABASE [{dbName}] SET MULTI_USER WITH ROLLBACK IMMEDIATE;
                        END");
                }
                catch { }
            }
        }

        private static void TryStartLocalDbInstanceSilently()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sqllocaldb",
                    Arguments = "start MSSQLLocalDB",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(10000);
            }
            catch { }
        }

        public static async Task EnsureSchemaV2Async(SqlConnection conn)
        {
            if (!_isEnsured)
            {
                try
                {
                    await EnsureDatabaseCreatedAsync(conn.ConnectionString);
                }
                catch { }
            }

            if (conn.State != ConnectionState.Open)
            {
                try
                {
                    await conn.OpenAsync();
                }
                catch (SqlException)
                {
                    await EnsureDatabaseCreatedAsync(conn.ConnectionString);
                    SqlConnection.ClearAllPools();

                    var retryBuilder = new SqlConnectionStringBuilder(conn.ConnectionString);
                    if (retryBuilder.ConnectTimeout < 30)
                    {
                        retryBuilder.ConnectTimeout = 30;
                        conn.ConnectionString = retryBuilder.ConnectionString;
                    }
                    await conn.OpenAsync();
                }
            }

            if (_isEnsured) return;

            await _schemaLock.WaitAsync();
            try
            {
                if (_isEnsured) return;

                await UpgradeToV2SchemaInternalAsync(conn);
                await EnsureIncentiveAndBalancingExtensionsAsync(conn);

                _isEnsured = true;
            }
            finally
            {
                _schemaLock.Release();
            }
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
                END

                IF OBJECT_ID('dbo.Departments', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.Departments (
                        DepartmentID INT IDENTITY(1,1) PRIMARY KEY,
                        DepartmentName NVARCHAR(150) NOT NULL UNIQUE,
                        DepartmentType NVARCHAR(100) NOT NULL DEFAULT N'أكاديمي',
                        IsActive BIT NOT NULL DEFAULT 1
                    );
                END

                IF OBJECT_ID('dbo.Categories', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.Categories (
                        CategoryID INT IDENTITY(1,1) PRIMARY KEY,
                        DepartmentID INT NULL,
                        CategoryName NVARCHAR(150) NOT NULL UNIQUE,
                        MaxIncentiveAmount DECIMAL(10,2) NOT NULL CONSTRAINT DF_Categories_MaxInc DEFAULT (0),
                        MinIncentiveAmount DECIMAL(10,2) NOT NULL CONSTRAINT DF_Categories_MinInc DEFAULT (0),
                        IsActive BIT NOT NULL DEFAULT 1
                    );
                END

                IF OBJECT_ID('dbo.MonthlyCategoryIncentives', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.MonthlyCategoryIncentives (
                        MonthlyIncentiveID INT IDENTITY(1,1) PRIMARY KEY,
                        EvalYear INT NOT NULL CHECK (EvalYear >= 2020 AND EvalYear <= 2100),
                        EvalMonth INT NOT NULL CHECK (EvalMonth BETWEEN 1 AND 12),
                        CategoryID INT NOT NULL,
                        MaxIncentiveAmount DECIMAL(10,2) NOT NULL DEFAULT 0,
                        MinIncentiveAmount DECIMAL(10,2) NOT NULL DEFAULT 0,
                        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
                        CONSTRAINT FK_MonthlyCatInc_Categories FOREIGN KEY (CategoryID) REFERENCES dbo.Categories(CategoryID) ON DELETE CASCADE,
                        CONSTRAINT UQ_MonthlyCatInc_Year_Month_Cat UNIQUE (EvalYear, EvalMonth, CategoryID)
                    );
                END

                IF OBJECT_ID('dbo.MonthlyCategoryEvaluatorGroups', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.MonthlyCategoryEvaluatorGroups (
                        MonthlyCatEvalGrpID INT IDENTITY(1,1) PRIMARY KEY,
                        EvalYear INT NOT NULL CHECK (EvalYear >= 2020 AND EvalYear <= 2100),
                        EvalMonth INT NOT NULL CHECK (EvalMonth BETWEEN 1 AND 12),
                        CategoryID INT NOT NULL,
                        GroupID INT NOT NULL,
                        WeightScore DECIMAL(5,2) NOT NULL CHECK (WeightScore > 0 AND WeightScore <= 100),
                        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
                        CONSTRAINT UQ_MonthlyCatEvalGrp UNIQUE (EvalYear, EvalMonth, CategoryID, GroupID)
                    );
                END

                IF OBJECT_ID('dbo.MonthlyEmployeeEvaluators', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.MonthlyEmployeeEvaluators (
                        MonthlyEmpEvalID INT IDENTITY(1,1) PRIMARY KEY,
                        EvalYear INT NOT NULL CHECK (EvalYear >= 2020 AND EvalYear <= 2100),
                        EvalMonth INT NOT NULL CHECK (EvalMonth BETWEEN 1 AND 12),
                        EmployeeID INT NOT NULL,
                        EvaluatorID INT NOT NULL,
                        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
                        CONSTRAINT UQ_MonthlyEmpEval UNIQUE (EvalYear, EvalMonth, EmployeeID, EvaluatorID)
                    );
                END

                IF OBJECT_ID('dbo.MonthlyEmployeeSnapshots', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.MonthlyEmployeeSnapshots (
                        MonthlyEmpSnapID INT IDENTITY(1,1) PRIMARY KEY,
                        EvalYear INT NOT NULL CHECK (EvalYear >= 2020 AND EvalYear <= 2100),
                        EvalMonth INT NOT NULL CHECK (EvalMonth BETWEEN 1 AND 12),
                        EmployeeID INT NOT NULL,
                        CategoryID INT NOT NULL,
                        HasIncentive BIT NOT NULL DEFAULT 1,
                        IsContinuing BIT NOT NULL DEFAULT 1,
                        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
                        CONSTRAINT UQ_MonthlyEmpSnap UNIQUE (EvalYear, EvalMonth, EmployeeID)
                    );
                END

                IF OBJECT_ID('dbo.TemplateExcludedEmployees', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.TemplateExcludedEmployees (
                        TemplateExcludedID INT IDENTITY(1,1) PRIMARY KEY,
                        EvaluatorID INT NOT NULL,
                        EmployeeID INT NOT NULL,
                        CONSTRAINT UQ_TmplExcl_Eval_Emp UNIQUE (EvaluatorID, EmployeeID)
                    );
                END

                IF OBJECT_ID('dbo.TemplateAssignedUsers', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.TemplateAssignedUsers (
                        TemplateAssignedUserID INT IDENTITY(1,1) PRIMARY KEY,
                        EvaluatorID INT NOT NULL,
                        UserID INT NOT NULL,
                        AssignedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
                        CONSTRAINT UQ_TmplAssignedUser_Eval_User UNIQUE (EvaluatorID, UserID)
                    );
                END

                IF OBJECT_ID('dbo.MonthlyEvaluatorTemplates', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.MonthlyEvaluatorTemplates (
                        MonthlyEvalTemplateID INT IDENTITY(1,1) PRIMARY KEY,
                        EvalYear INT NOT NULL CHECK (EvalYear >= 2020 AND EvalYear <= 2100),
                        EvalMonth INT NOT NULL CHECK (EvalMonth BETWEEN 1 AND 12),
                        EvaluatorID INT NOT NULL,
                        TemplateID INT NOT NULL,
                        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
                        CONSTRAINT UQ_MonthlyEvalTemplate UNIQUE (EvalYear, EvalMonth, EvaluatorID)
                    );
                END

                IF OBJECT_ID('dbo.SystemSettings', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.SystemSettings (
                        SettingKey NVARCHAR(100) NOT NULL PRIMARY KEY,
                        SettingValue NVARCHAR(MAX) NULL,
                        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME()
                    );
                END

                IF OBJECT_ID('dbo.MonthlyClosedReports', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.MonthlyClosedReports (
                        ClosedReportID INT IDENTITY(1,1) PRIMARY KEY,
                        EvalYear INT NOT NULL CHECK (EvalYear >= 2020 AND EvalYear <= 2100),
                        EvalMonth INT NOT NULL CHECK (EvalMonth BETWEEN 1 AND 12),
                        IsLocked BIT NOT NULL DEFAULT 1,
                        LockedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
                        LockedByUserName NVARCHAR(150) NOT NULL DEFAULT N'مدير النظام العام',
                        SnapshotJson NVARCHAR(MAX) NULL,
                        CONSTRAINT UQ_MonthlyClosedReport UNIQUE (EvalYear, EvalMonth)
                    );
                END

                IF OBJECT_ID('dbo.Jobs', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.Jobs (
                        JobID INT IDENTITY(1,1) PRIMARY KEY,
                        CategoryID INT NOT NULL,
                        JobTitle NVARCHAR(150) NOT NULL,
                        IsActive BIT NOT NULL DEFAULT 1,
                        CONSTRAINT FK_Jobs_Categories FOREIGN KEY (CategoryID) REFERENCES dbo.Categories(CategoryID),
                        CONSTRAINT UQ_Category_Job UNIQUE (CategoryID, JobTitle)
                    );
                END

                IF OBJECT_ID('dbo.JobTypes', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.JobTypes (
                        JobTypeID INT IDENTITY(1,1) PRIMARY KEY,
                        JobID INT NOT NULL,
                        JobTypeName NVARCHAR(150) NOT NULL,
                        IsActive BIT NOT NULL DEFAULT 1,
                        CONSTRAINT FK_JobTypes_Jobs FOREIGN KEY (JobID) REFERENCES dbo.Jobs(JobID),
                        CONSTRAINT UQ_Job_JobType UNIQUE (JobID, JobTypeName)
                    );
                END

                IF OBJECT_ID('dbo.EvaluatorGroups', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.EvaluatorGroups (
                        GroupID INT IDENTITY(1,1) PRIMARY KEY,
                        GroupName NVARCHAR(150) NOT NULL UNIQUE,
                        IsActive BIT NOT NULL DEFAULT 1,
                        IsSystemGroup BIT NOT NULL CONSTRAINT DF_EvalGrp_IsSys DEFAULT (0),
                        SystemGroupCode VARCHAR(30) NULL,
                        MaxAllowedCount INT NOT NULL CONSTRAINT DF_EvalGrp_MaxCnt DEFAULT (3)
                    );
                END

                IF OBJECT_ID('dbo.Evaluators', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.Evaluators (
                        EvaluatorID INT IDENTITY(1,1) PRIMARY KEY,
                        GroupID INT NOT NULL,
                        EvaluatorName NVARCHAR(150) NOT NULL,
                        IsActive BIT NOT NULL DEFAULT 1,
                        CONSTRAINT FK_Evaluators_Groups FOREIGN KEY (GroupID) REFERENCES dbo.EvaluatorGroups(GroupID),
                        CONSTRAINT UQ_Group_EvaluatorName UNIQUE (GroupID, EvaluatorName)
                    );
                END

                IF OBJECT_ID('dbo.CategoryEvaluatorGroups', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.CategoryEvaluatorGroups (
                        CategoryEvaluatorGroupID INT IDENTITY(1,1) PRIMARY KEY,
                        CategoryID INT NOT NULL,
                        GroupID INT NOT NULL,
                        WeightScore DECIMAL(5,2) NOT NULL CHECK (WeightScore > 0 AND WeightScore <= 100),
                        CONSTRAINT FK_CatEvalGroup_Categories FOREIGN KEY (CategoryID) REFERENCES dbo.Categories(CategoryID) ON DELETE CASCADE,
                        CONSTRAINT FK_CatEvalGroup_Groups FOREIGN KEY (GroupID) REFERENCES dbo.EvaluatorGroups(GroupID),
                        CONSTRAINT UQ_Category_EvalGroup UNIQUE (CategoryID, GroupID)
                    );
                END

                IF OBJECT_ID('dbo.Employees', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.Employees (
                        EmployeeID INT IDENTITY(1,1) PRIMARY KEY,
                        SystemName NVARCHAR(250) NOT NULL UNIQUE,
                        EvaluationName NVARCHAR(200) NOT NULL UNIQUE,
                        BranchID INT NOT NULL,
                        DepartmentID INT NULL,
                        CategoryID INT NOT NULL,
                        JobID INT NOT NULL,
                        JobTypeID INT NOT NULL,
                        HasIncentive BIT NOT NULL DEFAULT 1,
                        IsContinuing BIT NOT NULL DEFAULT 1,
                        IsActive BIT NOT NULL DEFAULT 1,
                        CreatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
                        CONSTRAINT FK_Employees_Branches FOREIGN KEY (BranchID) REFERENCES dbo.Branches(BranchID),
                        CONSTRAINT FK_Employees_Categories FOREIGN KEY (CategoryID) REFERENCES dbo.Categories(CategoryID),
                        CONSTRAINT FK_Employees_Jobs FOREIGN KEY (JobID) REFERENCES dbo.Jobs(JobID),
                        CONSTRAINT FK_Employees_JobTypes FOREIGN KEY (JobTypeID) REFERENCES dbo.JobTypes(JobTypeID)
                    );
                END

                IF OBJECT_ID('dbo.EmployeeEvaluators', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.EmployeeEvaluators (
                        EmployeeEvaluatorID INT IDENTITY(1,1) PRIMARY KEY,
                        EmployeeID INT NOT NULL,
                        EvaluatorID INT NOT NULL,
                        CONSTRAINT FK_EmpEval_Employees FOREIGN KEY (EmployeeID) REFERENCES dbo.Employees(EmployeeID) ON DELETE CASCADE,
                        CONSTRAINT FK_EmpEval_Evaluators FOREIGN KEY (EvaluatorID) REFERENCES dbo.Evaluators(EvaluatorID),
                        CONSTRAINT UQ_Employee_Evaluator UNIQUE (EmployeeID, EvaluatorID)
                    );
                END

                IF OBJECT_ID('dbo.Templates', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.Templates (
                        TemplateID INT IDENTITY(1,1) PRIMARY KEY,
                        TemplateGroupGuid UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
                        EvaluatorID INT NOT NULL,
                        VersionNumber INT NOT NULL DEFAULT 1,
                        IsCurrentVersion BIT NOT NULL DEFAULT 1,
                        IsDeleted BIT NOT NULL DEFAULT 0,
                        CreatedByUserID INT NULL,
                        CreatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
                        CONSTRAINT FK_Templates_Evaluators FOREIGN KEY (EvaluatorID) REFERENCES dbo.Evaluators(EvaluatorID)
                    );
                END

                IF OBJECT_ID('dbo.TemplateSheets', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.TemplateSheets (
                        SheetID INT IDENTITY(1,1) PRIMARY KEY,
                        TemplateID INT NOT NULL,
                        SheetName NVARCHAR(150) NOT NULL,
                        TargetCategoryID INT NOT NULL,
                        TargetBranchID INT NULL,
                        TargetCategoryName NVARCHAR(150) NOT NULL,
                        SortOrder INT NOT NULL DEFAULT 1,
                        CONSTRAINT FK_Sheets_Templates FOREIGN KEY (TemplateID) REFERENCES dbo.Templates(TemplateID) ON DELETE CASCADE,
                        CONSTRAINT FK_Sheets_Categories FOREIGN KEY (TargetCategoryID) REFERENCES dbo.Categories(CategoryID),
                        CONSTRAINT FK_Sheets_Branches FOREIGN KEY (TargetBranchID) REFERENCES dbo.Branches(BranchID)
                    );
                END

                IF OBJECT_ID('dbo.SheetCriteria', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.SheetCriteria (
                        CriteriaID INT IDENTITY(1,1) PRIMARY KEY,
                        SheetID INT NOT NULL,
                        CriteriaTitle NVARCHAR(150) NOT NULL DEFAULT N'قواعد التقييم',
                        MaxScore DECIMAL(5,2) NOT NULL DEFAULT 0 CHECK (MaxScore >= 0 AND MaxScore <= 100),
                        ColumnOrder INT NOT NULL,
                        CONSTRAINT FK_Criteria_Sheets FOREIGN KEY (SheetID) REFERENCES dbo.TemplateSheets(SheetID) ON DELETE CASCADE
                    );
                END

                IF OBJECT_ID('dbo.SheetEmployees', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.SheetEmployees (
                        SheetEmployeeID INT IDENTITY(1,1) PRIMARY KEY,
                        SheetID INT NOT NULL,
                        EmployeeID INT NOT NULL,
                        RowOrder INT NOT NULL,
                        CONSTRAINT FK_SheetEmp_Sheets FOREIGN KEY (SheetID) REFERENCES dbo.TemplateSheets(SheetID) ON DELETE CASCADE,
                        CONSTRAINT FK_SheetEmp_Employees FOREIGN KEY (EmployeeID) REFERENCES dbo.Employees(EmployeeID),
                        CONSTRAINT UQ_Sheet_Employee UNIQUE (SheetID, EmployeeID)
                    );
                END

                IF OBJECT_ID('dbo.EvaluationRecords', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.EvaluationRecords (
                        RecordID INT IDENTITY(1,1) PRIMARY KEY,
                        TemplateID INT NOT NULL,
                        EvaluatorID INT NOT NULL,
                        EvalYear INT NOT NULL CHECK (EvalYear >= 2020 AND EvalYear <= 2100),
                        EvalMonth INT NOT NULL CHECK (EvalMonth BETWEEN 1 AND 12),
                        CreatedByUserID INT NOT NULL,
                        CreatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
                        LastModifiedByUserID INT NULL,
                        LastModifiedAt DATETIME2 NULL,
                        SnapshotEvaluatorName NVARCHAR(150) NULL,
                        SnapshotGroupName NVARCHAR(150) NULL,
                        SnapshotVersionNumber INT NOT NULL CONSTRAINT DF_EvalRec_SnapVer DEFAULT (1),
                        SnapshotCreatedByFullName NVARCHAR(150) NULL,
                        RowVersion TIMESTAMP,
                        CONSTRAINT UQ_Template_Year_Month UNIQUE (TemplateID, EvalYear, EvalMonth)
                    );
                END

                IF OBJECT_ID('dbo.EvaluationScores', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.EvaluationScores (
                        ScoreID BIGINT IDENTITY(1,1) PRIMARY KEY,
                        RecordID INT NOT NULL,
                        SheetID INT NOT NULL,
                        EmployeeID INT NOT NULL,
                        CriteriaID INT NOT NULL,
                        GivenScore DECIMAL(5,2) NULL CHECK (GivenScore IS NULL OR GivenScore >= 0),
                        SnapshotEvaluatorWeight DECIMAL(5,2) NOT NULL,
                        SnapshotEvaluationName NVARCHAR(200) NULL,
                        SnapshotSystemName NVARCHAR(250) NULL,
                        SnapshotBranchName NVARCHAR(150) NULL,
                        SnapshotCategoryName NVARCHAR(150) NULL,
                        SnapshotJobTitle NVARCHAR(150) NULL,
                        SnapshotJobTypeName NVARCHAR(150) NULL,
                        SnapshotHasIncentive BIT NOT NULL CONSTRAINT DF_EvalScore_SnapInc DEFAULT (1),
                        SnapshotSheetName NVARCHAR(150) NULL,
                        SnapshotTargetCategoryName NVARCHAR(150) NULL,
                        SnapshotSheetSortOrder INT NOT NULL CONSTRAINT DF_EvalScore_SnapSheetOrd DEFAULT (1),
                        SnapshotCriteriaTitle NVARCHAR(150) NULL,
                        SnapshotCriteriaMaxScore DECIMAL(5,2) NOT NULL CONSTRAINT DF_EvalScore_SnapMax DEFAULT (100),
                        SnapshotCriteriaOrder INT NOT NULL CONSTRAINT DF_EvalScore_SnapCritOrd DEFAULT (1),
                        RawEnteredCount INT NULL,
                        CONSTRAINT FK_Scores_Records FOREIGN KEY (RecordID) REFERENCES dbo.EvaluationRecords(RecordID) ON DELETE CASCADE,
                        CONSTRAINT UQ_Record_Emp_Criteria UNIQUE (RecordID, EmployeeID, CriteriaID)
                    );
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
            ");
        }

        private static async Task EnsureIncentiveAndBalancingExtensionsAsync(SqlConnection conn)
        {
            // الدفعة الأولى (DDL): إضافة جميع الأعمدة والجداول الجديدة أولاً بشكل مستقل قبل استخدامها في أي استعلام
            await conn.ExecuteAsync(@"
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
                  AND kc.name <> 'UQ_Group_EvaluatorName'
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
                    ALTER TABLE dbo.Categories ADD MaxIncentiveAmount DECIMAL(10,2) NOT NULL CONSTRAINT DF_Categories_MaxInc DEFAULT (7000);

                IF COL_LENGTH('dbo.Categories', 'MinIncentiveAmount') IS NULL
                    ALTER TABLE dbo.Categories ADD MinIncentiveAmount DECIMAL(10,2) NOT NULL CONSTRAINT DF_Categories_MinInc DEFAULT (1500);

                IF COL_LENGTH('dbo.EvaluationRecords', 'SnapshotEvaluatorName') IS NULL
                    ALTER TABLE dbo.EvaluationRecords ADD SnapshotEvaluatorName NVARCHAR(150) NULL;
                IF COL_LENGTH('dbo.EvaluationRecords', 'SnapshotGroupName') IS NULL
                    ALTER TABLE dbo.EvaluationRecords ADD SnapshotGroupName NVARCHAR(150) NULL;
                IF COL_LENGTH('dbo.EvaluationRecords', 'SnapshotVersionNumber') IS NULL
                    ALTER TABLE dbo.EvaluationRecords ADD SnapshotVersionNumber INT NOT NULL CONSTRAINT DF_EvalRec_SnapVer DEFAULT (1);
                IF COL_LENGTH('dbo.EvaluationRecords', 'SnapshotCreatedByFullName') IS NULL
                    ALTER TABLE dbo.EvaluationRecords ADD SnapshotCreatedByFullName NVARCHAR(150) NULL;

                IF COL_LENGTH('dbo.EvaluationScores', 'SnapshotEvaluationName') IS NULL
                    ALTER TABLE dbo.EvaluationScores ADD SnapshotEvaluationName NVARCHAR(200) NULL;
                IF COL_LENGTH('dbo.EvaluationScores', 'SnapshotSystemName') IS NULL
                    ALTER TABLE dbo.EvaluationScores ADD SnapshotSystemName NVARCHAR(250) NULL;
                IF COL_LENGTH('dbo.EvaluationScores', 'SnapshotBranchName') IS NULL
                    ALTER TABLE dbo.EvaluationScores ADD SnapshotBranchName NVARCHAR(150) NULL;
                IF COL_LENGTH('dbo.EvaluationScores', 'SnapshotCategoryName') IS NULL
                    ALTER TABLE dbo.EvaluationScores ADD SnapshotCategoryName NVARCHAR(150) NULL;
                IF COL_LENGTH('dbo.EvaluationScores', 'SnapshotJobTitle') IS NULL
                    ALTER TABLE dbo.EvaluationScores ADD SnapshotJobTitle NVARCHAR(150) NULL;
                IF COL_LENGTH('dbo.EvaluationScores', 'SnapshotJobTypeName') IS NULL
                    ALTER TABLE dbo.EvaluationScores ADD SnapshotJobTypeName NVARCHAR(150) NULL;
                IF COL_LENGTH('dbo.EvaluationScores', 'SnapshotHasIncentive') IS NULL
                    ALTER TABLE dbo.EvaluationScores ADD SnapshotHasIncentive BIT NOT NULL CONSTRAINT DF_EvalScore_SnapInc DEFAULT (1);
                IF COL_LENGTH('dbo.EvaluationScores', 'SnapshotSheetName') IS NULL
                    ALTER TABLE dbo.EvaluationScores ADD SnapshotSheetName NVARCHAR(150) NULL;
                IF COL_LENGTH('dbo.EvaluationScores', 'SnapshotTargetCategoryName') IS NULL
                    ALTER TABLE dbo.EvaluationScores ADD SnapshotTargetCategoryName NVARCHAR(150) NULL;
                IF COL_LENGTH('dbo.EvaluationScores', 'SnapshotSheetSortOrder') IS NULL
                    ALTER TABLE dbo.EvaluationScores ADD SnapshotSheetSortOrder INT NOT NULL CONSTRAINT DF_EvalScore_SnapSheetOrd DEFAULT (1);
                IF COL_LENGTH('dbo.EvaluationScores', 'SnapshotCriteriaTitle') IS NULL
                    ALTER TABLE dbo.EvaluationScores ADD SnapshotCriteriaTitle NVARCHAR(150) NULL;
                IF COL_LENGTH('dbo.EvaluationScores', 'SnapshotCriteriaMaxScore') IS NULL
                    ALTER TABLE dbo.EvaluationScores ADD SnapshotCriteriaMaxScore DECIMAL(5,2) NOT NULL CONSTRAINT DF_EvalScore_SnapMax DEFAULT (100);
                IF COL_LENGTH('dbo.EvaluationScores', 'SnapshotCriteriaOrder') IS NULL
                    ALTER TABLE dbo.EvaluationScores ADD SnapshotCriteriaOrder INT NOT NULL CONSTRAINT DF_EvalScore_SnapCritOrd DEFAULT (1);
                IF COL_LENGTH('dbo.EvaluationScores', 'RawEnteredCount') IS NULL
                    ALTER TABLE dbo.EvaluationScores ADD RawEnteredCount INT NULL;

                IF EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.EvaluationScores')
                      AND name = 'GivenScore'
                      AND is_nullable = 0
                )
                BEGIN
                    DECLARE @ckGivenName NVARCHAR(256);
                    DECLARE ck_given_cur CURSOR LOCAL FAST_FORWARD FOR
                        SELECT cc.name
                        FROM sys.check_constraints cc
                        LEFT JOIN sys.columns c
                            ON c.object_id = cc.parent_object_id AND c.column_id = cc.parent_column_id
                        WHERE cc.parent_object_id = OBJECT_ID('dbo.EvaluationScores')
                          AND (c.name = 'GivenScore' OR cc.definition LIKE '%GivenScore%');
                    OPEN ck_given_cur;
                    FETCH NEXT FROM ck_given_cur INTO @ckGivenName;
                    WHILE @@FETCH_STATUS = 0
                    BEGIN
                        EXEC(N'ALTER TABLE dbo.EvaluationScores DROP CONSTRAINT [' + @ckGivenName + N'];');
                        FETCH NEXT FROM ck_given_cur INTO @ckGivenName;
                    END
                    CLOSE ck_given_cur;
                    DEALLOCATE ck_given_cur;

                    ALTER TABLE dbo.EvaluationScores ALTER COLUMN GivenScore DECIMAL(5,2) NULL;
                    IF OBJECT_ID('dbo.CK_EvaluationScores_GivenScore_NonNeg', 'C') IS NULL
                        ALTER TABLE dbo.EvaluationScores ADD CONSTRAINT CK_EvaluationScores_GivenScore_NonNeg CHECK (GivenScore IS NULL OR GivenScore >= 0);
                END

                IF COL_LENGTH('dbo.EvaluatorGroups', 'IsSystemGroup') IS NULL
                    ALTER TABLE dbo.EvaluatorGroups ADD IsSystemGroup BIT NOT NULL CONSTRAINT DF_EvalGrp_IsSys DEFAULT (0);
                IF COL_LENGTH('dbo.EvaluatorGroups', 'SystemGroupCode') IS NULL
                    ALTER TABLE dbo.EvaluatorGroups ADD SystemGroupCode VARCHAR(30) NULL;
                IF COL_LENGTH('dbo.EvaluatorGroups', 'MaxAllowedCount') IS NULL
                    ALTER TABLE dbo.EvaluatorGroups ADD MaxAllowedCount INT NOT NULL CONSTRAINT DF_EvalGrp_MaxCnt DEFAULT (3);

                IF OBJECT_ID('dbo.SystemTemplateExcludedEmployees', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.SystemTemplateExcludedEmployees (
                        ExcludedID INT IDENTITY(1,1) PRIMARY KEY,
                        GroupID INT NOT NULL,
                        EmployeeID INT NOT NULL,
                        CONSTRAINT UQ_SysExcl_Group_Emp UNIQUE (GroupID, EmployeeID)
                    );
                END

                IF OBJECT_ID('dbo.MonthlyCategoryIncentives', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.MonthlyCategoryIncentives (
                        MonthlyIncentiveID INT IDENTITY(1,1) PRIMARY KEY,
                        EvalYear INT NOT NULL CHECK (EvalYear >= 2020 AND EvalYear <= 2100),
                        EvalMonth INT NOT NULL CHECK (EvalMonth BETWEEN 1 AND 12),
                        CategoryID INT NOT NULL,
                        MaxIncentiveAmount DECIMAL(10,2) NOT NULL DEFAULT 0,
                        MinIncentiveAmount DECIMAL(10,2) NOT NULL DEFAULT 0,
                        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
                        CONSTRAINT FK_MonthlyCatInc_Categories FOREIGN KEY (CategoryID) REFERENCES dbo.Categories(CategoryID) ON DELETE CASCADE,
                        CONSTRAINT UQ_MonthlyCatInc_Year_Month_Cat UNIQUE (EvalYear, EvalMonth, CategoryID)
                    );
                END

                IF OBJECT_ID('dbo.MonthlyCategoryEvaluatorGroups', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.MonthlyCategoryEvaluatorGroups (
                        MonthlyCatEvalGrpID INT IDENTITY(1,1) PRIMARY KEY,
                        EvalYear INT NOT NULL CHECK (EvalYear >= 2020 AND EvalYear <= 2100),
                        EvalMonth INT NOT NULL CHECK (EvalMonth BETWEEN 1 AND 12),
                        CategoryID INT NOT NULL,
                        GroupID INT NOT NULL,
                        WeightScore DECIMAL(5,2) NOT NULL CHECK (WeightScore > 0 AND WeightScore <= 100),
                        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
                        CONSTRAINT UQ_MonthlyCatEvalGrp UNIQUE (EvalYear, EvalMonth, CategoryID, GroupID)
                    );
                END

                IF OBJECT_ID('dbo.MonthlyEmployeeEvaluators', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.MonthlyEmployeeEvaluators (
                        MonthlyEmpEvalID INT IDENTITY(1,1) PRIMARY KEY,
                        EvalYear INT NOT NULL CHECK (EvalYear >= 2020 AND EvalYear <= 2100),
                        EvalMonth INT NOT NULL CHECK (EvalMonth BETWEEN 1 AND 12),
                        EmployeeID INT NOT NULL,
                        EvaluatorID INT NOT NULL,
                        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
                        CONSTRAINT UQ_MonthlyEmpEval UNIQUE (EvalYear, EvalMonth, EmployeeID, EvaluatorID)
                    );
                END

                IF OBJECT_ID('dbo.MonthlyEmployeeSnapshots', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.MonthlyEmployeeSnapshots (
                        MonthlyEmpSnapID INT IDENTITY(1,1) PRIMARY KEY,
                        EvalYear INT NOT NULL CHECK (EvalYear >= 2020 AND EvalYear <= 2100),
                        EvalMonth INT NOT NULL CHECK (EvalMonth BETWEEN 1 AND 12),
                        EmployeeID INT NOT NULL,
                        CategoryID INT NOT NULL,
                        HasIncentive BIT NOT NULL DEFAULT 1,
                        IsContinuing BIT NOT NULL DEFAULT 1,
                        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
                        CONSTRAINT UQ_MonthlyEmpSnap UNIQUE (EvalYear, EvalMonth, EmployeeID)
                    );
                END

                IF OBJECT_ID('dbo.TemplateExcludedEmployees', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.TemplateExcludedEmployees (
                        TemplateExcludedID INT IDENTITY(1,1) PRIMARY KEY,
                        EvaluatorID INT NOT NULL,
                        EmployeeID INT NOT NULL,
                        CONSTRAINT UQ_TmplExcl_Eval_Emp UNIQUE (EvaluatorID, EmployeeID)
                    );
                END

                IF OBJECT_ID('dbo.TemplateAssignedUsers', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.TemplateAssignedUsers (
                        TemplateAssignedUserID INT IDENTITY(1,1) PRIMARY KEY,
                        EvaluatorID INT NOT NULL,
                        UserID INT NOT NULL,
                        AssignedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
                        CONSTRAINT UQ_TmplAssignedUser_Eval_User UNIQUE (EvaluatorID, UserID)
                    );
                END

                IF OBJECT_ID('dbo.MonthlyEvaluatorTemplates', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.MonthlyEvaluatorTemplates (
                        MonthlyEvalTemplateID INT IDENTITY(1,1) PRIMARY KEY,
                        EvalYear INT NOT NULL CHECK (EvalYear >= 2020 AND EvalYear <= 2100),
                        EvalMonth INT NOT NULL CHECK (EvalMonth BETWEEN 1 AND 12),
                        EvaluatorID INT NOT NULL,
                        TemplateID INT NOT NULL,
                        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
                        CONSTRAINT UQ_MonthlyEvalTemplate UNIQUE (EvalYear, EvalMonth, EvaluatorID)
                    );
                END

                IF OBJECT_ID('dbo.SystemSettings', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.SystemSettings (
                        SettingKey NVARCHAR(100) NOT NULL PRIMARY KEY,
                        SettingValue NVARCHAR(MAX) NULL,
                        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME()
                    );
                END

                IF OBJECT_ID('dbo.MonthlyClosedReports', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.MonthlyClosedReports (
                        ClosedReportID INT IDENTITY(1,1) PRIMARY KEY,
                        EvalYear INT NOT NULL CHECK (EvalYear >= 2020 AND EvalYear <= 2100),
                        EvalMonth INT NOT NULL CHECK (EvalMonth BETWEEN 1 AND 12),
                        IsLocked BIT NOT NULL DEFAULT 1,
                        LockedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
                        LockedByUserName NVARCHAR(150) NOT NULL DEFAULT N'مدير النظام العام',
                        SnapshotJson NVARCHAR(MAX) NULL,
                        CONSTRAINT UQ_MonthlyClosedReport UNIQUE (EvalYear, EvalMonth)
                    );
                END

                IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Records_Templates')
                    ALTER TABLE dbo.EvaluationRecords DROP CONSTRAINT FK_Records_Templates;
                IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Records_Evaluators')
                    ALTER TABLE dbo.EvaluationRecords DROP CONSTRAINT FK_Records_Evaluators;
                IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Records_Users')
                    ALTER TABLE dbo.EvaluationRecords DROP CONSTRAINT FK_Records_Users;
                IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Scores_Sheets')
                    ALTER TABLE dbo.EvaluationScores DROP CONSTRAINT FK_Scores_Sheets;
                IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Scores_Employees')
                    ALTER TABLE dbo.EvaluationScores DROP CONSTRAINT FK_Scores_Employees;
                IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Scores_Criteria')
                    ALTER TABLE dbo.EvaluationScores DROP CONSTRAINT FK_Scores_Criteria;
            ");

            // الدفعة الثانية (DML): بعد التأكد من وجود كافة الأعمدة، نقوم بتهيئة مجموعتي (الغياب) و(التأخير) وحساب المدير والصلاحيات
            await conn.ExecuteAsync(@"
                IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Username = N'admin')
                BEGIN
                    INSERT INTO dbo.Users (Username, PasswordHash, PasswordSalt, FullName, IsAdmin, MustChangePassword, IsActive)
                    VALUES (N'admin', N'8C6976E5B5410415BDE908BD4DEE15DFB167A9C873FC4BB8A81F6F2AB448A918', N'STATIC_INIT_SALT', N'مدير النظام العام', 1, 0, 1);
                END

                IF NOT EXISTS (SELECT 1 FROM dbo.Branches)
                BEGIN
                    INSERT INTO dbo.Branches (BranchName, IsActive) VALUES (N'الحصبة', 1), (N'حدة', 1);
                END

                -- تهيئة أو تحويل مجموعة (الغياب) الافتراضية
                DECLARE @AbsGroupID INT = (
                    SELECT TOP 1 GroupID FROM dbo.EvaluatorGroups
                    WHERE SystemGroupCode = 'ABSENCE' OR GroupName IN (N'الغياب', N'غياب')
                    ORDER BY GroupID ASC
                );

                IF @AbsGroupID IS NULL
                BEGIN
                    INSERT INTO dbo.EvaluatorGroups (GroupName, IsActive, IsSystemGroup, SystemGroupCode, MaxAllowedCount)
                    VALUES (N'الغياب', 1, 1, 'ABSENCE', 3);
                    SET @AbsGroupID = SCOPE_IDENTITY();
                END
                ELSE
                BEGIN
                    UPDATE dbo.EvaluatorGroups
                    SET GroupName = N'الغياب', IsActive = 1, IsSystemGroup = 1, SystemGroupCode = 'ABSENCE',
                        MaxAllowedCount = CASE WHEN MaxAllowedCount < 0 THEN 0 ELSE MaxAllowedCount END
                    WHERE GroupID = @AbsGroupID;
                END

                IF NOT EXISTS (SELECT 1 FROM dbo.Evaluators WHERE GroupID = @AbsGroupID)
                BEGIN
                    INSERT INTO dbo.Evaluators (GroupID, EvaluatorName, IsActive)
                    VALUES (@AbsGroupID, N'الغياب', 1);
                END
                ELSE
                BEGIN
                    DECLARE @PrimaryAbsEvalID INT = (SELECT TOP 1 EvaluatorID FROM dbo.Evaluators WHERE GroupID = @AbsGroupID ORDER BY IsActive DESC, EvaluatorID ASC);
                    UPDATE dbo.Evaluators SET EvaluatorName = N'الغياب', IsActive = 1 WHERE EvaluatorID = @PrimaryAbsEvalID;
                    UPDATE dbo.Evaluators SET IsActive = 0 WHERE GroupID = @AbsGroupID AND EvaluatorID <> @PrimaryAbsEvalID;
                END

                -- تهيئة أو تحويل مجموعة (التأخير) الافتراضية
                DECLARE @DelGroupID INT = (
                    SELECT TOP 1 GroupID FROM dbo.EvaluatorGroups
                    WHERE SystemGroupCode = 'DELAY' OR GroupName IN (N'التأخير', N'التاخير', N'تأخير', N'تاخير')
                    ORDER BY GroupID ASC
                );

                IF @DelGroupID IS NULL
                BEGIN
                    INSERT INTO dbo.EvaluatorGroups (GroupName, IsActive, IsSystemGroup, SystemGroupCode, MaxAllowedCount)
                    VALUES (N'التأخير', 1, 1, 'DELAY', 3);
                    SET @DelGroupID = SCOPE_IDENTITY();
                END
                ELSE
                BEGIN
                    UPDATE dbo.EvaluatorGroups
                    SET GroupName = N'التأخير', IsActive = 1, IsSystemGroup = 1, SystemGroupCode = 'DELAY',
                        MaxAllowedCount = CASE WHEN MaxAllowedCount < 0 THEN 0 ELSE MaxAllowedCount END
                    WHERE GroupID = @DelGroupID;
                END

                IF NOT EXISTS (SELECT 1 FROM dbo.Evaluators WHERE GroupID = @DelGroupID)
                BEGIN
                    INSERT INTO dbo.Evaluators (GroupID, EvaluatorName, IsActive)
                    VALUES (@DelGroupID, N'التأخير', 1);
                END
                ELSE
                BEGIN
                    DECLARE @PrimaryDelEvalID INT = (SELECT TOP 1 EvaluatorID FROM dbo.Evaluators WHERE GroupID = @DelGroupID ORDER BY IsActive DESC, EvaluatorID ASC);
                    UPDATE dbo.Evaluators SET EvaluatorName = N'التأخير', IsActive = 1 WHERE EvaluatorID = @PrimaryDelEvalID;
                    UPDATE dbo.Evaluators SET IsActive = 0 WHERE GroupID = @DelGroupID AND EvaluatorID <> @PrimaryDelEvalID;
                END

                DECLARE @Modules TABLE (Code VARCHAR(50), ArName NVARCHAR(100));
                INSERT INTO @Modules VALUES
                    ('MasterData', N'صفحة الأساسيات'),
                    ('IncentiveBasics', N'أساسيات الحوافز للفئات'),
                    ('Templates', N'قوالب التقييم'),
                    ('ScoreEntry', N'إدخال التقييمات الشهرية'),
                    ('ScoreBalancing', N'نسب الموازنة للتقييمات'),
                    ('HistoryLog', N'سجل التقييمات والأرشيف'),
                    ('FinalReport', N'التقرير النهائي والحوافز'),
                    ('UserManagement', N'إدارة المستخدمين والصلاحيات');

                INSERT INTO dbo.UserPermissions (UserID, ModuleCode, ModuleArabicName, CanRead, CanInsert, CanUpdate, CanDelete)
                SELECT u.UserID, m.Code, m.ArName, u.IsAdmin, u.IsAdmin, u.IsAdmin, u.IsAdmin
                FROM dbo.Users u
                CROSS JOIN @Modules m
                WHERE NOT EXISTS (
                    SELECT 1 FROM dbo.UserPermissions up WHERE up.UserID = u.UserID AND up.ModuleCode = m.Code
                );

                -- الترحيل التلقائي الأولي لبيانات الأساسيات الشهرية (أوزان الفئات + إسناد الموظفين + حالات الموظفين)
                DECLARE @SeedYear INT = YEAR(SYSDATETIME());
                DECLARE @SeedMonth INT = MONTH(SYSDATETIME());
                SELECT TOP 1 @SeedYear = EvalYear, @SeedMonth = EvalMonth
                FROM dbo.EvaluationRecords
                ORDER BY (EvalYear * 100 + EvalMonth) ASC;

                IF NOT EXISTS (SELECT 1 FROM dbo.MonthlyCategoryEvaluatorGroups)
                   AND EXISTS (SELECT 1 FROM dbo.CategoryEvaluatorGroups)
                BEGIN
                    INSERT INTO dbo.MonthlyCategoryEvaluatorGroups (EvalYear, EvalMonth, CategoryID, GroupID, WeightScore, UpdatedAt)
                    SELECT @SeedYear, @SeedMonth, CategoryID, GroupID, WeightScore, SYSDATETIME()
                    FROM dbo.CategoryEvaluatorGroups;
                END

                IF NOT EXISTS (SELECT 1 FROM dbo.MonthlyEmployeeEvaluators)
                   AND EXISTS (SELECT 1 FROM dbo.EmployeeEvaluators)
                BEGIN
                    INSERT INTO dbo.MonthlyEmployeeEvaluators (EvalYear, EvalMonth, EmployeeID, EvaluatorID, UpdatedAt)
                    SELECT DISTINCT @SeedYear, @SeedMonth, EmployeeID, EvaluatorID, SYSDATETIME()
                    FROM dbo.EmployeeEvaluators;
                END

                IF NOT EXISTS (SELECT 1 FROM dbo.MonthlyEmployeeSnapshots)
                   AND EXISTS (SELECT 1 FROM dbo.Employees WHERE IsActive = 1)
                BEGIN
                    INSERT INTO dbo.MonthlyEmployeeSnapshots (EvalYear, EvalMonth, EmployeeID, CategoryID, HasIncentive, IsContinuing, UpdatedAt)
                    SELECT @SeedYear, @SeedMonth, EmployeeID, CategoryID, HasIncentive, IsContinuing, SYSDATETIME()
                    FROM dbo.Employees
                    WHERE IsActive = 1;
                END
            ");
        }
    }
}









