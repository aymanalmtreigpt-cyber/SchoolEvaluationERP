using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class FinalReportView : UserControl
    {
        private readonly FinalReportService _reportService = new();
        private readonly MasterDataService _masterService = new();
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
            TxtMaxIncentive.Text = AppConfigService.DefaultMaxIncentiveAmount.ToString("0.##", CultureInfo.InvariantCulture);
            TxtMinIncentive.Text = AppConfigService.DefaultMinIncentiveAmount.ToString("0.##", CultureInfo.InvariantCulture);
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

            if (!decimal.TryParse(TxtMaxIncentive.Text.Trim().Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal maxAmt) || maxAmt < 0m)
            {
                maxAmt = AppConfigService.DefaultMaxIncentiveAmount;
            }

            if (!decimal.TryParse(TxtMinIncentive.Text.Trim().Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal minAmt) || minAmt < 0m)
            {
                minAmt = AppConfigService.DefaultMinIncentiveAmount;
            }

            bool round50 = ChkRoundTo50.IsChecked == true;
            bool onlyDeserving = ChkOnlyDeserving.IsChecked == true;
            int? branchId = (CmbBranch.SelectedItem as FilterOptionItem)?.Value;
            int? categoryId = (CmbCategory.SelectedItem as FilterOptionItem)?.Value;
            string search = TxtSearchEmployee.Text.Trim();

            _currentReport = await _reportService.GenerateMonthlyReportAsync(
                year,
                month,
                debitTitle,
                maxAmt,
                minAmt,
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
