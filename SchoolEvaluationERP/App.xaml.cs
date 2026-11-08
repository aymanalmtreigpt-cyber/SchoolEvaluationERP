using System.Windows;
using QuestPDF.Infrastructure;
using SchoolEvaluationERP.Views;

namespace SchoolEvaluationERP
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            QuestPDF.Settings.License = LicenseType.Community;

            // تفعيل ميزة التحديد المتعدد وإلغاء التحديد بالضغط غير السريع على جميع جداول النظام
            DataGridSelectionBehavior.RegisterGlobalHandlers();

            DispatcherUnhandledException += (_, args) =>
            {
                MessageBox.Show(
                    $"حدث خطأ أثناء تنفيذ العملية:\n{args.Exception.Message}\n\nالتفاصيل الداخليّة:\n{args.Exception.InnerException?.Message}",
                    "تنبيه النظام",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                args.Handled = true;
            };
        }
    }
}
