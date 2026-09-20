using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Renamer.Tests.TestSupport;

/// <summary>
/// Counts the reader commands a context executes, so a test can prove a read path issues a bounded
/// number of round-trips rather than one per id.
/// </summary>
/// <remarks>
/// The count is of commands and not of rows: EF may split one query with includes into a small,
/// constant number of readers, so an assertion built on it compares against the id count rather than
/// against an exact number.
/// </remarks>
public sealed class ReaderCountingInterceptor : DbCommandInterceptor
{
    public int ReaderCount { get; set; }

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
}
