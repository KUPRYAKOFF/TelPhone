using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace TelPhone.Core;

public static partial class ContactImport
{
    private static ImportBatch ReadWorkbook(string path)
    {
        try
        {
            using var document = SpreadsheetDocument.Open(path, isEditable: false);
            var workbook = document.WorkbookPart ?? throw new FormatException("В файле XLSX нет книги Excel.");
            var sheets = workbook.Workbook?.Sheets ?? throw new FormatException("В книге Excel нет листов.");
            string[] sharedStrings = workbook.SharedStringTablePart?.SharedStringTable?
                .Elements<SharedStringItem>().Select(item => item.InnerText).ToArray() ?? [];

            var contacts = new List<Contact>();
            var groups = new HashSet<string?>(StringComparer.OrdinalIgnoreCase);
            var invalid = 0;
            bool foundHeaders = false;
            foreach (var sheet in sheets.Elements<Sheet>())
            {
                if (sheet.State?.Value is { } state && state != SheetStateValues.Visible)
                    continue;
                if (sheet.Id?.Value is not { } id || !workbook.TryGetPartById(id, out var part) || part is not WorksheetPart worksheet)
                    continue;

                var result = ReadWorksheet(worksheet, sharedStrings);
                if (result is null)
                    continue;
                foundHeaders = true;
                contacts.AddRange(result.Contacts);
                invalid += result.Invalid;
                if (result.Contacts.Count > 0)
                    groups.Add(result.SuggestedGroup);
            }

            if (!foundHeaders)
                throw new FormatException("В XLSX не найдена таблица со столбцами «Фамилия Имя Отчество» и «Телефон».");

            return new ImportBatch(contacts, invalid, groups.Count == 1 ? groups.Single() : null);
        }
        catch (Exception error) when (error is OpenXmlPackageException or InvalidDataException or XmlException or FileFormatException)
        {
            throw new FormatException("Не удалось прочитать файл XLSX. Откройте его в редакторе таблиц и сохраните как книгу Excel.", error);
        }
    }

    private static ImportBatch? ReadWorksheet(WorksheetPart worksheet, string[] sharedStrings)
    {
        using var reader = OpenXmlReader.Create(worksheet);
        var contacts = new List<Contact>();
        int invalid = 0;
        int nameColumn = 0;
        int phoneColumn = 0;
        string? group = null;

        while (reader.Read())
        {
            if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
                continue;

            if (reader.LoadCurrentElement() is not Row row)
                continue;
            var values = ReadCells(row, sharedStrings);
            if (contacts.Count == 0 && group is null)
                group = values.Values.Select(ReadGroupHeading).FirstOrDefault(value => value is not null);

            if (nameColumn == 0)
            {
                var nameHeader = values.FirstOrDefault(cell => Matches(NameKeys, CollapseWhitespace(cell.Value)));
                var phoneHeader = values.FirstOrDefault(cell => Matches(PhoneKeys, CollapseWhitespace(cell.Value)));
                if (nameHeader.Key != 0 && phoneHeader.Key != 0)
                {
                    nameColumn = nameHeader.Key;
                    phoneColumn = phoneHeader.Key;
                }
                continue;
            }

            var name = values.GetValueOrDefault(nameColumn)?.Trim();
            var phone = values.GetValueOrDefault(phoneColumn);
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(phone))
                continue;
            if (name is not null && Matches(NameKeys, CollapseWhitespace(name)) &&
                phone is not null && Matches(PhoneKeys, CollapseWhitespace(phone)))
                continue;

            var normalized = PhoneNumber.Normalize(phone);
            if (string.IsNullOrWhiteSpace(name) || normalized is null)
            {
                invalid++;
                continue;
            }

            if ((normalized.Length == 11 && normalized.StartsWith('7')) ||
                (normalized.Length == 12 && normalized.StartsWith("+7", StringComparison.Ordinal)))
                normalized = "8" + normalized[^10..];
            contacts.Add(new Contact(name, normalized));
        }

        return nameColumn == 0 ? null : new ImportBatch(contacts, invalid, group);
    }

    private static Dictionary<int, string> ReadCells(Row row, string[] sharedStrings)
    {
        var values = new Dictionary<int, string>();
        int column = 0;
        foreach (var cell in row.Elements<Cell>())
        {
            if (cell.CellReference?.Value is { } reference)
            {
                column = 0;
                foreach (var character in reference)
                {
                    if (character is < 'A' or > 'Z')
                        break;
                    column = column * 26 + character - 'A' + 1;
                }
            }
            else
            {
                column++;
            }

            var value = cell.CellValue?.Text;
            var type = cell.DataType?.Value;
            if (type == CellValues.InlineString)
                value = cell.InlineString?.InnerText;
            else if (type == CellValues.SharedString)
                value = int.TryParse(value, out var index) && (uint)index < (uint)sharedStrings.Length ? sharedStrings[index] : null;
            else if (type == CellValues.Boolean || type == CellValues.Error || type == CellValues.Date)
                value = null;
            else if (type is null || type == CellValues.Number)
                value = decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                        number >= 0 && number == decimal.Truncate(number)
                    ? number.ToString("0", CultureInfo.InvariantCulture)
                    : null;

            if (!string.IsNullOrWhiteSpace(value))
                values[column] = value;
        }
        return values;
    }

    private static string CollapseWhitespace(string input) =>
        string.Join(" ", input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? ReadGroupHeading(string input)
    {
        var match = GroupHeadingPattern().Match(input);
        return match.Success
            ? $"{CollapseWhitespace(match.Groups["group"].Value)}-{match.Groups["year"].Value}"
            : null;
    }

    [GeneratedRegex(@"^\s*(?:\d+\s+)?(?<group>\p{L}[\p{L}\d]*(?:[ \t]+\d+)?)\s*[-–—]\s*(?<year>\d{2})(?=\s|,|$)")]
    private static partial Regex GroupHeadingPattern();
}
