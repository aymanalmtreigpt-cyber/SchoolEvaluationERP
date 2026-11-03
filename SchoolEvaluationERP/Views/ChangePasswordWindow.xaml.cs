using System.Windows;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class ChangePasswordWindow : Window
    {
        private readonly int _userId;
        private readonly AuthService _authService;

        public ChangePasswordWindow(int userId, bool isMandatoryFirstLogin = false, string? initialKnownPassword = null)
        {
            InitializeComponent();
            _userId = userId;
            _authService = new AuthService();

            if (!isMandatoryFirstLogin)
            {
                TxtHeaderTitle.Text = "تغيير كلمة المرور";
                TxtSubTitle.Text = "يمكنك تحديث كلمة المرور الخاصة بحسابك في أي وقت لضمان الأمان.";
            }

            if (!string.IsNullOrEmpty(initialKnownPassword))
            {
                TxtCurrentPassword.Password = initialKnownPassword;
                TxtNewPassword.Focus();
            }
            else
            {
                TxtCurrentPassword.Focus();
            }
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            ErrorBanner.Visibility = Visibility.Collapsed;

            string currentPwd = TxtCurrentPassword.Password;
            string newPwd = TxtNewPassword.Password;
            string confirmPwd = TxtConfirmPassword.Password;

            if (newPwd != confirmPwd)
            {
                TxtError.Text = "كلمة المرور الجديدة وتأكيدها غير متطابقين.";
                ErrorBanner.Visibility = Visibility.Visible;
                return;
            }

            BtnSave.IsEnabled = false;
            var (isSuccess, errorMsg) = await _authService.ChangeOwnPasswordAsync(_userId, currentPwd, newPwd);
            BtnSave.IsEnabled = true;

            if (!isSuccess)
            {
                TxtError.Text = errorMsg;
                ErrorBanner.Visibility = Visibility.Visible;
                return;
            }

            MessageBox.Show("تم تحديث كلمة المرور الخاصة بك بنجاح.", "تم الحفظ", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
