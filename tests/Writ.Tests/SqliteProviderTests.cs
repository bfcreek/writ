using Microsoft.Data.Sqlite;
using Writ.Compiler.Compilation;
using Writ.Compiler.Execution;

namespace Writ.Tests;

public class SqliteProviderTests
{
    [Fact]
    public void Sqlite_Use_discovers_tables_and_queries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "writ-sqlite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "demo.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={db}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE Item (id INTEGER, name TEXT); INSERT INTO Item VALUES (1, 'a');";
                command.ExecuteNonQuery();
            }

            var source = $"""
                Use db from sqlite "{db.Replace("\\", "\\\\")}"
                Allow reading files

                When run, returning an integer
                    the rows are ask db to Item
                    return the count of the rows
                """;
            var path = Path.Combine(dir, "query.writ");
            File.WriteAllText(path, source);
            var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
            Assert.True(result.Success, string.Join("; ", result.Diagnostics));
            var exec = new MachineExecutor().Execute(result.GeneratedCSharp!, "Main", "{}");
            Assert.True(exec.Success, string.Join("; ", exec.Errors));
            Assert.Equal(1L, Convert.ToInt64(exec.Value));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
