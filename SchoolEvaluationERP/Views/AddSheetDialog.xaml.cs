using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class AddSheetDialog : Window
    {
        private readonly TemplateService _service = new();
        private readonly int _evaluatorId;
        private readonly HashSet<int> _excludedCategoryIds;
        private readonly int? _evalYear;
        private readonly int? _evalMonth;

        public string EnteredSheetName { get; private set; } = string.Empty;
        public int SelectedCategoryId { get; private set; }
        public string SelectedCategoryName { get; private set; } = string.Empty;
        public int? SelectedBranchId { get; private set; }
        public string SelectedBranchName { get; private set; } = "جميع الفروع (كلاهما)";

        public AddSheetDialog(
            int evaluatorId,
            IEnumerable<int>? existingCategoryIds = null,
            int? evalYear = null,
            int? evalMonth = null)
        {
            _evaluatorId = evaluatorId;
            _excludedCategoryIds = existingCategoryIds != null
                ? new HashSet<int>(existingCategoryIds)
                : new HashSet<int>();
            _evalYear = evalYear;
            _evalMonth = evalMonth;

            InitializeComponent();

            Loaded += async (_, _) =>
            {
                List<TemplateCategoryOptionDto> categories = await _service.GetCategoriesForEvaluatorAsync(_evaluatorId, _evalYear, _evalMonth);
                var available = categories.Where(c => !_excludedCategoryIds.Contains(c.CategoryID)).ToList();

                CmbCategoryName.ItemsSource = available;
                if (available.Count > 0)
                {
                    CmbCategoryName.SelectedIndex = 0;
                }
                else
                {
                    BtnConfirmSheet.IsEnabled = false;
                    MessageBox.Show(
                        "جميع الفئات المخصصة لهذا المقيم مضافة بالفعل داخل هذا القالب.",
                        "لا توجد فئات متبقية",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    DialogResult = false;
                    Close();
                }
            };
        }

        private void BtnConfirmSheet_Click(object sender, RoutedEventArgs e)
        {
            var selectedCat = CmbCategoryName.SelectedItem as TemplateCategoryOptionDto;
            if (selectedCat == null)
            {
                MessageBox.Show("لا توجد فئة متاحة لهذا المقيم. يرجى تخصيص درجات الفئة في صفحة الأساسيات أولاً.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            EnteredSheetName = selectedCat.CategoryName;
            SelectedCategoryId = selectedCat.CategoryID;
            SelectedCategoryName = selectedCat.CategoryName;
            SelectedBranchId = null;
            SelectedBranchName = "جميع الفروع (كلاهما)";
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
