using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Atlas.Api;

/// <summary>
/// Opt-in schema migration for single-node deployments (Atlas__AutoMigrate=true). On an empty database EF Core
/// detects the migrations-history table by reading it and catching the failure, and that failed SELECT is logged at
/// Error level — two alarming lines on every first boot that mean nothing. We probe the catalog instead (no error to
/// log), create the history table when it is missing, and only then hand over to Migrate().
/// </summary>
internal static class AutoMigration
{
    private const string HistoryTable = "__ef_migrations_history";

    public static void Apply(DbContext db, string schema)
    {
        if (!HistoryTableExists(db, schema))
        {
            var history = db.GetService<IHistoryRepository>();
            var createSchema = "CREATE SCHEMA IF NOT EXISTS \"" + schema.Replace("\"", "\"\"") + "\""; // schema is a code constant, never user input
            db.Database.ExecuteSqlRaw(createSchema);
            db.Database.ExecuteSqlRaw(history.GetCreateIfNotExistsScript());
        }

        db.Database.Migrate();
    }

    private static bool HistoryTableExists(DbContext db, string schema)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            connection.Open();
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "select to_regclass(@name) is not null";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "name";
            parameter.Value = $"\"{schema}\".\"{HistoryTable}\"";
            command.Parameters.Add(parameter);
            return command.ExecuteScalar() is true;
        }
        finally
        {
            if (opened)
            {
                connection.Close();
            }
        }
    }
}
