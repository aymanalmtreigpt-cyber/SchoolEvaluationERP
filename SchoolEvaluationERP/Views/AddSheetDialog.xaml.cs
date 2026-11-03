using System.Collections.Generic;
using System.Windows;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class AddSheetDialog : Window
    {
        private readonly TemplateService _service;
        private readonly int _evaluatorId;

        public string EnteredSheetName { get; private set; } = string.Empty;
        public string SelectedCategoryName { get; private set; } = string.Empty;

        public AddSheetDialog(int evaluatorId, int nextSheetNumber = 1, string initialSheetName = "", string initialCategory = "")
        {
            InitializeComponent();
            _service = new TemplateService();
            _evaluatorId = evaluatorId;

            if (!string.IsNullOrEmpty(initialSheetName))
            {
                TxtDialogTitle.Text = "تعديل اسم وفئة ورقة التقييم";
                BtnConfirmSheet.Content = "💾 حفظ التعديلات";
                TxtSheetName.Text = initialSheetName;
            }
            else
            {
                TxtSheetName.Text = $"ورقة تقييم {nextSheetNumber}";
            }

            Loaded += async (_, _) =>
            {
                List<string> categories = await _service.GetDistinctCategoryNamesForEvaluatorAsync(_evaluatorId);
                CmbCategoryName.ItemsSource = categories;

                if (!string.IsNullOrEmpty(initialCategory) && categories.Contains(initialCategory))
                {
                    CmbCategoryName.SelectedItem = initialCategory;
                }
                else if (categories.Count > 0)
                {
                    CmbCategoryName.SelectedIndex = 0;
                }

                TxtSheetName.Focus();
                TxtSheetName.SelectAll();
            };
        }

        private void BtnConfirmSheet_Click(object sender, RoutedEventArgs e)
        {
            string sName = TxtSheetName.Text.Trim();
            string? cName = CmbCategoryName.SelectedItem as string;

            if (string.IsNullOrWhiteSpace(sName))
            {
                MessageBox.Show("يرجى إدخال تسمية الورقة.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(cName))
            {
                MessageBox.Show("يرجى اختيار فئة الموظفين لهذه الورقة.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            EnteredSheetName = sName;
            SelectedCategoryName = cName;
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
