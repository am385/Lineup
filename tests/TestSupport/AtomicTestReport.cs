using System.Text;

namespace Lineup.TestSupport;

/// <summary>Publishes activation-specific test readiness reports only after their contents have been completely written and closed.</summary>
internal static class AtomicTestReport
{
    /// <summary>Publishes a complete report from a synchronous launch barrier without blocking on asynchronous continuations.</summary>
    /// <param name="path">The activation-specific committed report path.</param>
    /// <param name="contents">The complete report contents.</param>
    internal static void Write(string path, string contents)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, contents, new UTF8Encoding(false));
            File.Move(temporaryPath, path);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    /// <summary>Writes and atomically publishes a complete report without replacing another activation's report.</summary>
    /// <param name="path">The activation-specific committed report path.</param>
    /// <param name="contents">The complete report contents.</param>
    /// <param name="cancellationToken">Token canceling publication before commit.</param>
    /// <returns>A task completing after the report is committed.</returns>
    internal static Task WriteAsync(string path, string contents, CancellationToken cancellationToken = default) =>
        WriteAsync(path, writer => writer.WriteAsync(contents.AsMemory(), cancellationToken), cancellationToken);

    /// <summary>Stages a producer's complete report, closes it and atomically commits it; failure never publishes partial readiness.</summary>
    /// <param name="path">The activation-specific committed report path.</param>
    /// <param name="write">The producer, which must finish writing before returning.</param>
    /// <param name="cancellationToken">Token canceling publication before commit.</param>
    /// <returns>A task completing after the report is committed.</returns>
    internal static async Task WriteAsync(string path, Func<TextWriter, Task> write, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await write(writer);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
