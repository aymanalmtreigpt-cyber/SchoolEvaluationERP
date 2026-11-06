using System;
using System.Collections.Generic;
using System.Linq;

namespace SchoolEvaluationERP.Models
{
    public class EmployeeMonthlyReportRowDto
    {
        public int RowNumber { get; set; }
        public int EmployeeID { get; set; }
        public string SystemName { get; set; } = string.Empty;
        public string EvaluationName { get; set; } = string.Empty;
        public int BranchID { get; set; }
        public string BranchName { get; set; } = string.Empty;
        public int DepartmentID { get; set; }
        public string DepartmentName { get; set; } = string.Empty;
        public int CategoryID { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public string JobTitle { get; set; } = string.Empty;
        public string JobTypeName { get; set; } = string.Empty;
        public bool HasIncentive { get; set; } = true;
        public bool IsContinuing { get; set; } = true;

        public string EvaluatorsBreakdownSummary { get; set; } = string.Empty;
        public bool IsAllEvaluatorsCompleted { get; set; }

        public decimal MonthlyEvaluationAverage { get; set; }
        public decimal ExactIncentivePercent { get; set; }
        public int DisplayedIncentivePercent { get; set; }
        public decimal FinalIncentiveAmount { get; set; }
        public string DebitAccount { get; set; } = string.Empty;
        public string CreditAccountSystemName { get; set; } = string.Empty;
        public string StatementText { get; set; } = string.Empty;
        public string EntryDateFormatted { get; set; } = string.Empty;

        public string FullJobDisplay => string.IsNullOrWhiteSpace(JobTypeName)
            ? JobTitle
            : $"{JobTitle} ({JobTypeName})";

        public string HasIncentiveDisplay => HasIncentive ? "بحافز ✔" : "بدون حافز (أداء فقط)";
        public string CompletionStatusDisplay => IsAllEvaluatorsCompleted ? "مكتمل ✔" : "غير مكتمل ⚠";
        public string MonthlyAverageDisplay => $"{MonthlyEvaluationAverage:0.##}%";
        public string IncentivePercentDisplay => HasIncentive ? $"{DisplayedIncentivePercent}%" : "بدون حافز";
        public string FinalAmountDisplay => $"{FinalIncentiveAmount:N0}";
    }

    public class MonthlyFinalReportSummary
    {
        public int Year { get; set; }
        public int Month { get; set; }
        public string DebitAccountTitle { get; set; } = string.Empty;
        public decimal MaxIncentiveAmount { get; set; }
        public decimal MinIncentiveAmount { get; set; }
        public bool RoundToNearest50 { get; set; }

        public List<EmployeeMonthlyReportRowDto> Rows { get; set; } = new();

        public int TotalEmployeesCount => Rows.Count;
        public int DeservingEmployeesCount => Rows.FindAll(r => r.FinalIncentiveAmount > 0m).Count;
        public decimal TotalIncentivePayout => Rows.Count == 0 ? 0m : Rows.Sum(r => r.FinalIncentiveAmount);
        public decimal OverallAverageScore => Rows.Count == 0 ? 0m : Math.Round(Rows.Average(r => r.MonthlyEvaluationAverage), 2, MidpointRounding.AwayFromZero);
    }
}
