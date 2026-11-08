using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;

namespace SchoolEvaluationERP.Services
{
    public class DatabaseStatsSummary
    {
        public string DatabaseName { get; set; } = "SchoolEvaluationDB";
        public string ServerName { get; set; } = string.Empty;
        public int BranchesCount { get; set; }
        public int CategoriesCount { get; set; }
        public int EvaluatorsCount { get; set; }
        public int EmployeesCount { get; set; }
        public int TemplatesCount { get; set; }
        public int MonthlyRecordsCount { get; set; }
        public int UsersCount { get; set; }
    }

    public class PortableDatabaseSnapshot
    {
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string CreatedBy { get; set; } = string.Empty;
        public List<Dictionary<string, object?>> Users { get; set; } = new();
        public List<Dictionary<string, object?>> UserPermissions { get; set; } = new();
        public List<Dictionary<string, object?>> Branches { get; set; } = new();
        public List<Dictionary<string, object?>> Categories { get; set; } = new();
        public List<Dictionary<string, object?>> Jobs { get; set; } = new();
        public List<Dictionary<string, object?>> JobTypes { get; set; } = new();
        public List<Dictionary<string, object?>> EvaluatorGroups { get; set; } = new();
        public List<Dictionary<string, object?>> Evaluators { get; set; } = new();
        public List<Dictionary<string, object?>> CategoryEvaluatorGroups { get; set; } = new();
        public List<Dictionary<string, object?>> Employees { get; set; } = new();
        public List<Dictionary<string, object?>> EmployeeEvaluators { get; set; } = new();
        public List<Dictionary<string, object?>> Templates { get; set; } = new();
        public List<Dictionary<string, object?>> TemplateSheets { get; set; } = new();
        public List<Dictionary<string, object?>> SheetCriteria { get; set; } = new();
        public List<Dictionary<string, object?>> SheetEmployees { get; set; } = new();
        public List<Dictionary<string, object?>> EvaluationRecords { get; set; } = new();
        public List<Dictionary<string, object?>> EvaluationScores { get; set; } = new();
        public List<Dictionary<string, object?>> EvaluationEmployeeAdjustments { get; set; } = new();
    }

    public class DatabaseBackupService
    {
        private readonly string _connectionString;

        public DatabaseBackupService() : this(AppConfigService.ConnectionString) { }

        public DatabaseBackupService(string connectionString)
        {
            _connectionString = connectionString;
        }

        private static bool EnsureAdminPermission(out string errorMessage)
        {
            errorMessage = string.Empty;
            if (AuthService.CurrentSession == null)
            {
                errorMessage = "انتهت جلسة العمل، يرجى إعادة تسجيل الدخول.";
                return false;
            }
            if (!AuthService.CurrentSession.IsAdmin)
            {
                errorMessage = "عذراً، ميزة إنشاء واستعادة النسخة الاحتياطية لقاعدة البيانات مخصصة لمدير النظام (Admin) فقط.";
                return false;
            }
            return true;
        }

        public async Task<DatabaseStatsSummary> GetDatabaseStatsAsync()
        {
            var builder = new SqlConnectionStringBuilder(_connectionString);
            var stats = new DatabaseStatsSummary
            {
                DatabaseName = string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "SchoolEvaluationDB" : builder.InitialCatalog,
                ServerName = builder.DataSource ?? "(localdb)\\MSSQLLocalDB"
            };

            using var conn = new SqlConnection(_connectionString);
            await DatabaseSchemaService.EnsureSchemaV2Async(conn);
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            stats.BranchesCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM dbo.Branches WHERE IsActive = 1");
            stats.CategoriesCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM dbo.Categories WHERE IsActive = 1");
            stats.EvaluatorsCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM dbo.Evaluators WHERE IsActive = 1");
            stats.EmployeesCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM dbo.Employees WHERE IsActive = 1");
            stats.TemplatesCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM dbo.Templates WHERE IsCurrentVersion = 1 AND IsDeleted = 0");
            stats.MonthlyRecordsCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM dbo.EvaluationRecords");
            stats.UsersCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM dbo.Users");

            return stats;
        }

        public async Task<(bool IsSuccess, string Message)> CreateBackupAsync(string targetFilePath)
        {
            if (!EnsureAdminPermission(out string permErr))
                return (false, permErr);

            if (string.IsNullOrWhiteSpace(targetFilePath))
                return (false, "يرجى تحديد مسار واسم ملف النسخة الاحتياطية.");

            try
            {
                string? folder = Path.GetDirectoryName(targetFilePath);
                if (!string.IsNullOrWhiteSpace(folder) && !Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                var builder = new SqlConnectionStringBuilder(_connectionString);
                string dbName = string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "SchoolEvaluationDB" : builder.InitialCatalog;

                if (targetFilePath.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var conn = new SqlConnection(_connectionString);
                        await conn.OpenAsync();

                        if (File.Exists(targetFilePath))
                        {
                            File.Delete(targetFilePath);
                        }

                        string backupSql = $@"
                            BACKUP DATABASE [{dbName}]
                            TO DISK = @DiskPath
                            WITH FORMAT, INIT,
                                 NAME = N'{dbName} Full Backup',
                                 SKIP, NOREWIND, NOUNLOAD, STATS = 10;";

                        await conn.ExecuteAsync(backupSql, new { DiskPath = targetFilePath }, commandTimeout: 180);

                        return (true, $"تم إنشاء النسخة الاحتياطية لقاعدة البيانات بنجاح في المسار المحدد:\n{targetFilePath}");
                    }
                    catch (SqlException)
                    {
                        using var conn = new SqlConnection(_connectionString);
                        await conn.OpenAsync();

                        string? serverDefaultBackupDir = await conn.ExecuteScalarAsync<string?>(
                            "SELECT CONVERT(NVARCHAR(500), SERVERPROPERTY('InstanceDefaultBackupPath'))");

                        if (!string.IsNullOrWhiteSpace(serverDefaultBackupDir) && Directory.Exists(serverDefaultBackupDir))
                        {
                            string tempBakPath = Path.Combine(serverDefaultBackupDir, $"{dbName}_Temp_{Guid.NewGuid():N}.bak");
                            string backupSql = $@"
                                BACKUP DATABASE [{dbName}]
                                TO DISK = @DiskPath
                                WITH FORMAT, INIT, NAME = N'{dbName} Full Backup';";

                            await conn.ExecuteAsync(backupSql, new { DiskPath = tempBakPath }, commandTimeout: 180);
                            File.Copy(tempBakPath, targetFilePath, overwrite: true);
                            try { File.Delete(tempBakPath); } catch { }

                            return (true, $"تم إنشاء النسخة الاحتياطية لقاعدة البيانات (.bak) بنجاح في المسار المحدد:\n{targetFilePath}");
                        }
                        throw;
                    }
                }

                await ExportPortableSnapshotAsync(targetFilePath);
                return (true, $"تم إنشاء النسخة الاحتياطية المحمولة الشاملة بنجاح في المسار المحدد:\n{targetFilePath}");
            }
            catch (Exception ex)
            {
                return (false, $"حدث خطأ أثناء إنشاء النسخة الاحتياطية:\n{ex.Message}");
            }
        }

        public async Task<(bool IsSuccess, string Message)> RestoreBackupAsync(string sourceFilePath)
        {
            if (!EnsureAdminPermission(out string permErr))
                return (false, permErr);

            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
                return (false, "ملف النسخة الاحتياطية المحدد غير موجود.");

            try
            {
                string firstChars = string.Empty;
                using (var sr = new StreamReader(sourceFilePath))
                {
                    char[] buf = new char[10];
                    int read = await sr.ReadAsync(buf, 0, buf.Length);
                    firstChars = new string(buf, 0, read).TrimStart();
                }

                if (firstChars.StartsWith("{"))
                {
                    await ImportPortableSnapshotAsync(sourceFilePath);
                    return (true, "تمت استعادة جميع بيانات النظام من النسخة الاحتياطية بنجاح!");
                }

                var builder = new SqlConnectionStringBuilder(_connectionString);
                string dbName = string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "SchoolEvaluationDB" : builder.InitialCatalog;

                var masterBuilder = new SqlConnectionStringBuilder(_connectionString)
                {
                    InitialCatalog = "master",
                    Pooling = false
                };

                SqlConnection.ClearAllPools();

                using var masterConn = new SqlConnection(masterBuilder.ConnectionString);
                await masterConn.OpenAsync();

                string restorePathToUse = sourceFilePath;
                string? tempCopiedPath = null;

                try
                {
                    try
                    {
                        await masterConn.QueryAsync("RESTORE HEADERONLY FROM DISK = @DiskPath", new { DiskPath = restorePathToUse });
                    }
                    catch
                    {
                        string? serverDefaultBackupDir = await masterConn.ExecuteScalarAsync<string?>(
                            "SELECT CONVERT(NVARCHAR(500), SERVERPROPERTY('InstanceDefaultBackupPath'))");

                        if (!string.IsNullOrWhiteSpace(serverDefaultBackupDir) && Directory.Exists(serverDefaultBackupDir))
                        {
                            tempCopiedPath = Path.Combine(serverDefaultBackupDir, $"{dbName}_RestoreTemp_{Guid.NewGuid():N}.bak");
                            File.Copy(sourceFilePath, tempCopiedPath, overwrite: true);
                            restorePathToUse = tempCopiedPath;
                        }
                    }

                    string restoreSql = $@"
                        IF DB_ID(N'{dbName}') IS NOT NULL
                        BEGIN
                            ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                        END

                        RESTORE DATABASE [{dbName}]
                        FROM DISK = @DiskPath
                        WITH REPLACE;

                        ALTER DATABASE [{dbName}] SET MULTI_USER;";

                    await masterConn.ExecuteAsync(restoreSql, new { DiskPath = restorePathToUse }, commandTimeout: 240);
                }
                finally
                {
                    if (!string.IsNullOrWhiteSpace(tempCopiedPath) && File.Exists(tempCopiedPath))
                    {
                        try { File.Delete(tempCopiedPath); } catch { }
                    }

                    try
                    {
                        await masterConn.ExecuteAsync($@"
                            IF DB_ID(N'{dbName}') IS NOT NULL
                            BEGIN
                                ALTER DATABASE [{dbName}] SET MULTI_USER;
                            END");
                    }
                    catch { }
                }

                SqlConnection.ClearAllPools();

                using var checkConn = new SqlConnection(_connectionString);
                await DatabaseSchemaService.ForceReverifySchemaAsync(checkConn);

                return (true, $"تمت استعادة قاعدة البيانات ({dbName}) بالكامل من النسخة الاحتياطية بنجاح!");
            }
            catch (Exception ex)
            {
                return (false, $"حدث خطأ أثناء استعادة النسخة الاحتياطية:\n{ex.Message}");
            }
        }

        private async Task ExportPortableSnapshotAsync(string filePath)
        {
            using var conn = new SqlConnection(_connectionString);
            await DatabaseSchemaService.EnsureSchemaV2Async(conn);
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            var snapshot = new PortableDatabaseSnapshot
            {
                CreatedAt = DateTime.Now,
                CreatedBy = AuthService.CurrentSession?.FullName ?? "Admin",
                Users = await QueryTableAsDictionariesAsync(conn, "SELECT UserID, Username, PasswordHash, PasswordSalt, FullName, IsAdmin, MustChangePassword, IsActive, CreatedAt FROM dbo.Users"),
                UserPermissions = await QueryTableAsDictionariesAsync(conn, "SELECT PermissionID, UserID, ModuleCode, ModuleArabicName, CanRead, CanInsert, CanUpdate, CanDelete FROM dbo.UserPermissions"),
                Branches = await QueryTableAsDictionariesAsync(conn, "SELECT BranchID, BranchName, IsActive FROM dbo.Branches"),
                Categories = await QueryTableAsDictionariesAsync(conn, "SELECT CategoryID, DepartmentID, CategoryName, MaxIncentiveAmount, MinIncentiveAmount, IsActive FROM dbo.Categories"),
                Jobs = await QueryTableAsDictionariesAsync(conn, "SELECT JobID, CategoryID, JobTitle, IsActive FROM dbo.Jobs"),
                JobTypes = await QueryTableAsDictionariesAsync(conn, "SELECT JobTypeID, JobID, JobTypeName, IsActive FROM dbo.JobTypes"),
                EvaluatorGroups = await QueryTableAsDictionariesAsync(conn, "SELECT GroupID, GroupName, IsActive FROM dbo.EvaluatorGroups"),
                Evaluators = await QueryTableAsDictionariesAsync(conn, "SELECT EvaluatorID, GroupID, EvaluatorName, IsActive FROM dbo.Evaluators"),
                CategoryEvaluatorGroups = await QueryTableAsDictionariesAsync(conn, "SELECT CategoryEvaluatorGroupID, CategoryID, GroupID, WeightScore FROM dbo.CategoryEvaluatorGroups"),
                Employees = await QueryTableAsDictionariesAsync(conn, "SELECT EmployeeID, SystemName, EvaluationName, BranchID, DepartmentID, CategoryID, JobID, JobTypeID, HasIncentive, IsContinuing, IsActive, CreatedAt FROM dbo.Employees"),
                EmployeeEvaluators = await QueryTableAsDictionariesAsync(conn, "SELECT EmployeeEvaluatorID, EmployeeID, EvaluatorID FROM dbo.EmployeeEvaluators"),
                Templates = await QueryTableAsDictionariesAsync(conn, "SELECT TemplateID, TemplateGroupGuid, EvaluatorID, VersionNumber, IsCurrentVersion, IsDeleted, CreatedByUserID, CreatedAt FROM dbo.Templates"),
                TemplateSheets = await QueryTableAsDictionariesAsync(conn, "SELECT SheetID, TemplateID, SheetName, TargetCategoryID, TargetBranchID, TargetCategoryName, SortOrder FROM dbo.TemplateSheets"),
                SheetCriteria = await QueryTableAsDictionariesAsync(conn, "SELECT CriteriaID, SheetID, CriteriaTitle, MaxScore, ColumnOrder FROM dbo.SheetCriteria"),
                SheetEmployees = await QueryTableAsDictionariesAsync(conn, "SELECT SheetEmployeeID, SheetID, EmployeeID, RowOrder FROM dbo.SheetEmployees"),
                EvaluationRecords = await QueryTableAsDictionariesAsync(conn, "SELECT RecordID, TemplateID, EvaluatorID, EvalYear, EvalMonth, CreatedByUserID, CreatedAt, LastModifiedByUserID, LastModifiedAt FROM dbo.EvaluationRecords"),
                EvaluationScores = await QueryTableAsDictionariesAsync(conn, "SELECT ScoreID, RecordID, SheetID, EmployeeID, CriteriaID, GivenScore, SnapshotEvaluatorWeight FROM dbo.EvaluationScores"),
                EvaluationEmployeeAdjustments = await QueryTableAsDictionariesAsync(conn, "SELECT AdjustmentID, RecordID, SheetID, EmployeeID, BalancingDelta FROM dbo.EvaluationEmployeeAdjustments")
            };

            string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json, System.Text.Encoding.UTF8);
        }

        private async Task ImportPortableSnapshotAsync(string filePath)
        {
            string json = await File.ReadAllTextAsync(filePath, System.Text.Encoding.UTF8);
            var snapshot = JsonSerializer.Deserialize<PortableDatabaseSnapshot>(json)
                ?? throw new InvalidOperationException("ملف النسخة الاحتياطية غير صالح.");

            using var conn = new SqlConnection(_connectionString);
            await DatabaseSchemaService.EnsureSchemaV2Async(conn);
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            using var tx = conn.BeginTransaction();
            try
            {
                await conn.ExecuteAsync(@"
                    DELETE FROM dbo.EvaluationEmployeeAdjustments;
                    DELETE FROM dbo.EvaluationScores;
                    DELETE FROM dbo.EvaluationRecords;
                    DELETE FROM dbo.SheetEmployees;
                    DELETE FROM dbo.SheetCriteria;
                    DELETE FROM dbo.TemplateSheets;
                    DELETE FROM dbo.Templates;
                    DELETE FROM dbo.EmployeeEvaluators;
                    DELETE FROM dbo.Employees;
                    DELETE FROM dbo.CategoryEvaluatorGroups;
                    DELETE FROM dbo.Evaluators;
                    DELETE FROM dbo.EvaluatorGroups;
                    DELETE FROM dbo.JobTypes;
                    DELETE FROM dbo.Jobs;
                    DELETE FROM dbo.Categories;
                    DELETE FROM dbo.Branches;
                    DELETE FROM dbo.UserPermissions;
                    DELETE FROM dbo.Users;", transaction: tx);

                await BulkInsertWithIdentityAsync(conn, tx, "dbo.Users", snapshot.Users);
                await BulkInsertWithIdentityAsync(conn, tx, "dbo.UserPermissions", snapshot.UserPermissions);
                await BulkInsertWithIdentityAsync(conn, tx, "dbo.Branches", snapshot.Branches);
                await BulkInsertWithIdentityAsync(conn, tx, "dbo.Categories", snapshot.Categories);
                await BulkInsertWithIdentityAsync(conn, tx, "dbo.Jobs", snapshot.Jobs);
                await BulkInsertWithIdentityAsync(conn, tx, "dbo.JobTypes", snapshot.JobTypes);
                await BulkInsertWithIdentityAsync(conn, tx, "dbo.EvaluatorGroups", snapshot.EvaluatorGroups);
                await BulkInsertWithIdentityAsync(conn, tx, "dbo.Evaluators", snapshot.Evaluators);
                await BulkInsertWithIdentityAsync(conn, tx, "dbo.CategoryEvaluatorGroups", snapshot.CategoryEvaluatorGroups);
                await BulkInsertWithIdentityAsync(conn, tx, "dbo.Employees", snapshot.Employees);
                await BulkInsertWithIdentityAsync(conn, tx, "dbo.EmployeeEvaluators", snapshot.EmployeeEvaluators);
                await BulkInsertWithIdentityAsync(conn, tx, "dbo.Templates", snapshot.Templates);
                await BulkInsertWithIdentityAsync(conn, tx, "dbo.TemplateSheets", snapshot.TemplateSheets);
                await BulkInsertWithIdentityAsync(conn, tx, "dbo.SheetCriteria", snapshot.SheetCriteria);
                await BulkInsertWithIdentityAsync(conn, tx, "dbo.SheetEmployees", snapshot.SheetEmployees);
                await BulkInsertWithIdentityAsync(conn, tx, "dbo.EvaluationRecords", snapshot.EvaluationRecords);
                await BulkInsertWithIdentityAsync(conn, tx, "dbo.EvaluationScores", snapshot.EvaluationScores);
                await BulkInsertWithIdentityAsync(conn, tx, "dbo.EvaluationEmployeeAdjustments", snapshot.EvaluationEmployeeAdjustments);

                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        private static async Task<List<Dictionary<string, object?>>> QueryTableAsDictionariesAsync(SqlConnection conn, string sql)
        {
            var result = new List<Dictionary<string, object?>>();
            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                result.Add(row);
            }
            return result;
        }

        private static async Task BulkInsertWithIdentityAsync(
            SqlConnection conn,
            SqlTransaction tx,
            string tableName,
            List<Dictionary<string, object?>> rows)
        {
            if (rows == null || rows.Count == 0) return;

            var dt = new DataTable();
            foreach (string colName in rows[0].Keys)
            {
                dt.Columns.Add(colName, typeof(object));
            }

            foreach (var r in rows)
            {
                var dr = dt.NewRow();
                foreach (var kv in r)
                {
                    if (kv.Value is JsonElement je)
                    {
                        dr[kv.Key] = ConvertJsonElement(je) ?? DBNull.Value;
                    }
                    else
                    {
                        dr[kv.Key] = kv.Value ?? DBNull.Value;
                    }
                }
                dt.Rows.Add(dr);
            }

            using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls, tx)
            {
                DestinationTableName = tableName,
                BulkCopyTimeout = 180
            };

            foreach (DataColumn col in dt.Columns)
            {
                bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            }

            await bulk.WriteToServerAsync(dt);
        }

        private static object? ConvertJsonElement(JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => je.TryGetInt64(out long l) ? l : je.GetDecimal(),
                JsonValueKind.String => je.GetString(),
                _ => je.ToString()
            };
        }
    }
}
