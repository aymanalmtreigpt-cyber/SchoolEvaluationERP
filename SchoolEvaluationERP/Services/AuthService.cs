using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;

namespace SchoolEvaluationERP.Services
{
    public class ModulePermission
    {
        public string ModuleCode { get; set; } = string.Empty;
        public string ModuleArabicName { get; set; } = string.Empty;
        public bool CanRead { get; set; }
        public bool CanInsert { get; set; }
        public bool CanUpdate { get; set; }
        public bool CanDelete { get; set; }
    }

    public class AuthenticatedUserSession
    {
        public int UserID { get; set; }
        public string Username { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public bool IsAdmin { get; set; }
        public bool MustChangePassword { get; set; }
        public Dictionary<string, ModulePermission> Permissions { get; set; } = new();

        public bool HasPermission(string moduleCode, string action)
        {
            if (IsAdmin) return true;
            if (!Permissions.TryGetValue(moduleCode, out var perm)) return false;

            return action.ToLower() switch
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

        public AuthService(string connectionString)
        {
            _connectionString = connectionString;
        }

        public static string ComputeSha256Hash(string rawPassword, string salt)
        {
            using var sha256 = SHA256.Create();
            // للبيانات الافتراضية الأولى للمدير العام فقط
            string combined = salt == "STATIC_INIT_SALT" ? rawPassword : $"{rawPassword}::{salt}";
            byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined));
            var builder = new StringBuilder();
            foreach (byte b in bytes)
                builder.Append(b.ToString("X2"));
            return builder.ToString();
        }

        public (bool IsSuccess, string ErrorMessage, bool RequirePasswordChange) Login(string username, string password)
        {
            using var conn = new SqlConnection(_connectionString);
            var user = conn.QueryFirstOrDefault<dynamic>(
                "SELECT * FROM dbo.Users WHERE Username = @Username AND IsActive = 1",
                new { Username = username.Trim() });

            if (user == null)
                return (false, "اسم المستخدم غير موجود أو الحساب موقوف.", false);

            string salt = user.PasswordSalt;
            string expectedHash = user.PasswordHash;
            string actualHash = salt == "STATIC_INIT_SALT" && username.ToLower() == "admin" && password == "admin"
                ? ComputeSha256Hash("admin", "STATIC_INIT_SALT")
                : ComputeSha256Hash(password, salt);

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

            var perms = conn.Query<ModulePermission>(
                "SELECT ModuleCode, ModuleArabicName, CanRead, CanInsert, CanUpdate, CanDelete FROM dbo.UserPermissions WHERE UserID = @UserID",
                new { UserID = session.UserID });

            foreach (var p in perms)
            {
                session.Permissions[p.ModuleCode] = p;
            }

            CurrentSession = session;
            return (true, string.Empty, session.MustChangePassword);
        }

        public void ChangeOwnPassword(int userId, string newPassword)
        {
            string newSalt = Guid.NewGuid().ToString("N");
            string newHash = ComputeSha256Hash(newPassword, newSalt);

            using var conn = new SqlConnection(_connectionString);
            conn.Execute(@"
                UPDATE dbo.Users 
                SET PasswordHash = @Hash, PasswordSalt = @Salt, MustChangePassword = 0 
                WHERE UserID = @UserID",
                new { Hash = newHash, Salt = newSalt, UserID = userId });

            if (CurrentSession != null && CurrentSession.UserID == userId)
            {
                CurrentSession.MustChangePassword = false;
            }
        }
    }
}
