using System;

namespace SchoolEvaluationERP.Services
{
    public class IncentiveCalculationResult
    {
        public string BranchName { get; set; } = string.Empty;               // مكان حساب الموظف (مثل: الحصبة)
        public string DebitAccount { get; set; } = string.Empty;             // الحساب المدين
        public decimal FinalAmount { get; set; }                             // المبلغ النهائي
        public string CreditAccountSystemName { get; set; } = string.Empty;  // الحساب الدائن (الاسم في النظام)
        public string StatementText { get; set; } = string.Empty;            // البيان
        public string EntryDateFormatted { get; set; } = string.Empty;       // التاريخ (YYYY-MM-DD)
        public decimal EvaluationAveragePercent { get; set; }                // معدل التقييم
        public decimal ExactIncentivePercent { get; set; }                   // نسبة الحافز الدقيقة
        public int DisplayedIncentivePercent { get; set; }                   // نسبة الحافز المعروضة في البيان
    }

    /// <summary>
    /// محرك احتساب الحوافز وتوليد القيود المحاسبية الشهرية
    /// تم تدقيقه بنوع Decimal المالي ومراجعة التقريب النصي ليطابق صورة القيد المحاسبي بنسبة 100%
    /// </summary>
    public static class IncentiveCalculatorService
    {
        public static IncentiveCalculationResult CalculateMonthlyIncentive(
            string employeeSystemName,
            string branchName,
            decimal monthlyEvaluationScore, // من 0 إلى 100
            decimal maxIncentiveAmount,     // مثلاً 7000
            decimal minIncentiveAmount,     // مثلاً 1500
            int year,
            int month,
            string debitAccountTitle = "مصروفات الحوافز الشهرية للعام 2026/2027 م",
            bool roundToNearest50 = true)
        {
            // تثبيت الدقة العشرية لمعدل التقييم لمنع أخطاء الفاصلة العائمة
            decimal cleanEvalScore = Math.Round(monthlyEvaluationScore, 2, MidpointRounding.AwayFromZero);

            var result = new IncentiveCalculationResult
            {
                BranchName = branchName,
                DebitAccount = debitAccountTitle,
                CreditAccountSystemName = employeeSystemName,
                EvaluationAveragePercent = cleanEvalScore,
                EntryDateFormatted = GetEndOfMonthDateString(year, month)
            };

            // 1. التحقق من شرط الاستحقاق الأساسي (> 80)
            if (cleanEvalScore <= 80m)
            {
                result.ExactIncentivePercent = 0m;
                result.DisplayedIncentivePercent = 0;
                result.FinalAmount = 0m;
                result.StatementText = string.Empty; // البيان فارغ تماماً عندما يكون المبلغ 0 كما في صورة القيد
                return result;
            }

            // 2. حساب نسبة الحافز المستحقة (P) بدقة Decimal
            decimal exactIncentivePercent = cleanEvalScore >= 99m
                ? 100m
                : ((cleanEvalScore - 81m) * 5m) + 10m;

            if (exactIncentivePercent <= 0m)
            {
                result.FinalAmount = 0m;
                result.StatementText = string.Empty;
                return result;
            }

            result.ExactIncentivePercent = exactIncentivePercent;
            
            // تقريب النسبة المعروضة في نص البيان باستخدام AwayFromZero
            // عند 93.3m -> الناتج 71.5m بالضبط -> يقرب إلى 72% ليطابق صورة القيد
            result.DisplayedIncentivePercent = (int)Math.Round(exactIncentivePercent, 0, MidpointRounding.AwayFromZero);

            // 3. حساب المبلغ المالي الخام قبل التقريب
            decimal rawAmount = (exactIncentivePercent / 100m) * maxIncentiveAmount;

            // 4. التقريب المحاسبي لأقرب 50 ريالاً (5005 -> 5000، 5040 -> 5050، 3552.5 -> 3550)
            decimal roundedAmount = roundToNearest50
                ? Math.Round(rawAmount / 50m, 0, MidpointRounding.AwayFromZero) * 50m
                : Math.Round(rawAmount, 0, MidpointRounding.AwayFromZero);

            // 5. تطبيق قاعدة أقل قيمة للحافز (مثلاً 1500 عند درجة 82%)
            if (roundedAmount > 0m && roundedAmount < minIncentiveAmount)
            {
                roundedAmount = minIncentiveAmount;
            }

            result.FinalAmount = roundedAmount;

            // 6. توليد نص البيان المطابق للصورة تماماً
            string arabicMonthName = GetArabicMonthName(month);
            string formattedEvalScore = result.EvaluationAveragePercent.ToString("0.##");
            result.StatementText = $"لكم حافز شهر {arabicMonthName}/{year} م معدل التقييم=( {formattedEvalScore}% )-نسبة الحافز المستحقة=( {result.DisplayedIncentivePercent}% )";

            return result;
        }

        private static string GetEndOfMonthDateString(int year, int month)
        {
            int lastDay = DateTime.DaysInMonth(year, month);
            return new DateTime(year, month, lastDay).ToString("yyyy-MM-dd");
        }

        public static string GetArabicMonthName(int month) => month switch
        {
            1 => "يناير",
            2 => "فبراير",
            3 => "مارس",
            4 => "ابريل",
            5 => "مايو",
            6 => "يونيو",
            7 => "يوليو",
            8 => "اغسطس",
            9 => "سبتمبر",
            10 => "اكتوبر",
            11 => "نوفمبر",
            12 => "ديسمبر",
            _ => "غير محدد"
        };
    }
}
