using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class ExcelImportReportDialog : Window
    {
        private readonly string _plainTextReport;

        public ExcelImportReportDialog(
            TemplateExcelImportResult result,
            string evaluatorName,
            int evalYear,
            int evalMonth)
        {
            InitializeComponent();

            string monthName = IncentiveCalculatorService.GetArabicMonthName(evalMonth);
            int missingCount = result.MissingEmployeesInExcel.Count;
            int extraCount = result.ExtraEmployeesInExcel.Count;
            var structureItems = BuildStructureWarningItems(result);
            int structureCount = structureItems.Count;
            bool hasWarnings = missingCount > 0 || extraCount > 0 || structureCount > 0 || result.ClampedWarningsCount > 0;

            if (!result.IsSuccess)
            {
                BannerStatus.Background = new SolidColorBrush(Color.FromRgb(254, 242, 242));
                BannerStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(252, 165, 165));
                TxtReportTitle.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                TxtReportSubtitle.Foreground = new SolidColorBrush(Color.FromRgb(153, 27, 27));
                TxtReportTitle.Text = "⚠ لم يتم سحب درجات مطابقة من ملف الإكسل";
                TxtReportSubtitle.Text = result.ErrorOrHintMessage;
            }
            else if (hasWarnings)
            {
                BannerStatus.Background = new SolidColorBrush(Color.FromRgb(255, 251, 235));
                BannerStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(252, 211, 77));
                TxtReportTitle.Foreground = new SolidColorBrush(Color.FromRgb(146, 64, 14));
                TxtReportSubtitle.Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9));
                TxtReportTitle.Text = $"✔ تم استيراد درجات ({result.ImportedEmployeesCount}) موظف مع وجود ملاحظات مطابقة";
                TxtReportSubtitle.Text = $"المقيم: {evaluatorName} — شهر {monthName} {evalYear} م | يرجى مراجعة قوائم الموظفين غير المتطابقين ثم الضغط على زر الحفظ.";
            }
            else
            {
                TxtReportTitle.Text = $"✔ تم استيراد درجات ({result.ImportedEmployeesCount}) موظف بمطابقة كاملة 100%";
                TxtReportSubtitle.Text = $"المقيم: {evaluatorName} — شهر {monthName} {evalYear} م | جميع الأوراق والأسماء وأعمدة المبادئ متطابقة — اضغط زر الحفظ للاعتماد.";
            }

            TxtKpiImported.Text = $"{result.ImportedEmployeesCount} موظف";
            TxtKpiMissingInExcel.Text = $"{missingCount} موظف";
            TxtKpiExtraInExcel.Text = $"{extraCount} موظف";
            TxtKpiStructureWarnings.Text = $"{structureCount} تنبيه";

            TabMissingInExcel.Header = $"⚠ غير موجودين في الإكسل ({missingCount})";
            TabExtraInExcel.Header = $"⚠ غير موجودين في النظام ({extraCount})";
            TabStructureWarnings.Header = $"📑 الأوراق والأعمدة ({structureCount})";

            GridMissingInExcel.ItemsSource = result.MissingEmployeesInExcel;
            GridExtraInExcel.ItemsSource = result.ExtraEmployeesInExcel;
            GridStructureWarnings.ItemsSource = structureItems;

            _plainTextReport = BuildPlainTextReport(result, structureItems, evaluatorName, monthName, evalYear);
            TxtFullReportText.Text = _plainTextReport;
        }

        private static List<ExcelImportMismatchEntry> BuildStructureWarningItems(TemplateExcelImportResult result)
        {
            var items = new List<ExcelImportMismatchEntry>();

            foreach (string sheetName in result.MissingSheetsInExcel)
            {
                items.Add(new ExcelImportMismatchEntry
                {
                    SheetName = sheetName,
                    ItemName = "ورقة غير موجودة في الإكسل",
                    Details = "موجودة في قالب النظام ولم يُعثر على ورقة مطابقة لها في ملف الإكسل."
                });
            }

            foreach (string sheetName in result.ExtraSheetsInExcel)
            {
                items.Add(new ExcelImportMismatchEntry
                {
                    SheetName = sheetName,
                    ItemName = "ورقة إضافية في الإكسل",
                    Details = "موجودة في ملف الإكسل ولم يُعثر على ورقة مطابقة لها في قالب النظام (تم تجاهلها)."
                });
            }

            items.AddRange(result.CriteriaColumnWarnings);
            return items;
        }

        private static string BuildPlainTextReport(
            TemplateExcelImportResult result,
            List<ExcelImportMismatchEntry> structureItems,
            string evaluatorName,
            string monthName,
            int evalYear)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"تقرير استيراد التقييمات من Excel — المقيم: {evaluatorName} — شهر {monthName} {evalYear} م");
            sb.AppendLine(new string('─', 62));
            sb.AppendLine($"• الأوراق المتطابقة: {result.MatchedSheetsCount} من أصل {result.TotalSystemSheetsCount} ورقة");
            sb.AppendLine($"• الموظفون الذين تم سحب درجاتهم: {result.ImportedEmployeesCount} موظف");
            if (result.MatchedEmptyEmployeesCount > 0)
            {
                sb.AppendLine($"• موظفون مطابقون ولكن خلايا درجاتهم فارغة في الإكسل (تُركوا فارغين): {result.MatchedEmptyEmployeesCount} موظف");
            }
            if (result.ClampedWarningsCount > 0)
            {
                sb.AppendLine($"• خلايا تجاوزت الحد الأقصى وتم ضبطها للدرجة القصوى: {result.ClampedWarningsCount} خلية");
            }

            if (!result.IsSuccess && !string.IsNullOrWhiteSpace(result.ErrorOrHintMessage))
            {
                sb.AppendLine();
                sb.AppendLine($"⚠ {result.ErrorOrHintMessage}");
            }

            if (result.MissingEmployeesInExcel.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"【1】 موظفون موجودون في النظام وغير موجودين في الإكسل ({result.MissingEmployeesInExcel.Count}):");
                foreach (var grp in result.MissingEmployeesInExcel.GroupBy(x => x.SheetName))
                {
                    sb.AppendLine($"  ▸ ورقة ({grp.Key}):");
                    foreach (var item in grp)
                    {
                        sb.AppendLine($"     - {item.ItemName}" + (string.IsNullOrWhiteSpace(item.Details) ? "" : $"  [{item.Details}]"));
                    }
                }
            }

            if (result.ExtraEmployeesInExcel.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"【2】 موظفون موجودون في الإكسل وغير موجودين في النظام (أو يوجد اختلاف حرفي في الاسم) ({result.ExtraEmployeesInExcel.Count}):");
                foreach (var grp in result.ExtraEmployeesInExcel.GroupBy(x => x.SheetName))
                {
                    sb.AppendLine($"  ▸ ورقة ({grp.Key}):");
                    foreach (var item in grp)
                    {
                        sb.AppendLine($"     - {item.ItemName}  ({item.Details})");
                    }
                }
            }

            if (structureItems.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"【3】 تنبيهات الأوراق وأعمدة مبادئ التقييم ({structureItems.Count}):");
                foreach (var item in structureItems)
                {
                    sb.AppendLine($"  • [{item.SheetName}] {item.ItemName}: {item.Details}");
                }
            }

            if (result.IsSuccess &&
                result.MissingEmployeesInExcel.Count == 0 &&
                result.ExtraEmployeesInExcel.Count == 0 &&
                structureItems.Count == 0)
            {
                sb.AppendLine();
                sb.AppendLine("✔ لم يتم رصد أي اختلافات — جميع الأوراق والأسماء وأعمدة المبادئ متطابقة تماماً.");
            }

            sb.AppendLine();
            sb.AppendLine("ملاحظة: لم يتم الحفظ في قاعدة البيانات بعد — يرجى مراجعة الجدول ثم الضغط على زر الحفظ.");
            return sb.ToString();
        }

        private void BtnCopyReport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_plainTextReport);
                TxtCopyFeedback.Text = "✔ تم نسخ التقرير إلى الحافظة!";
            }
            catch
            {
                TxtCopyFeedback.Text = "تعذر النسخ إلى الحافظة.";
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
