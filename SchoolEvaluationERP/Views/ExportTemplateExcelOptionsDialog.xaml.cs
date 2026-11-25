using System.Windows;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public enum TemplateExcelExportScope
    {
        Cancel = 0,
        SelectedMonthOnly = 1,
        AllTwelveMonths = 2
    }

    public partial class ExportTemplateExcelOptionsDialog : Window
    {
        public TemplateExcelExportScope SelectedScope { get; private set; } = TemplateExcelExportScope.Cancel;

        public ExportTemplateExcelOptionsDialog(string evaluatorName, int year, int month)
        {
            InitializeComponent();
            string monthName = IncentiveCalculatorService.GetArabicMonthName(month);

            TxtPromptSubtitle.Text = $"هل تريد استخراج قالب المقيم ({evaluatorName}) للشهر المحدد فقط أم لجميع أشهر سنة {year} م (12 شهراً)؟";
            BtnExportSelectedMonth.Content = $"📅 الشهر المحدد فقط (شهر {monthName} {year} م)";
            BtnExportAllMonths.Content = $"📁 جميع الشهور (12 شهراً لعام {year} م داخل مجلد)";
        }

        private void BtnExportSelectedMonth_Click(object sender, RoutedEventArgs e)
        {
            SelectedScope = TemplateExcelExportScope.SelectedMonthOnly;
            DialogResult = true;
            Close();
        }

        private void BtnExportAllMonths_Click(object sender, RoutedEventArgs e)
        {
            SelectedScope = TemplateExcelExportScope.AllTwelveMonths;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            SelectedScope = TemplateExcelExportScope.Cancel;
            DialogResult = false;
            Close();
        }
    }
}
