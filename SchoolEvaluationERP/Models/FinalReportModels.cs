using System;
using System.Collections.Generic;

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
        public string JobTitle { get; set; } = string.Empty;
        public int CategoryID { get; set; }
        public string CategoryName { get; set; } = string.Empty;

        public decimal? Week1Score { get; set; }
        public decimal? Week2Score { get; set; }
        public decimal? Week3Score { get; set; }
        public decimal? Week4Score { get; set; }

        public int ActualWeeksCount { get; set; }
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

        public string Week1Display => Week1Score.HasValue ? $"{Week1Score.Value:0.##}" : "—";
        public string Week2Display => Week2Score.HasValue ? $"{Week2Score.Value:0.##}" : "—";
        public string Week3Display => Week3Score.HasValue ? $"{Week3Score.Value:0.##}" : "—";
        public string Week4Display => Week4Score.HasValue ? $"{Week4Score.Value:0.##}" : "—";
        public string MonthlyAverageDisplay => $"{MonthlyEvaluationAverage:0.##}%";
        public string IncentivePercentDisplay => $"{DisplayedIncentivePercent}%";
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
        public decimal TotalIncentivePayout => Rows.Count == 0 ? 0m : System.Linq.Enumerable.Sum(Rows, r => r.FinalIncentiveAmount);
        public decimal OverallAverageScore => Rows.Count == 0 ? 0m : Math.Round(System.Linq.Enumerable.Average(Rows, r => r.MonthlyEvaluationAverage), 2, MidpointRounding.AwayFromZero);
    }
}
