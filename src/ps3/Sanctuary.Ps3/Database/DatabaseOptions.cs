namespace Sanctuary.Ps3.Database;

/// <summary>The database kinds the server can keep its characters in, as Sanctuary's <c>DatabaseProvider</c>.</summary>
public enum DatabaseProvider
{
    MySql,
    Sqlite,
}

/// <summary>Where the characters are kept, as Sanctuary's <c>DatabaseOptions</c> (its <c>Database</c> settings section).</summary>
public sealed class DatabaseOptions
{
    public required DatabaseProvider Provider { get; init; }

    /// <summary>The connection string: <c>Data Source=characters.db</c> for SQLite, <c>Server=...;Database=...;User=...;Password=...</c> for MySQL.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>
    /// For MySQL: the server's version, <c>major.minor.patch-type</c> (<c>8.0.21-mysql</c>, <c>10.5.3-mariadb</c>), as Sanctuary asks for it.
    /// When it is not given, the version is asked of the server when the database is opened.
    /// </summary>
    public string? VersionString { get; init; }
}
