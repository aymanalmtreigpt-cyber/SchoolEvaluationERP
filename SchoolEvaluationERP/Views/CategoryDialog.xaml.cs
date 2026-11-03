using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class CategoryDialog : Window
    {
        private readonly MasterDataService _service;
        private readonly int _editingCategoryId;
        private readonly int? _preselectedBranchId;
        private readonly string _initialCategoryName;
        private List<CategoryEvaluatorSelectRow> _evaluatorRows = new();

        public CategoryDialog(int editingCategoryId = 0, string initialName = "", int? branchId = null)
        {
            InitializeComponent();
            _service = new MasterDataService();
            _editingCategoryId = editingCategoryId;
            _initialCategoryName = initialName;
            _preselectedBranchId = branchId;

            if (_editingCategoryId > 0)
            {
                TxtDialogTitle.Text = $"تعديل الفئة وأوزان المقيمين: {initialName}";
            }

            Loaded += async (_, _) =>
            {
                TxtCategoryName.Text = _initialCategoryName;
                var branches = await _service.GetBranchesAsync();
                CmbBranch.ItemsSource = branches;

                if (_preselectedBranchId.HasValue)
                    CmbBranch.SelectedValue = _preselectedBranchId.Value;
                else if (branches.Count > 0)
                    CmbBranch.SelectedIndex = 0;

                _evaluatorRows = await _service.GetCategoryEvaluatorMatrixAsync(_editingCategoryId);
                foreach (var row in _evaluatorRows)
                {
                    row.PropertyChanged += EvaluatorRow_PropertyChanged;
                }

                GridEvaluators.ItemsSource = _evaluatorRows;
                UpdateLiveTotalCounter();
                TxtCategoryName.Focus();
            };
        }

        private void EvaluatorRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            UpdateLiveTotalCounter();
        }

        private void UpdateLiveTotalCounter()
        {
            decimal total = _evaluatorRows.Where(x => x.IsSelected).Sum(x => x.WeightScore);
            decimal remaining = 100m - total;

            if (total == 100m)
            {
                CounterBanner.Background = new SolidColorBrush(Color.FromRgb(236, 253, 245));
                CounterBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(167, 243, 208));
                TxtTotalScoreStatus.Foreground = new SolidColorBrush(Color.FromRgb(4, 120, 87));
                TxtTotalScoreStatus.Text = "✔ المجموع مكتمل: 100 من 100 درجة (جاهز للحفظ)";
            }
            else if (total < 100m)
            {
                CounterBanner.Background = new SolidColorBrush(Color.FromRgb(255, 251, 235));
                CounterBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(253, 230, 138));
                TxtTotalScoreStatus.Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9));
                TxtTotalScoreStatus.Text = $"مجموع الدرجات الحالي: {total:0.##} من 100  |  المتبقي: {remaining:0.##} درجة";
            }
            else
            {
                CounterBanner.Background = new SolidColorBrush(Color.FromRgb(254, 242, 242));
                CounterBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(254, 202, 202));
                TxtTotalScoreStatus.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                TxtTotalScoreStatus.Text = $"⚠ تجاوز الحد المسموح! المجموع الحالي: {total:0.##} من 100 (الزيادة: {Math.Abs(remaining):0.##})";
            }
        }

        private void BtnDistributeEqually_Click(object sender, RoutedEventArgs e)
        {
            var selected = _evaluatorRows.Where(x => x.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("يرجى وضع علامة صح (اختيار) أمام المقيمين الذين تريد توزيع الـ 100 درجة عليهم أولاً.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            decimal baseShare = Math.Floor((100m / selected.Count) * 100m) / 100m;
            decimal distributed = 0m;

            for (int i = 0; i < selected.Count; i++)
            {
                if (i == selected.Count - 1)
                {
                    selected[i].WeightScore = 100m - distributed;
                }
                else
                {
                    selected[i].WeightScore = baseShare;
                    distributed += baseShare;
                }
            }

            GridEvaluators.Items.Refresh();
            UpdateLiveTotalCounter();
        }

        private async void BtnSaveCategory_Click(object sender, RoutedEventArgs e)
        {
            if (CmbBranch.SelectedValue is not int branchId || branchId <= 0)
            {
                MessageBox.Show("يرجى اختيار الفرع أولاً.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnSaveCategory.IsEnabled = false;
            var (isSuccess, message) = await _service.SaveCategoryWithEvaluatorsAsync(
                _editingCategoryId,
                TxtCategoryName.Text,
                branchId,
                _evaluatorRows);
            BtnSaveCategory.IsEnabled = true;

            if (!isSuccess)
            {
                MessageBox.Show(message, "تنبيه التحقق من البيانات", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(message, "نجاح العملية", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
