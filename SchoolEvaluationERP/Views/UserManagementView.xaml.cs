using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class UserManagementView : UserControl
    {
        private readonly AuthService _authService;
        private int _editingUserId = 0;
        private List<PermissionRowViewModel> _currentPermissions = new();

        private static List<PermissionRowViewModel>? _copiedPermissionsBuffer;
        private static bool _copiedIsAdminFlag;
        private static string _copiedFromEmployeeName = string.Empty;

        private bool CanInsert => AuthService.CurrentSession?.HasPermission("UserManagement", "insert") == true;
        private bool CanUpdate => AuthService.CurrentSession?.HasPermission("UserManagement", "update") == true;
        private bool CanDelete => AuthService.CurrentSession?.HasPermission("UserManagement", "delete") == true;
        private bool IsCurrentUserAdmin => AuthService.CurrentSession?.IsAdmin == true;

        public UserManagementView()
        {
            InitializeComponent();
            _authService = new AuthService();
            Loaded += async (_, _) =>
            {
                await LoadUsersListAsync();

                if (CanInsert)
                {
                    PrepareNewUserForm();
                }
                else if (GridUsers.Items.Count > 0)
                {
                    GridUsers.SelectedIndex = 0;
                }
                else
                {
                    ApplyFormPermissionState(isNewMode: true, targetUserIsAdmin: false);
                }
            };
        }

        private async Task LoadUsersListAsync()
        {
            string search = TxtSearchUser.Text.Trim();
            var users = await _authService.GetAllUsersAsync(search);
            GridUsers.ItemsSource = users;
        }

        private void ApplyFormPermissionState(bool isNewMode, bool targetUserIsAdmin)
        {
            BtnNewUser.Visibility = CanInsert ? Visibility.Visible : Visibility.Collapsed;

            bool canEditCurrentForm = isNewMode ? CanInsert : CanUpdate;
            if (!isNewMode && targetUserIsAdmin && !IsCurrentUserAdmin)
            {
                canEditCurrentForm = false;
            }

            FormInputsContainer.IsEnabled = canEditCurrentForm;
            ChkIsAdmin.IsEnabled = canEditCurrentForm && IsCurrentUserAdmin;

            bool enableMatrix = canEditCurrentForm && (ChkIsAdmin.IsChecked != true);
            GridPermissions.IsReadOnly = !enableMatrix;
            GridPermissions.IsEnabled = enableMatrix;
            BtnSelectAllPerms.IsEnabled = enableMatrix;
            BtnClearAllPerms.IsEnabled = enableMatrix;

            BtnSaveUser.IsEnabled = canEditCurrentForm;

            bool canUseCopyPaste = CanInsert || CanUpdate;
            BtnCopyPerms.Visibility = canUseCopyPaste ? Visibility.Visible : Visibility.Collapsed;
            BtnPastePerms.Visibility = canUseCopyPaste ? Visibility.Visible : Visibility.Collapsed;
            BtnPastePerms.IsEnabled = canEditCurrentForm && (_copiedPermissionsBuffer != null);

            if (!string.IsNullOrEmpty(_copiedFromEmployeeName))
            {
                BtnPastePerms.ToolTip = $"لصق الصلاحيات المنسوخة من: {_copiedFromEmployeeName}";
            }

            if (canEditCurrentForm)
            {
                BtnSaveUser.Content = isNewMode
                    ? "💾 إنشاء الحساب وحفظ الصلاحيات"
                    : "💾 حفظ التعديلات والصلاحيات";
                TxtFormSubtitle.Text = "حدد بيانات الدخول ثم خصص صلاحيات (القراءة، الكتابة، التعديل، الحذف) أو الصقها من موظف آخر";
            }
            else
            {
                BtnSaveUser.Content = "🔒 وضع القراءة والعرض فقط (لا تملك صلاحية الإضافة أو التعديل)";
                TxtFormSubtitle.Text = "أنت تتصفح هذه الشاشة بصلاحية (قراءة فقط) — لا يمكن إنشاء أو تعديل الحسابات.";
            }

            bool allowDelete = !isNewMode && CanDelete && (!targetUserIsAdmin || IsCurrentUserAdmin);
            BtnDeleteUser.Visibility = allowDelete ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PrepareNewUserForm()
        {
            if (!CanInsert)
            {
                MessageBox.Show("عذراً، لا تملك صلاحية (إضافة وكتابة) لإنشاء مستخدم جديد.", "تنبيه صلاحيات", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _editingUserId = 0;
            GridUsers.SelectedItem = null;
            TxtFormTitle.Text = "إنشاء حساب مستخدم جديد";
            LblPasswordField.Text = "كلمة المرور الابتدائية (تُسلم للموظف)";
            TxtEditUsername.Text = string.Empty;
            TxtEditFullName.Text = string.Empty;
            TxtEditPlainPassword.Text = "123456";
            ChkIsAdmin.IsChecked = false;
            ChkMustChangePwd.IsChecked = true;
            ChkIsActive.IsChecked = true;

            _currentPermissions = PermissionRowViewModel.CreateDefaultMatrix(grantAll: false);
            _currentPermissions[2].CanRead = true;
            _currentPermissions[2].CanInsert = true;
            GridPermissions.ItemsSource = _currentPermissions;

            ApplyFormPermissionState(isNewMode: true, targetUserIsAdmin: false);
        }

        private void BtnNewUser_Click(object sender, RoutedEventArgs e)
        {
            PrepareNewUserForm();
            TxtEditUsername.Focus();
        }

        private async void TxtSearchUser_TextChanged(object sender, TextChangedEventArgs e)
        {
            await LoadUsersListAsync();
        }

        private async void GridUsers_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GridUsers.SelectedItem is not UserDto selected)
                return;

            _editingUserId = selected.UserID;
            TxtFormTitle.Text = CanUpdate
                ? $"تعديل حساب وصلاحيات: {selected.FullName}"
                : $"عرض بيانات وصلاحيات: {selected.FullName} (قراءة فقط)";
            LblPasswordField.Text = "إعادة تعيين كلمة السر (اتركه فارغاً للإبقاء على الحالية)";
            TxtEditUsername.Text = selected.Username;
            TxtEditFullName.Text = selected.FullName;
            TxtEditPlainPassword.Text = string.Empty;
            ChkIsAdmin.IsChecked = selected.IsAdmin;
            ChkMustChangePwd.IsChecked = selected.MustChangePassword;
            ChkIsActive.IsChecked = selected.IsActive;

            _currentPermissions = await _authService.GetUserPermissionsMatrixAsync(selected.UserID, selected.IsAdmin);
            GridPermissions.ItemsSource = _currentPermissions;

            ApplyFormPermissionState(isNewMode: false, targetUserIsAdmin: selected.IsAdmin);
        }

        private void ChkIsAdmin_Changed(object sender, RoutedEventArgs e)
        {
            if (_currentPermissions == null) return;
            bool isAdmin = ChkIsAdmin.IsChecked == true;
            if (isAdmin)
            {
                foreach (var row in _currentPermissions)
                {
                    row.CanRead = row.CanInsert = row.CanUpdate = row.CanDelete = true;
                }
            }

            bool isNewMode = _editingUserId == 0;
            ApplyFormPermissionState(isNewMode, targetUserIsAdmin: false);
        }

        private void BtnSelectAllPerms_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _currentPermissions)
            {
                row.CanRead = row.CanInsert = row.CanUpdate = row.CanDelete = true;
            }
        }

        private void BtnClearAllPerms_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _currentPermissions)
            {
                row.CanRead = row.CanInsert = row.CanUpdate = row.CanDelete = false;
            }
        }

        private void BtnCopyPerms_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPermissions == null || _currentPermissions.Count == 0)
                return;

            _copiedPermissionsBuffer = _currentPermissions.Select(p => new PermissionRowViewModel
            {
                ModuleCode = p.ModuleCode,
                ModuleArabicName = p.ModuleArabicName,
                CanRead = p.CanRead,
                CanInsert = p.CanInsert,
                CanUpdate = p.CanUpdate,
                CanDelete = p.CanDelete
            }).ToList();

            _copiedIsAdminFlag = ChkIsAdmin.IsChecked == true;
            _copiedFromEmployeeName = string.IsNullOrWhiteSpace(TxtEditFullName.Text)
                ? "النموذج الحالي"
                : TxtEditFullName.Text.Trim();

            bool isNewMode = _editingUserId == 0;
            bool targetIsAdmin = GridUsers.SelectedItem is UserDto u && u.IsAdmin;
            ApplyFormPermissionState(isNewMode, targetIsAdmin);

            MessageBox.Show(
                $"تم نسخ مصفوفة الصلاحيات الخاصة بـ ({_copiedFromEmployeeName}).\nيمكنك الآن اختيار موظف آخر (أو مستخدم جديد) والضغط على (📥 لصق الصلاحيات).",
                "تم نسخ الصلاحيات",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void BtnPastePerms_Click(object sender, RoutedEventArgs e)
        {
            if (_copiedPermissionsBuffer == null || _currentPermissions == null)
                return;

            if (IsCurrentUserAdmin)
            {
                ChkIsAdmin.IsChecked = _copiedIsAdminFlag;
            }

            foreach (var targetRow in _currentPermissions)
            {
                var sourceRow = _copiedPermissionsBuffer.FirstOrDefault(x => x.ModuleCode == targetRow.ModuleCode);
                if (sourceRow != null)
                {
                    targetRow.CanRead = sourceRow.CanRead;
                    targetRow.CanInsert = sourceRow.CanInsert;
                    targetRow.CanUpdate = sourceRow.CanUpdate;
                    targetRow.CanDelete = sourceRow.CanDelete;
                }
            }

            GridPermissions.Items.Refresh();

            MessageBox.Show(
                $"تم لصق الصلاحيات المنسوخة من ({_copiedFromEmployeeName}) بنجاح.\nاضغط على زر (💾 حفظ) لاعتمادها لهذا الموظف.",
                "تم لصق الصلاحيات",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private async void BtnSaveUser_Click(object sender, RoutedEventArgs e)
        {
            var dto = new UserDto
            {
                UserID = _editingUserId,
                Username = TxtEditUsername.Text.Trim(),
                FullName = TxtEditFullName.Text.Trim(),
                IsAdmin = ChkIsAdmin.IsChecked == true,
                MustChangePassword = ChkMustChangePwd.IsChecked == true,
                IsActive = ChkIsActive.IsChecked == true
            };

            string? plainPwd = string.IsNullOrWhiteSpace(TxtEditPlainPassword.Text)
                ? null
                : TxtEditPlainPassword.Text.Trim();

            if (_editingUserId > 0 && !string.IsNullOrEmpty(plainPwd))
            {
                dto.MustChangePassword = true;
            }

            BtnSaveUser.IsEnabled = false;
            var (isSuccess, message) = await _authService.SaveUserWithPermissionsAsync(dto, plainPwd, _currentPermissions);
            BtnSaveUser.IsEnabled = true;

            MessageBox.Show(message, isSuccess ? "نجاح العملية" : "تنبيه صلاحيات", MessageBoxButton.OK, isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (isSuccess)
            {
                await LoadUsersListAsync();
                if (_editingUserId == 0 && CanInsert)
                    PrepareNewUserForm();
            }
        }

        private async void BtnDeleteUser_Click(object sender, RoutedEventArgs e)
        {
            if (_editingUserId <= 0) return;

            var confirm = MessageBox.Show(
                $"هل أنت متأكد من حذف الحساب ({TxtEditUsername.Text})؟",
                "تأكيد الحذف",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            var (isSuccess, message) = await _authService.DeleteUserAsync(_editingUserId);
            MessageBox.Show(message, isSuccess ? "تمت العملية" : "تنبيه صلاحيات", MessageBoxButton.OK, isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (isSuccess)
            {
                await LoadUsersListAsync();
                if (CanInsert)
                    PrepareNewUserForm();
                else if (GridUsers.Items.Count > 0)
                    GridUsers.SelectedIndex = 0;
            }
        }
    }
}
