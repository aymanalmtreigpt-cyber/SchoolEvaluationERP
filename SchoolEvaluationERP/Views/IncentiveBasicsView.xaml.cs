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
        private readonly MasterDataService _masterDataService = new();
        private List<CategoryIncentiveSettingRow> _rows = new();
        private bool _isInitializing;

        private static Dictionary<int, (decimal MaxAmt, decimal MinAmt)>? _copiedIncentiveLimits;
        private static string _copiedSourcePeriodLabel = string.Empty;

        public bool CanEditIncentives =>
            AuthService.CurrentSession?.HasPermission("IncentiveBasics", "update") == true ||
            AuthService.CurrentSession?.HasPermission("IncentiveBasics", "insert") == true ||
            AuthService.CurrentSession?.HasPermission("MasterData", "update") == true;

        public bool CanAllocateCategoryWeights =>
            AuthService.CurrentSession?.HasPermission("MasterData", "update") == true ||
            AuthService.CurrentSession?.HasPermission("MasterData", "insert") == true;

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
            if (BtnAllocateCategoryWeights != null)
            {
                BtnAllocateCategoryWeights.Visibility = CanAllocateCategoryWeights ? Visibility.Visible : Visibility.Collapsed;
            }
            UpdateCopyPasteButtonsState();
        }

        private void UpdateCopyPasteButtonsState()
        {
            if (BtnCopyIncentiveLimits == null || BtnPasteIncentiveLimits == null) return;

            bool hasRows = _rows != null && _rows.Count > 0;
            bool hasCopied = _copiedIncentiveLimits != null && _copiedIncentiveLimits.Count > 0;

            BtnCopyIncentiveLimits.IsEnabled = hasRows;
            BtnPasteIncentiveLimits.IsEnabled = CanEditIncentives && hasRows && hasCopied;

            BtnPasteIncentiveLimits.ToolTip = hasCopied
                ? $"لصق قيم الحوافز المنسوخة من شهر ({_copiedSourcePeriodLabel}) على الشهر المعروض حالياً"
                : "انسخ قيم الحوافز من أي شهر أولاً لتفعيل زر اللصق";
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
            TxtQuickMax.Text = "0";
            TxtQuickMin.Text = "0";

            _isInitializing = false;
        }

        private async Task LoadCategoryIncentiveRowsAsync(
            Dictionary<int, (decimal MaxAmt, decimal MinAmt)>? preservedLimits = null,
            int? selectCategoryId = null)
        {
            if (_isInitializing || !IsLoaded) return;
            if (CmbYear.SelectedItem is not int year || CmbMonth.SelectedValue is not int month)
                return;

            bool round50 = ChkRoundTo50.IsChecked == true;
            _rows = await _service.GetCategoriesIncentiveRowsAsync(year, month, round50);

            if (preservedLimits != null && preservedLimits.Count > 0)
            {
                foreach (var r in _rows)
                {
                    if (preservedLimits.TryGetValue(r.CategoryID, out var lim))
                    {
                        r.MaxIncentiveAmount = lim.MaxAmt;
                        r.MinIncentiveAmount = lim.MinAmt;
                    }
                }
            }

            foreach (var r in _rows)
            {
                r.OnRowAmountsChanged = UpdateLiveGrandTotalsKpi;
            }

            if (preservedLimits == null)
            {
                if (_rows.Count > 0)
                {
                    TxtQuickMax.Text = _rows[0].MaxIncentiveText;
                    TxtQuickMin.Text = _rows[0].MinIncentiveText;
                }
                else
                {
                    TxtQuickMax.Text = "0";
                    TxtQuickMin.Text = "0";
                }
            }

            GridCategoryIncentives.ItemsSource = null;
            GridCategoryIncentives.ItemsSource = _rows;

            if (selectCategoryId.HasValue && selectCategoryId.Value > 0)
            {
                var match = _rows.FirstOrDefault(r => r.CategoryID == selectCategoryId.Value);
                if (match != null)
                {
                    GridCategoryIncentives.SelectedItem = match;
                    GridCategoryIncentives.ScrollIntoView(match);
                }
            }

            UpdateLiveGrandTotalsKpi();
            UpdateCopyPasteButtonsState();
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

        private void BtnCopyIncentiveLimits_Click(object sender, RoutedEventArgs e)
        {
            if (_rows == null || _rows.Count == 0)
            {
                MessageBox.Show("لا توجد فئات لنسخ قيم الحوافز منها.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            GridCategoryIncentives.CommitEdit(DataGridEditingUnit.Cell, true);
            GridCategoryIncentives.CommitEdit(DataGridEditingUnit.Row, true);

            _copiedIncentiveLimits = _rows.ToDictionary(
                r => r.CategoryID,
                r => (MaxAmt: r.MaxIncentiveAmount, MinAmt: r.MinIncentiveAmount));

            if (CmbYear.SelectedItem is int year && CmbMonth.SelectedValue is int month)
            {
                string mName = IncentiveCalculatorService.GetArabicMonthName(month);
                _copiedSourcePeriodLabel = $"{mName} {year} م";
            }
            else
            {
                _copiedSourcePeriodLabel = "الشهر المحدد";
            }

            UpdateCopyPasteButtonsState();

            MessageBox.Show(
                $"✔ تم نسخ قيم حدود الحوافز (الحد الأعلى والأدنى) لـ ({_copiedIncentiveLimits.Count}) فئة من شهر ({_copiedSourcePeriodLabel}).\n\n" +
                "انتقل الآن إلى الشهر الآخر واضغط (📥 لصق قيم الحوافز) ثم (💾 حفظ واعتماد حدود الحوافز للفئات).",
                "تم نسخ قيم الحوافز",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void BtnPasteIncentiveLimits_Click(object sender, RoutedEventArgs e)
        {
            if (!CanEditIncentives)
            {
                MessageBox.Show("عذراً، لا تملك صلاحية تعديل أساسيات الحوافز.", "تنبيه صلاحيات", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_copiedIncentiveLimits == null || _copiedIncentiveLimits.Count == 0)
            {
                MessageBox.Show("يرجى نسخ قيم الحوافز من شهر آخر أولاً.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_rows == null || _rows.Count == 0)
                return;

            GridCategoryIncentives.CommitEdit(DataGridEditingUnit.Cell, true);
            GridCategoryIncentives.CommitEdit(DataGridEditingUnit.Row, true);

            int pastedCount = 0;
            foreach (var r in _rows)
            {
                if (_copiedIncentiveLimits.TryGetValue(r.CategoryID, out var lim))
                {
                    r.MaxIncentiveAmount = lim.MaxAmt;
                    r.MinIncentiveAmount = lim.MinAmt;
                    pastedCount++;
                }
            }

            if (_rows.Count > 0)
            {
                TxtQuickMax.Text = _rows[0].MaxIncentiveText;
                TxtQuickMin.Text = _rows[0].MinIncentiveText;
            }

            GridCategoryIncentives.Items.Refresh();
            UpdateLiveGrandTotalsKpi();

            string targetLabel = (CmbYear.SelectedItem is int y && CmbMonth.SelectedValue is int m)
                ? $"{IncentiveCalculatorService.GetArabicMonthName(m)} {y} م"
                : "الشهر الحالي";

            MessageBox.Show(
                $"✔ تم لصق قيم حدود الحوافز لـ ({pastedCount}) فئة من شهر ({_copiedSourcePeriodLabel}) إلى شهر ({targetLabel}).\n\n" +
                "راجع الإجمالي المباشر ثم اضغط (💾 حفظ واعتماد حدود الحوافز للفئات) لاعتمادها في قاعدة البيانات.",
                "تم لصق قيم الحوافز",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private async void BtnAllocateCategoryWeights_Click(object sender, RoutedEventArgs e)
        {
            if (!CanAllocateCategoryWeights)
            {
                MessageBox.Show("عذراً، لا تملك صلاحية تعديل توزيع درجات المقيمين في (1. صفحة الأساسيات).", "تنبيه صلاحيات", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (CmbYear.SelectedItem is not int year || CmbMonth.SelectedValue is not int month)
            {
                MessageBox.Show("يرجى اختيار السنة والشهر أولاً.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            GridCategoryIncentives.CommitEdit(DataGridEditingUnit.Cell, true);
            GridCategoryIncentives.CommitEdit(DataGridEditingUnit.Row, true);

            BtnAllocateCategoryWeights.IsEnabled = false;
            try
            {
                var groups = await _masterDataService.GetEvaluatorGroupsAsync();
                if (groups.Count == 0)
                {
                    MessageBox.Show("يرجى أولاً إضافة مجموعة مقيمين واحدة على الأقل في (1. صفحة الأساسيات) قبل تخصيص الدرجات للفئات.", "تنبيه خطوة سابقة", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                int initialCatId = (GridCategoryIncentives.SelectedItems.Cast<CategoryIncentiveSettingRow>().LastOrDefault()
                                    ?? GridCategoryIncentives.SelectedItem as CategoryIncentiveSettingRow)?.CategoryID ?? 0;

                var unsavedLimitsSnapshot = _rows?.ToDictionary(
                    r => r.CategoryID,
                    r => (MaxAmt: r.MaxIncentiveAmount, MinAmt: r.MinIncentiveAmount));

                var dlg = new CategoryDialog(initialCatId, year, month)
                {
                    Owner = Window.GetWindow(this)
                };
                dlg.ShowDialog();

                if (dlg.HasSavedChanges)
                {
                    await LoadCategoryIncentiveRowsAsync(
                        preservedLimits: unsavedLimitsSnapshot,
                        selectCategoryId: initialCatId);
                }
            }
            finally
            {
                BtnAllocateCategoryWeights.IsEnabled = true;
            }
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

            if (CmbYear.SelectedItem is not int year || CmbMonth.SelectedValue is not int month)
            {
                MessageBox.Show("يرجى اختيار السنة والشهر أولاً.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnSaveCategoryIncentives.IsEnabled = false;
            var (isSuccess, message) = await _service.SaveCategoriesIncentiveSettingsAsync(year, month, _rows);
            BtnSaveCategoryIncentives.IsEnabled = true;

            MessageBox.Show(
                message,
                isSuccess ? "تم حفظ حدود الحوافز" : "تنبيه التحقق من البيانات",
                MessageBoxButton.OK,
                isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
    }
}









