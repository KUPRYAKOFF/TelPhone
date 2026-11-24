using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TelPhone.Core;

public sealed record HistoryEntry(long Id, string Phone, string ContactName, string DeviceSerial, DateTimeOffset CreatedAt,
    CommandStatus Status)
{
    public DateTime LocalTime => CreatedAt.LocalDateTime;
    public string StatusLabel => Status switch
    {
        CommandStatus.Pending or CommandStatus.Uncertain => "Результат команды неизвестен",
        CommandStatus.Sent => "Команда вызова отправлена",
        CommandStatus.DialOpened => "Открыта звонилка",
        CommandStatus.Rejected => "Ошибка команды вызова",
        _ => "Неизвестно"
    };
}
public sealed record ImportSummary(int Added, int Duplicates, int Invalid, int GroupChanged = 0);

public sealed class DialerDatabase(string path)
{
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Initialize()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        long version = (long)command.ExecuteScalar()!;
        if (version > 2)
            throw new InvalidDataException("База данных создана более новой версией TelPhone.");
        if (version == 0)
        {
            command.CommandText = """
                CREATE TABLE contacts (
                    id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    normalized_phone TEXT NOT NULL UNIQUE
                );
                CREATE TABLE call_history (
                    id INTEGER PRIMARY KEY,
                    phone TEXT NOT NULL,
                    contact_name_snapshot TEXT NOT NULL,
                    device_serial TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    command_status TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }
        if (version < 2)
        {
            command.CommandText = """
                ALTER TABLE contacts ADD COLUMN contact_group TEXT NOT NULL DEFAULT '';
                PRAGMA user_version = 2;
                """;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public ImportSummary Import(ImportBatch batch, string group = "", bool updateExistingGroup = false)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        group = ResolveGroupName(connection, transaction, group);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO contacts (name, normalized_phone, contact_group) VALUES ($name, $phone, $group)
                ON CONFLICT (normalized_phone) DO NOTHING;
            """;
        var name = command.Parameters.Add("$name", SqliteType.Text);
        var phone = command.Parameters.Add("$phone", SqliteType.Text);
        command.Parameters.AddWithValue("$group", group);
        using var update = updateExistingGroup && group.Length > 0 ? GroupUpdate(connection, transaction, group) : null;
        int added = 0;
        int changed = 0;
        foreach (var contact in batch.Contacts)
        {
            name.Value = contact.Name;
            phone.Value = contact.Phone;
            int inserted = command.ExecuteNonQuery();
            added += inserted;
            if (inserted == 0 && update is not null)
            {
                update.Parameters["$phone"].Value = contact.Phone;
                changed += update.ExecuteNonQuery();
            }
        }
        transaction.Commit();
        return new(added, batch.Contacts.Count - added - changed, batch.Invalid, changed);
    }

    public void SetContactGroup(string phone, string group)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var update = GroupUpdate(connection, transaction, ResolveGroupName(connection, transaction, group));
        update.Parameters["$phone"].Value = phone;
        update.ExecuteNonQuery();
        transaction.Commit();
    }

    private static SqliteCommand GroupUpdate(SqliteConnection connection, SqliteTransaction transaction, string group)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE contacts SET contact_group = $group
            WHERE normalized_phone = $phone AND contact_group <> $group;
            """;
        command.Parameters.AddWithValue("$group", group);
        command.Parameters.Add("$phone", SqliteType.Text);
        return command;
    }

    private static string ResolveGroupName(SqliteConnection connection, SqliteTransaction transaction, string group)
    {
        group = group.Trim();
        if (group.Length == 0)
            return group;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT DISTINCT contact_group FROM contacts";
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            var existing = rows.GetString(0);
            if (string.Equals(existing, group, StringComparison.OrdinalIgnoreCase))
                return existing;
        }
        return group;
    }

    public IReadOnlyList<Contact> GetContacts()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, normalized_phone, contact_group FROM contacts";
        using var rows = command.ExecuteReader();
        var contacts = new List<Contact>();
        while (rows.Read())
            contacts.Add(new Contact(rows.GetString(0), rows.GetString(1), rows.GetString(2)));
        return contacts.OrderBy(contact => contact.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(contact => contact.Phone, StringComparer.Ordinal).ToArray();
    }

    public long RecordAttempt(string phone, string serial)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO call_history (phone, contact_name_snapshot, device_serial, created_at, command_status)
            VALUES ($phone, COALESCE((SELECT name FROM contacts WHERE normalized_phone = $phone), ''),
                    $device, $at, $status)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$phone", phone);
        command.Parameters.AddWithValue("$device", serial);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$status", CommandStatus.Pending.ToString());
        return (long)command.ExecuteScalar()!;
    }

    public void FinishAttempt(long id, CommandStatus status)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE call_history SET command_status = $status WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status.ToString());
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<HistoryEntry> GetHistory()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, phone, contact_name_snapshot, device_serial, created_at, command_status
            FROM call_history ORDER BY id DESC;
            """;
        using var rows = command.ExecuteReader();
        var entries = new List<HistoryEntry>();
        while (rows.Read())
        {
            var status = Enum.TryParse<CommandStatus>(rows.GetString(5), out var parsed) ? parsed : CommandStatus.Uncertain;
            entries.Add(new(rows.GetInt64(0), rows.GetString(1), rows.GetString(2), rows.GetString(3),
                DateTimeOffset.ParseExact(rows.GetString(4), "O", CultureInfo.InvariantCulture), status));
        }
        return entries;
    }
}
