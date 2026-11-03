using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class LoginWindow : Window
    {
        private readonly AuthService _authService;

        public LoginWindow()
        {
            InitializeComponent();
            _authService = new AuthService();
            Loaded += async (_, _) =>
            {
                TxtPassword.Focus();
                await CheckLanServerAsync();
            };
        }

        private async Task CheckLanServerAsync()
        {
            TxtLanStatus.Text = "جاري فحص الاتصال بقاعدة البيانات...";
            LanStatusDot.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));

            var (isConnected, message) = await _authService.TestServerConnectionAsync();
            if (isConnected)
            {
                LanStatusDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                TxtLanStatus.Text = "متصل بسيرفر قاعدة البيانات (جاهز)";
                TxtLanStatus.ToolTip = message;
            }
            else
            {
                LanStatusDot.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                TxtLanStatus.Text = "السيرفر غير متصل (اضغط فحص السيرفر للتفاصيل)";
                TxtLanStatus.ToolTip = message;
            }
        }

        private async void BtnTestLan_Click(object sender, RoutedEventArgs e)
        {
            BtnTestLan.IsEnabled = false;
            var (isConnected, message) = await _authService.TestServerConnectionAsync();
            await CheckLanServerAsync();
            BtnTestLan.IsEnabled = true;

            MessageBox.Show(
                $"{message}\n\nمسار الاتصال الحالي في appsettings.json:\n{AppConfigService.ConnectionString}",
                isConnected ? "اتصال الشبكة ناجح" : "تنبيه اتصال قاعدة البيانات",
                MessageBoxButton.OK,
                isConnected ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private async void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ErrorBanner.Visibility = Visibility.Collapsed;
                string username = TxtUsername.Text.Trim();
                string password = TxtPassword.Password;

                BtnLogin.IsEnabled = false;
                BtnLogin.Content = "جاري التحقق من الصلاحيات...";

                var (isSuccess, errorMessage, requirePasswordChange) = await _authService.LoginAsync(username, password);

                BtnLogin.IsEnabled = true;
                BtnLogin.Content = "تسجيل الدخول للنظام";

                if (!isSuccess)
                {
                    TxtErrorMessage.Text = errorMessage;
                    ErrorBanner.Visibility = Visibility.Visible;
                    return;
                }

                if (requirePasswordChange && AuthService.CurrentSession != null)
                {
                    var changePwdWin = new ChangePasswordWindow(
                        AuthService.CurrentSession.UserID,
                        isMandatoryFirstLogin: true,
                        initialKnownPassword: password)
                    {
                        Owner = this
                    };

                    bool? changed = changePwdWin.ShowDialog();
                    if (changed != true)
                    {
                        AuthService.Logout();
                        TxtErrorMessage.Text = "يجب تغيير كلمة المرور الابتدائية للمتابعة إلى النظام.";
                        ErrorBanner.Visibility = Visibility.Visible;
                        return;
                    }
                }

                var mainWin = new MainWindow();
                Application.Current.MainWindow = mainWin;
                mainWin.Show();
                Close();
            }
            catch (Exception ex)
            {
                BtnLogin.IsEnabled = true;
                BtnLogin.Content = "تسجيل الدخول للنظام";
                MessageBox.Show($"تعذر فتح النافذة الرئيسية:\n{ex.Message}\n{ex.InnerException?.Message}", "خطأ تشغيل", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
