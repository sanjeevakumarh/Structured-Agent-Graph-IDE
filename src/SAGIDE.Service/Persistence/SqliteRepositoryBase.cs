using Microsoft.Data.Sqlite;

namespace SAGIDE.Service.Persistence;

/// <summary>
/// Shared connection-string holder for all SQLite repository classes.
/// Subclasses open their own short-lived connections so that SQLite's WAL mode
/// can serve concurrent reads during any write.
/// </summary>
public abstract class SqliteRepositoryBase
{
    protected readonly string _connectionString;

    protected SqliteRepositoryBase(string dbPath)
    {
        // Foreign Keys=False: Microsoft.Data.Sqlite enables FK enforcement by default.
        // Our FK declarations (task_results → task_history) are schema documentation only —
        // the application manages referential integrity through its persist-task-then-result flow.
        // Disabling avoids spurious FK failures when intermediate PersistTaskAsync calls are
        // swallowed by error handlers before PersistResultAsync runs.
        _connectionString = $"Data Source={dbPath};Pooling=True;Foreign Keys=False";
    }

    protected SqliteConnection OpenConnection() => new(_connectionString);
}
