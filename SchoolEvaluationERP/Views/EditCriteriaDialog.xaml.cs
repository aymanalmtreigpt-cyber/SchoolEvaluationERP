using System;
using System.Globalization;
using System.Windows;
using SchoolEvaluationERP.Models;

namespace SchoolEvaluationERP.Views
{
    public partial class EditCriteriaDialog : Window
    {
        private readonly int _otherColumnsSum;

        public string CriteriaTitleResult { get; private set; } = string.Empty;
        public decimal MaxScoreResult { get; private set; }
        public bool IsDeleteRequested { get; private set; }

        public EditCriteriaDialog(string currentTitle, decimal currentScore, decimal otherColumnsSum, bool canDeleteColumn)
        {
            InitializeComponent();
            _otherColumnsSum = (int)Math.Round(otherColumnsSum, 0, MidpointRounding.AwayFromZero);

            TxtCriteriaTitle.Text = currentTitle;
            TxtMaxScore.Text = Math.Round(currentScore, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
            BtnDeleteCol.Visibility = canDeleteColumn ? Visibility.Visible : Visibility.Collapsed;

            int remainingAllowed = Math.Max(0, 100 - _otherColumnsSum);
            TxtRemainingHint.Text = $"مجموع الأعمدة الأخرى حالياً: ({_otherColumnsSum}) درجة — المتبقي للوصول إلى 100 هو: ({remainingAllowed}) درجة.";

            Loaded += (_, _) =>
            {
                TxtCriteriaTitle.Focus();
                TxtCriteriaTitle.SelectAll();
            };
        }

        private void BtnUseRemaining_Click(object sender, RoutedEventArgs e)
        {
            int remaining = Math.Max(0, 100 - _otherColumnsSum);
            TxtMaxScore.Text = remaining.ToString(CultureInfo.InvariantCulture);
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string title = TxtCriteriaTitle.Text.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                MessageBox.Show("يرجى إدخال تسمية مبدأ التقييم.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string normalizedScore = WeeklyScoreRowViewModel.NormalizeAsciiDigits(TxtMaxScore.Text.Trim());
            if (!int.TryParse(normalizedScore, NumberStyles.Integer, CultureInfo.InvariantCulture, out int score) || score < 0 || score > 100)
            {
                MessageBox.Show("يرجى إدخال درجة صحيحة بدون كسور بين 0 و 100.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
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
