using TelPhone.Core;

namespace TelPhone.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("telphone-tests-");

    [Fact]
    public void ImportsCsvWithQuotedHeadersAndValuesAndSkipsInvalidRows()
    {
        var path = Write("contacts.csv", """
            "Имя";"Телефон"
            "Иван; Иванов";"+7 (999) 123-45-67"
            "Ошибочный номер";"no"
            "Короткая строка"
            """);
        var result = ContactImport.Read(path);
        Assert.Equal(new Contact("Иван; Иванов", "+79991234567"), Assert.Single(result.Contacts));
        Assert.Equal(2, result.Invalid);
    }

    [Fact]
    public void ImportsJsonWithoutDroppingLeadingZeros()
    {
        var result = ContactImport.Read(Write("contacts.json", """
            {"contacts":[{"fullName":"Иван","phone":"0123"},{"name":"Неверный","phone":123}]}
            """));
        Assert.Equal(new Contact("Иван", "0123"), Assert.Single(result.Contacts));
        Assert.Equal(1, result.Invalid);
    }

    [Fact]
    public void DatabasePersistsHistoryAndDeduplicatesAcrossFormats()
    {
        var dbPath = Path.Combine(temp.FullName, "telphone.db");
        var db = new DialerDatabase(dbPath);
        db.Initialize();

        var csv = ContactImport.Read(Write("contacts.csv", "name,phone\nИван,+7 (999) 123-45-67"));
        var json = ContactImport.Read(Write("contacts.json", """
            [{"name":"Дубликат","phone":"+79991234567"},{"name":"Анна","phone":"0123"}]
            """));
        Assert.Equal(new ImportSummary(1, 0, 0), db.Import(csv));
        Assert.Equal(new ImportSummary(1, 1, 0), db.Import(json));
        var id = db.RecordAttempt("+79991234567", "SAMSUNG");
        Assert.Equal(CommandStatus.Pending, Assert.Single(db.GetHistory()).Status);
        db.FinishAttempt(id, CommandStatus.Sent);

        var reopened = new DialerDatabase(dbPath);
        reopened.Initialize();
        Assert.Equal(2, reopened.GetContacts().Count);
        var entry = Assert.Single(reopened.GetHistory());
        Assert.Equal("Иван", entry.ContactName);
        Assert.Equal("SAMSUNG", entry.DeviceSerial);
        Assert.Equal(CommandStatus.Sent, entry.Status);
        Assert.Equal(TimeSpan.Zero, entry.CreatedAt.Offset);
    }

    private string Write(string name, string contents)
    {
        var path = Path.Combine(temp.FullName, name);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose() => temp.Delete(recursive: true);
}
