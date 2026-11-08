using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class IncentiveBasicsView : UserControl
    {
        private readonly IncentiveAndBalancingService _service = new();
        private List<CategoryIncentiveSettingRow> _rows = new();
        private bool _isInitializing;

        public bool CanEditIncentives =>
            AuthService.CurrentSession?.HasPermission("IncentiveBasics", "update") == true ||
            AuthService.CurrentSession?.HasPermission("IncentiveBasics", "insert") == true ||
            AuthService.CurrentSession?.HasPermission("MasterData", "update") == true;

        public IncentiveBasicsView()
        {
            _isInitializing = true;
            InitializeComponent();
            DataContext = this;
            _isInitializing = false;

            Loaded += async (_, _) =>
            {
                ApplyPermissionsToControls();
                InitializePeriodSelectors();
                await LoadCategoryIncentiveRowsAsync();
            };
        }

        private void ApplyPermissionsToControls()
        {
            bool canEdit = CanEditIncentives;
            BtnSaveCategoryIncentives.IsEnabled = canEdit;
            BtnApplyQuickToSelected.IsEnabled = canEdit;
            BtnApplyQuickToAll.IsEnabled = canEdit;
            TxtQuickMax.IsEnabled = canEdit;
            TxtQuickMin.IsEnabled = canEdit;
        }

        private void InitializePeriodSelectors()
        {
            _isInitializing = true;

            int currentYear = DateTime.Now.Year;
            CmbYear.ItemsSource = new List<int> { currentYear - 1, currentYear, currentYear + 1, currentYear + 2 };
            CmbYear.SelectedItem = currentYear;

            var months = MonthOptionItem.GetAllMonths();
            CmbMonth.ItemsSource = months;
            CmbMonth.SelectedValue = DateTime.Now.Month;

            ChkRoundTo50.IsChecked = AppConfigService.RoundToNearest50;
            TxtQuickMax.Text = AppConfigService.DefaultMaxIncentiveAmount.ToString("0.##", CultureInfo.InvariantCulture);
            TxtQuickMin.Text = AppConfigService.DefaultMinIncentiveAmount.ToString("0.##", CultureInfo.InvariantCulture);

            _isInitializing = false;
        }

        private async Task LoadCategoryIncentiveRowsAsync()
        {
            if (_isInitializing || !IsLoaded) return;
            if (CmbYear.SelectedItem is not int year || CmbMonth.SelectedValue is not int month)
                return;

            bool round50 = ChkRoundTo50.IsChecked == true;
            _rows = await _service.GetCategoriesIncentiveRowsAsync(year, month, round50);

            foreach (var r in _rows)
            {
                r.OnRowAmountsChanged = UpdateLiveGrandTotalsKpi;
            }

            GridCategoryIncentives.ItemsSource = _rows;
            UpdateLiveGrandTotalsKpi();
        }

        private void UpdateLiveGrandTotalsKpi()
        {
            if (_rows == null) return;

            int totalCats = _rows.Count;
            int totalEligible = _rows.Sum(r => r.IncentiveEligibleEmployeesCount);
            int totalDeserving = _rows.Sum(r => r.DeservingEmployeesCount);
            decimal grandTotalPayout = _rows.Sum(r => r.CategoryTotalIncentiveAmount);

            TxtKpiTotalCategories.Text = $"{totalCats} فئة";
            TxtKpiEligibleEmployees.Text = $"{totalEligible} موظف";
            TxtKpiDeservingEmployees.Text = $"{totalDeserving} مستحق";
            TxtKpiGrandTotalIncentives.Text = $"{grandTotalPayout:N0} ريال";

            if (CmbYear.SelectedItem is int year && CmbMonth.SelectedValue is int month)
            {
                string mName = IncentiveCalculatorService.GetArabicMonthName(month);
                TxtKpiMonthTitle.Text = $"إجمالي قيمة الحوافز لشهر ({mName} {year} م) — تحديث مباشر:";
            }
        }

        private async void PeriodFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await LoadCategoryIncentiveRowsAsync();
        }

        private void ChkRoundTo50_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializing || _rows == null) return;
            bool round50 = ChkRoundTo50.IsChecked == true;
            foreach (var r in _rows)
            {
                r.SetRoundingMode(round50);
            }
            UpdateLiveGrandTotalsKpi();
        }

        private async void BtnReloadMonthData_Click(object sender, RoutedEventArgs e)
        {
            await LoadCategoryIncentiveRowsAsync();
        }

        private bool TryReadQuickLimits(out decimal maxVal, out decimal minVal)
        {
            maxVal = 0m;
            minVal = 0m;

            string cleanMax = WeeklyScoreRowViewModel.NormalizeAsciiDigits(TxtQuickMax.Text.Trim()).Replace(",", ".");
            string cleanMin = WeeklyScoreRowViewModel.NormalizeAsciiDigits(TxtQuickMin.Text.Trim()).Replace(",", ".");

            if (!decimal.TryParse(cleanMax, NumberStyles.Any, CultureInfo.InvariantCulture, out maxVal) || maxVal < 0m)
            {
                MessageBox.Show("يرجى إدخال رقم صحيح في حقل (حد أعلى موحد سريع).", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (!decimal.TryParse(cleanMin, NumberStyles.Any, CultureInfo.InvariantCulture, out minVal) || minVal < 0m)
            {
                MessageBox.Show("يرجى إدخال رقم صحيح في حقل (حد أدنى موحد سريع).", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (minVal > maxVal && maxVal > 0m)
            {
                MessageBox.Show("الحد الأدنى للحافز لا يمكن أن يكون أكبر من الحد الأعلى.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        private void BtnApplyQuickToSelected_Click(object sender, RoutedEventArgs e)
        {
            if (!CanEditIncentives) return;

            var selectedRows = GridCategoryIncentives.SelectedItems.Cast<CategoryIncentiveSettingRow>().ToList();
            if (selectedRows.Count == 0)
            {
                MessageBox.Show("يرجى تحديد فئة واحدة أو أكثر من الجدول (بالماوس أو Ctrl أو Shift) لتطبيق القيمة عليها.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!TryReadQuickLimits(out decimal maxVal, out decimal minVal))
                return;

            foreach (var r in selectedRows)
            {
                r.MaxIncentiveAmount = maxVal;
                r.MinIncentiveAmount = minVal;
            }

            UpdateLiveGrandTotalsKpi();
        }

        private void BtnApplyQuickToAll_Click(object sender, RoutedEventArgs e)
        {
            if (!CanEditIncentives || _rows.Count == 0) return;

            if (!TryReadQuickLimits(out decimal maxVal, out decimal minVal))
                return;

            foreach (var r in _rows)
            {
                r.MaxIncentiveAmount = maxVal;
                r.MinIncentiveAmount = minVal;
            }

            UpdateLiveGrandTotalsKpi();
        }

        private async void BtnSaveCategoryIncentives_Click(object sender, RoutedEventArgs e)
        {
            if (!CanEditIncentives)
            {
                MessageBox.Show("عذراً، لا تملك صلاحية تعديل أساسيات الحوافز.", "تنبيه صلاحيات", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnSaveCategoryIncentives.IsEnabled = false;
            var (isSuccess, message) = await _service.SaveCategoriesIncentiveSettingsAsync(_rows);
            BtnSaveCategoryIncentives.IsEnabled = true;

            MessageBox.Show(
                message,
                isSuccess ? "تم حفظ حدود الحوافز" : "تنبيه التحقق من البيانات",
                MessageBoxButton.OK,
                isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
    }
}
