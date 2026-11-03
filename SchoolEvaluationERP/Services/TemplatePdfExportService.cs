using System;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SchoolEvaluationERP.Models;

namespace SchoolEvaluationERP.Services
{
    public static class TemplatePdfExportService
    {
        public static void GenerateTemplateA4Pdf(TemplateFullModel template, string outputFilePath)
        {
            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(1.2f, Unit.Centimetre);
                    page.PageColor(Colors.White);
                    page.ContentFromRightToLeft();

                    page.DefaultTextStyle(x => x.FontFamily("Arial").FontSize(10).FontColor(Colors.Grey.Darken4));

                    page.Header().PaddingBottom(8).BorderBottom(1.5f).BorderColor(Colors.Blue.Darken3).Row(row =>
                    {
                        row.RelativeItem().Column(col =>
                        {
                            col.Item().Text("استمارة التقييم الأسبوعي للموظفين").FontSize(15).Bold().FontColor(Colors.Blue.Darken4);
                            col.Item().PaddingTop(2).Text($"اسم المُقيّم المسؤول: {template.EvaluatorName}   |   رقم القالب: #{template.TemplateID} (v{template.VersionNumber})").FontSize(11).SemiBold();
                        });

                        row.ConstantItem(195).AlignLeft().Column(col =>
                        {
                            col.Item().Text($"تاريخ الطباعة: {DateTime.Now:yyyy-MM-dd}").FontSize(9.5f);
                            col.Item().PaddingTop(3).Text("الشهر: .......................   الأسبوع: ( 1 / 2 / 3 / 4 )").FontSize(9.5f).SemiBold();
                        });
                    });

                    page.Content().PaddingVertical(6).Column(mainCol =>
                    {
                        for (int i = 0; i < template.Sheets.Count; i++)
                        {
                            var sheet = template.Sheets[i];
                            bool isFirstSheet = (i == 0);

                            // ShowEntire يفحص هل توجد مساحة كافية في نفس ورقة A4 لطباعة الورقة التالية تحتها مباشرة
                            mainCol.Item()
                                   .PaddingTop(isFirstSheet ? 2 : 14)
                                   .ShowEntire()
                                   .Border(1)
                                   .BorderColor(Colors.Grey.Medium)
                                   .Padding(8)
                                   .Column(sheetCol =>
                                   {
                                       sheetCol.Item().Background(Colors.Grey.Lighten3).PaddingVertical(5).PaddingHorizontal(8).Row(r =>
                                       {
                                           r.RelativeItem().Text($"ورقة التقييم ({i + 1}): {sheet.SheetName}   —   الفئة: {sheet.TargetCategoryName}")
                                            .FontSize(11.5f).Bold().FontColor(Colors.Black);

                                           r.ConstantItem(170).AlignLeft().Text($"مجموع درجات المبادئ: {sheet.TotalCriteriaScore:0.##} من 100")
                                            .FontSize(10).SemiBold().FontColor(Colors.Blue.Darken3);
                                       });

                                       sheetCol.Item().PaddingTop(6).Table(table =>
                                       {
                                           int criteriaCount = sheet.CriteriaColumns.Count;

                                           table.ColumnsDefinition(cols =>
                                           {
                                               cols.ConstantColumn(28);
                                               cols.RelativeColumn(2.4f);
                                               cols.ConstantColumn(65);
                                               foreach (var _ in sheet.CriteriaColumns)
                                               {
                                                   cols.RelativeColumn(1.25f);
                                               }
                                               cols.ConstantColumn(55);
                                           });

                                           table.Header(header =>
                                           {
                                               header.Cell().Element(HeaderCellStyle).Text("م");
                                               header.Cell().Element(HeaderCellStyle).Text("الاسم التقييمي للموظف");
                                               header.Cell().Element(HeaderCellStyle).Text("الفرع");

                                               foreach (var crit in sheet.CriteriaColumns)
                                               {
                                                   header.Cell().Element(HeaderCellStyle).Column(c =>
                                                   {
                                                       c.Item().AlignCenter().Text(crit.CriteriaTitle).FontSize(9.5f).Bold();
                                                       c.Item().AlignCenter().Text($"(الدرجة: {crit.MaxScore:0.##})").FontSize(8.5f).FontColor(Colors.Blue.Darken3);
                                                   });
                                               }

                                               header.Cell().Element(HeaderCellStyle).Column(c =>
                                               {
                                                   c.Item().AlignCenter().Text("المجموع").FontSize(9.5f).Bold();
                                                   c.Item().AlignCenter().Text("(100)").FontSize(8.5f);
                                               });
                                           });

                                           int rowIndex = 1;
                                           foreach (var emp in sheet.EmployeeRows)
                                           {
                                               RenderEmployeeRow(table, rowIndex++, emp, criteriaCount);
                                           }
                                       });

                                       sheetCol.Item().PaddingTop(8).Row(signRow =>
                                       {
                                           signRow.RelativeItem().Text("توقيع المقيم المسؤول: .................................................").FontSize(9.5f);
                                           signRow.RelativeItem().AlignLeft().Text("تاريخ التسليم للإدارة: ...... / ...... / 2026 م").FontSize(9.5f);
                                       });
                                   });
                        }
                    });

                    page.Footer().PaddingTop(4).BorderTop(0.5f).BorderColor(Colors.Grey.Lighten1).Row(row =>
                    {
                        row.RelativeItem().Text("نظام تقييم موظفي المدارس واحتساب الحوافز — يعبأ ورقياً من قبل المقيم ويعاد للإدارة").FontSize(8.5f).FontColor(Colors.Grey.Darken2);
                        row.ConstantItem(100).AlignLeft().Text(txt =>
                        {
                            txt.Span("صفحة ").FontSize(8.5f);
                            txt.CurrentPageNumber().FontSize(8.5f).Bold();
                            txt.Span(" من ").FontSize(8.5f);
                            txt.TotalPages().FontSize(8.5f).Bold();
                        });
                    });
                });
            }).GeneratePdf(outputFilePath);
        }

        private static void RenderEmployeeRow(TableDescriptor table, int index, SheetEmployeeRowModel emp, int criteriaCount)
        {
            table.Cell().Element(BodyCellStyle).AlignCenter().Text(index.ToString());
            table.Cell().Element(BodyCellStyle).Text(emp.EvaluationName).SemiBold();
            table.Cell().Element(BodyCellStyle).AlignCenter().Text(emp.BranchName).FontSize(9);

            for (int c = 0; c < criteriaCount; c++)
            {
                table.Cell().Element(BodyCellStyle).Text("");
            }

            table.Cell().Element(BodyCellStyle).Text("");
        }

        private static IContainer HeaderCellStyle(IContainer container)
        {
            return container
                .Border(0.75f)
                .BorderColor(Colors.Grey.Darken1)
                .Background(Colors.Blue.Lighten5)
                .PaddingVertical(5)
                .PaddingHorizontal(4)
                .AlignCenter()
                .AlignMiddle();
        }

        private static IContainer BodyCellStyle(IContainer container)
        {
            return container
                .Border(0.5f)
                .BorderColor(Colors.Grey.Medium)
                .MinHeight(22)
                .PaddingVertical(4)
                .PaddingHorizontal(5)
                .AlignMiddle();
        }
    }
}
