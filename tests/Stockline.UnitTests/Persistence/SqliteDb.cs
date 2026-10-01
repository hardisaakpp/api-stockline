using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stockline.Infrastructure.Persistence;

namespace Stockline.UnitTests.Persistence;

public sealed class SqliteDb : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<StocklineDbContext> _options;

    public SqliteDb()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<StocklineDbContext>().UseSqlite(_connection).Options;
        using var context = NewContext();
        context.Database.EnsureCreated();
    }

    public StocklineDbContext NewContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}
