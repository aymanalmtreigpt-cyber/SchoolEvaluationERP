using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using SchoolEvaluationERP.Models;

namespace SchoolEvaluationERP.Services
{
    public class AuthenticatedUserSession
    {
        public int UserID { get; set; }
        public string Username { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public bool IsAdmin { get; set; }
        public bool MustChangePassword { get; set; }
        public Dictionary<string, PermissionRowViewModel> Permissions { get; set; } = new();

        public bool HasPermission(string moduleCode, string action)
        {
            if (IsAdmin) return true;
            if (!Permissions.TryGetValue(moduleCode, out var perm)) return false;

            return action.ToLowerInvariant() switch
            {
                "read" => perm.CanRead,
                "insert" => perm.CanInsert,
                "update" => perm.CanUpdate,
                "delete" => perm.CanDelete,
                _ => false
            };
        }
    }

    public class AuthService
    {
        private readonly string _connectionString;
        public static AuthenticatedUserSession? CurrentSession { get; private set; }

        public AuthService() : this(AppConfigService.ConnectionString) { }

        public AuthService(string connectionString)
        {
            _connectionString = connectionString;
        }

        public static string GenerateSecureSalt()
        {
            byte[] saltBytes = RandomNumberGenerator.GetBytes(16);
            return Convert.ToHexString(saltBytes);
        }

        public static string ComputeSha256Hash(string rawPassword, string salt)
        {
            string combined = salt == "STATIC_INIT_SALT" ? rawPassword : $"{rawPassword}::{salt}";
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
            return Convert.ToHexString(bytes);
        }

        public async Task<(bool IsConnected, string Message)> TestServerConnectionAsync()
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await DatabaseSchemaService.EnsureSchemaV2Async(conn);
                var dbName = await conn.ExecuteScalarAsync<string>("SELECT DB_NAME();");
                return (true, $"متصل بنجاح بقاعدة البيانات ({dbName}) عبر الشبكة.");
            }
            catch (Exception ex)
            {
                return (false, $"تعذر الاتصال بالسيرفر: {ex.Message}");
            }
        }

        public async Task<(bool IsSuccess, string ErrorMessage, bool RequirePasswordChange)> LoginAsync(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return (false, "يرجى إدخال اسم المستخدم وكلمة المرور.", false);

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await DatabaseSchemaService.EnsureSchemaV2Async(conn);

                var user = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT * FROM dbo.Users WHERE Username = @Username",
                    new { Username = username.Trim() });

                if (user == null)
                    return (false, "اسم المستخدم غير موجود في النظام.", false);

                if (!(bool)user.IsActive)
                    return (false, "هذا الحساب موقوف حالياً. يرجى مراجعة مدير النظام.", false);

                string salt = user.PasswordSalt;
                string expectedHash = user.PasswordHash;
                string actualHash = ComputeSha256Hash(password, salt);

                if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                    return (false, "كلمة المرور غير صحيحة.", false);

                var session = new AuthenticatedUserSession
                {
                    UserID = user.UserID,
                    Username = user.Username,
                    FullName = user.FullName,
                    IsAdmin = user.IsAdmin,
                    MustChangePassword = user.MustChangePassword
                };

                var perms = await conn.QueryAsync<PermissionRowViewModel>(
                    @"SELECT ModuleCode, ModuleArabicName, CanRead, CanInsert, CanUpdate, CanDelete 
                      FROM dbo.UserPermissions WHERE UserID = @UserID",
                    new { UserID = session.UserID });

                foreach (var p in perms)
                {
                    session.Permissions[p.ModuleCode] = p;
                }

                CurrentSession = session;
                return (true, string.Empty, session.MustChangePassword);
            }
            catch (SqlException ex)
            {
                return (false, $"خطأ في الاتصال بقاعدة البيانات عبر الشبكة (تأكد من تشغيل SQL Server وتنفيذ سكريبت القاعدة): {ex.Message}", false);
            }
            catch (Exception ex)
            {
                return (false, $"حدث خطأ غير متوقع: {ex.Message}", false);
            }
        }

        public static void Logout()
        {
            CurrentSession = null;
        }

        public async Task<(bool IsSuccess, string ErrorMessage)> ChangeOwnPasswordAsync(int userId, string currentPassword, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 4)
                return (false, "يجب ألا تقل كلمة المرور الجديدة عن 4 أحرف أو أرقام.");

            if (currentPassword == newPassword)
                return (false, "كلمة المرور الجديدة يجب أن تختلف عن كلمة المرور المؤقتة/الحالية.");

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            var user = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT PasswordHash, PasswordSalt FROM dbo.Users WHERE UserID = @UserID",
                new { UserID = userId });

            if (user == null)
                return (false, "لم يتم العثور على الحساب.");

            string currentActualHash = ComputeSha256Hash(currentPassword, (string)user.PasswordSalt);
            if (!string.Equals((string)user.PasswordHash, currentActualHash, StringComparison.OrdinalIgnoreCase))
                return (false, "كلمة المرور الحالية غير صحيحة.");

            string newSalt = GenerateSecureSalt();
            string newHash = ComputeSha256Hash(newPassword, newSalt);

            await conn.ExecuteAsync(@"
                UPDATE dbo.Users 
                SET PasswordHash = @Hash, PasswordSalt = @Salt, MustChangePassword = 0 
                WHERE UserID = @UserID",
                new { Hash = newHash, Salt = newSalt, UserID = userId });

            if (CurrentSession != null && CurrentSession.UserID == userId)
            {
                CurrentSession.MustChangePassword = false;
            }

            return (true, string.Empty);
        }

        // =========================================================================
        // عمليات إدارة المستخدمين والصلاحيات (User Management Repository Operations)
        // =========================================================================
        public async Task<List<UserDto>> GetAllUsersAsync(string searchFilter = "")
        {
            using var conn = new SqlConnection(_connectionString);
            string sql = @"
                SELECT UserID, Username, FullName, IsAdmin, MustChangePassword, IsActive 
                FROM dbo.Users
                WHERE (@Search = '' OR Username LIKE @SearchPattern OR FullName LIKE @SearchPattern)
                ORDER BY IsAdmin DESC, UserID ASC";

            var list = await conn.QueryAsync<UserDto>(sql, new
            {
                Search = searchFilter.Trim(),
                SearchPattern = $"%{searchFilter.Trim()}%"
            });
            return list.ToList();
        }

        public async Task<List<PermissionRowViewModel>> GetUserPermissionsMatrixAsync(int userId, bool isAdmin)
        {
            var defaultMatrix = PermissionRowViewModel.CreateDefaultMatrix(grantAll: isAdmin);
            if (userId <= 0) return defaultMatrix;

            using var conn = new SqlConnection(_connectionString);
            var dbPerms = (await conn.QueryAsync<PermissionRowViewModel>(
                @"SELECT ModuleCode, ModuleArabicName, CanRead, CanInsert, CanUpdate, CanDelete 
                  FROM dbo.UserPermissions WHERE UserID = @UserID",
                new { UserID = userId })).ToDictionary(x => x.ModuleCode);

            foreach (var row in defaultMatrix)
            {
                if (isAdmin)
                {
                    row.CanRead = row.CanInsert = row.CanUpdate = row.CanDelete = true;
                }
                else if (dbPerms.TryGetValue(row.ModuleCode, out var saved))
                {
                    row.CanRead = saved.CanRead;
                    row.CanInsert = saved.CanInsert;
                    row.CanUpdate = saved.CanUpdate;
                    row.CanDelete = saved.CanDelete;
                }
            }

            return defaultMatrix;
        }

        public async Task<(bool IsSuccess, string Message)> SaveUserWithPermissionsAsync(
            UserDto userDto,
            string? plainPasswordIfNewOrReset,
            List<PermissionRowViewModel> permissions)
        {
            // 1. فحص أمني صارم على مستوى الخدمة (Backend RBAC Enforcement)
            if (CurrentSession == null)
                return (false, "انتهت جلسة العمل، يرجى إعادة تسجيل الدخول.");

            bool isNewUser = userDto.UserID == 0;
            if (isNewUser && !CurrentSession.HasPermission("UserManagement", "insert"))
                return (false, "عذراً، لا تملك صلاحية (إضافة وكتابة) لإنشاء حسابات مستخدمين جدد.");

            if (!isNewUser && !CurrentSession.HasPermission("UserManagement", "update"))
                return (false, "عذراً، لا تملك صلاحية (تعديل) لتحديث بيانات أو صلاحيات المستخدمين.");

            // 2. منع تصعيد الصلاحيات (Privilege Escalation Guard)
            if (!CurrentSession.IsAdmin && userDto.IsAdmin)
                return (false, "عذراً، فقط مدير النظام العام (Admin) يمكنه منح صلاحية الإدارة الشاملة لحساب آخر.");

            if (string.IsNullOrWhiteSpace(userDto.Username) || string.IsNullOrWhiteSpace(userDto.FullName))
                return (false, "يرجى إدخال اسم المستخدم والاسم الكامل للموظف.");

            // 3. حماية المدير الحالي من إيقاف حسابه أو سحب صلاحية الإدارة عن نفسه بالخطأ
            if (userDto.UserID == CurrentSession.UserID)
            {
                if (!userDto.IsActive)
                    return (false, "لا يمكنك إيقاف حسابك الشخصي وأنت مسجل الدخول به حالياً.");
                if (!userDto.IsAdmin && CurrentSession.IsAdmin)
                    return (false, "لا يمكنك إلغاء صلاحية (مدير النظام) عن حسابك الشخصي الحالي منعاً لقفل النظام.");
            }

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            // إذا كان المستخدم المعدل مديراً عاماً، لا يسمح لغير المدير بتعديله
            if (!isNewUser && !CurrentSession.IsAdmin)
            {
                bool targetIsAdmin = await conn.ExecuteScalarAsync<bool>(
                    "SELECT IsAdmin FROM dbo.Users WHERE UserID = @UserID",
                    new { userDto.UserID });
                if (targetIsAdmin)
                    return (false, "لا يمكن لحساب غير مدير تعديل بيانات حساب مدير النظام.");
            }

            // التحقق من عدم تكرار اسم المستخدم
            int existingCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.Users WHERE Username = @Username AND UserID <> @UserID",
                new { Username = userDto.Username.Trim(), userDto.UserID });

            if (existingCount > 0)
                return (false, $"اسم المستخدم ({userDto.Username}) مستخدم بالفعل لحساب آخر.");

            using var tx = conn.BeginTransaction();
            try
            {
                int targetUserId = userDto.UserID;

                if (isNewUser)
                {
                    if (string.IsNullOrWhiteSpace(plainPasswordIfNewOrReset))
                        return (false, "يرجى تحديد كلمة المرور الابتدائية للحساب الجديد.");

                    string salt = GenerateSecureSalt();
                    string hash = ComputeSha256Hash(plainPasswordIfNewOrReset, salt);

                    string insertUserSql = @"
                        INSERT INTO dbo.Users (Username, PasswordHash, PasswordSalt, FullName, IsAdmin, MustChangePassword, IsActive)
                        VALUES (@Username, @Hash, @Salt, @FullName, @IsAdmin, @MustChangePassword, @IsActive);
                        SELECT CAST(SCOPE_IDENTITY() AS INT);";

                    targetUserId = await conn.ExecuteScalarAsync<int>(insertUserSql, new
                    {
                        Username = userDto.Username.Trim(),
                        Hash = hash,
                        Salt = salt,
                        FullName = userDto.FullName.Trim(),
                        userDto.IsAdmin,
                        userDto.MustChangePassword,
                        userDto.IsActive
                    }, tx);
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(plainPasswordIfNewOrReset))
                    {
                        string salt = GenerateSecureSalt();
                        string hash = ComputeSha256Hash(plainPasswordIfNewOrReset, salt);

                        await conn.ExecuteAsync(@"
                            UPDATE dbo.Users 
                            SET Username = @Username, FullName = @FullName, IsAdmin = @IsAdmin, 
                                IsActive = @IsActive, PasswordHash = @Hash, PasswordSalt = @Salt,
                                MustChangePassword = @MustChangePassword
                            WHERE UserID = @UserID",
                            new
                            {
                                Username = userDto.Username.Trim(),
                                FullName = userDto.FullName.Trim(),
                                userDto.IsAdmin,
                                userDto.IsActive,
                                Hash = hash,
                                Salt = salt,
                                userDto.MustChangePassword,
                                userDto.UserID
                            }, tx);
                    }
                    else
                    {
                        await conn.ExecuteAsync(@"
                            UPDATE dbo.Users 
                            SET Username = @Username, FullName = @FullName, IsAdmin = @IsAdmin, 
                                IsActive = @IsActive, MustChangePassword = @MustChangePassword
                            WHERE UserID = @UserID",
                            new
                            {
                                Username = userDto.Username.Trim(),
                                FullName = userDto.FullName.Trim(),
                                userDto.IsAdmin,
                                userDto.IsActive,
                                userDto.MustChangePassword,
                                userDto.UserID
                            }, tx);
                    }

                    await conn.ExecuteAsync("DELETE FROM dbo.UserPermissions WHERE UserID = @UserID", new { UserID = targetUserId }, tx);
                }

                string insertPermSql = @"
                    INSERT INTO dbo.UserPermissions (UserID, ModuleCode, ModuleArabicName, CanRead, CanInsert, CanUpdate, CanDelete)
                    VALUES (@UserID, @ModuleCode, @ModuleArabicName, @CanRead, @CanInsert, @CanUpdate, @CanDelete)";

                foreach (var p in permissions)
                {
                    bool r = userDto.IsAdmin || p.CanRead;
                    bool i = userDto.IsAdmin || p.CanInsert;
                    bool u = userDto.IsAdmin || p.CanUpdate;
                    bool d = userDto.IsAdmin || p.CanDelete;

                    await conn.ExecuteAsync(insertPermSql, new
                    {
                        UserID = targetUserId,
                        p.ModuleCode,
                        p.ModuleArabicName,
                        CanRead = r,
                        CanInsert = i,
                        CanUpdate = u,
                        CanDelete = d
                    }, tx);
                }

                await tx.CommitAsync();
                return (true, isNewUser ? "تم إنشاء الحساب وتخصيص الصلاحيات بنجاح." : "تم حفظ تعديلات الحساب والصلاحيات بنجاح.");
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return (false, $"فشل حفظ بيانات المستخدم: {ex.Message}");
            }
        }

        public async Task<(bool IsSuccess, string Message)> DeleteUserAsync(int userId)
        {
            if (CurrentSession == null)
                return (false, "انتهت جلسة العمل، يرجى إعادة تسجيل الدخول.");

            if (!CurrentSession.HasPermission("UserManagement", "delete"))
                return (false, "عذراً، لا تملك صلاحية (حذف) في شاشة إدارة المستخدمين.");

            if (userId == CurrentSession.UserID)
                return (false, "لا يمكنك حذف حسابك الشخصي المسجل به حالياً.");

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            bool targetIsAdmin = await conn.ExecuteScalarAsync<bool>(
                "SELECT IsAdmin FROM dbo.Users WHERE UserID = @UserID",
                new { UserID = userId });

            if (targetIsAdmin && !CurrentSession.IsAdmin)
                return (false, "لا يمكن لحساب غير مدير حذف حساب مدير النظام.");

            try
            {
                await conn.ExecuteAsync("DELETE FROM dbo.Users WHERE UserID = @UserID", new { UserID = userId });
                return (true, "تم حذف المستخدم وصلاحياته بنجاح.");
            }
            catch (SqlException)
            {
                await conn.ExecuteAsync("UPDATE dbo.Users SET IsActive = 0 WHERE UserID = @UserID", new { UserID = userId });
                return (true, "نظراً لوجود تقييمات سابقة مسجلة باسم هذا الموظف، تم إيقاف الحساب (تجميده) بدلاً من حذفه حفاظاً على سجل التقييمات.");
            }
        }
    }
}
