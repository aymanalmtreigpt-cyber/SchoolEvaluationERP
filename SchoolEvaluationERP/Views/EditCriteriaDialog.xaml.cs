using System;
using System.Globalization;
using System.Windows;

namespace SchoolEvaluationERP.Views
{
    public partial class EditCriteriaDialog : Window
    {
        private readonly decimal _otherColumnsSum;

        public string CriteriaTitleResult { get; private set; } = string.Empty;
        public decimal MaxScoreResult { get; private set; }
        public bool IsDeleteRequested { get; private set; }

        public EditCriteriaDialog(string currentTitle, decimal currentScore, decimal otherColumnsSum, bool canDeleteColumn)
        {
            InitializeComponent();
            _otherColumnsSum = otherColumnsSum;

            TxtCriteriaTitle.Text = currentTitle;
            TxtMaxScore.Text = currentScore.ToString("0.##", CultureInfo.InvariantCulture);
            BtnDeleteCol.Visibility = canDeleteColumn ? Visibility.Visible : Visibility.Collapsed;

            decimal remainingAllowed = Math.Max(0m, 100m - _otherColumnsSum);
            TxtRemainingHint.Text = $"مجموع الأعمدة الأخرى حالياً: ({_otherColumnsSum:0.##}) درجة — المتبقي للوصول إلى 100 هو: ({remainingAllowed:0.##}) درجة.";

            Loaded += (_, _) =>
            {
                TxtCriteriaTitle.Focus();
                TxtCriteriaTitle.SelectAll();
            };
        }

        private void BtnUseRemaining_Click(object sender, RoutedEventArgs e)
        {
            decimal remaining = Math.Max(0m, 100m - _otherColumnsSum);
            TxtMaxScore.Text = remaining.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string title = TxtCriteriaTitle.Text.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                MessageBox.Show("يرجى إدخال تسمية مبدأ التقييم.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string normalizedScore = TxtMaxScore.Text.Trim().Replace(',', '.');
            if (!decimal.TryParse(normalizedScore, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal score) || score < 0m || score > 100m)
            {
                MessageBox.Show("يرجى إدخال درجة صحيحة بين 0 و 100.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            CriteriaTitleResult = title;
            MaxScoreResult = score;
            IsDeleteRequested = false;
            DialogResult = true;
            Close();
        }

        private void BtnDeleteCol_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show("هل تريد حذف هذا العمود من ورقة التقييم؟", "تأكيد حذف العمود", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm == MessageBoxResult.Yes)
            {
                IsDeleteRequested = true;
                DialogResult = true;
                Close();
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
