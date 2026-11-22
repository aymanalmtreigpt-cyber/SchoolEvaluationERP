using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Dapper;
using Microsoft.Data.SqlClient;
using SchoolEvaluationERP.Models;

namespace SchoolEvaluationERP.Services
{
    public class MasterDataService
    {
        private readonly string _connectionString;

        public MasterDataService() : this(AppConfigService.ConnectionString) { }

        public MasterDataService(string connectionString)
        {
            _connectionString = connectionString;
        }

        private static bool CheckPermission(string action, out string errorMsg)
        {
            errorMsg = string.Empty;
            if (AuthService.CurrentSession == null)
            {
                errorMsg = "انتهت جلسة العمل، يرجى إعادة تسجيل الدخول.";
                return false;
            }
            if (!AuthService.CurrentSession.HasPermission("MasterData", action))
            {
                string arAction = action switch
                {
                    "insert" => "إضافة وكتابة",
                    "update" => "تعديل",
                    "delete" => "حذف",
                    _ => "قراءة"
                };
                errorMsg = $"عذراً، لا تملك صلاحية ({arAction}) في شاشة الأساسيات.";
                return false;
            }
            return true;
        }

        private async Task<SqlConnection> OpenReadyConnectionAsync()
        {
            var conn = new SqlConnection(_connectionString);
            await DatabaseSchemaService.EnsureSchemaV2Async(conn);
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync();
            }
            return conn;
        }

        // =========================================================================
        // 1. إدارة الفروع (Branches)
        // =========================================================================
        private static async Task PurgeOrphanedArchivedDataAsync(SqlConnection conn)
        {
            await conn.ExecuteAsync(@"
                DELETE se FROM dbo.SheetEmployees se INNER JOIN dbo.Employees e ON se.EmployeeID = e.EmployeeID WHERE e.IsActive = 0;
                DELETE ee FROM dbo.EmployeeEvaluators ee INNER JOIN dbo.Employees e ON ee.EmployeeID = e.EmployeeID WHERE e.IsActive = 0;

                DELETE t FROM dbo.Templates t
                WHERE t.IsDeleted = 1
                  AND NOT EXISTS (SELECT 1 FROM dbo.EvaluationRecords er WHERE er.TemplateID = t.TemplateID);

                DELETE e FROM dbo.Employees e
                WHERE e.IsActive = 0
                  AND NOT EXISTS (SELECT 1 FROM dbo.EvaluationScores es WHERE es.EmployeeID = e.EmployeeID);

                DELETE jt FROM dbo.JobTypes jt
                WHERE jt.IsActive = 0
                  AND NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.JobTypeID = jt.JobTypeID);

                DELETE j FROM dbo.Jobs j
                WHERE j.IsActive = 0
                  AND NOT EXISTS (SELECT 1 FROM dbo.JobTypes jt WHERE jt.JobID = j.JobID)
                  AND NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.JobID = j.JobID);

                DELETE ceg FROM dbo.CategoryEvaluatorGroups ceg
                INNER JOIN dbo.Categories c ON ceg.CategoryID = c.CategoryID
                WHERE c.IsActive = 0;

                DELETE c FROM dbo.Categories c
                WHERE c.IsActive = 0
                  AND NOT EXISTS (SELECT 1 FROM dbo.Jobs j WHERE j.CategoryID = c.CategoryID)
                  AND NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.CategoryID = c.CategoryID)
                  AND NOT EXISTS (SELECT 1 FROM dbo.TemplateSheets ts WHERE ts.TargetCategoryID = c.CategoryID);

                DELETE ev FROM dbo.Evaluators ev
                WHERE ev.IsActive = 0
                  AND NOT EXISTS (SELECT 1 FROM dbo.Templates t WHERE t.EvaluatorID = ev.EvaluatorID)
                  AND NOT EXISTS (SELECT 1 FROM dbo.EvaluationRecords er WHERE er.EvaluatorID = ev.EvaluatorID)
                  AND NOT EXISTS (SELECT 1 FROM dbo.EmployeeEvaluators ee WHERE ee.EvaluatorID = ev.EvaluatorID);

                DELETE eg FROM dbo.EvaluatorGroups eg
                WHERE eg.IsActive = 0
                  AND NOT EXISTS (SELECT 1 FROM dbo.Evaluators ev WHERE ev.GroupID = eg.GroupID)
                  AND NOT EXISTS (SELECT 1 FROM dbo.CategoryEvaluatorGroups ceg WHERE ceg.GroupID = eg.GroupID);

                DELETE b FROM dbo.Branches b
                WHERE b.IsActive = 0
                  AND NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.BranchID = b.BranchID)
                  AND NOT EXISTS (SELECT 1 FROM dbo.TemplateSheets ts WHERE ts.TargetBranchID = b.BranchID);
            ");
        }

        public async Task<List<BranchDto>> GetBranchesAsync()
        {
            using var conn = await OpenReadyConnectionAsync();
            string sql = @"
                SELECT b.BranchID, b.BranchName,
                       (SELECT COUNT(1) FROM dbo.Employees e WHERE e.BranchID = b.BranchID AND e.IsActive = 1) AS EmployeesCount
                FROM dbo.Branches b
                WHERE b.IsActive = 1
                ORDER BY b.BranchID ASC";
            return (await conn.QueryAsync<BranchDto>(sql)).ToList();
        }

        public async Task<(bool IsSuccess, string Message)> SaveBranchAsync(int branchId, string branchName)
        {
            string cleanName = (branchName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cleanName))
                return (false, "يرجى إدخال اسم الفرع.");

            if (!CheckPermission(branchId == 0 ? "insert" : "update", out string permErr))
                return (false, permErr);

            using var conn = await OpenReadyConnectionAsync();

            int dup = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.Branches WHERE BranchName = @Name AND BranchID <> @ID",
                new { Name = cleanName, ID = branchId });

            if (dup > 0)
                return (false, $"الفرع ({cleanName}) مسجل مسبقاً.");

            if (branchId == 0)
            {
                await conn.ExecuteAsync("INSERT INTO dbo.Branches (BranchName, IsActive) VALUES (@Name, 1)", new { Name = cleanName });
                return (true, "تمت إضافة الفرع بنجاح.");
            }
            else
            {
                await conn.ExecuteAsync("UPDATE dbo.Branches SET BranchName = @Name WHERE BranchID = @ID", new { Name = cleanName, ID = branchId });
                return (true, "تم تعديل اسم الفرع بنجاح.");
            }
        }

        public async Task<(bool IsSuccess, string Message)> DeleteBranchAsync(int branchId)
        {
            if (!CheckPermission("delete", out string permErr))
                return (false, permErr);

            using var conn = await OpenReadyConnectionAsync();
            await PurgeOrphanedArchivedDataAsync(conn);

            int activeEmps = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.Employees WHERE BranchID = @ID AND IsActive = 1", new { ID = branchId });
            if (activeEmps > 0)
                return (false, "لا يمكن حذف هذا الفرع لوجود موظفين نشطين مسجلين عليه حالياً.");

            try
            {
                await conn.ExecuteAsync("DELETE FROM dbo.Branches WHERE BranchID = @ID", new { ID = branchId });
                return (true, "تم حذف الفرع بنجاح.");
            }
            catch (SqlException)
            {
                await conn.ExecuteAsync("UPDATE dbo.Branches SET IsActive = 0 WHERE BranchID = @ID", new { ID = branchId });
                return (true, "تم حذف الفرع من القائمة النشطة (مع الاحتفاظ بارتباطه في أرشيف التقييمات السابقة).");
            }
        }

        // =========================================================================
        // 2. إدارة الأقسام (Departments: أكاديمي / إداري / غيره)
        // =========================================================================
        public async Task<List<DepartmentDto>> GetDepartmentsAsync()
        {
            using var conn = await OpenReadyConnectionAsync();
            string sql = @"
                SELECT d.DepartmentID, d.DepartmentName, d.DepartmentType,
                       (SELECT COUNT(1) FROM dbo.Categories c WHERE c.DepartmentID = d.DepartmentID AND c.IsActive = 1) AS CategoriesCount,
                       (SELECT COUNT(1) FROM dbo.Employees e WHERE e.DepartmentID = d.DepartmentID AND e.IsActive = 1) AS EmployeesCount
                FROM dbo.Departments d
                WHERE d.IsActive = 1
                ORDER BY d.DepartmentID ASC";
            return (await conn.QueryAsync<DepartmentDto>(sql)).ToList();
        }

        public async Task<(bool IsSuccess, string Message)> SaveDepartmentAsync(int departmentId, string departmentName, string departmentType)
        {
            string cleanName = (departmentName ?? string.Empty).Trim();
            string cleanType = string.IsNullOrWhiteSpace(departmentType) ? "أكاديمي" : departmentType.Trim();

            if (string.IsNullOrWhiteSpace(cleanName))
                return (false, "يرجى إدخال اسم القسم (مثل: القسم الأكاديمي، القسم الإداري).");

            if (!CheckPermission(departmentId == 0 ? "insert" : "update", out string permErr))
                return (false, permErr);

            using var conn = await OpenReadyConnectionAsync();

            int dup = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.Departments WHERE DepartmentName = @Name AND DepartmentID <> @ID",
                new { Name = cleanName, ID = departmentId });

            if (dup > 0)
                return (false, $"القسم ({cleanName}) مسجل مسبقاً.");

            if (departmentId == 0)
            {
                await conn.ExecuteAsync(
                    "INSERT INTO dbo.Departments (DepartmentName, DepartmentType, IsActive) VALUES (@Name, @Type, 1)",
                    new { Name = cleanName, Type = cleanType });
                return (true, "تمت إضافة القسم بنجاح.");
            }
            else
            {
                await conn.ExecuteAsync(
                    "UPDATE dbo.Departments SET DepartmentName = @Name, DepartmentType = @Type WHERE DepartmentID = @ID",
                    new { Name = cleanName, Type = cleanType, ID = departmentId });
                return (true, "تم تعديل بيانات القسم بنجاح.");
            }
        }

        public async Task<(bool IsSuccess, string Message)> DeleteDepartmentAsync(int departmentId)
        {
            if (!CheckPermission("delete", out string permErr))
                return (false, permErr);

            using var conn = await OpenReadyConnectionAsync();
            try
            {
                await conn.ExecuteAsync("DELETE FROM dbo.Departments WHERE DepartmentID = @ID", new { ID = departmentId });
                return (true, "تم حذف القسم بنجاح.");
            }
            catch (SqlException)
            {
                return (false, "لا يمكن حذف هذا القسم لوجود فئات أو موظفين مرتبطين به. احذف الفئات التابعة له أولاً.");
            }
        }

        // =========================================================================
        // 3. إدارة الفئات (Categories)
        // =========================================================================
        private static async Task<(int Year, int Month)> ResolvePeriodOrLatestAsync(
            SqlConnection conn, int? evalYear, int? evalMonth, SqlTransaction? tx = null)
        {
            if (evalYear.HasValue && evalMonth.HasValue &&
                evalYear.Value >= 2020 && evalYear.Value <= 2100 &&
                evalMonth.Value >= 1 && evalMonth.Value <= 12)
            {
                return (evalYear.Value, evalMonth.Value);
            }
            return await MonthlyPeriodConfigService.GetLatestConfiguredPeriodAsync(conn, tx);
        }

        public async Task<List<CategoryDto>> GetCategoriesAsync(
            int? filterDepartmentId = null,
            int? evalYear = null,
            int? evalMonth = null)
        {
            using var conn = await OpenReadyConnectionAsync();
            var (year, month) = await ResolvePeriodOrLatestAsync(conn, evalYear, evalMonth);

            string sql = @"
                SELECT c.CategoryID, ISNULL(c.DepartmentID, 0) AS DepartmentID, c.CategoryName,
                       (SELECT COUNT(1) FROM dbo.Jobs j WHERE j.CategoryID = c.CategoryID AND j.IsActive = 1) AS JobsCount,
                       (SELECT COUNT(1) FROM dbo.Employees e WHERE e.CategoryID = c.CategoryID AND e.IsActive = 1) AS EmployeesCount,
                       CAST(0 AS DECIMAL(5,2)) AS TotalWeights
                FROM dbo.Categories c
                WHERE c.IsActive = 1
                ORDER BY c.CategoryID ASC";

            var categories = (await conn.QueryAsync<CategoryDto>(sql)).ToList();

            var effectiveWeights = await MonthlyPeriodConfigService.GetEffectiveCategoryWeightsAsync(conn, year, month);
            var effectiveSnaps = await MonthlyPeriodConfigService.GetEffectiveEmployeeSnapshotsAsync(conn, year, month);
            var activeEmpIds = (await conn.QueryAsync<int>("SELECT EmployeeID FROM dbo.Employees WHERE IsActive = 1")).ToHashSet();

            var empCountByCat = effectiveSnaps.Values
                .Where(s => activeEmpIds.Contains(s.EmployeeID))
                .GroupBy(s => s.CategoryID)
                .ToDictionary(g => g.Key, g => g.Count());

            var groupMembers = await conn.QueryAsync<dynamic>(@"
                SELECT GroupID, EvaluatorName
                FROM dbo.Evaluators
                WHERE IsActive = 1
                ORDER BY EvaluatorID ASC");

            var membersByGroup = groupMembers
                .GroupBy(x => (int)x.GroupID)
                .ToDictionary(g => g.Key, g => string.Join("، ", g.Select(m => (string)m.EvaluatorName)));

            var weightsByCat = effectiveWeights
                .GroupBy(x => x.CategoryID)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var cat in categories)
            {
                if (effectiveSnaps.Count > 0)
                {
                    cat.EmployeesCount = empCountByCat.TryGetValue(cat.CategoryID, out int cnt) ? cnt : 0;
                }

                if (weightsByCat.TryGetValue(cat.CategoryID, out var catW) && catW.Count > 0)
                {
                    cat.TotalWeights = catW.Sum(w => w.WeightScore);
                    cat.EvaluatorsSummary = string.Join("  |  ", catW.Select(i =>
                    {
                        membersByGroup.TryGetValue(i.GroupID, out var mems);
                        return string.IsNullOrWhiteSpace(mems)
                            ? $"{i.GroupName}: {i.WeightScore:0.##} درجة"
                            : $"{i.GroupName} [{mems}]: {i.WeightScore:0.##} درجة";
                    }));
                }
                else
                {
                    cat.TotalWeights = 0m;
                    cat.EvaluatorsSummary = "لم يتم تخصيص مقيمين بعد (اضغط تخصيص الدرجات)";
                }
            }

            return categories;
        }

        public async Task<(bool IsSuccess, string Message)> SaveCategoryBasicAsync(int categoryId, string categoryName)
        {
            string cleanName = (categoryName ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(cleanName))
                return (false, "يرجى إدخال اسم الفئة (مثل: المعلمين، المشرفين، الوكلاء).");

            if (!CheckPermission(categoryId == 0 ? "insert" : "update", out string permErr))
                return (false, permErr);

            using var conn = await OpenReadyConnectionAsync();

            int dup = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.Categories WHERE CategoryName = @Name AND CategoryID <> @ID",
                new { Name = cleanName, ID = categoryId });

            if (dup > 0)
                return (false, $"الفئة ({cleanName}) مسجلة مسبقاً.");

            if (categoryId == 0)
            {
                await conn.ExecuteAsync(
                    "INSERT INTO dbo.Categories (DepartmentID, CategoryName, IsActive) VALUES (NULL, @Name, 1)",
                    new { Name = cleanName });
                return (true, "تمت إضافة الفئة بنجاح.");
            }
            else
            {
                await conn.ExecuteAsync(
                    "UPDATE dbo.Categories SET CategoryName = @Name WHERE CategoryID = @ID",
                    new { Name = cleanName, ID = categoryId });
                return (true, "تم تعديل بيانات الفئة بنجاح.");
            }
        }

        public async Task<(bool IsSuccess, string Message)> DeleteCategoryAsync(int categoryId)
        {
            if (!CheckPermission("delete", out string permErr))
                return (false, permErr);

            using var conn = await OpenReadyConnectionAsync();
            await PurgeOrphanedArchivedDataAsync(conn);

            int activeEmps = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.Employees WHERE CategoryID = @ID AND IsActive = 1", new { ID = categoryId });
            if (activeEmps > 0)
                return (false, "لا يمكن حذف هذه الفئة لوجود موظفين نشطين مسجلين عليها حالياً.");

            int activeTemplates = await conn.ExecuteScalarAsync<int>(@"
                SELECT COUNT(1) FROM dbo.TemplateSheets ts
                INNER JOIN dbo.Templates t ON ts.TemplateID = t.TemplateID
                WHERE ts.TargetCategoryID = @ID AND t.IsDeleted = 0 AND t.IsCurrentVersion = 1", new { ID = categoryId });
            if (activeTemplates > 0)
                return (false, "لا يمكن حذف هذه الفئة لوجود قوالب تقييم فعالة مرتبطة بها حالياً.");

            await conn.ExecuteAsync("DELETE FROM dbo.CategoryEvaluatorGroups WHERE CategoryID = @ID", new { ID = categoryId });

            await conn.ExecuteAsync(@"
                DELETE jt FROM dbo.JobTypes jt
                INNER JOIN dbo.Jobs j ON jt.JobID = j.JobID
                WHERE j.CategoryID = @ID
                  AND NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.JobTypeID = jt.JobTypeID);

                UPDATE jt SET jt.IsActive = 0
                FROM dbo.JobTypes jt
                INNER JOIN dbo.Jobs j ON jt.JobID = j.JobID
                WHERE j.CategoryID = @ID;

                DELETE j FROM dbo.Jobs j
                WHERE j.CategoryID = @ID
                  AND NOT EXISTS (SELECT 1 FROM dbo.JobTypes jt WHERE jt.JobID = j.JobID)
                  AND NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.JobID = j.JobID);

                UPDATE dbo.Jobs SET IsActive = 0 WHERE CategoryID = @ID;
            ", new { ID = categoryId });

            try
            {
                await conn.ExecuteAsync("DELETE FROM dbo.Categories WHERE CategoryID = @ID", new { ID = categoryId });
                return (true, "تم حذف الفئة ووظائفها التابعة بنجاح.");
            }
            catch (SqlException)
            {
                await conn.ExecuteAsync("UPDATE dbo.Categories SET IsActive = 0 WHERE CategoryID = @ID", new { ID = categoryId });
                return (true, "تم حذف الفئة من القائمة النشطة (مع الاحتفاظ بارتباطها في أرشيف التقييمات السابقة).");
            }
        }

        // =========================================================================
        // 4. إدارة الوظائف التابعة للفئات (Jobs)
        // =========================================================================
        public async Task<List<JobDto>> GetJobsAsync(int? filterCategoryId = null)
        {
            using var conn = await OpenReadyConnectionAsync();
            string sql = @"
                SELECT j.JobID, j.CategoryID, c.CategoryName, ISNULL(c.DepartmentID, 0) AS DepartmentID, j.JobTitle,
                       (SELECT COUNT(1) FROM dbo.JobTypes jt WHERE jt.JobID = j.JobID AND jt.IsActive = 1) AS JobTypesCount,
                       (SELECT COUNT(1) FROM dbo.Employees e WHERE e.JobID = j.JobID AND e.IsActive = 1) AS EmployeesCount
                FROM dbo.Jobs j
                INNER JOIN dbo.Categories c ON j.CategoryID = c.CategoryID
                WHERE j.IsActive = 1 AND (@CatID IS NULL OR j.CategoryID = @CatID)
                ORDER BY c.CategoryID ASC, j.JobID ASC";
            return (await conn.QueryAsync<JobDto>(sql, new { CatID = filterCategoryId })).ToList();
        }

        public async Task<(bool IsSuccess, string Message)> SaveJobAsync(int jobId, int categoryId, string jobTitle)
        {
            string cleanTitle = (jobTitle ?? string.Empty).Trim();
            if (categoryId <= 0)
                return (false, "يرجى اختيار الفئة التي تنتمي إليها الوظيفة أولاً.");

            if (string.IsNullOrWhiteSpace(cleanTitle))
                return (false, "يرجى إدخال اسم الوظيفة (مثل: معلم، مربية).");

            if (!CheckPermission(jobId == 0 ? "insert" : "update", out string permErr))
                return (false, permErr);

            using var conn = await OpenReadyConnectionAsync();

            int dup = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.Jobs WHERE CategoryID = @CatID AND JobTitle = @Title AND JobID <> @ID",
                new { CatID = categoryId, Title = cleanTitle, ID = jobId });

            if (dup > 0)
                return (false, $"الوظيفة ({cleanTitle}) مسجلة مسبقاً تحت هذه الفئة.");

            if (jobId == 0)
            {
                await conn.ExecuteAsync(
                    "INSERT INTO dbo.Jobs (CategoryID, JobTitle, IsActive) VALUES (@CatID, @Title, 1)",
                    new { CatID = categoryId, Title = cleanTitle });
                return (true, "تمت إضافة الوظيفة بنجاح.");
            }
            else
            {
                await conn.ExecuteAsync(
                    "UPDATE dbo.Jobs SET CategoryID = @CatID, JobTitle = @Title WHERE JobID = @ID",
                    new { CatID = categoryId, Title = cleanTitle, ID = jobId });
                return (true, "تم تعديل الوظيفة بنجاح.");
            }
        }

        public async Task<(bool IsSuccess, string Message)> SaveJobsBatchAsync(int categoryId, List<string> jobTitles)
        {
            if (categoryId <= 0)
                return (false, "يرجى اختيار الفئة التي تنتمي إليها الوظائف أولاً.");

            var cleaned = (jobTitles ?? new List<string>())
                .Select(t => (t ?? string.Empty).Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (cleaned.Count == 0)
                return (false, "يرجى إضافة وظيفة واحدة على الأقل إلى القائمة قبل الحفظ.");

            var dupInList = cleaned
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (dupInList != null)
                return (false, $"الوظيفة ({dupInList.Key}) مكررة أكثر من مرة داخل القائمة.");

            if (!CheckPermission("insert", out string permErr))
                return (false, permErr);

            using var conn = await OpenReadyConnectionAsync();

            foreach (string title in cleaned)
            {
                int dupDb = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(1) FROM dbo.Jobs WHERE CategoryID = @CatID AND JobTitle = @Title",
                    new { CatID = categoryId, Title = title });
                if (dupDb > 0)
                    return (false, $"الوظيفة ({title}) مسجلة مسبقاً تحت هذه الفئة.");
            }

            using var tx = conn.BeginTransaction();
            try
            {
                foreach (string title in cleaned)
                {
                    await conn.ExecuteAsync(
                        "INSERT INTO dbo.Jobs (CategoryID, JobTitle, IsActive) VALUES (@CatID, @Title, 1)",
                        new { CatID = categoryId, Title = title }, tx);
                }

                await tx.CommitAsync();
                return (true, $"تم حفظ ({cleaned.Count}) وظيفة بنجاح.");
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return (false, $"تعذر حفظ الوظائف: {ex.Message}");
            }
        }

        public async Task<(bool IsSuccess, string Message)> DeleteJobAsync(int jobId)
        {
            if (!CheckPermission("delete", out string permErr))
                return (false, permErr);

            using var conn = await OpenReadyConnectionAsync();
            await PurgeOrphanedArchivedDataAsync(conn);

            int activeEmps = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.Employees WHERE JobID = @ID AND IsActive = 1", new { ID = jobId });
            if (activeEmps > 0)
                return (false, "لا يمكن حذف هذه الوظيفة لوجود موظفين نشطين مسجلين عليها حالياً.");

            await conn.ExecuteAsync(@"
                DELETE jt FROM dbo.JobTypes jt
                WHERE jt.JobID = @ID
                  AND NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.JobTypeID = jt.JobTypeID);

                UPDATE dbo.JobTypes SET IsActive = 0 WHERE JobID = @ID;
            ", new { ID = jobId });

            try
            {
                await conn.ExecuteAsync("DELETE FROM dbo.Jobs WHERE JobID = @ID", new { ID = jobId });
                return (true, "تم حذف الوظيفة وأنواعها التابعة بنجاح.");
            }
            catch (SqlException)
            {
                await conn.ExecuteAsync("UPDATE dbo.Jobs SET IsActive = 0 WHERE JobID = @ID", new { ID = jobId });
                return (true, "تم حذف الوظيفة من القائمة النشطة (مع الاحتفاظ بارتباطها في أرشيف التقييمات السابقة).");
            }
        }

        // =========================================================================
        // 5. إدارة أنواع الوظائف التابعة للوظيفة (JobTypes - إلزامي دائماً)
        // =========================================================================
        public async Task<List<JobTypeDto>> GetJobTypesAsync(int? filterJobId = null)
        {
            using var conn = await OpenReadyConnectionAsync();
            string sql = @"
                SELECT jt.JobTypeID, jt.JobID, j.JobTitle, j.CategoryID, c.CategoryName, jt.JobTypeName,
                       (SELECT COUNT(1) FROM dbo.Employees e WHERE e.JobTypeID = jt.JobTypeID AND e.IsActive = 1) AS EmployeesCount
                FROM dbo.JobTypes jt
                INNER JOIN dbo.Jobs j ON jt.JobID = j.JobID
                INNER JOIN dbo.Categories c ON j.CategoryID = c.CategoryID
                WHERE jt.IsActive = 1 AND (@JobID IS NULL OR jt.JobID = @JobID)
                ORDER BY j.JobID ASC, jt.JobTypeID ASC";
            return (await conn.QueryAsync<JobTypeDto>(sql, new { JobID = filterJobId })).ToList();
        }

        public async Task<(bool IsSuccess, string Message)> SaveJobTypeAsync(int jobTypeId, int jobId, string jobTypeName)
        {
            string cleanName = (jobTypeName ?? string.Empty).Trim();
            if (jobId <= 0)
                return (false, "يرجى اختيار الوظيفة التي يتبعها نوع الوظيفة أولاً.");

            if (string.IsNullOrWhiteSpace(cleanName))
                return (false, "يرجى إدخال نوع الوظيفة (مثل: معلم لغة عربية، معلم لغة إنجليزية).");

            if (!CheckPermission(jobTypeId == 0 ? "insert" : "update", out string permErr))
                return (false, permErr);

            using var conn = await OpenReadyConnectionAsync();

            int dup = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.JobTypes WHERE JobID = @JobID AND JobTypeName = @Name AND JobTypeID <> @ID",
                new { JobID = jobId, Name = cleanName, ID = jobTypeId });

            if (dup > 0)
                return (false, $"نوع الوظيفة ({cleanName}) مسجل مسبقاً تحت هذه الوظيفة.");

            if (jobTypeId == 0)
            {
                await conn.ExecuteAsync(
                    "INSERT INTO dbo.JobTypes (JobID, JobTypeName, IsActive) VALUES (@JobID, @Name, 1)",
                    new { JobID = jobId, Name = cleanName });
                return (true, "تمت إضافة نوع الوظيفة بنجاح.");
            }
            else
            {
                await conn.ExecuteAsync(
                    "UPDATE dbo.JobTypes SET JobID = @JobID, JobTypeName = @Name WHERE JobTypeID = @ID",
                    new { JobID = jobId, Name = cleanName, ID = jobTypeId });
                return (true, "تم تعديل نوع الوظيفة بنجاح.");
            }
        }

        public async Task<(bool IsSuccess, string Message)> SaveJobTypesBatchAsync(int jobId, List<string> jobTypeNames)
        {
            if (jobId <= 0)
                return (false, "يرجى اختيار الوظيفة التي تتبعها أنواع الوظائف أولاً.");

            var cleaned = (jobTypeNames ?? new List<string>())
                .Select(t => (t ?? string.Empty).Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (cleaned.Count == 0)
                return (false, "يرجى إضافة نوع وظيفة واحد على الأقل إلى القائمة قبل الحفظ.");

            var dupInList = cleaned
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (dupInList != null)
                return (false, $"نوع الوظيفة ({dupInList.Key}) مكرر أكثر من مرة داخل القائمة.");

            if (!CheckPermission("insert", out string permErr))
                return (false, permErr);

            using var conn = await OpenReadyConnectionAsync();

            foreach (string name in cleaned)
            {
                int dupDb = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(1) FROM dbo.JobTypes WHERE JobID = @JobID AND JobTypeName = @Name",
                    new { JobID = jobId, Name = name });
                if (dupDb > 0)
                    return (false, $"نوع الوظيفة ({name}) مسجل مسبقاً تحت هذه الوظيفة.");
            }

            using var tx = conn.BeginTransaction();
            try
            {
                foreach (string name in cleaned)
                {
                    await conn.ExecuteAsync(
                        "INSERT INTO dbo.JobTypes (JobID, JobTypeName, IsActive) VALUES (@JobID, @Name, 1)",
                        new { JobID = jobId, Name = name }, tx);
                }

                await tx.CommitAsync();
                return (true, $"تم حفظ ({cleaned.Count}) نوع وظيفة بنجاح.");
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return (false, $"تعذر حفظ أنواع الوظائف: {ex.Message}");
            }
        }

        public async Task<(bool IsSuccess, string Message)> DeleteJobTypeAsync(int jobTypeId)
        {
            if (!CheckPermission("delete", out string permErr))
                return (false, permErr);

            using var conn = await OpenReadyConnectionAsync();
            await PurgeOrphanedArchivedDataAsync(conn);

            int activeEmps = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.Employees WHERE JobTypeID = @ID AND IsActive = 1", new { ID = jobTypeId });
            if (activeEmps > 0)
                return (false, "لا يمكن حذف نوع الوظيفة لوجود موظفين نشطين مسجلين عليه حالياً.");

            try
            {
                await conn.ExecuteAsync("DELETE FROM dbo.JobTypes WHERE JobTypeID = @ID", new { ID = jobTypeId });
                return (true, "تم حذف نوع الوظيفة بنجاح.");
            }
            catch (SqlException)
            {
                await conn.ExecuteAsync("UPDATE dbo.JobTypes SET IsActive = 0 WHERE JobTypeID = @ID", new { ID = jobTypeId });
                return (true, "تم حذف نوع الوظيفة من القائمة النشطة (مع الاحتفاظ بارتباطه في أرشيف التقييمات السابقة).");
            }
        }

        // =========================================================================
        // 6. إدارة مجموعات المقيمين والمقيمين بداخلها (EvaluatorGroups & Evaluators)
        // =========================================================================
        public async Task<List<EvaluatorDto>> GetEvaluatorsAsync()
        {
            using var conn = await OpenReadyConnectionAsync();
            string sql = @"
                SELECT ev.EvaluatorID, ev.GroupID, eg.GroupName, ev.EvaluatorName,
                       (SELECT COUNT(1) FROM dbo.CategoryEvaluatorGroups ceg WHERE ceg.GroupID = ev.GroupID) AS AssignedCategoriesCount,
                       (SELECT COUNT(1) FROM dbo.EmployeeEvaluators ee INNER JOIN dbo.Employees e ON ee.EmployeeID = e.EmployeeID WHERE ee.EvaluatorID = ev.EvaluatorID AND e.IsActive = 1) AS AssignedEmployeesCount
                FROM dbo.Evaluators ev
                INNER JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                WHERE ev.IsActive = 1 AND eg.IsActive = 1
                ORDER BY eg.GroupID ASC, ev.EvaluatorID ASC";
            return (await conn.QueryAsync<EvaluatorDto>(sql)).ToList();
        }

        public async Task<(bool IsSuccess, string Message)> SaveSystemEvaluatorGroupSettingsAsync(int groupId, int maxAllowedCount)
        {
            if (!CheckPermission("update", out string permErr) && !CheckPermission("insert", out permErr))
                return (false, permErr);

            using var conn = await OpenReadyConnectionAsync();
            await conn.ExecuteAsync(
                "UPDATE dbo.EvaluatorGroups SET MaxAllowedCount = @MaxCnt WHERE GroupID = @GID",
                new { MaxCnt = maxAllowedCount, GID = groupId });
            var templateSvc = new TemplateService(_connectionString);
            await templateSvc.EnsureSystemGroupTemplateAsync(groupId, conn);
            return (true, "تم حفظ إعدادات الحد المسموح به وتحديث القالب التلقائي بنجاح.");
        }

        private static async Task EnsureSystemGroupsActiveInDbAsync(SqlConnection conn)
        {
            await conn.ExecuteAsync(@"
                DECLARE @AbsGID INT = (SELECT TOP 1 GroupID FROM dbo.EvaluatorGroups WHERE SystemGroupCode = 'ABSENCE' OR GroupName IN (N'الغياب', N'غياب') ORDER BY GroupID ASC);
                IF @AbsGID IS NULL
                BEGIN
                    INSERT INTO dbo.EvaluatorGroups (GroupName, IsActive, IsSystemGroup, SystemGroupCode, MaxAllowedCount) VALUES (N'الغياب', 1, 1, 'ABSENCE', 3);
                    SET @AbsGID = SCOPE_IDENTITY();
                END
                ELSE
                BEGIN
                    UPDATE dbo.EvaluatorGroups SET GroupName = N'الغياب', IsActive = 1, IsSystemGroup = 1, SystemGroupCode = 'ABSENCE', MaxAllowedCount = CASE WHEN ISNULL(MaxAllowedCount, -1) < 0 THEN 0 ELSE MaxAllowedCount END WHERE GroupID = @AbsGID;
                END

                IF NOT EXISTS (SELECT 1 FROM dbo.Evaluators WHERE GroupID = @AbsGID)
                    INSERT INTO dbo.Evaluators (GroupID, EvaluatorName, IsActive) VALUES (@AbsGID, N'الغياب', 1);
                ELSE
                BEGIN
                    DECLARE @AbsEvID INT = (SELECT TOP 1 EvaluatorID FROM dbo.Evaluators WHERE GroupID = @AbsGID ORDER BY IsActive DESC, EvaluatorID ASC);
                    UPDATE dbo.Evaluators SET EvaluatorName = N'الغياب', IsActive = 1 WHERE EvaluatorID = @AbsEvID;
                END

                DECLARE @DelGID INT = (SELECT TOP 1 GroupID FROM dbo.EvaluatorGroups WHERE SystemGroupCode = 'DELAY' OR GroupName IN (N'التأخير', N'التاخير', N'تأخير', N'تاخير') ORDER BY GroupID ASC);
                IF @DelGID IS NULL
                BEGIN
                    INSERT INTO dbo.EvaluatorGroups (GroupName, IsActive, IsSystemGroup, SystemGroupCode, MaxAllowedCount) VALUES (N'التأخير', 1, 1, 'DELAY', 3);
                    SET @DelGID = SCOPE_IDENTITY();
                END
                ELSE
                BEGIN
                    UPDATE dbo.EvaluatorGroups SET GroupName = N'التأخير', IsActive = 1, IsSystemGroup = 1, SystemGroupCode = 'DELAY', MaxAllowedCount = CASE WHEN ISNULL(MaxAllowedCount, -1) < 0 THEN 0 ELSE MaxAllowedCount END WHERE GroupID = @DelGID;
                END

                IF NOT EXISTS (SELECT 1 FROM dbo.Evaluators WHERE GroupID = @DelGID)
                    INSERT INTO dbo.Evaluators (GroupID, EvaluatorName, IsActive) VALUES (@DelGID, N'التأخير', 1);
                ELSE
                BEGIN
                    DECLARE @DelEvID INT = (SELECT TOP 1 EvaluatorID FROM dbo.Evaluators WHERE GroupID = @DelGID ORDER BY IsActive DESC, EvaluatorID ASC);
                    UPDATE dbo.Evaluators SET EvaluatorName = N'التأخير', IsActive = 1 WHERE EvaluatorID = @DelEvID;
                END
            ");
        }

        public async Task<List<EvaluatorGroupDto>> GetEvaluatorGroupsAsync(int? evalYear = null, int? evalMonth = null)
        {
            using var conn = await OpenReadyConnectionAsync();
            await EnsureSystemGroupsActiveInDbAsync(conn);
            var (year, month) = await ResolvePeriodOrLatestAsync(conn, evalYear, evalMonth);

            var groups = (await conn.QueryAsync<EvaluatorGroupDto>(@"
                SELECT eg.GroupID, eg.GroupName, ISNULL(eg.IsSystemGroup, 0) AS IsSystemGroup,
                       ISNULL(eg.SystemGroupCode, N'') AS SystemGroupCode, ISNULL(eg.MaxAllowedCount, 3) AS MaxAllowedCount,
                       0 AS AssignedCategoriesCount
                FROM dbo.EvaluatorGroups eg
                WHERE eg.IsActive = 1
                ORDER BY eg.IsSystemGroup DESC, eg.GroupID ASC")).ToList();

            var effWeights = await MonthlyPeriodConfigService.GetEffectiveCategoryWeightsAsync(conn, year, month);
            var catsCountByGroup = effWeights
                .GroupBy(w => w.GroupID)
                .ToDictionary(g => g.Key, g => g.Select(x => x.CategoryID).Distinct().Count());

            foreach (var g in groups)
            {
                g.AssignedCategoriesCount = catsCountByGroup.TryGetValue(g.GroupID, out int cCnt) ? cCnt : 0;
            }

            var evaluators = await GetEvaluatorsAsync();
            var evalByGroup = evaluators.GroupBy(e => e.GroupID).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var grp in groups)
            {
                if (evalByGroup.TryGetValue(grp.GroupID, out var mems))
                {
                    grp.Members = mems;
                    grp.MembersCount = mems.Count;
                    grp.MembersSummary = grp.IsSystemGroup
                        ? (grp.SystemGroupCode == "ABSENCE"
                            ? $"قالب غياب تلقائي موحد (الحد المسموح: {grp.MaxAllowedCount} أيام — ما زاد درجته 0)"
                            : $"قالب تأخير تلقائي موحد (الحد المسموح: {grp.MaxAllowedCount} مرات — ما زاد درجته 0)")
                        : string.Join(" ، ", mems.Select(m => m.EvaluatorName));
                }
                else
                {
                    grp.MembersCount = 0;
                    grp.MembersSummary = "بدون مقيمين";
                }
            }

            return groups;
        }

        public async Task<(bool IsSuccess, string Message)> SaveEvaluatorGroupAsync(
            int groupId,
            string groupName,
            List<(int EvaluatorID, string EvaluatorName)> members)
        {
            string cleanGroup = (groupName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cleanGroup))
                return (false, "يرجى إدخال اسم مجموعة المقيمين (أو المسمى الإشرافي).");

            var cleanedMembers = (members ?? new())
                .Select(m => (m.EvaluatorID, Name: (m.EvaluatorName ?? string.Empty).Trim()))
                .Where(m => !string.IsNullOrWhiteSpace(m.Name))
                .ToList();

            if (cleanedMembers.Count == 0)
                return (false, "يرجى إدخال اسم مقيم واحد على الأقل داخل المجموعة.");

            var dupInternal = cleanedMembers
                .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (dupInternal != null)
                return (false, $"اسم المقيم ({dupInternal.Key}) مكرر مرتين داخل نفس القائمة.");

            if (!CheckPermission(groupId == 0 ? "insert" : "update", out string permErr))
                return (false, permErr);

            using var conn = await OpenReadyConnectionAsync();

            int dupGroup = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.EvaluatorGroups WHERE GroupName = @Name AND GroupID <> @ID",
                new { Name = cleanGroup, ID = groupId });
            if (dupGroup > 0)
                return (false, $"اسم المجموعة ({cleanGroup}) مسجل مسبقاً.");

            foreach (var mem in cleanedMembers)
            {
                int dupEval = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(1) FROM dbo.Evaluators WHERE EvaluatorName = @Name AND EvaluatorID <> @ID",
                    new { mem.Name, ID = mem.EvaluatorID });
                if (dupEval > 0)
                    return (false, $"اسم المقيم ({mem.Name}) مسجل بالفعل في مجموعة أخرى أو سجل آخر.");
            }

            using var tx = conn.BeginTransaction();
            try
            {
                int targetGroupId = groupId;
                if (targetGroupId == 0)
                {
                    targetGroupId = await conn.ExecuteScalarAsync<int>(@"
                        INSERT INTO dbo.EvaluatorGroups (GroupName, IsActive)
                        VALUES (@Name, 1);
                        SELECT CAST(SCOPE_IDENTITY() AS INT);",
                        new { Name = cleanGroup }, tx);
                }
                else
                {
                    await conn.ExecuteAsync(
                        "UPDATE dbo.EvaluatorGroups SET GroupName = @Name WHERE GroupID = @ID",
                        new { Name = cleanGroup, ID = targetGroupId }, tx);

                    var existingIds = (await conn.QueryAsync<int>(
                        "SELECT EvaluatorID FROM dbo.Evaluators WHERE GroupID = @GID AND IsActive = 1",
                        new { GID = targetGroupId }, tx)).ToList();

                    var keptIds = cleanedMembers.Where(m => m.EvaluatorID > 0).Select(m => m.EvaluatorID).ToHashSet();
                    foreach (int oldId in existingIds.Where(id => !keptIds.Contains(id)))
                    {
                        int linkedTemplatesOrRecords = await conn.ExecuteScalarAsync<int>(@"
                            SELECT (SELECT COUNT(1) FROM dbo.Templates WHERE EvaluatorID = @EID)
                                 + (SELECT COUNT(1) FROM dbo.EvaluationRecords WHERE EvaluatorID = @EID)",
                            new { EID = oldId }, tx);

                        await conn.ExecuteAsync("DELETE FROM dbo.EmployeeEvaluators WHERE EvaluatorID = @EID", new { EID = oldId }, tx);

                        if (linkedTemplatesOrRecords > 0)
                        {
                            await conn.ExecuteAsync("UPDATE dbo.Evaluators SET IsActive = 0 WHERE EvaluatorID = @EID", new { EID = oldId }, tx);
                        }
                        else
                        {
                            await conn.ExecuteAsync("DELETE FROM dbo.Evaluators WHERE EvaluatorID = @EID", new { EID = oldId }, tx);
                        }
                    }
                }

                int singleActiveEvaluatorId = 0;
                foreach (var mem in cleanedMembers)
                {
                    if (mem.EvaluatorID > 0)
                    {
                        await conn.ExecuteAsync(
                            "UPDATE dbo.Evaluators SET EvaluatorName = @Name, GroupID = @GID, IsActive = 1 WHERE EvaluatorID = @EID",
                            new { mem.Name, GID = targetGroupId, EID = mem.EvaluatorID }, tx);
                        singleActiveEvaluatorId = mem.EvaluatorID;
                    }
                    else
                    {
                        singleActiveEvaluatorId = await conn.ExecuteScalarAsync<int>(@"
                            INSERT INTO dbo.Evaluators (GroupID, EvaluatorName, IsActive)
                            VALUES (@GID, @Name, 1);
                            SELECT CAST(SCOPE_IDENTITY() AS INT);",
                            new { GID = targetGroupId, mem.Name }, tx);
                    }
                }

                // إذا كانت المجموعة تتكون من مقيم واحد فقط ومرتبطة بفئات، يربط تلقائياً بموظفي تلك الفئات
                if (cleanedMembers.Count == 1 && singleActiveEvaluatorId > 0)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.EmployeeEvaluators (EmployeeID, EvaluatorID)
                        SELECT e.EmployeeID, @EvalID
                        FROM dbo.Employees e
                        INNER JOIN dbo.CategoryEvaluatorGroups ceg ON e.CategoryID = ceg.CategoryID
                        WHERE ceg.GroupID = @GID
                          AND NOT EXISTS (
                              SELECT 1 FROM dbo.EmployeeEvaluators ee
                              WHERE ee.EmployeeID = e.EmployeeID AND ee.EvaluatorID = @EvalID
                          );",
                        new { EvalID = singleActiveEvaluatorId, GID = targetGroupId }, tx);
                }

                await tx.CommitAsync();
                return (true, groupId == 0 ? "تم إنشاء مجموعة المقيمين وحفظ الأسماء بنجاح." : "تم تحديث مجموعة المقيمين بنجاح.");
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return (false, $"فشل حفظ مجموعة المقيمين: {ex.Message}");
            }
        }

        public async Task<(bool IsSuccess, string Message)> DeleteEvaluatorGroupAsync(int groupId)
        {
            if (!CheckPermission("delete", out string permErr))
                return (false, permErr);

            using var conn = await OpenReadyConnectionAsync();
            int isSysGrp = await conn.ExecuteScalarAsync<int>(
                "SELECT ISNULL(IsSystemGroup, 0) FROM dbo.EvaluatorGroups WHERE GroupID = @GID",
                new { GID = groupId });
            if (isSysGrp == 1)
                return (false, "لا يمكن حذف مجموعة (الغياب / التأخير) لأنها مجموعة نظام افتراضية ثابتة.");
            await PurgeOrphanedArchivedDataAsync(conn);

            int activeTemplates = await conn.ExecuteScalarAsync<int>(@"
                SELECT COUNT(1) FROM dbo.Templates t
                INNER JOIN dbo.Evaluators ev ON t.EvaluatorID = ev.EvaluatorID
                WHERE ev.GroupID = @GID AND t.IsDeleted = 0 AND t.IsCurrentVersion = 1", new { GID = groupId });
            if (activeTemplates > 0)
                return (false, "لا يمكن حذف هذه المجموعة لوجود قوالب تقييم فعالة لأحد مقيميها حالياً.");

            await conn.ExecuteAsync("DELETE FROM dbo.CategoryEvaluatorGroups WHERE GroupID = @GID", new { GID = groupId });
            await conn.ExecuteAsync(@"
                DELETE ee FROM dbo.EmployeeEvaluators ee
                INNER JOIN dbo.Evaluators ev ON ee.EvaluatorID = ev.EvaluatorID
                WHERE ev.GroupID = @GID", new { GID = groupId });

            await conn.ExecuteAsync(@"
                DELETE ev FROM dbo.Evaluators ev
                WHERE ev.GroupID = @GID
                  AND NOT EXISTS (SELECT 1 FROM dbo.Templates t WHERE t.EvaluatorID = ev.EvaluatorID)
                  AND NOT EXISTS (SELECT 1 FROM dbo.EvaluationRecords er WHERE er.EvaluatorID = ev.EvaluatorID);

                UPDATE dbo.Evaluators SET IsActive = 0 WHERE GroupID = @GID;
            ", new { GID = groupId });

            try
            {
                await conn.ExecuteAsync("DELETE FROM dbo.EvaluatorGroups WHERE GroupID = @GID", new { GID = groupId });
                return (true, "تم حذف مجموعة المقيمين بنجاح.");
            }
            catch (SqlException)
            {
                await conn.ExecuteAsync("UPDATE dbo.EvaluatorGroups SET IsActive = 0 WHERE GroupID = @GID", new { GID = groupId });
                return (true, "تم حذف مجموعة المقيمين من القائمة النشطة (مع الاحتفاظ بأرشيف التقييمات الشهرية السابقة).");
            }
        }

        // =========================================================================
        // 7. تخصيص مجموعات المقيمين ودرجات التقييم للفئات (شرط المجموع = 100 درجة)
        // =========================================================================
        public async Task<List<CategoryEvaluatorGroupSelectRow>> GetCategoryEvaluatorGroupMatrixAsync(
            int categoryId, int? evalYear = null, int? evalMonth = null)
        {
            using var conn = await OpenReadyConnectionAsync();
            var (year, month) = await ResolvePeriodOrLatestAsync(conn, evalYear, evalMonth);
            var groups = await GetEvaluatorGroupsAsync(year, month);
            var existingWeights = new Dictionary<int, decimal>();

            if (categoryId > 0)
            {
                var effWeights = await MonthlyPeriodConfigService.GetEffectiveCategoryWeightsAsync(conn, year, month);
                foreach (var r in effWeights.Where(w => w.CategoryID == categoryId))
                {
                    existingWeights[r.GroupID] = r.WeightScore;
                }
            }

            return groups.Select(g =>
            {
                bool hasWeight = existingWeights.TryGetValue(g.GroupID, out decimal w) && w > 0m;
                return new CategoryEvaluatorGroupSelectRow
                {
                    GroupID = g.GroupID,
                    GroupName = g.GroupName,
                    MembersCount = g.MembersCount,
                    MembersSummary = g.MembersSummary,
                    IsSelected = hasWeight,
                    WeightScore = hasWeight ? w : 0m
                };
            }).ToList();
        }

        public async Task<(bool IsSuccess, string Message)> SaveCategoryEvaluatorWeightsAsync(
            int categoryId,
            List<CategoryEvaluatorGroupSelectRow> selectedGroups,
            int? evalYear = null,
            int? evalMonth = null)
        {
            if (categoryId <= 0)
                return (false, "يرجى اختيار الفئة المراد تخصيص درجات مقيميها.");

            if (!CheckPermission("update", out string permErr) && !CheckPermission("insert", out permErr))
                return (false, permErr);

            var activeRows = (selectedGroups ?? new()).Where(x => x.IsSelected && x.WeightScore > 0m).ToList();
            if (activeRows.Count == 0)
                return (false, "يرجى اختيار مجموعة مقيمين واحدة على الأقل وتحديد درجتها لهذه الفئة.");

            decimal totalWeight = activeRows.Sum(x => x.WeightScore);
            if (totalWeight != 100m)
            {
                return (false,
                    $"شرط النظام: يجب أن يكون مجموع درجات مجموعات المقيمين للفئة يساوي 100 درجة بالضبط.\nالمجموع الحالي هو ({totalWeight:0.##}) درجة.");
            }

            using var conn = await OpenReadyConnectionAsync();
            var (year, month) = await ResolvePeriodOrLatestAsync(conn, evalYear, evalMonth);

            var (isLocked, lockedBy, _) = await MonthlyPeriodConfigService.GetMonthLockStatusAsync(conn, year, month);
            if (isLocked)
            {
                string lockPeriod = $"{IncentiveCalculatorService.GetArabicMonthName(month)} {year} م";
                return (false, $"لا يمكن تعديل توزيع درجات شهر ({lockPeriod}) لأنه معتمد ومغلق نهائياً بواسطة ({lockedBy}).");
            }

            using var tx = conn.BeginTransaction();
            try
            {
                await MonthlyPeriodConfigService.EnsureMonthMaterializedAsync(conn, tx, year, month);

                await conn.ExecuteAsync(
                    "DELETE FROM dbo.MonthlyCategoryEvaluatorGroups WHERE EvalYear = @Year AND EvalMonth = @Month AND CategoryID = @CatID",
                    new { Year = year, Month = month, CatID = categoryId }, tx);

                foreach (var row in activeRows)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.MonthlyCategoryEvaluatorGroups (EvalYear, EvalMonth, CategoryID, GroupID, WeightScore, UpdatedAt)
                        VALUES (@Year, @Month, @CatID, @GID, @Weight, SYSDATETIME())",
                        new { Year = year, Month = month, CatID = categoryId, GID = row.GroupID, Weight = row.WeightScore }, tx);
                }

                await conn.ExecuteAsync(@"
                    DELETE mee
                    FROM dbo.MonthlyEmployeeEvaluators mee
                    INNER JOIN dbo.MonthlyEmployeeSnapshots mes
                        ON mes.EmployeeID = mee.EmployeeID AND mes.EvalYear = mee.EvalYear AND mes.EvalMonth = mee.EvalMonth
                    INNER JOIN dbo.Evaluators ev ON mee.EvaluatorID = ev.EvaluatorID
                    WHERE mee.EvalYear = @Year AND mee.EvalMonth = @Month
                      AND mes.CategoryID = @CatID
                      AND NOT EXISTS (
                          SELECT 1 FROM dbo.MonthlyCategoryEvaluatorGroups mceg
                          WHERE mceg.EvalYear = @Year AND mceg.EvalMonth = @Month
                            AND mceg.CategoryID = @CatID AND mceg.GroupID = ev.GroupID
                      );", new { Year = year, Month = month, CatID = categoryId }, tx);

                await conn.ExecuteAsync(@"
                    INSERT INTO dbo.MonthlyEmployeeEvaluators (EvalYear, EvalMonth, EmployeeID, EvaluatorID, UpdatedAt)
                    SELECT mes.EmployeeID, @Year, @Month, ev.EvaluatorID, SYSDATETIME()
                    FROM dbo.MonthlyEmployeeSnapshots mes
                    INNER JOIN dbo.MonthlyCategoryEvaluatorGroups mceg
                        ON mes.CategoryID = mceg.CategoryID AND mceg.EvalYear = @Year AND mceg.EvalMonth = @Month
                    INNER JOIN dbo.Evaluators ev ON ev.GroupID = mceg.GroupID AND ev.IsActive = 1
                    WHERE mes.EvalYear = @Year AND mes.EvalMonth = @Month
                      AND mes.CategoryID = @CatID
                      AND (SELECT COUNT(1) FROM dbo.Evaluators ev2 WHERE ev2.GroupID = mceg.GroupID AND ev2.IsActive = 1) = 1
                      AND NOT EXISTS (
                          SELECT 1 FROM dbo.MonthlyEmployeeEvaluators mee
                          WHERE mee.EvalYear = @Year AND mee.EvalMonth = @Month
                            AND mee.EmployeeID = mes.EmployeeID AND mee.EvaluatorID = ev.EvaluatorID
                      );", new { Year = year, Month = month, CatID = categoryId }, tx);

                var (latestY, latestM) = await MonthlyPeriodConfigService.GetLatestConfiguredPeriodAsync(conn, tx);
                bool isLatest = (year * 100 + month) >= (latestY * 100 + latestM);

                await MonthlyPeriodConfigService.SyncLiveTablesIfLatestPeriodAsync(conn, tx, year, month);
                await tx.CommitAsync();

                if (isLatest)
                {
                    var systemGroups = (await conn.QueryAsync<int>("SELECT GroupID FROM dbo.EvaluatorGroups WHERE IsSystemGroup = 1 AND IsActive = 1")).ToList();
                    var templateSvc = new TemplateService(_connectionString);
                    foreach (int sysGroupId in systemGroups)
                    {
                        await templateSvc.EnsureSystemGroupTemplateAsync(sysGroupId, conn);
                    }
                }

                string periodLabel = $"{IncentiveCalculatorService.GetArabicMonthName(month)} {year} م";
                return (true, $"تم اعتماد تخصيص المقيمين وتوزيع الـ 100 درجة للفئة في شهر ({periodLabel}) بنجاح دون التأثير على الشهور السابقة.");
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return (false, $"فشل حفظ تخصيص الدرجات: {ex.Message}");
            }
        }

        // =========================================================================
        // 8. إدارة الموظفين + تحديد المقيمين المسؤولين + استيراد وتصدير Excel
        // =========================================================================
        public async Task<List<EmployeeEvaluatorGroupSection>> GetCategoryGroupsForEmployeeDialogAsync(
            int categoryId, int employeeId = 0, int? evalYear = null, int? evalMonth = null)
        {
            using var conn = await OpenReadyConnectionAsync();
            if (categoryId <= 0) return new List<EmployeeEvaluatorGroupSection>();

            var (year, month) = await ResolvePeriodOrLatestAsync(conn, evalYear, evalMonth);

            var assignedEvalIds = new HashSet<int>();
            if (employeeId > 0)
            {
                var effAssigns = await MonthlyPeriodConfigService.GetEffectiveEmployeeAssignmentsAsync(conn, year, month);
                assignedEvalIds = effAssigns.Where(a => a.EmployeeID == employeeId).Select(a => a.EvaluatorID).ToHashSet();
            }

            var catGroups = (await MonthlyPeriodConfigService.GetEffectiveCategoryWeightsAsync(conn, year, month))
                .Where(w => w.CategoryID == categoryId)
                .OrderByDescending(w => w.WeightScore)
                .ThenBy(w => w.GroupID)
                .ToList();

            var groupIds = catGroups.Select(g => g.GroupID).Distinct().ToList();
            if (groupIds.Count == 0) return new List<EmployeeEvaluatorGroupSection>();

            var evaluators = (await conn.QueryAsync<EvaluatorDto>(@"
                SELECT ev.EvaluatorID, ev.GroupID, ev.EvaluatorName
                FROM dbo.Evaluators ev
                WHERE ev.GroupID IN @GroupIds AND ev.IsActive = 1
                ORDER BY ev.EvaluatorID ASC",
                new { GroupIds = groupIds })).ToList();

            var evalByGroup = evaluators.GroupBy(e => e.GroupID).ToDictionary(g => g.Key, g => g.ToList());
            var sections = new List<EmployeeEvaluatorGroupSection>();

            foreach (var cg in catGroups)
            {
                int gId = cg.GroupID;
                evalByGroup.TryGetValue(gId, out var mems);
                mems ??= new List<EvaluatorDto>();

                bool isSingle = mems.Count == 1;
                var section = new EmployeeEvaluatorGroupSection
                {
                    GroupID = gId,
                    GroupName = cg.GroupName,
                    GroupWeight = cg.WeightScore
                };

                foreach (var m in mems)
                {
                    bool selected = isSingle || assignedEvalIds.Contains(m.EvaluatorID);
                    section.Members.Add(new EmployeeEvaluatorMemberItem
                    {
                        EvaluatorID = m.EvaluatorID,
                        GroupID = gId,
                        EvaluatorName = m.EvaluatorName,
                        IsSingleMemberGroup = isSingle,
                        IsSelected = selected
                    });
                }

                sections.Add(section);
            }

            return sections;
        }

        public async Task<List<EmployeeDto>> GetEmployeesAsync(
            string search = "",
            int? branchId = null,
            int? categoryId = null,
            string statusFilter = "ALL",
            int? evalYear = null,
            int? evalMonth = null)
        {
            using var conn = await OpenReadyConnectionAsync();
            var (year, month) = await ResolvePeriodOrLatestAsync(conn, evalYear, evalMonth);

            string sql = @"
                SELECT e.EmployeeID, e.SystemName, e.EvaluationName,
                       e.BranchID, b.BranchName,
                       ISNULL(e.DepartmentID, 0) AS DepartmentID,
                       e.CategoryID, c.CategoryName,
                       e.JobID, j.JobTitle,
                       e.JobTypeID, jt.JobTypeName,
                       e.HasIncentive, e.IsContinuing
                FROM dbo.Employees e
                INNER JOIN dbo.Branches b ON e.BranchID = b.BranchID
                INNER JOIN dbo.Categories c ON e.CategoryID = c.CategoryID
                INNER JOIN dbo.Jobs j ON e.JobID = j.JobID
                INNER JOIN dbo.JobTypes jt ON e.JobTypeID = jt.JobTypeID
                WHERE e.IsActive = 1
                  AND (@Search = '' OR e.SystemName LIKE @Pattern OR e.EvaluationName LIKE @Pattern OR j.JobTitle LIKE @Pattern OR jt.JobTypeName LIKE @Pattern)
                  AND (@BranchID IS NULL OR e.BranchID = @BranchID)
                ORDER BY b.BranchName ASC, e.EvaluationName ASC";

            var employees = (await conn.QueryAsync<EmployeeDto>(sql, new
            {
                Search = (search ?? string.Empty).Trim(),
                Pattern = $"%{(search ?? string.Empty).Trim()}%",
                BranchID = branchId
            })).ToList();

            var catNameById = (await conn.QueryAsync<dynamic>("SELECT CategoryID, CategoryName FROM dbo.Categories"))
                .ToDictionary(x => (int)x.CategoryID, x => (string)x.CategoryName);

            var snaps = await MonthlyPeriodConfigService.GetEffectiveEmployeeSnapshotsAsync(conn, year, month);
            foreach (var emp in employees)
            {
                if (snaps.TryGetValue(emp.EmployeeID, out var snap))
                {
                    emp.CategoryID = snap.CategoryID;
                    if (catNameById.TryGetValue(snap.CategoryID, out var cname))
                        emp.CategoryName = cname;
                    emp.HasIncentive = snap.HasIncentive;
                    emp.IsContinuing = snap.IsContinuing;
                }
            }

            if (categoryId.HasValue && categoryId.Value > 0)
                employees = employees.Where(e => e.CategoryID == categoryId.Value).ToList();

            if (statusFilter == "CONT_INC")
                employees = employees.Where(e => e.IsContinuing && e.HasIncentive).ToList();
            else if (statusFilter == "CONT_NO_INC")
                employees = employees.Where(e => e.IsContinuing && !e.HasIncentive).ToList();
            else if (statusFilter == "DISCONT")
                employees = employees.Where(e => !e.IsContinuing).ToList();

            var catGroups = await MonthlyPeriodConfigService.GetEffectiveCategoryWeightsAsync(conn, year, month);
            var catGroupsMap = catGroups
                .GroupBy(x => x.CategoryID)
                .ToDictionary(g => g.Key, g => g.ToList());

            var empEvals = await MonthlyPeriodConfigService.GetEffectiveEmployeeAssignmentsAsync(conn, year, month);
            var empEvalMap = empEvals
                .GroupBy(x => x.EmployeeID)
                .ToDictionary(g => g.Key, g => g.ToList());

            var activeTemplateSheets = (await conn.QueryAsync<dynamic>(@"
                SELECT t.EvaluatorID, ts.TargetCategoryID, ts.TargetBranchID, ISNULL(eg.IsSystemGroup, 0) AS IsSystemGroup
                FROM dbo.Templates t
                INNER JOIN dbo.Evaluators ev ON t.EvaluatorID = ev.EvaluatorID
                LEFT JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                INNER JOIN dbo.TemplateSheets ts ON ts.TemplateID = t.TemplateID
                WHERE t.IsCurrentVersion = 1 AND t.IsDeleted = 0")).ToList();

            var empExcludedFromTemplate = (await conn.QueryAsync<dynamic>(
                "SELECT EvaluatorID, EmployeeID FROM dbo.TemplateExcludedEmployees"))
                .Select(x => ((int)x.EvaluatorID, (int)x.EmployeeID)).ToHashSet();

            var activeTemplatesByEval = activeTemplateSheets
                .GroupBy(x => (int)x.EvaluatorID)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var emp in employees)
            {
                empEvalMap.TryGetValue(emp.EmployeeID, out var assignedRows);
                assignedRows ??= new List<EffectiveEmployeeAssignmentDto>();
                emp.SelectedEvaluatorIds = assignedRows.Select(r => r.EvaluatorID).ToList();

                if (!catGroupsMap.TryGetValue(emp.CategoryID, out var reqGroups) || reqGroups.Count == 0)
                {
                    emp.AssignedEvaluatorsSummary = "لم تُخصص درجات مقيمي الفئة بعد";
                    emp.IsCoverageComplete = false;
                    emp.TemplateCoverageStatus = "الفئة بلا تخصيص درجات";
                    continue;
                }

                var summaryParts = new List<string>();
                var requiredEvalList = new List<(int EvalId, string EvalName)>();
                bool anyUncoveredGroup = false;

                foreach (var grp in reqGroups)
                {
                    int gId = grp.GroupID;
                    string gName = grp.GroupName;
                    decimal gWeight = grp.WeightScore;

                    var chosenInGrp = assignedRows.Where(r => r.GroupID == gId).ToList();
                    if (chosenInGrp.Count == 0)
                    {
                        anyUncoveredGroup = true;
                        summaryParts.Add($"{gName}: [غير محدد ⚠]");
                    }
                    else
                    {
                        decimal share = Math.Round(gWeight / chosenInGrp.Count, 2, MidpointRounding.AwayFromZero);
                        foreach (var ch in chosenInGrp)
                        {
                            requiredEvalList.Add((ch.EvaluatorID, ch.EvaluatorName));
                        }
                        string names = string.Join("، ", chosenInGrp.Select(c => $"{c.EvaluatorName} ({share:0.##})"));
                        summaryParts.Add(names);
                    }
                }

                emp.AssignedEvaluatorsSummary = string.Join("  |  ", summaryParts);

                if (!emp.IsContinuing)
                {
                    emp.IsCoverageComplete = true;
                    emp.TemplateCoverageStatus = "منقطع (لا يظهر في التقييمات)";
                    continue;
                }

                if (anyUncoveredGroup)
                {
                    emp.IsCoverageComplete = false;
                    emp.TemplateCoverageStatus = "ينقصه تحديد مقيم من المجموعة ⚠";
                    continue;
                }

                var missingNames = new List<string>();
                foreach (var req in requiredEvalList)
                {
                    if (empExcludedFromTemplate.Contains((req.EvalId, emp.EmployeeID)))
                        continue;

                    if (!activeTemplatesByEval.TryGetValue(req.EvalId, out var sheetsForEval))
                    {
                        missingNames.Add(req.EvalName);
                        continue;
                    }

                    bool hasMatchingSheet = sheetsForEval.Any(sh =>
                        Convert.ToBoolean(sh.IsSystemGroup) ||
                        ((int)sh.TargetCategoryID == emp.CategoryID &&
                         (sh.TargetBranchID == null || (int)sh.TargetBranchID == emp.BranchID)));

                    if (!hasMatchingSheet)
                    {
                        missingNames.Add(req.EvalName);
                    }
                }

                var distinctMissing = missingNames.Distinct().ToList();
                if (distinctMissing.Count == 0 && requiredEvalList.Count > 0)
                {
                    emp.IsCoverageComplete = true;
                    emp.TemplateCoverageStatus = "مكتمل في القوالب ✔";
                }
                else
                {
                    emp.IsCoverageComplete = false;
                    emp.TemplateCoverageStatus = $"ينقصه ورقة قالب: {string.Join("، ", distinctMissing)} ⚠";
                }
            }

            return employees;
        }

        public async Task<(bool IsSuccess, string Message)> SaveEmployeeAsync(
            EmployeeDto dto, int? evalYear = null, int? evalMonth = null)
        {
            string sysName = (dto.SystemName ?? string.Empty).Trim();
            string evalName = (dto.EvaluationName ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(sysName) || string.IsNullOrWhiteSpace(evalName))
                return (false, "يرجى إدخال كل من (الاسم في النظام) و(الاسم التقييمي).");

            if (dto.BranchID <= 0)
                return (false, "يرجى اختيار الفرع.");
            if (dto.CategoryID <= 0)
                return (false, "يرجى اختيار الفئة.");
            if (dto.JobID <= 0)
                return (false, "يرجى اختيار الوظيفة.");
            if (dto.JobTypeID <= 0)
                return (false, "يرجى اختيار نوع الوظيفة (إلزامي دائماً).");

            if (!CheckPermission(dto.EmployeeID == 0 ? "insert" : "update", out string permErr))
                return (false, permErr);

            using var conn = await OpenReadyConnectionAsync();
            var (year, month) = await ResolvePeriodOrLatestAsync(conn, evalYear, evalMonth);

            var (isLocked, lockedBy, _) = await MonthlyPeriodConfigService.GetMonthLockStatusAsync(conn, year, month);
            if (isLocked)
            {
                string lockPeriod = $"{IncentiveCalculatorService.GetArabicMonthName(month)} {year} م";
                return (false, $"لا يمكن تعديل إسناد وحالة الموظف في شهر ({lockPeriod}) لأنه معتمد ومغلق نهائياً بواسطة ({lockedBy}).");
            }

            int dupSys = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.Employees WHERE SystemName = @Sys AND EmployeeID <> @ID",
                new { Sys = sysName, ID = dto.EmployeeID });
            if (dupSys > 0)
                return (false, $"الاسم في النظام ({sysName}) مسجل لموظف آخر بالفعل.");

            int dupEval = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.Employees WHERE EvaluationName = @Eval AND EmployeeID <> @ID",
                new { Eval = evalName, ID = dto.EmployeeID });
            if (dupEval > 0)
                return (false, $"الاسم التقييمي ({evalName}) مسجل لموظف آخر بالفعل.");

            var catGroups = (await MonthlyPeriodConfigService.GetEffectiveCategoryWeightsAsync(conn, year, month))
                .Where(w => w.CategoryID == dto.CategoryID)
                .ToList();

            decimal totalCatWeights = catGroups.Sum(x => x.WeightScore);
            if (catGroups.Count == 0 || totalCatWeights != 100m)
            {
                string periodLabel = $"{IncentiveCalculatorService.GetArabicMonthName(month)} {year} م";
                return (false, $"يرجى أولاً تخصيص مجموعات المقيمين ودرجات التقييم (100 درجة) لهذه الفئة في شهر ({periodLabel}) قبل حفظ الموظف.");
            }

            var groupIds = catGroups.Select(g => g.GroupID).Distinct().ToList();
            var activeGroupEvaluators = (await conn.QueryAsync<EvaluatorDto>(@"
                SELECT ev.EvaluatorID, ev.GroupID, ev.EvaluatorName
                FROM dbo.Evaluators ev
                WHERE ev.GroupID IN @GroupIds AND ev.IsActive = 1",
                new { GroupIds = groupIds })).ToList();

            var selectedSet = (dto.SelectedEvaluatorIds ?? new List<int>()).ToHashSet();
            var finalEvaluatorIds = new HashSet<int>();

            foreach (var grp in catGroups)
            {
                int gId = grp.GroupID;
                string gName = grp.GroupName;
                var mems = activeGroupEvaluators.Where(e => e.GroupID == gId).ToList();

                if (mems.Count == 1)
                {
                    finalEvaluatorIds.Add(mems[0].EvaluatorID);
                }
                else
                {
                    var picked = mems.Where(m => selectedSet.Contains(m.EvaluatorID)).ToList();
                    if (picked.Count == 0)
                    {
                        return (false, $"يرجى اختيار مقيم واحد على الأقل من المجموعة ({gName}) ليكتمل نصاب تقييم الموظف (100 درجة).");
                    }
                    foreach (var p in picked)
                    {
                        finalEvaluatorIds.Add(p.EvaluatorID);
                    }
                }
            }

            using var tx = conn.BeginTransaction();
            try
            {
                await MonthlyPeriodConfigService.EnsureMonthMaterializedAsync(conn, tx, year, month);
                var (latestY, latestM) = await MonthlyPeriodConfigService.GetLatestConfiguredPeriodAsync(conn, tx);
                bool isLatest = (year * 100 + month) >= (latestY * 100 + latestM);

                int targetEmpId = dto.EmployeeID;
                if (targetEmpId == 0)
                {
                    string insertSql = @"
                        INSERT INTO dbo.Employees
                            (SystemName, EvaluationName, BranchID, DepartmentID, CategoryID, JobID, JobTypeID, HasIncentive, IsContinuing, IsActive)
                        VALUES
                            (@Sys, @Eval, @BranchID, NULL, @CatID, @JobID, @JobTypeID, @HasInc, @IsCont, 1);
                        SELECT CAST(SCOPE_IDENTITY() AS INT);";

                    targetEmpId = await conn.ExecuteScalarAsync<int>(insertSql, new
                    {
                        Sys = sysName,
                        Eval = evalName,
                        dto.BranchID,
                        CatID = dto.CategoryID,
                        dto.JobID,
                        dto.JobTypeID,
                        HasInc = dto.HasIncentive,
                        IsCont = dto.IsContinuing
                    }, tx);
                }
                else
                {
                    if (isLatest)
                    {
                        await conn.ExecuteAsync(@"
                            UPDATE dbo.Employees
                            SET SystemName = @Sys,
                                EvaluationName = @Eval,
                                BranchID = @BranchID,
                                CategoryID = @CatID,
                                JobID = @JobID,
                                JobTypeID = @JobTypeID,
                                HasIncentive = @HasInc,
                                IsContinuing = @IsCont
                            WHERE EmployeeID = @ID", new
                        {
                            Sys = sysName,
                            Eval = evalName,
                            dto.BranchID,
                            CatID = dto.CategoryID,
                            dto.JobID,
                            dto.JobTypeID,
                            HasInc = dto.HasIncentive,
                            IsCont = dto.IsContinuing,
                            ID = targetEmpId
                        }, tx);
                    }
                    else
                    {
                        await conn.ExecuteAsync(@"
                            UPDATE dbo.Employees
                            SET SystemName = @Sys,
                                EvaluationName = @Eval,
                                BranchID = @BranchID,
                                JobID = @JobID,
                                JobTypeID = @JobTypeID
                            WHERE EmployeeID = @ID", new
                        {
                            Sys = sysName,
                            Eval = evalName,
                            dto.BranchID,
                            dto.JobID,
                            dto.JobTypeID,
                            ID = targetEmpId
                        }, tx);
                    }
                }

                await conn.ExecuteAsync(@"
                    IF EXISTS (SELECT 1 FROM dbo.MonthlyEmployeeSnapshots WHERE EvalYear = @Year AND EvalMonth = @Month AND EmployeeID = @EmpID)
                    BEGIN
                        UPDATE dbo.MonthlyEmployeeSnapshots
                        SET CategoryID = @CatID, HasIncentive = @HasInc, IsContinuing = @IsCont, UpdatedAt = SYSDATETIME()
                        WHERE EvalYear = @Year AND EvalMonth = @Month AND EmployeeID = @EmpID;
                    END
                    ELSE
                    BEGIN
                        INSERT INTO dbo.MonthlyEmployeeSnapshots (EvalYear, EvalMonth, EmployeeID, CategoryID, HasIncentive, IsContinuing, UpdatedAt)
                        VALUES (@Year, @Month, @EmpID, @CatID, @HasInc, @IsCont, SYSDATETIME());
                    END",
                    new
                    {
                        Year = year,
                        Month = month,
                        EmpID = targetEmpId,
                        CatID = dto.CategoryID,
                        HasInc = dto.HasIncentive,
                        IsCont = dto.IsContinuing
                    }, tx);

                await conn.ExecuteAsync(
                    "DELETE FROM dbo.MonthlyEmployeeEvaluators WHERE EvalYear = @Year AND EvalMonth = @Month AND EmployeeID = @EmpID",
                    new { Year = year, Month = month, EmpID = targetEmpId }, tx);

                foreach (int evalId in finalEvaluatorIds)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.MonthlyEmployeeEvaluators (EvalYear, EvalMonth, EmployeeID, EvaluatorID, UpdatedAt)
                        VALUES (@Year, @Month, @EmpID, @EvalID, SYSDATETIME())",
                        new { Year = year, Month = month, EmpID = targetEmpId, EvalID = evalId }, tx);
                }

                await MonthlyPeriodConfigService.SyncLiveTablesIfLatestPeriodAsync(conn, tx, year, month);
                await tx.CommitAsync();

                string pLabel = $"{IncentiveCalculatorService.GetArabicMonthName(month)} {year} م";
                return (true, dto.EmployeeID == 0
                    ? $"تمت إضافة الموظف وربطه بالمقيمين المسؤولين عنه لشهر ({pLabel}) بنجاح."
                    : $"تم تحديث بيانات الموظف ومقيميه لشهر ({pLabel}) بنجاح دون التأثير على الشهور السابقة.");
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return (false, $"فشل حفظ بيانات الموظف: {ex.Message}");
            }
        }

        public async Task<(bool IsSuccess, string Message)> DeleteEmployeeAsync(int employeeId)
        {
            if (!CheckPermission("delete", out string permErr))
                return (false, permErr);

            using var conn = await OpenReadyConnectionAsync();
            await conn.ExecuteAsync("DELETE FROM dbo.SheetEmployees WHERE EmployeeID = @ID", new { ID = employeeId });
            await conn.ExecuteAsync("DELETE FROM dbo.EmployeeEvaluators WHERE EmployeeID = @ID", new { ID = employeeId });

            try
            {
                await conn.ExecuteAsync("DELETE FROM dbo.Employees WHERE EmployeeID = @ID", new { ID = employeeId });
                return (true, "تم حذف الموظف نهائياً بنجاح.");
            }
            catch (SqlException)
            {
                await conn.ExecuteAsync("UPDATE dbo.Employees SET IsActive = 0 WHERE EmployeeID = @ID", new { ID = employeeId });
                return (true, "تم حذف الموظف من القائمة النشطة (مع الاحتفاظ بدرجاته في أرشيف التقييمات السابقة).");
            }
        }

        // =========================================================================
        // 9. قوالب واستيراد Excel للمقيمين وأوزان الفئات + الموظفين ومصفوفة (1 / 0)
        // =========================================================================
        private sealed class ParsedExcelGroupSpan
        {
            public int GroupID { get; set; }
            public string GroupName { get; set; } = string.Empty;
            public int StartCol { get; set; }
            public int EndCol { get; set; }
            public List<string> MemberNames { get; } = new();
        }

        private static string NormalizeDigits(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            var sb = new StringBuilder(input.Length);
            foreach (char ch in input)
            {
                if (ch >= '٠' && ch <= '٩') sb.Append((char)('0' + (ch - '٠')));
                else if (ch >= '۰' && ch <= '۹') sb.Append((char)('0' + (ch - '۰')));
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string NormalizeArabicKey(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            var sb = new StringBuilder(input.Length);
            foreach (char ch in input.Trim())
            {
                switch (ch)
                {
                    case 'أ':
                    case 'إ':
                    case 'آ':
                        sb.Append('ا');
                        break;
                    case 'ة':
                        sb.Append('ه');
                        break;
                    case 'ى':
                        sb.Append('ي');
                        break;
                    case 'ـ':
                        break;
                    default:
                        sb.Append(ch);
                        break;
                }
            }
            return string.Join(" ", sb.ToString().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static bool IsTotalSummaryHeader(string text)
        {
            string norm = NormalizeArabicKey(text);
            return norm.Equals("الاجمالي", StringComparison.OrdinalIgnoreCase)
                || norm.Equals("المجموع", StringComparison.OrdinalIgnoreCase)
                || norm.Equals("اجمالي", StringComparison.OrdinalIgnoreCase)
                || norm.Equals("total", StringComparison.OrdinalIgnoreCase);
        }

        private static string[,] ReadTabularGridFromFile(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".csv")
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                string content = Encoding.UTF8.GetString(bytes);
                if (content.Contains('\uFFFD'))
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    content = Encoding.GetEncoding(1256).GetString(bytes);
                }

                var parsedRows = ParseCsvContent(content);
                if (parsedRows.Count == 0)
                    return new string[0, 0];

                int maxCols = parsedRows.Max(r => r.Count);
                var grid = new string[parsedRows.Count + 1, maxCols + 1];
                for (int r = 0; r < parsedRows.Count; r++)
                {
                    var lineCols = parsedRows[r];
                    for (int c = 0; c < maxCols; c++)
                    {
                        grid[r + 1, c + 1] = c < lineCols.Count ? lineCols[c].Trim() : string.Empty;
                    }
                }
                return grid;
            }
            else
            {
                using var wb = new XLWorkbook(filePath);
                var ws = wb.Worksheets.First();
                var rangeUsed = ws.RangeUsed();
                if (rangeUsed == null)
                    return new string[0, 0];

                int lastRow = rangeUsed.LastRow().RowNumber();
                int lastCol = rangeUsed.LastColumn().ColumnNumber();
                var grid = new string[lastRow + 1, lastCol + 1];

                for (int r = 1; r <= lastRow; r++)
                {
                    for (int c = 1; c <= lastCol; c++)
                    {
                        grid[r, c] = ws.Cell(r, c).GetString().Trim();
                    }
                }
                return grid;
            }
        }

        private static List<List<string>> ParseCsvContent(string content)
        {
            var rows = new List<List<string>>();
            var currentRow = new List<string>();
            var currentField = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < content.Length; i++)
            {
                char ch = content[i];
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < content.Length && content[i + 1] == '"')
                        {
                            currentField.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        currentField.Append(ch);
                    }
                }
                else
                {
                    if (ch == '"' && currentField.Length == 0)
                    {
                        inQuotes = true;
                    }
                    else if (ch == ',')
                    {
                        currentRow.Add(currentField.ToString());
                        currentField.Clear();
                    }
                    else if (ch == '\r' || ch == '\n')
                    {
                        if (ch == '\r' && i + 1 < content.Length && content[i + 1] == '\n')
                            i++;

                        currentRow.Add(currentField.ToString());
                        currentField.Clear();
                        if (currentRow.Any(cell => !string.IsNullOrWhiteSpace(cell)) || rows.Count < 2)
                        {
                            rows.Add(currentRow);
                        }
                        currentRow = new List<string>();
                    }
                    else if (ch != '\uFEFF')
                    {
                        currentField.Append(ch);
                    }
                }
            }

            if (currentField.Length > 0 || currentRow.Count > 0)
            {
                currentRow.Add(currentField.ToString());
                if (currentRow.Any(cell => !string.IsNullOrWhiteSpace(cell)))
                {
                    rows.Add(currentRow);
                }
            }

            return rows;
        }

        public async Task GenerateEvaluatorsExcelTemplateAsync(string filePath, int? evalYear = null, int? evalMonth = null)
        {
            var evalGroups = (await GetEvaluatorGroupsAsync(evalYear, evalMonth)).Where(g => g.Members.Count > 0).ToList();
            var categories = await GetCategoriesAsync(null, evalYear, evalMonth);

            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("بيانات المقيمين وأوزان الفئات");
            ws.RightToLeft = true;

            // العمود الأول: اسم الفئة (مدمج في الصفين 1 و 2)
            ws.Range(1, 1, 2, 1).Merge();
            ws.Cell(1, 1).Value = "اسم الفئة";
            var catHeaderRange = ws.Range(1, 1, 2, 1);
            catHeaderRange.Style.Font.Bold = true;
            catHeaderRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E293B");
            catHeaderRange.Style.Font.FontColor = XLColor.White;
            catHeaderRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            catHeaderRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            catHeaderRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;

            var groupsToRender = new List<(int GroupId, string GroupName, List<string> Members)>();
            if (evalGroups.Count > 0)
            {
                foreach (var g in evalGroups)
                {
                    groupsToRender.Add((g.GroupID, g.GroupName, g.Members.Select(m => m.EvaluatorName).ToList()));
                }
            }
            else
            {
                groupsToRender.Add((0, "الوكلاء", new List<string> { "منيف الشامي", "اميرة طاهر", "منال الحاشدي" }));
                groupsToRender.Add((0, "المشرفين", new List<string> { "فارس دهيش", "زهراء العودي", "ايمن مشلي" }));
                groupsToRender.Add((0, "المحاسبين", new List<string> { "رضوان المطري", "عبد الكريم", "وليد الشرماني" }));
            }

            var groupColSpans = new List<(int GroupId, string GroupName, int StartCol, int EndCol)>();
            int colCursor = 2;

            foreach (var grp in groupsToRender)
            {
                int startCol = colCursor;
                int endCol = colCursor + grp.Members.Count - 1;

                if (endCol > startCol)
                {
                    ws.Range(1, startCol, 1, endCol).Merge();
                }

                ws.Cell(1, startCol).Value = grp.GroupName;
                var grpHeaderRange = ws.Range(1, startCol, 1, endCol);
                grpHeaderRange.Style.Font.Bold = true;
                grpHeaderRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A8A");
                grpHeaderRange.Style.Font.FontColor = XLColor.White;
                grpHeaderRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                grpHeaderRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                grpHeaderRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;

                for (int i = 0; i < grp.Members.Count; i++)
                {
                    var memCell = ws.Cell(2, startCol + i);
                    memCell.Value = grp.Members[i];
                    memCell.Style.Font.Bold = true;
                    memCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#DBEAFE");
                    memCell.Style.Font.FontColor = XLColor.FromHtml("#1E3A8A");
                    memCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    memCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                    memCell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                }

                groupColSpans.Add((grp.GroupId, grp.GroupName, startCol, endCol));
                colCursor = endCol + 1;
            }

            int totalCol = colCursor;
            ws.Range(1, totalCol, 2, totalCol).Merge();
            ws.Cell(1, totalCol).Value = "الاجمالي";
            var totalHeaderRange = ws.Range(1, totalCol, 2, totalCol);
            totalHeaderRange.Style.Font.Bold = true;
            totalHeaderRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#065F46");
            totalHeaderRange.Style.Font.FontColor = XLColor.White;
            totalHeaderRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            totalHeaderRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            totalHeaderRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;

            ws.Row(1).Height = 25;
            ws.Row(2).Height = 24;

            using var conn = await OpenReadyConnectionAsync();
            var existingWeights = (await conn.QueryAsync<dynamic>("SELECT CategoryID, GroupID, WeightScore FROM dbo.CategoryEvaluatorGroups"))
                .ToDictionary(x => ((int)x.CategoryID, (int)x.GroupID), x => (decimal)x.WeightScore);

            int rowIdx = 3;
            if (categories.Count > 0 && evalGroups.Count > 0)
            {
                foreach (var cat in categories)
                {
                    ws.Cell(rowIdx, 1).Value = cat.CategoryName;
                    ws.Cell(rowIdx, 1).Style.Font.Bold = true;
                    ws.Cell(rowIdx, 1).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;

                    foreach (var span in groupColSpans)
                    {
                        if (span.EndCol > span.StartCol)
                            ws.Range(rowIdx, span.StartCol, rowIdx, span.EndCol).Merge();

                        existingWeights.TryGetValue((cat.CategoryID, span.GroupId), out decimal w);
                        ws.Cell(rowIdx, span.StartCol).Value = w;

                        var wRange = ws.Range(rowIdx, span.StartCol, rowIdx, span.EndCol);
                        wRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        wRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    }

                    string lastGrpLetter = ws.Column(totalCol - 1).ColumnLetter();
                    ws.Cell(rowIdx, totalCol).FormulaA1 = $"SUM(B{rowIdx}:{lastGrpLetter}{rowIdx})";
                    ws.Cell(rowIdx, totalCol).Style.Font.Bold = true;
                    ws.Cell(rowIdx, totalCol).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Cell(rowIdx, totalCol).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    rowIdx++;
                }
            }
            else
            {
                var sampleRows = new (string CatName, decimal[] Weights)[]
                {
                    ("المعلمين", new[] { 60m, 40m, 0m }),
                    ("المشرفين", new[] { 0m, 50m, 50m }),
                    ("عمال النظافة", new[] { 0m, 40m, 60m }),
                    ("التوجية", new[] { 50m, 50m, 0m })
                };

                foreach (var sr in sampleRows)
                {
                    ws.Cell(rowIdx, 1).Value = sr.CatName;
                    ws.Cell(rowIdx, 1).Style.Font.Bold = true;
                    ws.Cell(rowIdx, 1).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;

                    for (int gIdx = 0; gIdx < groupColSpans.Count; gIdx++)
                    {
                        var span = groupColSpans[gIdx];
                        if (span.EndCol > span.StartCol)
                            ws.Range(rowIdx, span.StartCol, rowIdx, span.EndCol).Merge();

                        decimal val = gIdx < sr.Weights.Length ? sr.Weights[gIdx] : 0m;
                        ws.Cell(rowIdx, span.StartCol).Value = val;

                        var wRange = ws.Range(rowIdx, span.StartCol, rowIdx, span.EndCol);
                        wRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        wRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    }

                    string lastGrpLetter = ws.Column(totalCol - 1).ColumnLetter();
                    ws.Cell(rowIdx, totalCol).FormulaA1 = $"SUM(B{rowIdx}:{lastGrpLetter}{rowIdx})";
                    ws.Cell(rowIdx, totalCol).Style.Font.Bold = true;
                    ws.Cell(rowIdx, totalCol).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Cell(rowIdx, totalCol).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    rowIdx++;
                }
            }

            ws.Columns().AdjustToContents();
            wb.SaveAs(filePath);
        }

        public async Task<(bool IsSuccess, string SummaryMessage)> ImportEvaluatorsAndWeightsFromExcelAsync(string filePath, int? evalYear = null, int? evalMonth = null)
        {
            if (!CheckPermission("insert", out string permErr) && !CheckPermission("update", out permErr))
                return (false, permErr);

            if (!File.Exists(filePath))
                return (false, "الملف المحدد غير موجود.");

            try
            {
                var grid = ReadTabularGridFromFile(filePath);
                int totalRows = grid.GetLength(0) - 1;
                int totalCols = grid.GetLength(1) - 1;

                if (totalRows < 2 || totalCols < 2)
                    return (false, "ملف المقيمين فارغ أو لا يحتوي على صفوف المجموعات وأسماء المقيمين.");

                // 1) قراءة مجموعات التقييم من الصف 1 وأسماء أعضاء كل مجموعة من الصف 2
                var parsedGroups = new List<ParsedExcelGroupSpan>();
                ParsedExcelGroupSpan? currentGroup = null;

                for (int c = 2; c <= totalCols; c++)
                {
                    string r1 = grid[1, c].Trim();
                    string r2 = grid[2, c].Trim();

                    if (IsTotalSummaryHeader(r1) && string.IsNullOrWhiteSpace(r2))
                    {
                        currentGroup = null;
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(r1))
                    {
                        if (currentGroup == null || !string.Equals(NormalizeArabicKey(currentGroup.GroupName), NormalizeArabicKey(r1), StringComparison.OrdinalIgnoreCase))
                        {
                            currentGroup = new ParsedExcelGroupSpan
                            {
                                GroupName = r1,
                                StartCol = c,
                                EndCol = c
                            };
                            parsedGroups.Add(currentGroup);
                        }
                        else
                        {
                            currentGroup.EndCol = c;
                        }
                    }
                    else if (currentGroup != null && !string.IsNullOrWhiteSpace(r2))
                    {
                        currentGroup.EndCol = c;
                    }

                    if (currentGroup != null && !string.IsNullOrWhiteSpace(r2))
                    {
                        if (!currentGroup.MemberNames.Any(m => string.Equals(NormalizeArabicKey(m), NormalizeArabicKey(r2), StringComparison.OrdinalIgnoreCase)))
                        {
                            currentGroup.MemberNames.Add(r2);
                        }
                    }
                }

                foreach (var grp in parsedGroups.Where(g => g.MemberNames.Count == 0))
                {
                    grp.MemberNames.Add(grp.GroupName);
                }

                if (parsedGroups.Count == 0)
                    return (false, "لم يتم العثور على أي مجموعات مقيمين في الصف الأول من الملف.");

                using var conn = await OpenReadyConnectionAsync();
                using var tx = conn.BeginTransaction();

                int groupsSaved = 0;
                int membersSaved = 0;
                int categoriesWeighted = 0;
                var warnings = new List<string>();

                try
                {
                    var dbGroups = (await conn.QueryAsync<dynamic>("SELECT GroupID, GroupName FROM dbo.EvaluatorGroups", transaction: tx)).ToList();
                    var dbEvaluators = (await conn.QueryAsync<dynamic>("SELECT EvaluatorID, GroupID, EvaluatorName FROM dbo.Evaluators", transaction: tx)).ToList();

                    // 2) حفظ/تحديث مجموعات التقييم وأعضائها
                    foreach (var grp in parsedGroups)
                    {
                        var existingGrp = dbGroups.FirstOrDefault(g =>
                            string.Equals(NormalizeArabicKey((string)g.GroupName), NormalizeArabicKey(grp.GroupName), StringComparison.OrdinalIgnoreCase));

                        int targetGroupId;
                        if (existingGrp == null)
                        {
                            targetGroupId = await conn.ExecuteScalarAsync<int>(@"
                                INSERT INTO dbo.EvaluatorGroups (GroupName, IsActive)
                                VALUES (@Name, 1);
                                SELECT CAST(SCOPE_IDENTITY() AS INT);",
                                new { Name = grp.GroupName }, tx);
                            dbGroups.Add(new { GroupID = targetGroupId, GroupName = grp.GroupName });
                        }
                        else
                        {
                            targetGroupId = (int)existingGrp.GroupID;
                            await conn.ExecuteAsync(
                                "UPDATE dbo.EvaluatorGroups SET GroupName = @Name, IsActive = 1 WHERE GroupID = @ID",
                                new { Name = grp.GroupName, ID = targetGroupId }, tx);
                        }

                        grp.GroupID = targetGroupId;
                        groupsSaved++;

                        var keptEvaluatorIds = new HashSet<int>();
                        foreach (string memName in grp.MemberNames)
                        {
                            var existingEval = dbEvaluators.FirstOrDefault(e =>
                                string.Equals(NormalizeArabicKey((string)e.EvaluatorName), NormalizeArabicKey(memName), StringComparison.OrdinalIgnoreCase));

                            int evalId;
                            if (existingEval == null)
                            {
                                evalId = await conn.ExecuteScalarAsync<int>(@"
                                    INSERT INTO dbo.Evaluators (GroupID, EvaluatorName, IsActive)
                                    VALUES (@GID, @Name, 1);
                                    SELECT CAST(SCOPE_IDENTITY() AS INT);",
                                    new { GID = targetGroupId, Name = memName }, tx);
                                dbEvaluators.Add(new { EvaluatorID = evalId, GroupID = targetGroupId, EvaluatorName = memName });
                            }
                            else
                            {
                                evalId = (int)existingEval.EvaluatorID;
                                await conn.ExecuteAsync(
                                    "UPDATE dbo.Evaluators SET GroupID = @GID, EvaluatorName = @Name, IsActive = 1 WHERE EvaluatorID = @ID",
                                    new { GID = targetGroupId, Name = memName, ID = evalId }, tx);
                            }

                            keptEvaluatorIds.Add(evalId);
                            membersSaved++;
                        }

                        // أرشفة أو حذف أي عضو سابق في نفس المجموعة لم يعد موجوداً في الملف
                        var oldGroupEvalIds = (await conn.QueryAsync<int>(
                            "SELECT EvaluatorID FROM dbo.Evaluators WHERE GroupID = @GID AND IsActive = 1",
                            new { GID = targetGroupId }, tx)).ToList();

                        foreach (int oldId in oldGroupEvalIds.Where(id => !keptEvaluatorIds.Contains(id)))
                        {
                            int refsCount = await conn.ExecuteScalarAsync<int>(@"
                                SELECT (SELECT COUNT(1) FROM dbo.Templates WHERE EvaluatorID = @EID)
                                     + (SELECT COUNT(1) FROM dbo.EvaluationRecords WHERE EvaluatorID = @EID)",
                                new { EID = oldId }, tx);

                            await conn.ExecuteAsync("DELETE FROM dbo.EmployeeEvaluators WHERE EvaluatorID = @EID", new { EID = oldId }, tx);
                            if (refsCount > 0)
                                await conn.ExecuteAsync("UPDATE dbo.Evaluators SET IsActive = 0 WHERE EvaluatorID = @EID", new { EID = oldId }, tx);
                            else
                                await conn.ExecuteAsync("DELETE FROM dbo.Evaluators WHERE EvaluatorID = @EID", new { EID = oldId }, tx);
                        }
                    }

                    // 3) قراءة الفئات ودرجات مجموعات التقييم من الصف 3 فصاعداً
                    var dbCategories = (await conn.QueryAsync<CategoryDto>(
                        "SELECT CategoryID, ISNULL(DepartmentID, 0) AS DepartmentID, CategoryName FROM dbo.Categories",
                        transaction: tx)).ToList();

                    var (targetYear, targetMonth) = await ResolvePeriodOrLatestAsync(conn, evalYear, evalMonth, tx);
                    var (isMonthLocked, lockedByUser, _) = await MonthlyPeriodConfigService.GetMonthLockStatusAsync(conn, targetYear, targetMonth, tx);
                    if (isMonthLocked)
                    {
                        await tx.RollbackAsync();
                        string lockLbl = $"{IncentiveCalculatorService.GetArabicMonthName(targetMonth)} {targetYear} م";
                        return (false, $"لا يمكن استيراد أوزان الفئات لشهر ({lockLbl}) لأنه معتمد ومغلق نهائياً بواسطة ({lockedByUser}).");
                    }
                    await MonthlyPeriodConfigService.EnsureMonthMaterializedAsync(conn, tx, targetYear, targetMonth);

                    for (int r = 3; r <= totalRows; r++)
                    {
                        string catName = grid[r, 1].Trim();
                        if (string.IsNullOrWhiteSpace(catName) || IsTotalSummaryHeader(catName))
                            continue;

                        var matchedCat = dbCategories.FirstOrDefault(c =>
                            string.Equals(c.CategoryName.Trim(), catName, StringComparison.OrdinalIgnoreCase))
                            ?? dbCategories.FirstOrDefault(c =>
                                string.Equals(NormalizeArabicKey(c.CategoryName), NormalizeArabicKey(catName), StringComparison.OrdinalIgnoreCase));

                        if (matchedCat == null)
                        {
                            int newCatId = await conn.ExecuteScalarAsync<int>(@"
                                INSERT INTO dbo.Categories (DepartmentID, CategoryName, IsActive)
                                VALUES (NULL, @Name, 1);
                                SELECT CAST(SCOPE_IDENTITY() AS INT);",
                                new { Name = catName }, tx);
                            matchedCat = new CategoryDto { CategoryID = newCatId, CategoryName = catName };
                            dbCategories.Add(matchedCat);
                        }
                        else
                        {
                            await conn.ExecuteAsync("UPDATE dbo.Categories SET IsActive = 1 WHERE CategoryID = @ID", new { ID = matchedCat.CategoryID }, tx);
                        }

                        var catWeights = new List<(int GroupID, decimal Weight)>();
                        foreach (var grp in parsedGroups)
                        {
                            decimal grpWeight = 0m;
                            for (int c = grp.StartCol; c <= grp.EndCol; c++)
                            {
                                string rawCell = NormalizeDigits(grid[r, c].Trim());
                                if (!string.IsNullOrWhiteSpace(rawCell) &&
                                    decimal.TryParse(rawCell.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsedVal))
                                {
                                    grpWeight = parsedVal;
                                    break;
                                }
                            }

                            if (grpWeight > 0m)
                            {
                                catWeights.Add((grp.GroupID, grpWeight));
                            }
                        }

                        if (catWeights.Count == 0)
                            continue;

                        decimal sumWeights = catWeights.Sum(x => x.Weight);
                        if (sumWeights != 100m)
                        {
                            warnings.Add($"سطر {r} — الفئة ({catName}): مجموع الدرجات ({sumWeights:0.##}) لا يساوي 100 درجة.");
                            continue;
                        }

                        await conn.ExecuteAsync(
                            "DELETE FROM dbo.MonthlyCategoryEvaluatorGroups WHERE EvalYear = @Year AND EvalMonth = @Month AND CategoryID = @CatID",
                            new { Year = targetYear, Month = targetMonth, CatID = matchedCat.CategoryID }, tx);

                        foreach (var cw in catWeights)
                        {
                            await conn.ExecuteAsync(@"
                                INSERT INTO dbo.MonthlyCategoryEvaluatorGroups (EvalYear, EvalMonth, CategoryID, GroupID, WeightScore, UpdatedAt)
                                VALUES (@Year, @Month, @CatID, @GID, @Weight, SYSDATETIME())",
                                new { Year = targetYear, Month = targetMonth, CatID = matchedCat.CategoryID, GID = cw.GroupID, cw.Weight }, tx);
                        }

                        await conn.ExecuteAsync(@"
                            DELETE mee
                            FROM dbo.MonthlyEmployeeEvaluators mee
                            INNER JOIN dbo.MonthlyEmployeeSnapshots mes
                                ON mes.EmployeeID = mee.EmployeeID AND mes.EvalYear = mee.EvalYear AND mes.EvalMonth = mee.EvalMonth
                            INNER JOIN dbo.Evaluators ev ON mee.EvaluatorID = ev.EvaluatorID
                            WHERE mee.EvalYear = @Year AND mee.EvalMonth = @Month
                              AND mes.CategoryID = @CatID
                              AND NOT EXISTS (
                                  SELECT 1 FROM dbo.MonthlyCategoryEvaluatorGroups mceg
                                  WHERE mceg.EvalYear = @Year AND mceg.EvalMonth = @Month
                                    AND mceg.CategoryID = @CatID AND mceg.GroupID = ev.GroupID
                              );", new { Year = targetYear, Month = targetMonth, CatID = matchedCat.CategoryID }, tx);

                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.MonthlyEmployeeEvaluators (EvalYear, EvalMonth, EmployeeID, EvaluatorID, UpdatedAt)
                            SELECT mes.EmployeeID, @Year, @Month, ev.EvaluatorID, SYSDATETIME()
                            FROM dbo.MonthlyEmployeeSnapshots mes
                            INNER JOIN dbo.MonthlyCategoryEvaluatorGroups mceg
                                ON mes.CategoryID = mceg.CategoryID AND mceg.EvalYear = @Year AND mceg.EvalMonth = @Month
                            INNER JOIN dbo.Evaluators ev ON ev.GroupID = mceg.GroupID AND ev.IsActive = 1
                            WHERE mes.EvalYear = @Year AND mes.EvalMonth = @Month
                              AND mes.CategoryID = @CatID
                              AND (SELECT COUNT(1) FROM dbo.Evaluators ev2 WHERE ev2.GroupID = mceg.GroupID AND ev2.IsActive = 1) = 1
                              AND NOT EXISTS (
                                  SELECT 1 FROM dbo.MonthlyEmployeeEvaluators mee
                                  WHERE mee.EvalYear = @Year AND mee.EvalMonth = @Month
                                    AND mee.EmployeeID = mes.EmployeeID AND mee.EvaluatorID = ev.EvaluatorID
                              );", new { Year = targetYear, Month = targetMonth, CatID = matchedCat.CategoryID }, tx);

                        categoriesWeighted++;
                    }

                    await MonthlyPeriodConfigService.SyncLiveTablesIfLatestPeriodAsync(conn, tx, targetYear, targetMonth);
                    await tx.CommitAsync();

                    string targetPeriodLbl = $"{IncentiveCalculatorService.GetArabicMonthName(targetMonth)} {targetYear} م";
                    string summary = $"اكتمل استيراد بيانات المقيمين وأوزان الفئات لشهر ({targetPeriodLbl}) بنجاح دون التأثير على الشهور السابقة:\n" +
                                     $"• مجموعات المقيمين المحفوظة: {groupsSaved}\n" +
                                     $"• إجمالي أسماء المقيمين: {membersSaved}\n" +
                                     $"• الفئات التي تم اعتماد توزيع الـ 100 درجة لها: {categoriesWeighted}";

                    if (warnings.Count > 0)
                    {
                        summary += $"\n\n⚠ ملاحظات أثناء الاستيراد ({warnings.Count}):\n" + string.Join("\n", warnings.Take(6));
                    }

                    return (true, summary);
                }
                catch
                {
                    await tx.RollbackAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                return (false, $"حدث خطأ أثناء قراءة ملف المقيمين (تأكد من إغلاق الملف في برنامج Excel قبل الاستيراد): {ex.Message}");
            }
        }

        public async Task GenerateEmployeesExcelTemplateAsync(string filePath, int? evalYear = null, int? evalMonth = null)
        {
            var branches = await GetBranchesAsync();
            var categories = await GetCategoriesAsync(null, evalYear, evalMonth);
            var jobs = await GetJobsAsync();
            var jobTypes = await GetJobTypesAsync();
            var evalGroups = (await GetEvaluatorGroupsAsync(evalYear, evalMonth)).Where(g => g.Members.Count > 0).ToList();
            var existingEmployees = await GetEmployeesAsync("", null, null, "ALL", evalYear, evalMonth);

            using var wb = new XLWorkbook();

            // الورقة الأولى: إدخال بيانات الموظفين والمقيمين بنظام المصفوفة (1 / 0)
            var ws = wb.Worksheets.Add("بيانات الموظفين");
            ws.RightToLeft = true;

            string[] baseHeaders =
            {
                "الاسم في النظام",
                "الاسم التقييمي",
                "الفرع",
                "الفئة",
                "الوظيفة",
                "نوع الوظيفة",
                "الحافز (بحافز / بدون حافز)",
                "حالة الدوام (مستمر / منقطع)"
            };

            for (int i = 0; i < baseHeaders.Length; i++)
            {
                int col = i + 1;
                ws.Range(1, col, 2, col).Merge();
                ws.Cell(1, col).Value = baseHeaders[i];
                var hRange = ws.Range(1, col, 2, col);
                hRange.Style.Font.Bold = true;
                hRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E293B");
                hRange.Style.Font.FontColor = XLColor.White;
                hRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                hRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                hRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            }

            var groupsToRender = new List<(string GroupName, List<string> Members)>();
            if (evalGroups.Count > 0)
            {
                foreach (var g in evalGroups)
                {
                    groupsToRender.Add((g.GroupName, g.Members.Select(m => m.EvaluatorName).ToList()));
                }
            }
            else
            {
                groupsToRender.Add(("الوكلاء", new List<string> { "منيف الشامي", "اميرة طاهر", "منال الحاشدي" }));
                groupsToRender.Add(("المشرفين", new List<string> { "فارس دهيش", "زهراء العودي", "ايمن مشلي" }));
            }

            int evalColCursor = 9;
            foreach (var grp in groupsToRender)
            {
                int startCol = evalColCursor;
                int endCol = evalColCursor + grp.Members.Count - 1;

                if (endCol > startCol)
                {
                    ws.Range(1, startCol, 1, endCol).Merge();
                }

                ws.Cell(1, startCol).Value = grp.GroupName;
                var grpHeader = ws.Range(1, startCol, 1, endCol);
                grpHeader.Style.Font.Bold = true;
                grpHeader.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A8A");
                grpHeader.Style.Font.FontColor = XLColor.White;
                grpHeader.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                grpHeader.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                grpHeader.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;

                for (int mIdx = 0; mIdx < grp.Members.Count; mIdx++)
                {
                    var mCell = ws.Cell(2, startCol + mIdx);
                    mCell.Value = grp.Members[mIdx];
                    mCell.Style.Font.Bold = true;
                    mCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#DBEAFE");
                    mCell.Style.Font.FontColor = XLColor.FromHtml("#1E3A8A");
                    mCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    mCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                    mCell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                }

                evalColCursor = endCol + 1;
            }

            ws.Row(1).Height = 25;
            ws.Row(2).Height = 24;

            int totalEvaluatorCols = evalColCursor - 9;
            string sampleBranch = branches.FirstOrDefault()?.BranchName ?? "الحصبة";

            if (existingEmployees.Count > 0 && evalGroups.Count > 0)
            {
                var orderedEvalIds = evalGroups.SelectMany(g => g.Members).Select(m => m.EvaluatorID).ToList();
                int rIdx = 3;
                foreach (var emp in existingEmployees)
                {
                    ws.Cell(rIdx, 1).Value = emp.SystemName;
                    ws.Cell(rIdx, 2).Value = emp.EvaluationName;
                    ws.Cell(rIdx, 3).Value = emp.BranchName;
                    ws.Cell(rIdx, 4).Value = emp.CategoryName;
                    ws.Cell(rIdx, 5).Value = emp.JobTitle;
                    ws.Cell(rIdx, 6).Value = emp.JobTypeName;
                    ws.Cell(rIdx, 7).Value = emp.HasIncentive ? "بحافز" : "بدون حافز";
                    ws.Cell(rIdx, 8).Value = emp.IsContinuing ? "مستمر" : "منقطع";

                    var assignedSet = (emp.SelectedEvaluatorIds ?? new List<int>()).ToHashSet();
                    for (int offset = 0; offset < orderedEvalIds.Count; offset++)
                    {
                        var flagCell = ws.Cell(rIdx, 9 + offset);
                        flagCell.Value = assignedSet.Contains(orderedEvalIds[offset]) ? 1 : 0;
                        flagCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    }
                    rIdx++;
                }
            }
            else
            {
                // الصف 3 والصف 4: أمثلة توضيحية مطابقة لنموذج المصفوفة (1 = ينتمي، 0 = لا ينتمي)
                ws.Cell(3, 1).Value = "ابتسام ابراهيم علي الزيادي - استاذة/";
                ws.Cell(3, 2).Value = "ابتسام الزيادي";
                ws.Cell(3, 3).Value = sampleBranch;
                ws.Cell(3, 4).Value = "المعلمين";
                ws.Cell(3, 5).Value = "معلم";
                ws.Cell(3, 6).Value = "معلم لغة عربية";
                ws.Cell(3, 7).Value = "بحافز";
                ws.Cell(3, 8).Value = "مستمر";

                ws.Cell(4, 1).Value = "ابراهيم عبدالعزيز عبداللطيف المعلمي - استاذ/";
                ws.Cell(4, 2).Value = "ابراهيم المعلمي";
                ws.Cell(4, 3).Value = sampleBranch;
                ws.Cell(4, 4).Value = "المعلمين";
                ws.Cell(4, 5).Value = "معلم";
                ws.Cell(4, 6).Value = "معلم لغة إنجليزية";
                ws.Cell(4, 7).Value = "بدون حافز";
                ws.Cell(4, 8).Value = "مستمر";

                for (int offset = 0; offset < totalEvaluatorCols; offset++)
                {
                    int col = 9 + offset;
                    ws.Cell(3, col).Value = (offset == 2) ? 0 : 1;
                    ws.Cell(3, col).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                    ws.Cell(4, col).Value = (offset == 1 || offset == 2 || offset == 3) ? 1 : 0;
                    ws.Cell(4, col).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                }
            }

            ws.Columns().AdjustToContents();

            // الورقة الثانية: دليل مرجعي بالبيانات والمقيمين المسجلين في النظام
            var wsRef = wb.Worksheets.Add("دليل البيانات والمقيمين");
            wsRef.RightToLeft = true;

            wsRef.Cell(1, 1).Value = "الفروع المتاحة";
            wsRef.Cell(1, 2).Value = "الفئة";
            wsRef.Cell(1, 3).Value = "الوظيفة";
            wsRef.Cell(1, 4).Value = "نوع الوظيفة";
            wsRef.Cell(1, 5).Value = "مجموعة المقيمين";
            wsRef.Cell(1, 6).Value = "أسماء المقيمين في المجموعة";

            var refHeader = wsRef.Range(1, 1, 1, 6);
            refHeader.Style.Font.Bold = true;
            refHeader.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A8A");
            refHeader.Style.Font.FontColor = XLColor.White;

            for (int i = 0; i < branches.Count; i++)
            {
                wsRef.Cell(i + 2, 1).Value = branches[i].BranchName;
            }

            int hRow = 2;
            foreach (var jt in jobTypes)
            {
                wsRef.Cell(hRow, 2).Value = jt.CategoryName;
                wsRef.Cell(hRow, 3).Value = jt.JobTitle;
                wsRef.Cell(hRow, 4).Value = jt.JobTypeName;
                hRow++;
            }

            int gRow = 2;
            foreach (var grp in evalGroups)
            {
                wsRef.Cell(gRow, 5).Value = grp.GroupName;
                wsRef.Cell(gRow, 6).Value = grp.MembersSummary;
                gRow++;
            }

            wsRef.Columns().AdjustToContents();
            wb.SaveAs(filePath);
        }

        public async Task<(bool IsSuccess, string SummaryMessage)> ImportEmployeesFromExcelAsync(string filePath, int? evalYear = null, int? evalMonth = null)
        {
            if (!CheckPermission("insert", out string permErr))
                return (false, permErr);

            if (!File.Exists(filePath))
                return (false, "ملف الإكسل المحدد غير موجود.");

            try
            {
                var grid = ReadTabularGridFromFile(filePath);
                int totalRows = grid.GetLength(0) - 1;
                int totalCols = grid.GetLength(1) - 1;

                if (totalRows < 3 || totalCols < 8)
                    return (false, "ملف الموظفين لا يحتوي على الترتيب المطلوب (الصف 1 للرؤوس والمجموعات، الصف 2 لأسماء المقيمين، ومن الصف 3 تبدأ بيانات الموظفين).");

                // دعم كلٍ من القالب الجديد (8 أعمدة أساسية بدون قسم) أو القالب السابق (9 أعمدة أساسية مع عمود القسم في العمود 4)
                bool hasLegacyDeptColumn = NormalizeArabicKey(grid[1, 4]).Contains("قسم");
                int evalStartCol = hasLegacyDeptColumn ? 10 : 9;

                using var conn = await OpenReadyConnectionAsync();

                var branches = (await conn.QueryAsync<BranchDto>("SELECT BranchID, BranchName FROM dbo.Branches"))
                    .GroupBy(b => NormalizeArabicKey(b.BranchName), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().BranchID, StringComparer.OrdinalIgnoreCase);
                var categories = (await conn.QueryAsync<CategoryDto>("SELECT CategoryID, CategoryName FROM dbo.Categories")).ToList();
                var jobs = (await conn.QueryAsync<JobDto>("SELECT JobID, CategoryID, JobTitle FROM dbo.Jobs")).ToList();
                var jobTypes = (await conn.QueryAsync<JobTypeDto>("SELECT JobTypeID, JobID, JobTypeName FROM dbo.JobTypes")).ToList();

                var dbGroups = (await conn.QueryAsync<dynamic>("SELECT GroupID, GroupName FROM dbo.EvaluatorGroups")).ToList();
                var allEvaluators = await GetEvaluatorsAsync();

                // 1) قراءة أعمدة مجموعات التقييم (الصف 1) وأسماء المقيمين (الصف 2)
                var evaluatorColMap = new Dictionary<int, int>();
                string currentGroupName = string.Empty;

                for (int c = evalStartCol; c <= totalCols; c++)
                {
                    string r1 = grid[1, c].Trim();
                    string r2 = grid[2, c].Trim();

                    if (IsTotalSummaryHeader(r1) && string.IsNullOrWhiteSpace(r2))
                    {
                        currentGroupName = string.Empty;
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(r1))
                    {
                        currentGroupName = r1;
                    }

                    string memberName = !string.IsNullOrWhiteSpace(r2) ? r2 : r1;
                    if (string.IsNullOrWhiteSpace(memberName))
                        continue;

                    var matchedEval = allEvaluators.FirstOrDefault(e =>
                        string.Equals(e.EvaluatorName.Trim(), memberName, StringComparison.OrdinalIgnoreCase))
                        ?? allEvaluators.FirstOrDefault(e =>
                            string.Equals(NormalizeArabicKey(e.EvaluatorName), NormalizeArabicKey(memberName), StringComparison.OrdinalIgnoreCase));

                    if (matchedEval == null)
                    {
                        string grpNameToUse = !string.IsNullOrWhiteSpace(currentGroupName) ? currentGroupName : memberName;
                        var matchedGrp = dbGroups.FirstOrDefault(g =>
                            string.Equals(NormalizeArabicKey((string)g.GroupName), NormalizeArabicKey(grpNameToUse), StringComparison.OrdinalIgnoreCase));

                        int grpId;
                        if (matchedGrp == null)
                        {
                            grpId = await conn.ExecuteScalarAsync<int>(@"
                                INSERT INTO dbo.EvaluatorGroups (GroupName, IsActive)
                                VALUES (@Name, 1);
                                SELECT CAST(SCOPE_IDENTITY() AS INT);",
                                new { Name = grpNameToUse });
                            dbGroups.Add(new { GroupID = grpId, GroupName = grpNameToUse });
                        }
                        else
                        {
                            grpId = (int)matchedGrp.GroupID;
                            await conn.ExecuteAsync("UPDATE dbo.EvaluatorGroups SET IsActive = 1 WHERE GroupID = @ID", new { ID = grpId });
                        }

                        int newEvalId = await conn.ExecuteScalarAsync<int>(@"
                            INSERT INTO dbo.Evaluators (GroupID, EvaluatorName, IsActive)
                            VALUES (@GID, @Name, 1);
                            SELECT CAST(SCOPE_IDENTITY() AS INT);",
                            new { GID = grpId, Name = memberName });

                        matchedEval = new EvaluatorDto { EvaluatorID = newEvalId, GroupID = grpId, GroupName = grpNameToUse, EvaluatorName = memberName };
                        allEvaluators.Add(matchedEval);
                    }

                    evaluatorColMap[c] = matchedEval.EvaluatorID;
                }

                var (targetYear, targetMonth) = await ResolvePeriodOrLatestAsync(conn, evalYear, evalMonth);
                var (isMonthLocked, lockedByUser, _) = await MonthlyPeriodConfigService.GetMonthLockStatusAsync(conn, targetYear, targetMonth);
                if (isMonthLocked)
                {
                    string lockLbl = $"{IncentiveCalculatorService.GetArabicMonthName(targetMonth)} {targetYear} م";
                    return (false, $"لا يمكن استيراد الموظفين وإسنادهم لشهر ({lockLbl}) لأنه معتمد ومغلق نهائياً بواسطة ({lockedByUser}).");
                }

                await MonthlyPeriodConfigService.EnsureMonthMaterializedAsync(conn, null, targetYear, targetMonth);
                var (latestY, latestM) = await MonthlyPeriodConfigService.GetLatestConfiguredPeriodAsync(conn);
                bool isLatestPeriod = (targetYear * 100 + targetMonth) >= (latestY * 100 + latestM);

                var effWeightsForImport = await MonthlyPeriodConfigService.GetEffectiveCategoryWeightsAsync(conn, targetYear, targetMonth);
                var catGroupsMap = effWeightsForImport
                    .GroupBy(x => x.CategoryID)
                    .ToDictionary(g => g.Key, g => g.Select(r => r.GroupID).ToList());

                int insertedCount = 0;
                int updatedCount = 0;
                var skippedReasons = new List<string>();

                // 2) قراءة صفوف الموظفين ابتداءً من الصف الثالث
                for (int r = 3; r <= totalRows; r++)
                {
                    string sysName = grid[r, 1].Trim();
                    string evalName = grid[r, 2].Trim();
                    string branchName = grid[r, 3].Trim();
                    int shift = hasLegacyDeptColumn ? 1 : 0;
                    string catName = grid[r, 4 + shift].Trim();
                    string jobTitle = grid[r, 5 + shift].Trim();
                    string jobTypeName = grid[r, 6 + shift].Trim();
                    string incentiveText = grid[r, 7 + shift].Trim();
                    string continuingText = grid[r, 8 + shift].Trim();

                    if (string.IsNullOrWhiteSpace(sysName) && string.IsNullOrWhiteSpace(evalName))
                        continue;

                    if (string.IsNullOrWhiteSpace(evalName)) evalName = sysName;
                    if (string.IsNullOrWhiteSpace(sysName)) sysName = evalName;

                    if (string.IsNullOrWhiteSpace(branchName) ||
                        string.IsNullOrWhiteSpace(catName) ||
                        string.IsNullOrWhiteSpace(jobTitle) ||
                        string.IsNullOrWhiteSpace(jobTypeName))
                    {
                        skippedReasons.Add($"سطر {r} ({evalName}): ينقصه أحد الحقول الإلزامية (الفرع / الفئة / الوظيفة / نوع الوظيفة).");
                        continue;
                    }

                    string normBranch = NormalizeArabicKey(branchName);
                    if (!branches.TryGetValue(normBranch, out int branchId))
                    {
                        branchId = await conn.ExecuteScalarAsync<int>(
                            "INSERT INTO dbo.Branches (BranchName, IsActive) VALUES (@Name, 1); SELECT CAST(SCOPE_IDENTITY() AS INT);",
                            new { Name = branchName });
                        branches[normBranch] = branchId;
                    }

                    await conn.ExecuteAsync("UPDATE dbo.Branches SET IsActive = 1 WHERE BranchID = @BID", new { BID = branchId });

                    var matchedCat = categories.FirstOrDefault(c => string.Equals(NormalizeArabicKey(c.CategoryName), NormalizeArabicKey(catName), StringComparison.OrdinalIgnoreCase));
                    if (matchedCat == null)
                    {
                        int newCatId = await conn.ExecuteScalarAsync<int>(
                            "INSERT INTO dbo.Categories (DepartmentID, CategoryName, IsActive) VALUES (NULL, @Name, 1); SELECT CAST(SCOPE_IDENTITY() AS INT);",
                            new { Name = catName });
                        matchedCat = new CategoryDto { CategoryID = newCatId, CategoryName = catName };
                        categories.Add(matchedCat);
                    }
                    else
                    {
                        await conn.ExecuteAsync("UPDATE dbo.Categories SET IsActive = 1 WHERE CategoryID = @CID", new { CID = matchedCat.CategoryID });
                    }

                    var matchedJob = jobs.FirstOrDefault(j => j.CategoryID == matchedCat.CategoryID && string.Equals(NormalizeArabicKey(j.JobTitle), NormalizeArabicKey(jobTitle), StringComparison.OrdinalIgnoreCase));
                    if (matchedJob == null)
                    {
                        int newJobId = await conn.ExecuteScalarAsync<int>(
                            "INSERT INTO dbo.Jobs (CategoryID, JobTitle, IsActive) VALUES (@CatID, @Title, 1); SELECT CAST(SCOPE_IDENTITY() AS INT);",
                            new { CatID = matchedCat.CategoryID, Title = jobTitle });
                        matchedJob = new JobDto { JobID = newJobId, CategoryID = matchedCat.CategoryID, JobTitle = jobTitle };
                        jobs.Add(matchedJob);
                    }
                    else
                    {
                        await conn.ExecuteAsync("UPDATE dbo.Jobs SET IsActive = 1 WHERE JobID = @JID", new { JID = matchedJob.JobID });
                    }

                    var matchedJobType = jobTypes.FirstOrDefault(jt => jt.JobID == matchedJob.JobID && string.Equals(NormalizeArabicKey(jt.JobTypeName), NormalizeArabicKey(jobTypeName), StringComparison.OrdinalIgnoreCase));
                    if (matchedJobType == null)
                    {
                        int newJtId = await conn.ExecuteScalarAsync<int>(
                            "INSERT INTO dbo.JobTypes (JobID, JobTypeName, IsActive) VALUES (@JobID, @Name, 1); SELECT CAST(SCOPE_IDENTITY() AS INT);",
                            new { JobID = matchedJob.JobID, Name = jobTypeName });
                        matchedJobType = new JobTypeDto { JobTypeID = newJtId, JobID = matchedJob.JobID, JobTypeName = jobTypeName };
                        jobTypes.Add(matchedJobType);
                    }
                    else
                    {
                        await conn.ExecuteAsync("UPDATE dbo.JobTypes SET IsActive = 1 WHERE JobTypeID = @JTID", new { JTID = matchedJobType.JobTypeID });
                    }

                    string normInc = NormalizeDigits(incentiveText);
                    bool hasIncentive = !(normInc.Contains("بدون") || normInc == "0" || normInc.Equals("لا", StringComparison.OrdinalIgnoreCase) || normInc.Equals("false", StringComparison.OrdinalIgnoreCase));

                    string normCont = NormalizeDigits(continuingText);
                    bool isContinuing = !(normCont.Contains("منقطع") || normCont == "0" || normCont.Equals("لا", StringComparison.OrdinalIgnoreCase) || normCont.Equals("false", StringComparison.OrdinalIgnoreCase));

                    // قراءة أرقام (1 / 0) تحت أعمدة المقيمين
                    var chosenEvaluatorIds = new HashSet<int>();
                    foreach (var kv in evaluatorColMap)
                    {
                        string rawFlag = NormalizeDigits(grid[r, kv.Key].Trim());
                        if (string.IsNullOrWhiteSpace(rawFlag) || rawFlag == "0")
                            continue;

                        if (rawFlag == "1" ||
                            rawFlag.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                            rawFlag == "نعم" ||
                            rawFlag == "✔" ||
                            (decimal.TryParse(rawFlag, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal flagNum) && flagNum > 0m))
                        {
                            chosenEvaluatorIds.Add(kv.Value);
                        }
                    }

                    // إضافة المقيمين في المجموعات الأحادية المخصصة للفئة تلقائياً
                    if (catGroupsMap.TryGetValue(matchedCat.CategoryID, out var grpIds))
                    {
                        foreach (int gId in grpIds)
                        {
                            var grpMembers = allEvaluators.Where(e => e.GroupID == gId).ToList();
                            if (grpMembers.Count == 1)
                            {
                                chosenEvaluatorIds.Add(grpMembers[0].EvaluatorID);
                            }
                        }
                    }

                    var existingEmpId = await conn.ExecuteScalarAsync<int?>(
                        "SELECT EmployeeID FROM dbo.Employees WHERE SystemName = @Sys OR EvaluationName = @Eval",
                        new { Sys = sysName, Eval = evalName });

                    int targetEmpId;
                    if (existingEmpId == null)
                    {
                        targetEmpId = await conn.ExecuteScalarAsync<int>(@"
                            INSERT INTO dbo.Employees
                                (SystemName, EvaluationName, BranchID, DepartmentID, CategoryID, JobID, JobTypeID, HasIncentive, IsContinuing, IsActive)
                            VALUES
                                (@Sys, @Eval, @BranchID, NULL, @CatID, @JobID, @JobTypeID, @HasInc, @IsCont, 1);
                            SELECT CAST(SCOPE_IDENTITY() AS INT);",
                            new
                            {
                                Sys = sysName,
                                Eval = evalName,
                                BranchID = branchId,
                                CatID = matchedCat.CategoryID,
                                JobID = matchedJob.JobID,
                                JobTypeID = matchedJobType.JobTypeID,
                                HasInc = hasIncentive,
                                IsCont = isContinuing
                            });
                        insertedCount++;
                    }
                    else
                    {
                        targetEmpId = existingEmpId.Value;
                        if (isLatestPeriod)
                        {
                            await conn.ExecuteAsync(@"
                                UPDATE dbo.Employees
                                SET SystemName = @Sys,
                                    EvaluationName = @Eval,
                                    BranchID = @BranchID,
                                    CategoryID = @CatID,
                                    JobID = @JobID,
                                    JobTypeID = @JobTypeID,
                                    HasIncentive = @HasInc,
                                    IsContinuing = @IsCont,
                                    IsActive = 1
                                WHERE EmployeeID = @ID",
                                new
                                {
                                    Sys = sysName,
                                    Eval = evalName,
                                    BranchID = branchId,
                                    CatID = matchedCat.CategoryID,
                                    JobID = matchedJob.JobID,
                                    JobTypeID = matchedJobType.JobTypeID,
                                    HasInc = hasIncentive,
                                    IsCont = isContinuing,
                                    ID = targetEmpId
                                });
                        }
                        else
                        {
                            await conn.ExecuteAsync(@"
                                UPDATE dbo.Employees
                                SET SystemName = @Sys,
                                    EvaluationName = @Eval,
                                    BranchID = @BranchID,
                                    JobID = @JobID,
                                    JobTypeID = @JobTypeID,
                                    IsActive = 1
                                WHERE EmployeeID = @ID",
                                new
                                {
                                    Sys = sysName,
                                    Eval = evalName,
                                    BranchID = branchId,
                                    JobID = matchedJob.JobID,
                                    JobTypeID = matchedJobType.JobTypeID,
                                    ID = targetEmpId
                                });
                        }
                        updatedCount++;
                    }

                    await conn.ExecuteAsync(@"
                        IF EXISTS (SELECT 1 FROM dbo.MonthlyEmployeeSnapshots WHERE EvalYear = @Year AND EvalMonth = @Month AND EmployeeID = @EmpID)
                        BEGIN
                            UPDATE dbo.MonthlyEmployeeSnapshots
                            SET CategoryID = @CatID, HasIncentive = @HasInc, IsContinuing = @IsCont, UpdatedAt = SYSDATETIME()
                            WHERE EvalYear = @Year AND EvalMonth = @Month AND EmployeeID = @EmpID;
                        END
                        ELSE
                        BEGIN
                            INSERT INTO dbo.MonthlyEmployeeSnapshots (EvalYear, EvalMonth, EmployeeID, CategoryID, HasIncentive, IsContinuing, UpdatedAt)
                            VALUES (@Year, @Month, @EmpID, @CatID, @HasInc, @IsCont, SYSDATETIME());
                        END",
                        new
                        {
                            Year = targetYear,
                            Month = targetMonth,
                            EmpID = targetEmpId,
                            CatID = matchedCat.CategoryID,
                            HasInc = hasIncentive,
                            IsCont = isContinuing
                        });

                    if (evaluatorColMap.Count > 0 || chosenEvaluatorIds.Count > 0)
                    {
                        await conn.ExecuteAsync(
                            "DELETE FROM dbo.MonthlyEmployeeEvaluators WHERE EvalYear = @Year AND EvalMonth = @Month AND EmployeeID = @EmpID",
                            new { Year = targetYear, Month = targetMonth, EmpID = targetEmpId });
                        foreach (int evId in chosenEvaluatorIds)
                        {
                            await conn.ExecuteAsync(
                                "INSERT INTO dbo.MonthlyEmployeeEvaluators (EvalYear, EvalMonth, EmployeeID, EvaluatorID, UpdatedAt) VALUES (@Year, @Month, @EmpID, @EvalID, SYSDATETIME())",
                                new { Year = targetYear, Month = targetMonth, EmpID = targetEmpId, EvalID = evId });
                        }
                    }
                }

                await MonthlyPeriodConfigService.SyncLiveTablesIfLatestPeriodAsync(conn, null, targetYear, targetMonth);

                string importPeriodLbl = $"{IncentiveCalculatorService.GetArabicMonthName(targetMonth)} {targetYear} م";
                string report = $"اكتمل استيراد ملف الموظفين وإسنادهم لشهر ({importPeriodLbl}) بنجاح دون التأثير على الشهور السابقة:\n• موظفون جدد تمت إضافتهم: {insertedCount}\n• موظفون تم تحديث بياناتهم ومقيميهم: {updatedCount}";
                if (skippedReasons.Count > 0)
                {
                    report += $"\n• أسطر تم تخطيها ({skippedReasons.Count}):\n" + string.Join("\n", skippedReasons.Take(6));
                }

                return (true, report);
            }
            catch (Exception ex)
            {
                return (false, $"حدث خطأ أثناء قراءة ملف الموظفين (تأكد من إغلاق الملف في برنامج Excel قبل الاستيراد): {ex.Message}");
            }
        }
    }
}



















