using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class DatabaseBackupView : UserControl
    {
        private readonly DatabaseBackupService _backupService = new();

        public DatabaseBackupView()
        {
            InitializeComponent();

            string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string defaultFileName = $"SchoolEvaluationDB_Backup_{DateTime.Now:yyyy-MM-dd_HH-mm}.bak";
            TxtBackupTargetPath.Text = Path.Combine(desktopDir, defaultFileName);

            Loaded += async (_, _) =>
            {
                await RefreshDatabaseStatsAsync();
            };
        }

        private async Task RefreshDatabaseStatsAsync()
        {
            try
            {
                var stats = await _backupService.GetDatabaseStatsAsync();
                TxtDbConnectionInfo.Text = $"قاعدة البيانات النشطة: {stats.DatabaseName}   |   الخادم (Server): {stats.ServerName}";
                TxtStatBranchesCats.Text = $"{stats.BranchesCount} فرع / {stats.CategoriesCount} فئة";
                TxtStatEvaluators.Text = $"{stats.EvaluatorsCount} مقيم";
                TxtStatEmployees.Text = $"{stats.EmployeesCount} موظف";
                TxtStatTemplates.Text = $"{stats.TemplatesCount} قالب";
                TxtStatRecords.Text = $"{stats.MonthlyRecordsCount} سجل";
                TxtStatUsers.Text = $"{stats.UsersCount} مستخدم";
            }
            catch (Exception ex)
            {
                TxtDbConnectionInfo.Text = $"تعذر قراءة إحصائيات قاعدة البيانات: {ex.Message}";
            }
        }

        private async void BtnRefreshStats_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDatabaseStatsAsync();
        }

        private void BtnBrowseBackupTarget_Click(object sender, RoutedEventArgs e)
        {
            var sfd = new SaveFileDialog
            {
                Title = "تحديد مكان تخزين النسخة الاحتياطية لقاعدة البيانات",
                Filter = "SQL Server Backup (*.bak)|*.bak|Portable ERP Backup (*.erpbackup)|*.erpbackup",
                FileName = $"SchoolEvaluationDB_Backup_{DateTime.Now:yyyy-MM-dd_HH-mm}.bak"
            };

            if (!string.IsNullOrWhiteSpace(TxtBackupTargetPath.Text))
            {
                string? dir = Path.GetDirectoryName(TxtBackupTargetPath.Text);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    sfd.InitialDirectory = dir;
                }
            }

            if (sfd.ShowDialog() == true)
            {
                TxtBackupTargetPath.Text = sfd.FileName;
            }
        }

        private async void BtnExecuteBackup_Click(object sender, RoutedEventArgs e)
        {
            if (AuthService.CurrentSession?.IsAdmin != true)
            {
                MessageBox.Show("عذراً، هذه الميزة مخصصة لمدير النظام (Admin) فقط.", "تنبيه صلاحيات", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string targetPath = TxtBackupTargetPath.Text.Trim();
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                BtnBrowseBackupTarget_Click(sender, e);
                targetPath = TxtBackupTargetPath.Text.Trim();
                if (string.IsNullOrWhiteSpace(targetPath))
                    return;
            }

            BtnExecuteBackup.IsEnabled = false;
            BtnExecuteBackup.Content = "⏳ جاري إنشاء النسخة الاحتياطية...";

            var (isSuccess, message) = await _backupService.CreateBackupAsync(targetPath);

            BtnExecuteBackup.IsEnabled = true;
            BtnExecuteBackup.Content = "💾 إنشاء وحفظ النسخة الاحتياطية الآن";

            BannerBackupStatus.Visibility = Visibility.Visible;
            if (isSuccess)
            {
                BannerBackupStatus.Background = new SolidColorBrush(Color.FromRgb(236, 253, 245));
                BannerBackupStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(167, 243, 208));
                TxtBackupStatusMessage.Foreground = new SolidColorBrush(Color.FromRgb(4, 120, 87));
                TxtBackupStatusMessage.Text = $"✔ {message}";

                var openFolder = MessageBox.Show(
                    $"{message}\n\nهل تريد فتح المجلد الذي حُفظت فيه النسخة الاحتياطية؟",
                    "تم إنشاء النسخة الاحتياطية بنجاح",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (openFolder == MessageBoxResult.Yes)
                {
                    string? folder = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{targetPath}\"",
                            UseShellExecute = true
                        });
                    }
                }
            }
            else
            {
                BannerBackupStatus.Background = new SolidColorBrush(Color.FromRgb(254, 242, 242));
                BannerBackupStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(254, 202, 202));
                TxtBackupStatusMessage.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                TxtBackupStatusMessage.Text = $"⚠ {message}";

                MessageBox.Show(message, "تنبيه النسخ الاحتياطي", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnBrowseRestoreSource_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Title = "اختيار ملف النسخة الاحتياطية لاستعادة قاعدة البيانات",
                Filter = "ملفات النسخ الاحتياطي (*.bak;*.erpbackup)|*.bak;*.erpbackup|SQL Server Backup (*.bak)|*.bak|Portable ERP Backup (*.erpbackup)|*.erpbackup"
            };

            if (ofd.ShowDialog() == true)
            {
                TxtRestoreSourcePath.Text = ofd.FileName;
            }
        }

        private async void BtnExecuteRestore_Click(object sender, RoutedEventArgs e)
        {
            if (AuthService.CurrentSession?.IsAdmin != true)
            {
                MessageBox.Show("عذراً، استعادة قاعدة البيانات مخصصة لمدير النظام (Admin) فقط.", "تنبيه صلاحيات", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string sourcePath = TxtRestoreSourcePath.Text.Trim();
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                BtnBrowseRestoreSource_Click(sender, e);
                sourcePath = TxtRestoreSourcePath.Text.Trim();
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                    return;
            }

            var confirm = MessageBox.Show(
                $"هل أنت متأكد من استعادة قاعدة البيانات من الملف التالي؟\n\n{sourcePath}\n\n" +
                "⚠️ تنبيه: سيتم استبدال جميع البيانات الحالية في النظام بالبيانات الموجودة داخل هذه النسخة الاحتياطية.",
                "تأكيد استعادة النسخة الاحتياطية",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;

            BtnExecuteRestore.IsEnabled = false;
            BtnExecuteRestore.Content = "⏳ جاري استعادة قاعدة البيانات...";

            var (isSuccess, message) = await _backupService.RestoreBackupAsync(sourcePath);

            BtnExecuteRestore.IsEnabled = true;
            BtnExecuteRestore.Content = "♻️ استعادة قاعدة البيانات من الملف المحدد الآن";

            BannerRestoreStatus.Visibility = Visibility.Visible;
            if (isSuccess)
            {
                BannerRestoreStatus.Background = new SolidColorBrush(Color.FromRgb(236, 253, 245));
                BannerRestoreStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(167, 243, 208));
                TxtRestoreStatusMessage.Foreground = new SolidColorBrush(Color.FromRgb(4, 120, 87));
                TxtRestoreStatusMessage.Text = $"✔ {message}";

                await RefreshDatabaseStatsAsync();

                MessageBox.Show(
                    $"{message}\n\nتم تحديث جميع الجداول والإحصائيات بنجاح.",
                    "تمت الاستعادة بنجاح",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                BannerRestoreStatus.Background = new SolidColorBrush(Color.FromRgb(254, 242, 242));
                BannerRestoreStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(254, 202, 202));
                TxtRestoreStatusMessage.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                TxtRestoreStatusMessage.Text = $"⚠ {message}";

                MessageBox.Show(message, "خطأ في الاستعادة", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
