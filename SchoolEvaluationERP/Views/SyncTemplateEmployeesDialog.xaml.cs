using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class SyncTemplateEmployeesDialog : Window
    {
        private readonly List<TemplateSyncEmployeeItem> _allAdded;
        private readonly List<TemplateSyncEmployeeItem> _allRemoved;
        private readonly int _currentTotalEmployeesInWorkspace;

        public List<TemplateSyncEmployeeItem> SelectedToAdd { get; private set; } = new();
        public List<TemplateSyncEmployeeItem> SelectedToRemove { get; private set; } = new();

        public SyncTemplateEmployeesDialog(
            WeeklyEvaluationWorkspaceModel workspace,
            TemplateSyncComparisonResult comparison,
            int currentTotalEmployeesInWorkspace)
        {
            InitializeComponent();

            _allAdded = comparison.NewlyAddedToTemplate ?? new List<TemplateSyncEmployeeItem>();
            _allRemoved = comparison.RemovedFromTemplate ?? new List<TemplateSyncEmployeeItem>();
            _currentTotalEmployeesInWorkspace = currentTotalEmployeesInWorkspace;

            string monthName = IncentiveCalculatorService.GetArabicMonthName(workspace.EvalMonth);
            TxtPeriodInfo.Text = $"{workspace.EvaluatorName} — شهر {monthName} {workspace.EvalYear} م";

            foreach (var item in _allAdded)
            {
                item.IsSelected = false;
                item.OnSelectionChanged = UpdateSelectionSummary;
            }

            foreach (var item in _allRemoved)
            {
                item.IsSelected = false;
                item.OnSelectionChanged = UpdateSelectionSummary;
            }

            TxtAddedHeader.Text = $"➕ موظفون مضافون حديثاً في القالب — غير موجودين في هذا الشهر ({_allAdded.Count})";
            TxtRemovedHeader.Text = $"➖ موظفون أُزيلوا من القالب الحالي — ما زالوا موجودين في هذا الشهر ({_allRemoved.Count})";

            EmptyAddedOverlay.Visibility = _allAdded.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnSelectAllAdded.IsEnabled = _allAdded.Count > 0;
            BtnUnselectAllAdded.IsEnabled = _allAdded.Count > 0;

            EmptyRemovedOverlay.Visibility = _allRemoved.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnSelectAllRemoved.IsEnabled = _allRemoved.Count > 0;
            BtnUnselectAllRemoved.IsEnabled = _allRemoved.Count > 0;

            ApplyFilter();
            UpdateSelectionSummary();
        }

        private void ApplyFilter()
        {
            string query = (TxtSearchSync?.Text ?? string.Empty).Trim();

            var filteredAdded = string.IsNullOrEmpty(query)
                ? _allAdded
                : _allAdded.Where(x => MatchesSearch(x, query)).ToList();

            var filteredRemoved = string.IsNullOrEmpty(query)
                ? _allRemoved
                : _allRemoved.Where(x => MatchesSearch(x, query)).ToList();

            GridAddedEmployees.ItemsSource = filteredAdded;
            GridRemovedEmployees.ItemsSource = filteredRemoved;
        }

        private static bool MatchesSearch(TemplateSyncEmployeeItem item, string query)
        {
            return item.EvaluationName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.SheetName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.BranchName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.CategoryName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.FullJobDisplay.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        private void TxtSearchSync_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtSearchSync.Text = string.Empty;
            TxtSearchSync.Focus();
        }

        private void BtnSelectAllAdded_Click(object sender, RoutedEventArgs e)
        {
            if (GridAddedEmployees.ItemsSource is IEnumerable<TemplateSyncEmployeeItem> visible)
            {
                foreach (var item in visible) item.IsSelected = true;
            }
            UpdateSelectionSummary();
        }

        private void BtnUnselectAllAdded_Click(object sender, RoutedEventArgs e)
        {
            if (GridAddedEmployees.ItemsSource is IEnumerable<TemplateSyncEmployeeItem> visible)
            {
                foreach (var item in visible) item.IsSelected = false;
            }
            UpdateSelectionSummary();
        }

        private void BtnSelectAllRemoved_Click(object sender, RoutedEventArgs e)
        {
            if (GridRemovedEmployees.ItemsSource is IEnumerable<TemplateSyncEmployeeItem> visible)
            {
                foreach (var item in visible) item.IsSelected = true;
            }
            UpdateSelectionSummary();
        }

        private void BtnUnselectAllRemoved_Click(object sender, RoutedEventArgs e)
        {
            if (GridRemovedEmployees.ItemsSource is IEnumerable<TemplateSyncEmployeeItem> visible)
            {
                foreach (var item in visible) item.IsSelected = false;
            }
            UpdateSelectionSummary();
        }

        private void UpdateSelectionSummary()
        {
            int addCnt = _allAdded.Count(x => x.IsSelected);
            int remCnt = _allRemoved.Count(x => x.IsSelected);

            TxtSelectionSummary.Text = $"المحدد حالياً: إضافة ({addCnt}) موظف  |  إزالة ({remCnt}) موظف";
            BtnApplySync.IsEnabled = (addCnt + remCnt) > 0;
        }

        private void BtnApplySync_Click(object sender, RoutedEventArgs e)
        {
            var toAdd = _allAdded.Where(x => x.IsSelected).ToList();
            var toRemove = _allRemoved.Where(x => x.IsSelected).ToList();

            if (toAdd.Count == 0 && toRemove.Count == 0)
            {
                MessageBox.Show("يرجى تحديد موظف واحد على الأقل لإضافته أو إزالته.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int projectedTotal = _currentTotalEmployeesInWorkspace + toAdd.Count - toRemove.Count;
            if (projectedTotal <= 0)
            {
                MessageBox.Show(
                    "لا يمكن إزالة جميع موظفي هذا الشهر؛ يجب أن يبقى موظف واحد على الأقل داخل سجل التقييم الشهري.",
                    "تنبيه حماية السجل",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (toRemove.Count > 0)
            {
                var confirm = MessageBox.Show(
                    $"سيتم تطبيق التغييرات التالية على جدول الشهر الحالي:\n• إضافة: ({toAdd.Count}) موظف\n• إزالة: ({toRemove.Count}) موظف (مع حذف درجاتهم المحفوظة في هذا الشهر عند الحفظ)\n\nهل تريد المتابعة؟",
                    "تأكيد المزامنة",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes)
                    return;
            }

            SelectedToAdd = toAdd;
            SelectedToRemove = toRemove;
            DialogResult = true;
            Close();
        }
    }
}
