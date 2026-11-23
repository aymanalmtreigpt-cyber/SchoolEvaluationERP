using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

            string cleanInitial = (currentTitle ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            TxtCriteriaTitle.Text = cleanInitial.Length > 80 ? cleanInitial[..80] : cleanInitial;
            UpdateTitleCharCount();
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

        private void TxtCriteriaTitle_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateTitleCharCount();
        }

        private void UpdateTitleCharCount()
        {
            if (TxtTitleCharCount == null || TxtCriteriaTitle == null) return;
            int len = TxtCriteriaTitle.Text.Length;
            TxtTitleCharCount.Text = $"{len} / 80 حرف";
            TxtTitleCharCount.Foreground = len >= 80
                ? new SolidColorBrush(Color.FromRgb(185, 28, 28))
                : (len >= 65
                    ? new SolidColorBrush(Color.FromRgb(180, 83, 9))
                    : new SolidColorBrush(Color.FromRgb(100, 116, 139)));
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string title = string.Join(" ", TxtCriteriaTitle.Text
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));

            if (string.IsNullOrWhiteSpace(title))
            {
                MessageBox.Show("يرجى إدخال تسمية مبدأ التقييم.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (title.Length > 80)
            {
                MessageBox.Show("الحد الأقصى لطول تسمية مبدأ التقييم هو 80 حرفاً فقط.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
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



