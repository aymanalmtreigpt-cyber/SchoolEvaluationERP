using System;
using System.IO;
using Microsoft.Extensions.Configuration;

namespace SchoolEvaluationERP.Services
{
    /// <summary>
    /// خدمة قراءة الإعدادات لمرة واحدة في الذاكرة (Singleton Caching) لضمان أقصى سرعة عبر الشبكة
    /// </summary>
    public static class AppConfigService
    {
        private static readonly IConfigurationRoot _config;

        static AppConfigService()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

            _config = builder.Build();
        }

        public static string ConnectionString =>
            _config.GetConnectionString("SchoolErpLanConnection")
            ?? "Server=localhost;Database=SchoolEvaluationDB;Trusted_Connection=True;TrustServerCertificate=True;";

        public static string DefaultDebitAccountName =>
            _config["AccountingSettings:DefaultDebitAccountName"] ?? "مصروفات الحوافز الشهرية للعام 2026/2027 م";

        public static decimal DefaultMaxIncentiveAmount =>
            decimal.TryParse(_config["AccountingSettings:DefaultMaxIncentiveAmount"], out var val) ? val : 7000m;

        public static decimal DefaultMinIncentiveAmount =>
            decimal.TryParse(_config["AccountingSettings:DefaultMinIncentiveAmount"], out var val) ? val : 1500m;

        public static bool RoundToNearest50 =>
            !bool.TryParse(_config["AccountingSettings:RoundToNearest50"], out var val) || val;
    }
}
