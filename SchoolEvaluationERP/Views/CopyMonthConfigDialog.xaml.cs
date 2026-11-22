using System;
using System.Collections.Generic;
using System.Windows;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class CopyMonthConfigDialog : Window
    {
        private readonly MonthlyPeriodConfigService _periodService = new();

        public int DestinationYear { get; private set; }
        public int DestinationMonth { get; private set; }

        public CopyMonthConfigDialog(int currentYear, int currentMonth)
        {
            InitializeComponent();

            int baseYear = DateTime.Now.Year;
            var years = new List<int> { baseYear - 1, baseYear, baseYear + 1, baseYear + 2 };
            if (!years.Contains(currentYear)) years.Add(currentYear);
            years.Sort();

            var months = MonthOptionItem.GetAllMonths();

            CmbSrcYear.ItemsSource = years;
            CmbDstYear.ItemsSource = years;
            CmbSrcMonth.ItemsSource = months;
            CmbDstMonth.ItemsSource = MonthOptionItem.GetAllMonths();

            CmbSrcYear.SelectedItem = currentYear;
            CmbSrcMonth.SelectedValue = currentMonth;

            int nextMonth = currentMonth == 12 ? 1 : currentMonth + 1;
            int nextYear = currentMonth == 12 ? currentYear + 1 : currentYear;
            if (!years.Contains(nextYear)) { years.Add(nextYear); years.Sort(); }

            CmbDstYear.SelectedItem = nextYear;
            CmbDstMonth.SelectedValue = nextMonth;
        }

        private async void BtnExecuteCopy_Click(object sender, RoutedEventArgs e)
        {
            if (CmbSrcYear.SelectedItem is not int srcY || CmbSrcMonth.SelectedValue is not int srcM ||
                CmbDstYear.SelectedItem is not int dstY || CmbDstMonth.SelectedValue is not int dstM)
            {
                MessageBox.Show("يرجى اختيار السنة والشهر للمصدر والهدف.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (srcY == dstY && srcM == dstM)
            {
                MessageBox.Show("الشهر المصدر والشهر الهدف متطابقان؛ يرجى اختيار شهر هدف مختلف.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string srcLabel = $"{IncentiveCalculatorService.GetArabicMonthName(srcM)} {srcY} م";
            string dstLabel = $"{IncentiveCalculatorService.GetArabicMonthName(dstM)} {dstY} م";

            var confirm = MessageBox.Show(
                $"سيتم نسخ الإعدادات المحددة من ({srcLabel}) ولصقها في ({dstLabel})، مما سيستبدل أي تخصيص سابق في ({dstLabel}) فقط دون التأثير على أي شهر آخر.\n\nهل تريد المتابعة؟",
                "تأكيد النسخ واللصق",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            BtnExecuteCopy.IsEnabled = false;
            var (isSuccess, msg) = await _periodService.CopyPeriodConfigAsync(
                srcY, srcM, dstY, dstM,
                ChkCopyWeights.IsChecked == true,
                ChkCopyAssignments.IsChecked == true,
                ChkCopyIncentives.IsChecked == true);
            BtnExecuteCopy.IsEnabled = true;

            MessageBox.Show(msg, isSuccess ? "تم النسخ واللصق بنجاح" : "تنبيه", MessageBoxButton.OK,
                isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (isSuccess)
            {
                DestinationYear = dstY;
                DestinationMonth = dstM;
                DialogResult = true;
                Close();
            }
        }
    }
}
