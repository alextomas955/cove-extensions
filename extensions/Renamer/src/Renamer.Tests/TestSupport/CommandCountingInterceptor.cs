using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Renamer.Tests.TestSupport;

// Counts the commands a context executes, so a test can prove a path issues a bounded number of
// round-trips rather than one per item. The reader count is of commands and not of rows: EF may
// split one query with includes into a small, constant number of readers, so an assertion built on
// it compares two populations or a stated bound rather than an exact number. NonQueryTexts carries
// the SQL of every non-query so a test can count the statements one table saw.
public sealed class CommandCountingInterceptor : DbCommandInterceptor
{
    public int ReaderCount { get; set; }

    // The SQL of every executed non-query, in execution order.
    public ConcurrentQueue<string> NonQueryTexts { get; } = new();

    // How many executed non-queries deleted FROM table. Matched on the delete target and not on the
    // table appearing anywhere in the statement: a delete from one table can name another in its
    // WHERE clause, and counting those would report one sweep as several.
    public int DeletesAgainst(string table) => NonQueryTexts.Count(
        sql => Regex.IsMatch(
            sql, $"""DELETE\s+FROM\s+"?{Regex.Escape(table)}"?""",
            RegexOptions.IgnoreCase));

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        ReaderCount++;
        return ValueTask.FromResult(result);
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        ReaderCount++;
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        NonQueryTexts.Enqueue(command.CommandText);
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        NonQueryTexts.Enqueue(command.CommandText);
        return result;
    }
}
