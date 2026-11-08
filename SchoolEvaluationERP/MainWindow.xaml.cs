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
        private bool _isSidebarCollapsed;
        private double _savedSidebarWidth = 260;

        public MainWindow()
        {
            InitializeComponent();

            _navButtons = new List<Button>
            {
                NavMasterData,
                NavIncentiveBasics,
                NavTemplates,
                NavScoreEntry,
                NavScoreBalancing,
                NavHistoryLog,
                NavFinalReport,
                NavUserManagement,
                NavDatabaseBackup
            };

            ApplyUserSessionAndPermissions();
        }

        private void BtnToggleSidebar_Click(object sender, RoutedEventArgs e)
        {
            if (!_isSidebarCollapsed)
            {
                if (ColSidebar.ActualWidth >= 195)
                    _savedSidebarWidth = ColSidebar.ActualWidth;

                ColSidebar.MinWidth = 0;
                ColSidebar.Width = new GridLength(0);
                SidebarContainer.Visibility = Visibility.Collapsed;
                SidebarSplitter.Visibility = Visibility.Collapsed;
                BtnToggleSidebar.Content = "☰ إظهار القائمة";
                _isSidebarCollapsed = true;
            }
            else
            {
                SidebarContainer.Visibility = Visibility.Visible;
                SidebarSplitter.Visibility = Visibility.Visible;
                ColSidebar.MinWidth = 195;
                ColSidebar.Width = new GridLength(_savedSidebarWidth);
                BtnToggleSidebar.Content = "☰ القائمة";
                _isSidebarCollapsed = false;
            }
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
                bool canRead = moduleCode == "DatabaseBackup" ? session.IsAdmin : session.HasPermission(moduleCode, "read");

                btn.Visibility = canRead ? Visibility.Visible : Visibility.Collapsed;
                if (canRead && firstAllowedButton == null)
                {
                    firstAllowedButton = btn;
                }
            }

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
                case "MasterData":
                    TxtActivePageTitle.Text = "صفحة الأساسيات (الهيكل التنظيمي، مجموعات المقيمين، تخصيص الدرجات، الموظفين)";
                    MainContentHost.Content = new MasterDataView();
                    break;

                case "IncentiveBasics":
                    TxtActivePageTitle.Text = "أساسيات الحوافز وتخصيص الحد الأعلى والأدنى لكل فئة";
                    MainContentHost.Content = new IncentiveBasicsView();
                    break;

                case "Templates":
                    TxtActivePageTitle.Text = "إنشاء وتجهيز قوالب التقييم متعددة الأوراق (A4 PDF / Excel)";
                    MainContentHost.Content = new TemplatesView();
                    break;

                case "ScoreEntry":
                    TxtActivePageTitle.Text = "إدخال التقييمات الشهرية للموظفين";
                    MainContentHost.Content = new ScoreEntryView();
                    break;

                case "ScoreBalancing":
                    TxtActivePageTitle.Text = "نسب الموازنة ومعالجة نسب التقييم غير المنطقية للمقيمين";
                    MainContentHost.Content = new ScoreBalancingView();
                    break;

                case "HistoryLog":
                    TxtActivePageTitle.Text = "سجل التقييمات الشهرية والأرشيف التاريخي";
                    MainContentHost.Content = new HistoryLogView();
                    break;

                case "FinalReport":
                    TxtActivePageTitle.Text = "التقرير الشهري النهائي وصرف الحوافز المحاسبية";
                    MainContentHost.Content = new FinalReportView();
                    break;

                case "UserManagement":
                    TxtActivePageTitle.Text = "إدارة المستخدمين وصلاحيات النظام (RBAC)";
                    MainContentHost.Content = new UserManagementView();
                    break;

                case "DatabaseBackup":
                    TxtActivePageTitle.Text = "إدارة النسخ الاحتياطي واستعادة قاعدة البيانات";
                    MainContentHost.Content = new DatabaseBackupView();
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

            TxtPermissionsBadge.Text = $"صلاحياتك: {string.Join(" | ", allowed)}";
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
