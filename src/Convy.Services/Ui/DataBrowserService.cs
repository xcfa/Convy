using System.Data.Common;
using System.Text;
using System.Text.Json.Serialization;
using Convy.Data.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Convy.Services.Ui;

/// <summary>A table the UI can browse.</summary>
public sealed record DataTableInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("columns")] IReadOnlyList<DataColumnInfo> Columns,
    [property: JsonPropertyName("rows")] long Rows);

/// <param name="Kind">
/// How to show the value: <c>text</c>, <c>number</c>, <c>bool</c>, <c>time</c> (as stored),
/// <c>unix_time</c> (converted to an ISO time), <c>blob</c> (only its size in bytes).
/// </param>
public sealed record DataColumnInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind);

/// <summary>One page of a table; each row has a value per column, in column order.</summary>
public sealed record DataPage(
    [property: JsonPropertyName("table")] string Table,
    [property: JsonPropertyName("columns")] IReadOnlyList<DataColumnInfo> Columns,
    [property: JsonPropertyName("rows")] IReadOnlyList<object?[]> Rows,
    [property: JsonPropertyName("total")] long Total,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("limit")] int Limit);

/// <summary>What to read from a table.</summary>
/// <param name="Search">Text that any text column must contain (case-insensitive for ASCII).</param>
/// <param name="Sort">Column to sort by; newest rows first when not given.</param>
/// <param name="Descending">Sort direction.</param>
public sealed record DataQuery(string? Search, string? Sort, bool Descending, int Offset, int Limit);

/// <summary>
/// Read-only view of the database for the UI. The tables and columns come from the EF model,
/// never from the request, so the SQL is built from trusted names only; values are parameters.
/// Columns that hold secrets are left out and binary columns are shown as their size.
/// </summary>
public sealed class DataBrowserService
{
    public const int MaxLimit = 200;
    private const int MaxTextLength = 2000;

    /// <summary>Columns never shown: a search result's content id holds the Prowlarr API key.</summary>
    private static readonly HashSet<(string Table, string Column)> Hidden =
    [
        ("SearchResults", "ContentId"),
    ];

    private readonly IDbContextFactory<ConvyDbContext> _convyDb;
    private readonly IDbContextFactory<SettingsDbContext> _settingsDb;

    public DataBrowserService(IDbContextFactory<ConvyDbContext> convyDb, IDbContextFactory<SettingsDbContext> settingsDb)
    {
        _convyDb = convyDb;
        _settingsDb = settingsDb;
    }

    public async Task<IReadOnlyList<DataTableInfo>> GetTablesAsync(CancellationToken cancellationToken)
    {
        var result = new List<DataTableInfo>();

        await using (var db = await _convyDb.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            result.AddRange(await DescribeAsync(db, cancellationToken).ConfigureAwait(false));
        }

        await using (var db = await _settingsDb.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            result.AddRange(await DescribeAsync(db, cancellationToken).ConfigureAwait(false));
        }

        return result.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    public async Task<DataPage> ReadAsync(string table, DataQuery query, CancellationToken cancellationToken)
    {
        await using (var db = await _convyDb.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            if (FindTable(db, table) is { } convyTable)
            {
                return await ReadAsync(db, convyTable, query, cancellationToken).ConfigureAwait(false);
            }
        }

        await using (var db = await _settingsDb.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            if (FindTable(db, table) is { } settingsTable)
            {
                return await ReadAsync(db, settingsTable, query, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new ConvyRequestException($"Unknown table '{table}'.");
    }

    private static async Task<IReadOnlyList<DataTableInfo>> DescribeAsync(DbContext db, CancellationToken cancellationToken)
    {
        var existing = await ExistingTablesAsync(db, cancellationToken).ConfigureAwait(false);
        var result = new List<DataTableInfo>();

        foreach (var table in Tables(db).Where(t => existing.Contains(t.Name)))
        {
            var rows = await ScalarAsync(db, $"SELECT COUNT(*) FROM {Quote(table.Name)}", [], cancellationToken).ConfigureAwait(false);
            result.Add(new DataTableInfo(table.Name, table.Columns.Select(c => c.Info).ToList(), rows));
        }

        return result;
    }

    private static async Task<DataPage> ReadAsync(DbContext db, TableModel table, DataQuery query, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(query.Limit, 1, MaxLimit);
        var offset = Math.Max(0, query.Offset);

        var parameters = new List<(string Name, object Value)>();
        var where = string.Empty;
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var searchable = table.Columns.Where(c => c.Info.Kind is "text" or "time").ToList();
            if (searchable.Count == 0)
            {
                return new DataPage(table.Name, table.Columns.Select(c => c.Info).ToList(), [], 0, offset, limit);
            }

            parameters.Add(("@search", "%" + EscapeLike(query.Search.Trim()) + "%"));
            where = " WHERE " + string.Join(" OR ", searchable.Select(c => $"{Quote(c.Info.Name)} LIKE @search ESCAPE '\\'"));
        }

        var sortColumn = query.Sort is null
            ? null
            : table.Columns.FirstOrDefault(c => string.Equals(c.Info.Name, query.Sort, StringComparison.Ordinal))
              ?? throw new ConvyRequestException($"Unknown column '{query.Sort}' in table '{table.Name}'.");
        var direction = query.Descending ? "DESC" : "ASC";
        var order = sortColumn is null
            ? $"rowid {direction}"
            : $"{Quote(sortColumn.Info.Name)} {direction}, rowid {direction}";

        var total = await ScalarAsync(db, $"SELECT COUNT(*) FROM {Quote(table.Name)}{where}", parameters, cancellationToken)
            .ConfigureAwait(false);

        var select = string.Join(", ", table.Columns.Select(SelectExpression));
        var sql = $"SELECT {select} FROM {Quote(table.Name)}{where} ORDER BY {order} LIMIT @limit OFFSET @offset";
        parameters.Add(("@limit", limit));
        parameters.Add(("@offset", offset));

        var rows = new List<object?[]>();
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = CreateCommand(db, sql, parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = new object?[table.Columns.Count];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : Present(table.Columns[i].Info.Kind, reader.GetValue(i));
                }

                rows.Add(row);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        return new DataPage(table.Name, table.Columns.Select(c => c.Info).ToList(), rows, total, offset, limit);
    }

    private static string SelectExpression(ColumnModel column) => column.Info.Kind switch
    {
        "blob" => $"length({Quote(column.Info.Name)})",
        // One character more than shown, so a cut value can be marked.
        "text" => $"substr({Quote(column.Info.Name)}, 1, {MaxTextLength + 1})",
        _ => Quote(column.Info.Name),
    };

    private static object? Present(string kind, object value) => kind switch
    {
        "bool" => Convert.ToInt64(value) != 0,
        "unix_time" => DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(value)),
        "text" when value is string { Length: > MaxTextLength } text => text[..MaxTextLength] + "…",
        _ => value,
    };

    private static TableModel? FindTable(DbContext db, string name) =>
        Tables(db).FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    private static IEnumerable<TableModel> Tables(DbContext db)
    {
        foreach (var entity in db.Model.GetEntityTypes())
        {
            if (entity.GetTableName() is not { } tableName)
            {
                continue;
            }

            var store = StoreObjectIdentifier.Table(tableName, entity.GetSchema());
            var columns = entity.GetProperties()
                .Select(p => (Property: p, Column: p.GetColumnName(store)))
                .Where(x => x.Column is not null && !Hidden.Contains((tableName, x.Column)))
                .Select(x => new ColumnModel(new DataColumnInfo(x.Column!, KindOf(x.Property))))
                .ToList();

            yield return new TableModel(tableName, columns);
        }
    }

    private static string KindOf(IProperty property)
    {
        var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;

        if (type == typeof(byte[])) return "blob";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(DateTimeOffset) || type == typeof(DateTime)) return "time";
        // The search cache keeps its times as unix seconds (CreatedAt, ExpiresAt).
        if (type == typeof(long) && property.Name.EndsWith("At", StringComparison.Ordinal)) return "unix_time";
        if (type.IsPrimitive || type == typeof(decimal)) return "number";
        return "text";
    }

    private static async Task<HashSet<string>> ExistingTablesAsync(DbContext db, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = CreateCommand(db, "SELECT name FROM sqlite_master WHERE type = 'table'", []);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                names.Add(reader.GetString(0));
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        return names;
    }

    private static async Task<long> ScalarAsync(
        DbContext db, string sql, IReadOnlyList<(string Name, object Value)> parameters, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = CreateCommand(db, sql, parameters);
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static DbCommand CreateCommand(DbContext db, string sql, IReadOnlyList<(string Name, object Value)> parameters)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string EscapeLike(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '%' or '_' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private sealed record TableModel(string Name, IReadOnlyList<ColumnModel> Columns);

    private sealed record ColumnModel(DataColumnInfo Info);
}
