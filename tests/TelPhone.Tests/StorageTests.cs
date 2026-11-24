using Microsoft.Data.Sqlite;
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

    [Fact]
    public void AssignsOneGroupToTheEntireImportAndMovesMatchingNumbersWhenRequested()
    {
        var path = Path.Combine(temp.FullName, "telphone.db");
        var db = new DialerDatabase(path);
        db.Initialize();
        var csv = ContactImport.Read(Write("contacts.csv", "name,phone\nАнна,0123\nИван,4567"));
        var json = ContactImport.Read(Write("contacts.json", """
            [{"name":"Другое имя","phone":"01-23"},{"name":"Ольга","phone":"8901"},{"phone":"8901"}]
            """));

        Assert.Equal(new ImportSummary(2, 0, 0), db.Import(csv, "  Работа  "));
        Assert.Equal(new ImportSummary(1, 2, 0), db.Import(json, " Личные "));
        Assert.Equal(new ImportSummary(0, 2, 0, 1), db.Import(json, "личные", updateExistingGroup: true));
        Assert.Equal(new ImportSummary(0, 3, 0), db.Import(json, "Личные", updateExistingGroup: true));
        Assert.Equal(new ImportSummary(0, 3, 0), db.Import(json, "", updateExistingGroup: true));

        var reopened = new DialerDatabase(path);
        reopened.Initialize();
        var contacts = reopened.GetContacts().ToDictionary(contact => contact.Phone);
        Assert.Equal(new Contact("Анна", "0123", "Личные"), contacts["0123"]);
        Assert.Equal(new Contact("Иван", "4567", "Работа"), contacts["4567"]);
        Assert.Equal(new Contact("Ольга", "8901", "Личные"), contacts["8901"]);
    }

    [Fact]
    public void MovesExistingContactsAndClearsGroup()
    {
        var db = new DialerDatabase(Path.Combine(temp.FullName, "telphone.db"));
        db.Initialize();
        db.Import(new ImportBatch([new Contact("Анна", "0123"), new Contact("Иван", "4567")], 0), "Работа");
        db.SetContactGroup("0123", "Личные");
        db.SetContactGroup("4567", "  личные ");
        Assert.All(db.GetContacts(), contact => Assert.Equal("Личные", contact.Group));

        db.SetContactGroup("0123", "");
        Assert.Equal(new Contact("Анна", "0123"), Assert.Single(db.GetContacts(), contact => contact.Group.Length == 0));
    }

    [Fact]
    public void MigratesExistingContactsAndCallHistory()
    {
        var path = Path.Combine(temp.FullName, "telphone.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE contacts (id INTEGER PRIMARY KEY, name TEXT NOT NULL, normalized_phone TEXT NOT NULL UNIQUE);
                CREATE TABLE call_history (id INTEGER PRIMARY KEY, phone TEXT NOT NULL, contact_name_snapshot TEXT NOT NULL,
                    device_serial TEXT NOT NULL, created_at TEXT NOT NULL, command_status TEXT NOT NULL);
                INSERT INTO contacts VALUES (1, 'Анна', '0123');
                INSERT INTO call_history VALUES (1, '0123', 'Анна', 'SAMSUNG', '2026-11-24T03:00:00.0000000+00:00', 'Sent');
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }

        var db = new DialerDatabase(path);
        db.Initialize();
        db.Initialize();
        Assert.Equal(new Contact("Анна", "0123"), Assert.Single(db.GetContacts()));
        var history = Assert.Single(db.GetHistory());
        Assert.Equal("Анна", history.ContactName);
        Assert.Equal(CommandStatus.Sent, history.Status);
        db.SetContactGroup("0123", "Работа");
        Assert.Equal("Работа", Assert.Single(db.GetContacts()).Group);
    }

    private string Write(string name, string contents)
    {
        var path = Path.Combine(temp.FullName, name);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose() => temp.Delete(recursive: true);
}
