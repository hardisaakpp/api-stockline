using Microsoft.EntityFrameworkCore;

namespace Stockline.Infrastructure.Persistence;

internal static class UniqueViolations
{
    // SQL Server: 2601 = duplicate key in a unique index, 2627 = unique constraint.
    private static readonly int[] SqlServerNumbers = [2601, 2627];

    /// <summary>Whether the save failed because a unique index or constraint rejected a duplicate.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception) => exception.InnerException switch
    {
        Microsoft.Data.SqlClient.SqlException sql => SqlServerNumbers.Contains(sql.Number),
        // SQLite is only used by the tests; matched by name so the production assembly does not reference it.
        { } inner when inner.GetType().Name == "SqliteException" => inner.Message.Contains("UNIQUE constraint failed"),
        _ => false,
    };
}
