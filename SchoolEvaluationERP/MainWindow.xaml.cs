using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SchoolEvaluationERP.Services;
using SchoolEvaluationERP.Views;

namespace SchoolEvaluationERP
{
    public partial class MainWindow : Window
    {
        private readonly List<Button> _navButtons;

        public MainWindow()
        {
            InitializeComponent();

            _navButtons = new List<Button>
            {
                NavMasterData,
                NavTemplates,
                NavScoreEntry,
                NavHistoryLog,
                NavFinalReport,
                NavUserManagement
            };

            ApplyUserSessionAndPermissions();
        }

        private void ApplyUserSessionAndPermissions()
        {
            var session = AuthService.CurrentSession;
            if (session == null)
            {
                var login = new LoginWindow();
                login.Show();
                Close();
                return;
            }

            TxtCurrentUserName.Text = session.FullName;
            TxtCurrentUserRole.Text = session.IsAdmin ? "مدير النظام (صلاحية كاملة)" : $"حساب مستخدم ({session.Username})";

            Button? firstAllowedButton = null;

            foreach (var btn in _navButtons)
            {
                string moduleCode = btn.Tag?.ToString() ?? string.Empty;
                bool canRead = session.HasPermission(moduleCode, "read");

                btn.Visibility = canRead ? Visibility.Visible : Visibility.Collapsed;
                if (canRead && firstAllowedButton == null)
                {
                    firstAllowedButton = btn;
                }
            }

            // افتراضياً نفتح (1. صفحة الأساسيات) عند تسجيل الدخول، أو أول شاشة مسموحة للمستخدم
            if (session.HasPermission("MasterData", "read"))
            {
                NavigateToModule("MasterData", NavMasterData);
            }
            else if (firstAllowedButton != null)
            {
                NavigateToModule(firstAllowedButton.Tag?.ToString() ?? string.Empty, firstAllowedButton);
            }
            else
            {
                MessageBox.Show("لا توجد أي صلاحيات قراءة مخصصة لهذا الحساب. يرجى مراجعة مدير النظام.", "تنبيه صلاحيات", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void NavButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string moduleCode)
            {
                NavigateToModule(moduleCode, btn);
            }
        }

        private void NavigateToModule(string moduleCode, Button activeBtn)
        {
            // تلوين الزر النشط في القائمة الجانبية
            foreach (var btn in _navButtons)
            {
                btn.Background = Brushes.Transparent;
                btn.Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225));
            }

            activeBtn.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235));
            activeBtn.Foreground = Brushes.White;

            UpdatePermissionBadge(moduleCode);

            switch (moduleCode)
            {
                case "UserManagement":
                    TxtActivePageTitle.Text = "إدارة المستخدمين وصلاحيات النظام (RBAC)";
                    MainContentHost.Content = new UserManagementView();
                    break;

                case "MasterData":
                    TxtActivePageTitle.Text = "صفحة الأساسيات (الفروع، الوظائف، الفئات، المقيمين، الموظفين)";
                    MainContentHost.Content = new MasterDataView();
                    break;

                case "Templates":
                    TxtActivePageTitle.Text = "إنشاء وتجهيز قوالب التقييم متعددة الأوراق (A4)";
                    MainContentHost.Content = new TemplatesView();
                    break;

                case "ScoreEntry":
                    TxtActivePageTitle.Text = "إدخال التقييمات الأسبوعية للموظفين";
                    MainContentHost.Content = new ScoreEntryView();
                    break;

                case "HistoryLog":
                    TxtActivePageTitle.Text = "سجل التقييمات والأرشيف التاريخي";
                    MainContentHost.Content = new HistoryLogView();
                    break;

                case "FinalReport":
                    TxtActivePageTitle.Text = "التقرير الشهري النهائي وصرف الحوافز المحاسبية";
                    MainContentHost.Content = new FinalReportView();
                    break;
            }
        }

        private void UpdatePermissionBadge(string moduleCode)
        {
            var session = AuthService.CurrentSession;
            if (session == null) return;

            var allowed = new List<string>();
            if (session.HasPermission(moduleCode, "read")) allowed.Add("قراءة ✔");
            if (session.HasPermission(moduleCode, "insert")) allowed.Add("إضافة ✔");
            if (session.HasPermission(moduleCode, "update")) allowed.Add("تعديل ✔");
            if (session.HasPermission(moduleCode, "delete")) allowed.Add("حذف ✔");

            TxtPermissionsBadge.Text = $"صلاحياتك في هذه الشاشة: {string.Join("  |  ", allowed)}";
        }

        private UIElement CreatePhasePlaceholder(string title, string description)
        {
            var card = new Border
            {
                Style = (Style)FindResource("CardStyle"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxWidth = 650,
                Padding = new Thickness(32)
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            });
            stack.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105))
            });

            card.Child = stack;
            return card;
        }

        private void BtnChangeOwnPassword_Click(object sender, RoutedEventArgs e)
        {
            if (AuthService.CurrentSession == null) return;
            var dlg = new ChangePasswordWindow(AuthService.CurrentSession.UserID, isMandatoryFirstLogin: false)
            {
                Owner = this
            };
            dlg.ShowDialog();
        }

        private void BtnLogout_Click(object sender, RoutedEventArgs e)
        {
            AuthService.Logout();
            var login = new LoginWindow();
            login.Show();
            Close();
        }
    }
}





