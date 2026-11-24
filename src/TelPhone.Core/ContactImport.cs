using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace TelPhone.Core;

public sealed record Contact(string Name, string Phone, string Group = "")
{
    public string GroupLabel => Group.Length == 0 ? "Без группы" : Group;
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Phone : Name;
}

public sealed record ImportBatch(IReadOnlyList<Contact> Contacts, int Invalid, string? SuggestedGroup = null);

public static partial class ContactImport
{
    private static readonly string[] PhoneKeys = ["phone", "number", "телефон", "номер", "номер телефона"];
    private static readonly string[] NameKeys = ["name", "fullName", "имя", "фио", "фамилия имя отчество", "фамилия, имя, отчество"];

    public static ImportBatch Read(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".xlsx")
            return ReadWorkbook(path);
        if (extension is not (".csv" or ".json"))
            throw new FormatException("Выберите файл XLSX, CSV или JSON.");

        using var reader = new StreamReader(path, new UTF8Encoding(false, true));
        return extension == ".csv" ? ReadCsv(reader.ReadToEnd()) : ReadJson(reader.ReadToEnd());
    }

    private static ImportBatch ReadCsv(string text)
    {
        using var parser = new TextFieldParser(new StringReader(text));
        foreach (var delimiter in new[] { ",", ";", "\t" })
        {
            using var headerParser = new TextFieldParser(new StringReader(text));
            headerParser.SetDelimiters(delimiter);
            string[] header;
            try { header = headerParser.ReadFields() ?? []; }
            catch (MalformedLineException) { continue; }
            int phoneColumn = Array.FindIndex(header, key => Matches(PhoneKeys, key));
            if (phoneColumn < 0)
                continue;

            int nameColumn = Array.FindIndex(header, key => Matches(NameKeys, key));
            parser.SetDelimiters(delimiter);
            parser.ReadFields();
            var rows = new List<Contact>();
            int invalid = 0;
            while (!parser.EndOfData)
            {
                try
                {
                    var fields = parser.ReadFields() ?? [];
                    if (fields.Length != header.Length)
                    {
                        invalid++;
                        continue;
                    }

                    Add(rows, ref invalid, nameColumn >= 0 ? fields[nameColumn] : null, fields[phoneColumn]);
                }
                catch (MalformedLineException)
                {
                    invalid++;
                }
            }
            return new(rows, invalid);
        }
        throw new FormatException("В CSV нужен заголовок phone (или «Телефон»).");
    }

    private static ImportBatch ReadJson(string text)
    {
        using var document = JsonDocument.Parse(text);
        var items = document.RootElement;
        if (items.ValueKind == JsonValueKind.Object)
            items = Find(items, ["contacts"]);
        if (items.ValueKind != JsonValueKind.Array)
            throw new FormatException("В JSON нужен массив контактов или объект с массивом contacts.");

        var rows = new List<Contact>();
        int invalid = 0;
        foreach (var item in items.EnumerateArray())
        {
            Add(rows, ref invalid, AsString(Find(item, NameKeys)), AsString(Find(item, PhoneKeys)));
        }
        return new(rows, invalid);
    }

    private static string? AsString(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static JsonElement Find(JsonElement item, string[] keys)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in item.EnumerateObject())
            {
                if (Matches(keys, property.Name))
                    return property.Value;
            }
        }
        return default;
    }

    private static bool Matches(string[] keys, string key)
        => keys.Contains(key.Trim(), StringComparer.OrdinalIgnoreCase);

    private static void Add(List<Contact> rows, ref int invalid, string? name, string? phone)
    {
        if (PhoneNumber.Normalize(phone) is { } normalized)
            rows.Add(new Contact(name?.Trim() ?? "", normalized));
        else
            invalid++;
    }
}
