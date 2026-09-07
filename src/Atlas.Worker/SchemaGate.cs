using Atlas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Atlas.Worker;

/// <summary>
/// The API owns the schema (Atlas__AutoMigrate); on a fresh install the worker can start a second earlier and would
/// otherwise query tables that do not exist yet. Every background loop waits here first: no pending migrations, or
/// ten minutes, whichever comes first — then it proceeds and the normal error handling takes over.
/// </summary>
internal static class SchemaGate
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(10);

    public static async Task WaitAsync(IServiceScopeFactory scopeFactory, ILogger logger, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + MaxWait;
        var announced = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();
                // Probe the history table without EF's own lookup: EF would log a failed command while the API is
                // still creating it. to_regclass answers null instead of erroring when the relation does not exist.
                var historyExists = await HistoryTableExistsAsync(db, cancellationToken);
                var pending = historyExists ? (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Count() : db.Database.GetMigrations().Count();
                if (pending == 0)
                {
                    if (announced)
                    {
                        logger.LogInformation("Schema ready; starting.");
                    }

                    return;
                }

                if (!announced)
                {
                    logger.LogInformation("Waiting for the API to apply {Count} pending migration(s) before starting.", pending);
                    announced = true;
                }
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or System.Net.Sockets.SocketException)
            {
                if (!announced)
                {
                    logger.LogInformation("Database not reachable yet ({Reason}); waiting.", ex.Message);
                    announced = true;
                }
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                logger.LogWarning("Schema still not ready after {Minutes} minutes; starting anyway.", MaxWait.TotalMinutes);
                return;
            }

            await Task.Delay(Poll, cancellationToken);
        }
    }

    private static async Task<bool> HistoryTableExistsAsync(AtlasDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "select to_regclass('atlas.__ef_migrations_history') is not null";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is true;
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }
}
