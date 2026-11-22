using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class FinalReportView : UserControl
    {
        private readonly FinalReportService _reportService = new();
        private readonly MasterDataService _masterService = new();
        private readonly MonthlyPeriodConfigService _periodService = new();
        private MonthlyFinalReportSummary? _currentReport;
        private bool _isInitializing;

        public FinalReportView()
        {
            _isInitializing = true;
            InitializeComponent();
            _isInitializing = false;

            Loaded += async (_, _) =>
            {
                await InitializeFiltersAndSettingsAsync();
                await RefreshReportAsync();
            };
        }

        private async Task InitializeFiltersAndSettingsAsync()
        {
            _isInitializing = true;

            int currentYear = DateTime.Now.Year;
            CmbYear.ItemsSource = new List<int> { currentYear - 1, currentYear, currentYear + 1, currentYear + 2 };
            CmbYear.SelectedItem = currentYear;

            var months = MonthOptionItem.GetAllMonths();
            CmbMonth.ItemsSource = months;
            CmbMonth.SelectedValue = DateTime.Now.Month;

            var branches = await _masterService.GetBranchesAsync();
            var branchOptions = new List<FilterOptionItem> { new() { Value = null, DisplayText = "جميع الفروع" } };
            foreach (var b in branches)
            {
                branchOptions.Add(new FilterOptionItem { Value = b.BranchID, DisplayText = b.BranchName });
            }
            CmbBranch.ItemsSource = branchOptions;
            CmbBranch.SelectedIndex = 0;

            var categories = await _masterService.GetCategoriesAsync();
            var catOptions = new List<FilterOptionItem> { new() { Value = null, DisplayText = "جميع الفئات" } };
            foreach (var c in categories)
            {
                catOptions.Add(new FilterOptionItem { Value = c.CategoryID, DisplayText = c.DisplayFullName });
            }
            CmbCategory.ItemsSource = catOptions;
            CmbCategory.SelectedIndex = 0;

            TxtDebitAccount.Text = AppConfigService.DefaultDebitAccountName;
            ChkRoundTo50.IsChecked = AppConfigService.RoundToNearest50;

            _isInitializing = false;
        }

        private async Task RefreshReportAsync()
        {
            if (_isInitializing || !IsLoaded) return;
            if (CmbYear.SelectedItem is not int year || CmbMonth.SelectedValue is not int month)
                return;

            string debitTitle = string.IsNullOrWhiteSpace(TxtDebitAccount.Text)
                ? AppConfigService.DefaultDebitAccountName
                : TxtDebitAccount.Text.Trim();

            bool round50 = ChkRoundTo50.IsChecked == true;
            bool onlyDeserving = ChkOnlyDeserving.IsChecked == true;
            int? branchId = (CmbBranch.SelectedItem as FilterOptionItem)?.Value;
            int? categoryId = (CmbCategory.SelectedItem as FilterOptionItem)?.Value;
            string search = TxtSearchEmployee.Text.Trim();

            _currentReport = await _reportService.GenerateMonthlyReportAsync(
                year,
                month,
                debitTitle,
                AppConfigService.DefaultMaxIncentiveAmount,
                AppConfigService.DefaultMinIncentiveAmount,
                round50,
                branchId,
                categoryId,
                search,
                onlyDeserving);

            GridAccountingJournal.ItemsSource = _currentReport.Rows;
            GridDetailedBreakdown.ItemsSource = _currentReport.Rows;

            TxtKpiTotalEmployees.Text = $"{_currentReport.TotalEmployeesCount} موظف";
            TxtKpiDeservingCount.Text = $"{_currentReport.DeservingEmployeesCount} مستحق";
            TxtKpiAverageScore.Text = $"{_currentReport.OverallAverageScore:0.##} %";
            TxtKpiTotalPayout.Text = $"{_currentReport.TotalIncentivePayout:N0} ريال";

            UpdateReportLockVisualState();
        }

        private void UpdateReportLockVisualState()
        {
            if (_currentReport == null) return;

            bool isLocked = _currentReport.IsMonthLocked;
            bool isAdmin = AuthService.CurrentSession?.IsAdmin == true;
            bool canEditReport = isAdmin
                || AuthService.CurrentSession?.HasPermission("FinalReport", "update") == true
                || AuthService.CurrentSession?.HasPermission("FinalReport", "insert") == true;

            TxtReportPeriodStatus.Text = string.IsNullOrWhiteSpace(_currentReport.EffectiveConfigPeriodLabel)
                ? "حالة الفترة: مفتوح للاحتساب"
                : _currentReport.EffectiveConfigPeriodLabel;

            if (isLocked)
            {
                BadgeReportPeriodStatus.Background = new SolidColorBrush(Color.FromRgb(254, 242, 242));
                BadgeReportPeriodStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(254, 202, 202));
                TxtReportPeriodStatus.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
            }
            else
            {
                BadgeReportPeriodStatus.Background = new SolidColorBrush(Color.FromRgb(239, 246, 255));
                BadgeReportPeriodStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(191, 219, 254));
                TxtReportPeriodStatus.Foreground = new SolidColorBrush(Color.FromRgb(30, 64, 175));
            }

            BtnLockMonth.Visibility = (!isLocked && canEditReport) ? Visibility.Visible : Visibility.Collapsed;
            BtnUnlockMonth.Visibility = (isLocked && isAdmin) ? Visibility.Visible : Visibility.Collapsed;
            BtnRebuildMonthConfig.Visibility = (!isLocked && canEditReport) ? Visibility.Visible : Visibility.Collapsed;

            TxtDebitAccount.IsReadOnly = isLocked;
            ChkRoundTo50.IsEnabled = !isLocked;
        }

        private async void BtnLockMonth_Click(object sender, RoutedEventArgs e)
        {
            if (CmbYear.SelectedItem is not int year || CmbMonth.SelectedValue is not int month)
                return;

            string mName = IncentiveCalculatorService.GetArabicMonthName(month);
            var confirm = MessageBox.Show(
                $"هل أنت متأكد من اعتماد وإغلاق شهر ({mName} {year} م) نهائياً؟\n\n" +
                "• سيتم حفظ وتجميد نسخة كاملة من القيد المحاسبي ودرجات وحوافز هذا الشهر.\n" +
                "• لن يتأثر هذا الشهر بأي تعديلات لاحقة في المقيمين أو الفئات أو الموظفين.\n" +
                "• سيُمنع تعديل درجات أو حوافز هذا الشهر ما لم يقم مدير النظام بفك الإغلاق.",
                "تأكيد اعتماد وإغلاق الشهر المالي",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            string debitTitle = string.IsNullOrWhiteSpace(TxtDebitAccount.Text)
                ? AppConfigService.DefaultDebitAccountName
                : TxtDebitAccount.Text.Trim();
            bool round50 = ChkRoundTo50.IsChecked == true;

            BtnLockMonth.IsEnabled = false;
            var (ok, msg) = await _reportService.LockMonthReportAsync(
                year,
                month,
                debitTitle,
                AppConfigService.DefaultMaxIncentiveAmount,
                AppConfigService.DefaultMinIncentiveAmount,
                round50);
            BtnLockMonth.IsEnabled = true;

            MessageBox.Show(msg, ok ? "تم اعتماد وإغلاق الشهر" : "تنبيه", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (ok) await RefreshReportAsync();
        }

        private async void BtnUnlockMonth_Click(object sender, RoutedEventArgs e)
        {
            if (CmbYear.SelectedItem is not int year || CmbMonth.SelectedValue is not int month)
                return;

            string mName = IncentiveCalculatorService.GetArabicMonthName(month);
            var confirm = MessageBox.Show(
                $"هل تريد فك إغلاق شهر ({mName} {year} م)؟\nسيعود التقرير للاحتساب الحي وفق أساسيات الشهر وسيُسمح بالتعديل مجدداً.",
                "تأكيد فك إغلاق الشهر",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            BtnUnlockMonth.IsEnabled = false;
            var (ok, msg) = await _reportService.UnlockMonthReportAsync(year, month);
            BtnUnlockMonth.IsEnabled = true;

            MessageBox.Show(msg, ok ? "تم فك إغلاق الشهر" : "تنبيه", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (ok) await RefreshReportAsync();
        }

        private async void BtnRebuildMonthConfig_Click(object sender, RoutedEventArgs e)
        {
            if (CmbYear.SelectedItem is not int year || CmbMonth.SelectedValue is not int month)
                return;

            string mName = IncentiveCalculatorService.GetArabicMonthName(month);
            var confirm = MessageBox.Show(
                $"ستقوم هذه الأداة باسترجاع وبناء أوزان الفئات وإسناد الموظفين لشهر ({mName} {year} م) من التقييمات الشهرية المحفوظة فيه وإعادة احتساب التقرير فوراً.\n\n" +
                "(ممتاز لإنقاذ أشهر 7 و 8 و 9 وإعادة قيودها كما كانت قبل تعديلات شهر 10).\n\nهل تريد المتابعة؟",
                "استرجاع أساسيات الشهر من التقييمات المحفوظة",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            BtnRebuildMonthConfig.IsEnabled = false;
            var (ok, msg) = await _periodService.RebuildMonthConfigFromSavedEvaluationsAsync(year, month);
            BtnRebuildMonthConfig.IsEnabled = true;

            MessageBox.Show(msg, ok ? "تم استرجاع أساسيات الشهر" : "تنبيه", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (ok) await RefreshReportAsync();
        }

        private async void BtnCalculateReport_Click(object sender, RoutedEventArgs e)
        {
            await RefreshReportAsync();
        }

        private async void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await RefreshReportAsync();
        }

        private async void TxtSearchEmployee_TextChanged(object sender, TextChangedEventArgs e)
        {
            await RefreshReportAsync();
        }

        private async void ChkOption_Changed(object sender, RoutedEventArgs e)
        {
            await RefreshReportAsync();
        }

        private async void BtnExportExcel_Click(object sender, RoutedEventArgs e)
        {
            await RefreshReportAsync();
            if (_currentReport == null || _currentReport.Rows.Count == 0)
            {
                MessageBox.Show("لا توجد بيانات في التقرير لتصديرها.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string monthName = IncentiveCalculatorService.GetArabicMonthName(_currentReport.Month);
            var sfd = new SaveFileDialog
            {
                Title = "تصدير القيد المحاسبي والتقرير الشهري إلى Excel",
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                FileName = $"القيد_المحاسبي_وتقرير_الحوافز_{monthName}_{_currentReport.Year}.xlsx"
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    _reportService.ExportMonthlyReportToExcel(_currentReport, sfd.FileName);

                    var openNow = MessageBox.Show(
                        $"تم تصدير ملف الإكسل (ورقة القيد المحاسبي + ورقة تفاصيل التقييم الشهري) بنجاح في المسار:\n{sfd.FileName}\n\nهل تريد فتح ملف الإكسل الآن؟",
                        "تم التصدير إلى Excel",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);

                    if (openNow == MessageBoxResult.Yes)
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = sfd.FileName,
                            UseShellExecute = true
                        });
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"حدث خطأ أثناء تصدير ملف الإكسل:\n{ex.Message}", "خطأ تصدير", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BtnExportPdf_Click(object sender, RoutedEventArgs e)
        {
            await RefreshReportAsync();
            if (_currentReport == null || _currentReport.Rows.Count == 0)
            {
                MessageBox.Show("لا توجد بيانات في التقرير لطباعتها.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string monthName = IncentiveCalculatorService.GetArabicMonthName(_currentReport.Month);
            var sfd = new SaveFileDialog
            {
                Title = "تصدير وطباعة كشف القيد المحاسبي والحوافز بحجم A4",
                Filter = "PDF Document (*.pdf)|*.pdf",
                FileName = $"كشف_صرف_الحوافز_والقيد_{monthName}_{_currentReport.Year}.pdf"
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    _reportService.ExportMonthlyReportToPdf(_currentReport, sfd.FileName);

                    var openNow = MessageBox.Show(
                        $"تم توليد كشف الـ PDF الرسمي بحجم A4 بنجاح في المسار:\n{sfd.FileName}\n\nهل تريد فتح الملف الآن لمعاينته وطباعته؟",
                        "تم توليد كشف PDF",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);

                    if (openNow == MessageBoxResult.Yes)
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = sfd.FileName,
                            UseShellExecute = true
                        });
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"حدث خطأ أثناء توليد ملف الـ PDF:\n{ex.Message}", "خطأ طباعة", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}



