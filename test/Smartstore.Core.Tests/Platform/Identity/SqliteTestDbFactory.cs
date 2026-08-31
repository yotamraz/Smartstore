using System;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Smartstore.Data;
using Smartstore.Data.Providers;

namespace Smartstore.Core.Tests.Platform.Identity;

/// <summary>
/// A test-only DbFactory that configures EF Core to use SQLite in-memory,
/// which unlike the InMemory provider supports relational operations such as ExecuteDeleteAsync.
/// </summary>
internal class SqliteTestDbFactory : DbFactory
{
    private readonly SqliteConnection _connection;

    public SqliteTestDbFactory(SqliteConnection connection)
    {
        _connection = connection;
    }

    public override DbSystemType DbSystem { get; } = DbSystemType.Unknown;

    public override DbConnectionStringBuilder CreateConnectionStringBuilder(string connectionString)
        => throw new NotImplementedException();

    public override DbConnectionStringBuilder CreateConnectionStringBuilder(
        string server,
        string database,
        string userName,
        string password)
        => throw new NotImplementedException();

    public override DataProvider CreateDataProvider(DatabaseFacade database)
        => throw new NotImplementedException();

    public override TContext CreateDbContext<TContext>(string connectionString, int? commandTimeout = null)
        => throw new NotImplementedException();

    public override DbContextOptionsBuilder ConfigureDbContext(DbContextOptionsBuilder builder, string connectionString)
    {
        return builder.UseSqlite(_connection);
    }
}
