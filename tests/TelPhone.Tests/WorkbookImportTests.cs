using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using TelPhone.Core;

namespace TelPhone.Tests;

public sealed class WorkbookImportTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("usbphone-xlsx-");

    [Fact]
    public void ReadsTypicalWorkbookWithMergedHeadersAndNumericOrTextPhones()
    {
        var file = Path.Combine(temp.FullName, "Ведомость.xlsx");
        using (var document = SpreadsheetDocument.Create(file, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook(new Sheets());
            var strings = workbook.AddNewPart<SharedStringTablePart>();
            strings.SharedStringTable = new SharedStringTable(new SharedStringItem(new Text("Фамилия Имя Отчество")));
            var data = new SheetData(
                new Row { RowIndex = 1 },
                new Row(Shared("B2", 0), Text("C2", "Телефон"), Text("D2", "Занятость выпускников")) { RowIndex = 2 },
                new Row(Text("D3", "Трудоустройство")) { RowIndex = 3 },
                new Row(Text("D4", "Предприятие")) { RowIndex = 4 },
                new Row(Text("A5", "4 ТиТО -21,  год выпуска 2025г.")) { RowIndex = 5 },
                new Row(Text("B6", "Иванов Иван Иванович  "), Numeric("C6", "7.9010000001E+10")) { RowIndex = 6 },
                new Row(Text("B7", "Петров Пётр Петрович"), Text("C7", "+7\u00A0901\u00A0000 0002")) { RowIndex = 7 },
                new Row(Text("B8", "Сидорова Анна"), Numeric("C8", "89010000003")) { RowIndex = 8 },
                new Row(Text("B9", "Строка без телефона")) { RowIndex = 9 },
                new Row(Text("A10", "Итого")) { RowIndex = 10 });
            AddSheet(workbook, "Лист1", data,
                new MergeCells(new MergeCell { Reference = "B2:B4" }, new MergeCell { Reference = "C2:C4" },
                    new MergeCell { Reference = "A5:F5" }));
            AddSheet(workbook, "Примечания", new SheetData(new Row(Text("A1", "Не таблица контактов"))));
        }

        var batch = ContactImport.Read(file);
        Assert.Equal("ТиТО-21", batch.SuggestedGroup);
        Assert.Equal(1, batch.Invalid);
        Assert.Equal(new[]
        {
            new Contact("Иванов Иван Иванович", "89010000001"),
            new Contact("Петров Пётр Петрович", "89010000002"),
            new Contact("Сидорова Анна", "89010000003")
        }, batch.Contacts);
    }

    [Fact]
    public void RequiresNameAndPhoneHeaders()
    {
        var file = Path.Combine(temp.FullName, "Без телефонов.xlsx");
        using (var document = SpreadsheetDocument.Create(file, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook(new Sheets());
            AddSheet(workbook, "Лист1", new SheetData(new Row(Text("A1", "Имя"), Text("B1", "Дата"))));
        }

        Assert.Contains("Телефон", Assert.Throws<FormatException>(() => ContactImport.Read(file)).Message);
    }

    [Fact]
    public void ReportsAnInvalidWorkbookInImportTerms()
    {
        var file = Path.Combine(temp.FullName, "Повреждённый.xlsx");
        File.WriteAllText(file, "not an Excel workbook");
        Assert.Contains("XLSX", Assert.Throws<FormatException>(() => ContactImport.Read(file)).Message);
    }

    private static void AddSheet(WorkbookPart workbook, string name, SheetData data, MergeCells? merges = null)
    {
        var part = workbook.AddNewPart<WorksheetPart>();
        part.Worksheet = new Worksheet(data);
        if (merges is not null)
            part.Worksheet.Append(merges);
        var sheets = workbook.Workbook!.Sheets!;
        sheets.Append(new Sheet { Id = workbook.GetIdOfPart(part), SheetId = (uint)sheets.ChildElements.Count + 1, Name = name });
    }

    private static Cell Text(string address, string value) =>
        new() { CellReference = address, DataType = CellValues.InlineString, InlineString = new InlineString(new Text(value)) };

    private static Cell Numeric(string address, string value) =>
        new() { CellReference = address, DataType = CellValues.Number, CellValue = new CellValue(value) };

    private static Cell Shared(string address, int index) =>
        new() { CellReference = address, DataType = CellValues.SharedString, CellValue = new CellValue(index) };

    public void Dispose() => temp.Delete(recursive: true);
}
