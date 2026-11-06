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
            try
            {
                await conn.ExecuteAsync("DELETE FROM dbo.Branches WHERE BranchID = @ID", new { ID = branchId });
                return (true, "تم حذف الفرع بنجاح.");
            }
            catch (SqlException)
            {
                return (false, "لا يمكن حذف هذا الفرع لوجود موظفين أو أوراق قوالب مرتبطين به حالياً.");
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
        public async Task<List<CategoryDto>> GetCategoriesAsync(int? filterDepartmentId = null)
        {
            using var conn = await OpenReadyConnectionAsync();
            string sql = @"
                SELECT c.CategoryID, ISNULL(c.DepartmentID, 0) AS DepartmentID, c.CategoryName,
                       (SELECT COUNT(1) FROM dbo.Jobs j WHERE j.CategoryID = c.CategoryID AND j.IsActive = 1) AS JobsCount,
                       (SELECT COUNT(1) FROM dbo.Employees e WHERE e.CategoryID = c.CategoryID AND e.IsActive = 1) AS EmployeesCount,
                       ISNULL((SELECT SUM(ceg.WeightScore) FROM dbo.CategoryEvaluatorGroups ceg WHERE ceg.CategoryID = c.CategoryID), 0) AS TotalWeights
                FROM dbo.Categories c
                WHERE c.IsActive = 1
                ORDER BY c.CategoryID ASC";

            var categories = (await conn.QueryAsync<CategoryDto>(sql)).ToList();

            var groupLinks = await conn.QueryAsync<dynamic>(@"
                SELECT ceg.CategoryID, eg.GroupID, eg.GroupName, ceg.WeightScore
                FROM dbo.CategoryEvaluatorGroups ceg
                INNER JOIN dbo.EvaluatorGroups eg ON ceg.GroupID = eg.GroupID
                WHERE eg.IsActive = 1
                ORDER BY ceg.WeightScore DESC");

            var groupMembers = await conn.QueryAsync<dynamic>(@"
                SELECT GroupID, EvaluatorName
                FROM dbo.Evaluators
                WHERE IsActive = 1
                ORDER BY EvaluatorID ASC");

            var membersByGroup = groupMembers
                .GroupBy(x => (int)x.GroupID)
                .ToDictionary(g => g.Key, g => string.Join("، ", g.Select(m => (string)m.EvaluatorName)));

            var summaryByCat = groupLinks
                .GroupBy(x => (int)x.CategoryID)
                .ToDictionary(g => g.Key, g => string.Join("  |  ", g.Select(i =>
                {
                    int gId = (int)i.GroupID;
                    string gName = (string)i.GroupName;
                    decimal w = (decimal)i.WeightScore;
                    membersByGroup.TryGetValue(gId, out var mems);
                    return string.IsNullOrWhiteSpace(mems)
                        ? $"{gName}: {w:0.##} درجة"
                        : $"{gName} [{mems}]: {w:0.##} درجة";
                })));

            foreach (var cat in categories)
            {
                cat.EvaluatorsSummary = summaryByCat.TryGetValue(cat.CategoryID, out var sum)
                    ? sum
                    : "لم يتم تخصيص مقيمين بعد (اضغط تخصيص الدرجات)";
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
            try
            {
                await conn.ExecuteAsync("DELETE FROM dbo.Categories WHERE CategoryID = @ID", new { ID = categoryId });
                return (true, "تم حذف الفئة بنجاح.");
            }
            catch (SqlException)
            {
                return (false, "لا يمكن حذف هذه الفئة لوجود وظائف أو موظفين أو قوالب تقييم مرتبطة بها حالياً.");
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
            try
            {
                await conn.ExecuteAsync("DELETE FROM dbo.Jobs WHERE JobID = @ID", new { ID = jobId });
                return (true, "تم حذف الوظيفة بنجاح.");
            }
            catch (SqlException)
            {
                return (false, "لا يمكن حذف هذه الوظيفة لوجود أنواع وظائف أو موظفين مرتبطين بها حالياً.");
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
            try
            {
                await conn.ExecuteAsync("DELETE FROM dbo.JobTypes WHERE JobTypeID = @ID", new { ID = jobTypeId });
                return (true, "تم حذف نوع الوظيفة بنجاح.");
            }
            catch (SqlException)
            {
                return (false, "لا يمكن حذف نوع الوظيفة لوجود موظفين مسجلين عليه حالياً.");
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

        public async Task<List<EvaluatorGroupDto>> GetEvaluatorGroupsAsync()
        {
            using var conn = await OpenReadyConnectionAsync();
            var groups = (await conn.QueryAsync<EvaluatorGroupDto>(@"
                SELECT eg.GroupID, eg.GroupName,
                       (SELECT COUNT(1) FROM dbo.CategoryEvaluatorGroups ceg WHERE ceg.GroupID = eg.GroupID) AS AssignedCategoriesCount
                FROM dbo.EvaluatorGroups eg
                WHERE eg.IsActive = 1
                ORDER BY eg.GroupID ASC")).ToList();

            var evaluators = await GetEvaluatorsAsync();
            var evalByGroup = evaluators.GroupBy(e => e.GroupID).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var grp in groups)
            {
                if (evalByGroup.TryGetValue(grp.GroupID, out var mems))
                {
                    grp.Members = mems;
                    grp.MembersCount = mems.Count;
                    grp.MembersSummary = string.Join(" ، ", mems.Select(m => m.EvaluatorName));
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

            int catCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.CategoryEvaluatorGroups WHERE GroupID = @GID",
                new { GID = groupId });
            if (catCount > 0)
                return (false, "لا يمكن حذف هذه المجموعة لأنها مخصصة حالياً في درجات إحدى الفئات. قم بإلغاء تخصيصها من الفئة أولاً.");

            using var tx = conn.BeginTransaction();
            try
            {
                await conn.ExecuteAsync(@"
                    DELETE ee FROM dbo.EmployeeEvaluators ee
                    INNER JOIN dbo.Evaluators ev ON ee.EvaluatorID = ev.EvaluatorID
                    WHERE ev.GroupID = @GID", new { GID = groupId }, tx);

                await conn.ExecuteAsync("DELETE FROM dbo.Evaluators WHERE GroupID = @GID", new { GID = groupId }, tx);
                await conn.ExecuteAsync("DELETE FROM dbo.EvaluatorGroups WHERE GroupID = @GID", new { GID = groupId }, tx);

                await tx.CommitAsync();
                return (true, "تم حذف مجموعة المقيمين بنجاح.");
            }
            catch (SqlException)
            {
                await tx.RollbackAsync();
                return (false, "لا يمكن حذف هذه المجموعة لارتباط أحد مقيميها بقوالب أو سجلات تقييم محفوظة.");
            }
        }

        // =========================================================================
        // 7. تخصيص مجموعات المقيمين ودرجات التقييم للفئات (شرط المجموع = 100 درجة)
        // =========================================================================
        public async Task<List<CategoryEvaluatorGroupSelectRow>> GetCategoryEvaluatorGroupMatrixAsync(int categoryId)
        {
            using var conn = await OpenReadyConnectionAsync();
            var groups = await GetEvaluatorGroupsAsync();
            var existingWeights = new Dictionary<int, decimal>();

            if (categoryId > 0)
            {
                var rows = await conn.QueryAsync<dynamic>(
                    "SELECT GroupID, WeightScore FROM dbo.CategoryEvaluatorGroups WHERE CategoryID = @CatID",
                    new { CatID = categoryId });
                foreach (var r in rows)
                {
                    existingWeights[(int)r.GroupID] = (decimal)r.WeightScore;
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
            List<CategoryEvaluatorGroupSelectRow> selectedGroups)
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
            using var tx = conn.BeginTransaction();
            try
            {
                await conn.ExecuteAsync(
                    "DELETE FROM dbo.CategoryEvaluatorGroups WHERE CategoryID = @CatID",
                    new { CatID = categoryId }, tx);

                foreach (var row in activeRows)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.CategoryEvaluatorGroups (CategoryID, GroupID, WeightScore)
                        VALUES (@CatID, @GID, @Weight)",
                        new { CatID = categoryId, GID = row.GroupID, Weight = row.WeightScore }, tx);
                }

                // حذف إسناد الموظفين في هذه الفئة لأي مقيم لم تعد مجموعته مخصصة لهذه الفئة
                await conn.ExecuteAsync(@"
                    DELETE ee
                    FROM dbo.EmployeeEvaluators ee
                    INNER JOIN dbo.Employees e ON ee.EmployeeID = e.EmployeeID
                    INNER JOIN dbo.Evaluators ev ON ee.EvaluatorID = ev.EvaluatorID
                    WHERE e.CategoryID = @CatID
                      AND NOT EXISTS (
                          SELECT 1 FROM dbo.CategoryEvaluatorGroups ceg
                          WHERE ceg.CategoryID = @CatID AND ceg.GroupID = ev.GroupID
                      );", new { CatID = categoryId }, tx);

                // إسناد تلقائي لأي مجموعة مكونة من شخص واحد فقط لجميع موظفي هذه الفئة
                await conn.ExecuteAsync(@"
                    INSERT INTO dbo.EmployeeEvaluators (EmployeeID, EvaluatorID)
                    SELECT e.EmployeeID, ev.EvaluatorID
                    FROM dbo.Employees e
                    INNER JOIN dbo.CategoryEvaluatorGroups ceg ON e.CategoryID = ceg.CategoryID
                    INNER JOIN dbo.Evaluators ev ON ev.GroupID = ceg.GroupID AND ev.IsActive = 1
                    WHERE e.CategoryID = @CatID
                      AND (SELECT COUNT(1) FROM dbo.Evaluators ev2 WHERE ev2.GroupID = ceg.GroupID AND ev2.IsActive = 1) = 1
                      AND NOT EXISTS (
                          SELECT 1 FROM dbo.EmployeeEvaluators ee
                          WHERE ee.EmployeeID = e.EmployeeID AND ee.EvaluatorID = ev.EvaluatorID
                      );", new { CatID = categoryId }, tx);

                await tx.CommitAsync();
                return (true, "تم اعتماد تخصيص المقيمين وتوزيع الـ 100 درجة للفئة بنجاح.");
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
        public async Task<List<EmployeeEvaluatorGroupSection>> GetCategoryGroupsForEmployeeDialogAsync(int categoryId, int employeeId = 0)
        {
            using var conn = await OpenReadyConnectionAsync();
            if (categoryId <= 0) return new List<EmployeeEvaluatorGroupSection>();

            var assignedEvalIds = new HashSet<int>();
            if (employeeId > 0)
            {
                var ids = await conn.QueryAsync<int>(
                    "SELECT EvaluatorID FROM dbo.EmployeeEvaluators WHERE EmployeeID = @EmpID",
                    new { EmpID = employeeId });
                assignedEvalIds = ids.ToHashSet();
            }

            var catGroups = (await conn.QueryAsync<dynamic>(@"
                SELECT ceg.GroupID, eg.GroupName, ceg.WeightScore
                FROM dbo.CategoryEvaluatorGroups ceg
                INNER JOIN dbo.EvaluatorGroups eg ON ceg.GroupID = eg.GroupID
                WHERE ceg.CategoryID = @CatID AND eg.IsActive = 1
                ORDER BY ceg.WeightScore DESC, eg.GroupID ASC",
                new { CatID = categoryId })).ToList();

            var evaluators = (await conn.QueryAsync<EvaluatorDto>(@"
                SELECT ev.EvaluatorID, ev.GroupID, ev.EvaluatorName
                FROM dbo.Evaluators ev
                INNER JOIN dbo.CategoryEvaluatorGroups ceg ON ev.GroupID = ceg.GroupID
                WHERE ceg.CategoryID = @CatID AND ev.IsActive = 1
                ORDER BY ev.EvaluatorID ASC",
                new { CatID = categoryId })).ToList();

            var evalByGroup = evaluators.GroupBy(e => e.GroupID).ToDictionary(g => g.Key, g => g.ToList());
            var sections = new List<EmployeeEvaluatorGroupSection>();

            foreach (var cg in catGroups)
            {
                int gId = (int)cg.GroupID;
                evalByGroup.TryGetValue(gId, out var mems);
                mems ??= new List<EvaluatorDto>();

                bool isSingle = mems.Count == 1;
                var section = new EmployeeEvaluatorGroupSection
                {
                    GroupID = gId,
                    GroupName = (string)cg.GroupName,
                    GroupWeight = (decimal)cg.WeightScore
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
            string statusFilter = "ALL")
        {
            using var conn = await OpenReadyConnectionAsync();

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
                  AND (@CategoryID IS NULL OR e.CategoryID = @CategoryID)
                ORDER BY b.BranchName ASC, e.EvaluationName ASC";

            var employees = (await conn.QueryAsync<EmployeeDto>(sql, new
            {
                Search = (search ?? string.Empty).Trim(),
                Pattern = $"%{(search ?? string.Empty).Trim()}%",
                BranchID = branchId,
                CategoryID = categoryId
            })).ToList();

            if (statusFilter == "CONT_INC")
                employees = employees.Where(e => e.IsContinuing && e.HasIncentive).ToList();
            else if (statusFilter == "CONT_NO_INC")
                employees = employees.Where(e => e.IsContinuing && !e.HasIncentive).ToList();
            else if (statusFilter == "DISCONT")
                employees = employees.Where(e => !e.IsContinuing).ToList();

            var catGroups = (await conn.QueryAsync<dynamic>(@"
                SELECT ceg.CategoryID, ceg.GroupID, eg.GroupName, ceg.WeightScore
                FROM dbo.CategoryEvaluatorGroups ceg
                INNER JOIN dbo.EvaluatorGroups eg ON ceg.GroupID = eg.GroupID
                WHERE eg.IsActive = 1")).ToList();

            var catGroupsMap = catGroups
                .GroupBy(x => (int)x.CategoryID)
                .ToDictionary(g => g.Key, g => g.ToList());

            var empEvals = (await conn.QueryAsync<dynamic>(@"
                SELECT ee.EmployeeID, ee.EvaluatorID, ev.GroupID, ev.EvaluatorName
                FROM dbo.EmployeeEvaluators ee
                INNER JOIN dbo.Evaluators ev ON ee.EvaluatorID = ev.EvaluatorID
                WHERE ev.IsActive = 1")).ToList();

            var empEvalMap = empEvals
                .GroupBy(x => (int)x.EmployeeID)
                .ToDictionary(g => g.Key, g => g.ToList());

            var empInTemplates = (await conn.QueryAsync<dynamic>(@"
                SELECT DISTINCT se.EmployeeID, t.EvaluatorID
                FROM dbo.SheetEmployees se
                INNER JOIN dbo.TemplateSheets ts ON se.SheetID = ts.SheetID
                INNER JOIN dbo.Templates t ON ts.TemplateID = t.TemplateID
                WHERE t.IsCurrentVersion = 1 AND t.IsDeleted = 0")).ToList();

            var empTemplateMap = empInTemplates
                .GroupBy(x => (int)x.EmployeeID)
                .ToDictionary(g => g.Key, g => g.Select(i => (int)i.EvaluatorID).ToHashSet());

            foreach (var emp in employees)
            {
                empEvalMap.TryGetValue(emp.EmployeeID, out var assignedRows);
                assignedRows ??= new List<dynamic>();
                emp.SelectedEvaluatorIds = assignedRows.Select(r => (int)r.EvaluatorID).ToList();

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
                    int gId = (int)grp.GroupID;
                    string gName = (string)grp.GroupName;
                    decimal gWeight = (decimal)grp.WeightScore;

                    var chosenInGrp = assignedRows.Where(r => (int)r.GroupID == gId).ToList();
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
                            requiredEvalList.Add(((int)ch.EvaluatorID, (string)ch.EvaluatorName));
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

                empTemplateMap.TryGetValue(emp.EmployeeID, out var coveredEvalIds);
                coveredEvalIds ??= new HashSet<int>();

                var missingNames = requiredEvalList
                    .Where(r => !coveredEvalIds.Contains(r.EvalId))
                    .Select(r => r.EvalName)
                    .Distinct()
                    .ToList();

                if (missingNames.Count == 0 && requiredEvalList.Count > 0)
                {
                    emp.IsCoverageComplete = true;
                    emp.TemplateCoverageStatus = "مكتمل في القوالب ✔";
                }
                else
                {
                    emp.IsCoverageComplete = false;
                    emp.TemplateCoverageStatus = $"ينقصه قالب: {string.Join("، ", missingNames)} ⚠";
                }
            }

            return employees;
        }

        public async Task<(bool IsSuccess, string Message)> SaveEmployeeAsync(EmployeeDto dto)
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

            // التحقق من اكتمال تخصيص الـ 100 درجة للفئة واختيار مقيم واحد على الأقل من كل مجموعة مخصصة
            var catGroups = (await conn.QueryAsync<dynamic>(@"
                SELECT ceg.GroupID, eg.GroupName, ceg.WeightScore
                FROM dbo.CategoryEvaluatorGroups ceg
                INNER JOIN dbo.EvaluatorGroups eg ON ceg.GroupID = eg.GroupID
                WHERE ceg.CategoryID = @CatID AND eg.IsActive = 1",
                new { CatID = dto.CategoryID })).ToList();

            decimal totalCatWeights = catGroups.Sum(x => (decimal)x.WeightScore);
            if (catGroups.Count == 0 || totalCatWeights != 100m)
            {
                return (false, "يرجى أولاً تخصيص مجموعات المقيمين ودرجات التقييم (100 درجة) لهذه الفئة من تبويب (تخصيص المقيمين ودرجات الفئات) قبل حفظ الموظف.");
            }

            var activeGroupEvaluators = (await conn.QueryAsync<EvaluatorDto>(@"
                SELECT ev.EvaluatorID, ev.GroupID, ev.EvaluatorName
                FROM dbo.Evaluators ev
                INNER JOIN dbo.CategoryEvaluatorGroups ceg ON ev.GroupID = ceg.GroupID
                WHERE ceg.CategoryID = @CatID AND ev.IsActive = 1",
                new { CatID = dto.CategoryID })).ToList();

            var selectedSet = (dto.SelectedEvaluatorIds ?? new List<int>()).ToHashSet();
            var finalEvaluatorIds = new HashSet<int>();

            foreach (var grp in catGroups)
            {
                int gId = (int)grp.GroupID;
                string gName = (string)grp.GroupName;
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
                    string updateSql = @"
                        UPDATE dbo.Employees
                        SET SystemName = @Sys,
                            EvaluationName = @Eval,
                            BranchID = @BranchID,
                            CategoryID = @CatID,
                            JobID = @JobID,
                            JobTypeID = @JobTypeID,
                            HasIncentive = @HasInc,
                            IsContinuing = @IsCont
                        WHERE EmployeeID = @ID";

                    await conn.ExecuteAsync(updateSql, new
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

                    await conn.ExecuteAsync("DELETE FROM dbo.EmployeeEvaluators WHERE EmployeeID = @ID", new { ID = targetEmpId }, tx);
                }

                foreach (int evalId in finalEvaluatorIds)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.EmployeeEvaluators (EmployeeID, EvaluatorID)
                        VALUES (@EmpID, @EvalID)",
                        new { EmpID = targetEmpId, EvalID = evalId }, tx);
                }

                await tx.CommitAsync();
                return (true, dto.EmployeeID == 0
                    ? "تمت إضافة الموظف وربطه بالمقيمين المسؤولين عنه (100 درجة) بنجاح."
                    : "تم تحديث بيانات الموظف ومقيميه بنجاح.");
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
            try
            {
                await conn.ExecuteAsync("DELETE FROM dbo.Employees WHERE EmployeeID = @ID", new { ID = employeeId });
                return (true, "تم حذف الموظف بنجاح.");
            }
            catch (SqlException)
            {
                await conn.ExecuteAsync("UPDATE dbo.Employees SET IsActive = 0 WHERE EmployeeID = @ID", new { ID = employeeId });
                return (true, "نظراً لوجود تقييمات أو قوالب سابقة مرتبطة بهذا الموظف، تم أرشفته (إيقاف نشاطه) حفاظاً على السجلات التاريخية.");
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

        public async Task GenerateEvaluatorsExcelTemplateAsync(string filePath)
        {
            var evalGroups = (await GetEvaluatorGroupsAsync()).Where(g => g.Members.Count > 0).ToList();
            var categories = await GetCategoriesAsync();

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

        public async Task<(bool IsSuccess, string SummaryMessage)> ImportEvaluatorsAndWeightsFromExcelAsync(string filePath)
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
                            "DELETE FROM dbo.CategoryEvaluatorGroups WHERE CategoryID = @CatID",
                            new { CatID = matchedCat.CategoryID }, tx);

                        foreach (var cw in catWeights)
                        {
                            await conn.ExecuteAsync(@"
                                INSERT INTO dbo.CategoryEvaluatorGroups (CategoryID, GroupID, WeightScore)
                                VALUES (@CatID, @GID, @Weight)",
                                new { CatID = matchedCat.CategoryID, GID = cw.GroupID, cw.Weight }, tx);
                        }

                        // إزالة ربط الموظفين بالمجموعات التي أصبحت درجتها 0 لهذه الفئة
                        await conn.ExecuteAsync(@"
                            DELETE ee
                            FROM dbo.EmployeeEvaluators ee
                            INNER JOIN dbo.Employees e ON ee.EmployeeID = e.EmployeeID
                            INNER JOIN dbo.Evaluators ev ON ee.EvaluatorID = ev.EvaluatorID
                            WHERE e.CategoryID = @CatID
                              AND NOT EXISTS (
                                  SELECT 1 FROM dbo.CategoryEvaluatorGroups ceg
                                  WHERE ceg.CategoryID = @CatID AND ceg.GroupID = ev.GroupID
                              );", new { CatID = matchedCat.CategoryID }, tx);

                        // إسناد تلقائي لأي مجموعة تتكون من مقيم واحد فقط
                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.EmployeeEvaluators (EmployeeID, EvaluatorID)
                            SELECT e.EmployeeID, ev.EvaluatorID
                            FROM dbo.Employees e
                            INNER JOIN dbo.CategoryEvaluatorGroups ceg ON e.CategoryID = ceg.CategoryID
                            INNER JOIN dbo.Evaluators ev ON ev.GroupID = ceg.GroupID AND ev.IsActive = 1
                            WHERE e.CategoryID = @CatID
                              AND (SELECT COUNT(1) FROM dbo.Evaluators ev2 WHERE ev2.GroupID = ceg.GroupID AND ev2.IsActive = 1) = 1
                              AND NOT EXISTS (
                                  SELECT 1 FROM dbo.EmployeeEvaluators ee
                                  WHERE ee.EmployeeID = e.EmployeeID AND ee.EvaluatorID = ev.EvaluatorID
                              );", new { CatID = matchedCat.CategoryID }, tx);

                        categoriesWeighted++;
                    }

                    await tx.CommitAsync();

                    string summary = $"اكتمل استيراد بيانات المقيمين وأوزان الفئات بنجاح:\n" +
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

        public async Task GenerateEmployeesExcelTemplateAsync(string filePath)
        {
            var branches = await GetBranchesAsync();
            var categories = await GetCategoriesAsync();
            var jobs = await GetJobsAsync();
            var jobTypes = await GetJobTypesAsync();
            var evalGroups = (await GetEvaluatorGroupsAsync()).Where(g => g.Members.Count > 0).ToList();

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

        public async Task<(bool IsSuccess, string SummaryMessage)> ImportEmployeesFromExcelAsync(string filePath)
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

                var branches = (await GetBranchesAsync()).ToDictionary(b => NormalizeArabicKey(b.BranchName), b => b.BranchID, StringComparer.OrdinalIgnoreCase);
                var categories = (await GetCategoriesAsync()).ToList();
                var jobs = (await GetJobsAsync()).ToList();
                var jobTypes = (await GetJobTypesAsync()).ToList();

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

                var catGroupsRaw = (await conn.QueryAsync<dynamic>("SELECT CategoryID, GroupID FROM dbo.CategoryEvaluatorGroups")).ToList();
                var catGroupsMap = catGroupsRaw
                    .GroupBy(x => (int)x.CategoryID)
                    .ToDictionary(g => g.Key, g => g.Select(r => (int)r.GroupID).ToList());

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

                    var matchedCat = categories.FirstOrDefault(c => string.Equals(NormalizeArabicKey(c.CategoryName), NormalizeArabicKey(catName), StringComparison.OrdinalIgnoreCase));
                    if (matchedCat == null)
                    {
                        int newCatId = await conn.ExecuteScalarAsync<int>(
                            "INSERT INTO dbo.Categories (DepartmentID, CategoryName, IsActive) VALUES (NULL, @Name, 1); SELECT CAST(SCOPE_IDENTITY() AS INT);",
                            new { Name = catName });
                        matchedCat = new CategoryDto { CategoryID = newCatId, CategoryName = catName };
                        categories.Add(matchedCat);
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

                    var matchedJobType = jobTypes.FirstOrDefault(jt => jt.JobID == matchedJob.JobID && string.Equals(NormalizeArabicKey(jt.JobTypeName), NormalizeArabicKey(jobTypeName), StringComparison.OrdinalIgnoreCase));
                    if (matchedJobType == null)
                    {
                        int newJtId = await conn.ExecuteScalarAsync<int>(
                            "INSERT INTO dbo.JobTypes (JobID, JobTypeName, IsActive) VALUES (@JobID, @Name, 1); SELECT CAST(SCOPE_IDENTITY() AS INT);",
                            new { JobID = matchedJob.JobID, Name = jobTypeName });
                        matchedJobType = new JobTypeDto { JobTypeID = newJtId, JobID = matchedJob.JobID, JobTypeName = jobTypeName };
                        jobTypes.Add(matchedJobType);
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
                        updatedCount++;
                    }

                    if (evaluatorColMap.Count > 0 || chosenEvaluatorIds.Count > 0)
                    {
                        await conn.ExecuteAsync("DELETE FROM dbo.EmployeeEvaluators WHERE EmployeeID = @EmpID", new { EmpID = targetEmpId });
                        foreach (int evId in chosenEvaluatorIds)
                        {
                            await conn.ExecuteAsync(
                                "INSERT INTO dbo.EmployeeEvaluators (EmployeeID, EvaluatorID) VALUES (@EmpID, @EvalID)",
                                new { EmpID = targetEmpId, EvalID = evId });
                        }
                    }
                }

                string report = $"اكتمل استيراد ملف الموظفين بنجاح:\n• موظفون جدد تمت إضافتهم: {insertedCount}\n• موظفون تم تحديث بياناتهم ومقيميهم: {updatedCount}";
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
