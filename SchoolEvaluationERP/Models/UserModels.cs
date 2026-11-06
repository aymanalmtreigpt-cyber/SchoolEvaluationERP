using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SchoolEvaluationERP.Models
{
    public class UserDto
    {
        public int UserID { get; set; }
        public string Username { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public bool IsAdmin { get; set; }
        public bool MustChangePassword { get; set; }
        public bool IsActive { get; set; } = true;

        public string RoleDisplay => IsAdmin ? "مدير النظام (صلاحية كاملة)" : "مستخدم مخصص";
        public string StatusDisplay => IsActive ? "نشط" : "موقوف";
        public string PasswordStatusDisplay => MustChangePassword ? "مطالب بتغييرها عند الدخول" : "معتمدة";
    }

    /// <summary>
    /// يمثل صف صلاحية شاشة واحدة في جدول الصلاحيات مع منطق الترابط الذكي:
    /// تفعيل (إضافة أو تعديل أو حذف) يفعل (قراءة) تلقائياً، وإلغاء (قراءة) يلغي البقية تلقائياً.
    /// </summary>
    public class PermissionRowViewModel : INotifyPropertyChanged
    {
        private bool _canRead;
        private bool _canInsert;
        private bool _canUpdate;
        private bool _canDelete;

        public string ModuleCode { get; set; } = string.Empty;
        public string ModuleArabicName { get; set; } = string.Empty;

        public bool CanRead
        {
            get => _canRead;
            set
            {
                if (_canRead != value)
                {
                    _canRead = value;
                    OnPropertyChanged();
                    if (!_canRead)
                    {
                        CanInsert = false;
                        CanUpdate = false;
                        CanDelete = false;
                    }
                }
            }
        }

        public bool CanInsert
        {
            get => _canInsert;
            set
            {
                if (_canInsert != value)
                {
                    _canInsert = value;
                    OnPropertyChanged();
                    if (_canInsert && !_canRead) CanRead = true;
                }
            }
        }

        public bool CanUpdate
        {
            get => _canUpdate;
            set
            {
                if (_canUpdate != value)
                {
                    _canUpdate = value;
                    OnPropertyChanged();
                    if (_canUpdate && !_canRead) CanRead = true;
                }
            }
        }

        public bool CanDelete
        {
            get => _canDelete;
            set
            {
                if (_canDelete != value)
                {
                    _canDelete = value;
                    OnPropertyChanged();
                    if (_canDelete && !_canRead) CanRead = true;
                }
            }
        }

        public static List<PermissionRowViewModel> CreateDefaultMatrix(bool grantAll = false)
        {
            return new List<PermissionRowViewModel>
            {
                new() { ModuleCode = "MasterData", ModuleArabicName = "1. صفحة الأساسيات والهيكل التنظيمي", CanRead = grantAll, CanInsert = grantAll, CanUpdate = grantAll, CanDelete = grantAll },
                new() { ModuleCode = "Templates", ModuleArabicName = "2. إنشاء وتجهيز قوالب التقييم (A4 / Excel)", CanRead = grantAll, CanInsert = grantAll, CanUpdate = grantAll, CanDelete = grantAll },
                new() { ModuleCode = "ScoreEntry", ModuleArabicName = "3. إدخال التقييمات الشهرية", CanRead = grantAll, CanInsert = grantAll, CanUpdate = grantAll, CanDelete = grantAll },
                new() { ModuleCode = "HistoryLog", ModuleArabicName = "4. سجل التقييمات والأرشيف", CanRead = grantAll, CanInsert = grantAll, CanUpdate = grantAll, CanDelete = grantAll },
                new() { ModuleCode = "FinalReport", ModuleArabicName = "5. التقرير النهائي وصرف الحوافز", CanRead = grantAll, CanInsert = grantAll, CanUpdate = grantAll, CanDelete = grantAll },
                new() { ModuleCode = "UserManagement", ModuleArabicName = "6. إدارة المستخدمين والصلاحيات", CanRead = grantAll, CanInsert = grantAll, CanUpdate = grantAll, CanDelete = grantAll }
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
