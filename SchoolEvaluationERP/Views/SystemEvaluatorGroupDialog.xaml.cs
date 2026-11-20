using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class SystemEvaluatorGroupDialog : Window
    {
        private readonly MasterDataService _service = new();
        private readonly EvaluatorGroupDto _group;

        public SystemEvaluatorGroupDialog(EvaluatorGroupDto group)
        {
            InitializeComponent();
            _group = group;

            bool isAbsence = _group.SystemGroupCode == "ABSENCE" || _group.GroupName.Contains("غياب");

            TxtDialogTitle.Text = isAbsence
                ? "⚙️ إعدادات مجموعة (الغياب) الافتراضية"
                : "⚙️ إعدادات مجموعة (التأخير) الافتراضية";

            TxtGroupNameReadOnly.Text = _group.GroupName;

            LblMaxAllowedCount.Text = isAbsence
                ? "عدد أيام الغياب المسموح بها في الشهر (يمكنك إدخال 0 إذا كان غياب يوم واحد يجعل الدرجة صفراً):"
                : "عدد مرات التأخير المسموح بها في الشهر (يمكنك إدخال 0 إذا كان تأخير مرة واحدة يجعل الدرجة صفراً):";

            TxtMaxAllowedCount.Text = (_group.MaxAllowedCount >= 0 ? _group.MaxAllowedCount : 0).ToString(CultureInfo.InvariantCulture);
            UpdateLiveFormulaPreview();

            Loaded += (_, _) =>
            {
                TxtMaxAllowedCount.Focus();
                TxtMaxAllowedCount.SelectAll();
            };
        }

        private void TxtMaxAllowedCount_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateLiveFormulaPreview();
        }

        private void UpdateLiveFormulaPreview()
        {
            if (TxtFormulaPreview == null) return;

            string raw = WeeklyScoreRowViewModel.NormalizeAsciiDigits(TxtMaxAllowedCount.Text.Trim());
            if (!int.TryParse(raw, out int maxAllowed) || maxAllowed < 0)
            {
                TxtFormulaPreview.Text = "يرجى إدخال عدد صحيح (0 أو أكثر).";
                return;
            }

            string unit = (_group.SystemGroupCode == "ABSENCE" || _group.GroupName.Contains("غياب")) ? "يوم" : "مرة";

            if (maxAllowed == 0)
            {
                TxtFormulaPreview.Text = $"• 0 {unit} = 100%   |   • 1 {unit} فأكثر = 0% (صفر — بدون فترة سماح)";
                return;
            }

            var parts = new List<string> { $"• 0 {unit} = 100%" };

            int previewLimit = Math.Min(maxAllowed, 6);
            for (int c = 1; c <= previewLimit; c++)
            {
                decimal pct = WeeklyScoreRowViewModel.CalculateGradualScoreFromCount(c, maxAllowed);
                parts.Add($"• {c} = {pct:0.##}%");
            }
            if (maxAllowed > 6)
            {
                decimal lastPct = WeeklyScoreRowViewModel.CalculateGradualScoreFromCount(maxAllowed, maxAllowed);
                parts.Add($"... • {maxAllowed} = {lastPct:0.##}%");
            }

            parts.Add($"• {maxAllowed + 1} فأكثر = 0% (صفر)");
            TxtFormulaPreview.Text = string.Join("   |   ", parts);
        }

        private async void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            string raw = WeeklyScoreRowViewModel.NormalizeAsciiDigits(TxtMaxAllowedCount.Text.Trim());
            if (!int.TryParse(raw, out int maxAllowed) || maxAllowed < 0 || maxAllowed > 60)
            {
                MessageBox.Show("يرجى إدخال عدد صحيح للحد المسموح به (من 0 إلى 60).", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtMaxAllowedCount.Focus();
                TxtMaxAllowedCount.SelectAll();
                return;
            }

            BtnSaveSettings.IsEnabled = false;
            var (isSuccess, msg) = await _service.SaveSystemEvaluatorGroupSettingsAsync(_group.GroupID, maxAllowed);
            BtnSaveSettings.IsEnabled = true;

            if (!isSuccess)
            {
                MessageBox.Show(msg, "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(msg, "تم الحفظ", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
    }
}
