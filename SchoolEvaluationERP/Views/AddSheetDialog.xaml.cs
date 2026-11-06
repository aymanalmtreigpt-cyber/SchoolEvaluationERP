using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class AddSheetDialog : Window
    {
        private readonly TemplateService _service = new();
        private readonly int _evaluatorId;
        private readonly int? _initialCategoryId;
        private readonly string _initialCategoryName;
        private readonly int? _initialBranchId;
        private bool _isLoading;

        public string EnteredSheetName { get; private set; } = string.Empty;
        public int SelectedCategoryId { get; private set; }
        public string SelectedCategoryName { get; private set; } = string.Empty;
        public int? SelectedBranchId { get; private set; }
        public string SelectedBranchName { get; private set; } = "جميع الفروع (كلاهما)";

        public AddSheetDialog(
            int evaluatorId,
            int nextSheetNumber = 1,
            string initialSheetName = "",
            int? initialCategoryId = null,
            string initialCategoryName = "",
            int? initialBranchId = null)
        {
            _evaluatorId = evaluatorId;
            _initialCategoryId = initialCategoryId;
            _initialCategoryName = initialCategoryName;
            _initialBranchId = (initialBranchId.HasValue && initialBranchId.Value > 0) ? initialBranchId.Value : null;

            _isLoading = true;
            InitializeComponent();
            _isLoading = false;

            if (!string.IsNullOrEmpty(initialSheetName))
            {
                TxtDialogTitle.Text = "تعديل اسم وفئة وفرع ورقة التقييم";
                BtnConfirmSheet.Content = "💾 حفظ التعديلات";
                TxtSheetName.Text = initialSheetName;
            }
            else
            {
                TxtSheetName.Text = $"ورقة تقييم {nextSheetNumber}";
            }

            Loaded += async (_, _) =>
            {
                _isLoading = true;
                List<TemplateCategoryOptionDto> categories = await _service.GetCategoriesForEvaluatorAsync(_evaluatorId);
                CmbCategoryName.ItemsSource = categories;

                TemplateCategoryOptionDto? match = null;
                if (_initialCategoryId.HasValue && _initialCategoryId.Value > 0)
                {
                    match = categories.FirstOrDefault(c => c.CategoryID == _initialCategoryId.Value);
                }

                if (match == null && !string.IsNullOrWhiteSpace(_initialCategoryName))
                {
                    match = categories.FirstOrDefault(c =>
                        string.Equals(c.CategoryName, _initialCategoryName, StringComparison.OrdinalIgnoreCase));
                }

                if (match != null)
                {
                    CmbCategoryName.SelectedItem = match;
                }
                else if (categories.Count > 0)
                {
                    CmbCategoryName.SelectedIndex = 0;
                }

                _isLoading = false;

                if (CmbCategoryName.SelectedItem is TemplateCategoryOptionDto activeCat)
                {
                    await PopulateBranchesForCategoryAsync(activeCat.CategoryID, _initialBranchId);
                }

                TxtSheetName.Focus();
                TxtSheetName.SelectAll();
            };
        }

        private async void CmbCategoryName_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (CmbCategoryName.SelectedItem is TemplateCategoryOptionDto selectedCat)
            {
                await PopulateBranchesForCategoryAsync(selectedCat.CategoryID, null);
            }
        }

        private async Task PopulateBranchesForCategoryAsync(int categoryId, int? preselectBranchId)
        {
            var branchOptions = await _service.GetBranchOptionsForEvaluatorCategoryAsync(_evaluatorId, categoryId);
            CmbBranchFilter.ItemsSource = branchOptions;

            if (preselectBranchId.HasValue && preselectBranchId.Value > 0)
            {
                var found = branchOptions.FirstOrDefault(b => b.BranchID == preselectBranchId.Value);
                CmbBranchFilter.SelectedItem = found ?? branchOptions.FirstOrDefault();
            }
            else
            {
                // الخيار الأول افتراضياً هو: جميع الفروع (كلاهما)
                CmbBranchFilter.SelectedIndex = branchOptions.Count > 0 ? 0 : -1;
            }
        }

        private void BtnConfirmSheet_Click(object sender, RoutedEventArgs e)
        {
            string sName = TxtSheetName.Text.Trim();
            var selectedCat = CmbCategoryName.SelectedItem as TemplateCategoryOptionDto;
            var selectedBranch = CmbBranchFilter.SelectedItem as TemplateBranchOptionDto;

            if (string.IsNullOrWhiteSpace(sName))
            {
                MessageBox.Show("يرجى إدخال تسمية الورقة.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (selectedCat == null)
            {
                MessageBox.Show("لا توجد فئة مخصصة لهذا المقيم. يرجى تخصيص درجات الفئة في صفحة الأساسيات أولاً.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            EnteredSheetName = sName;
            SelectedCategoryId = selectedCat.CategoryID;
            SelectedCategoryName = selectedCat.CategoryName;
            SelectedBranchId = selectedBranch?.BranchID;
            SelectedBranchName = selectedBranch?.BranchName ?? "جميع الفروع (كلاهما)";
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
