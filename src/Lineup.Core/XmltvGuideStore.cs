using System.Security.Cryptography;
using System.Text;

namespace Lineup.Core;

/// <summary>
/// Stores the last validated canonical XMLTV document on disk.
/// </summary>
public class XmltvGuideStore
{
    private readonly string _cachePath;
    private string GenerationPath => $"{_cachePath}.generation";

    /// <summary>
    /// Initializes a new instance of the <see cref="XmltvGuideStore"/> class.
    /// </summary>
    /// <param name="cachePath">Private cache path for the canonical document.</param>
    public XmltvGuideStore(string cachePath)
    {
        _cachePath = Path.GetFullPath(cachePath);
    }

    /// <summary>
    /// Atomically replaces the cached canonical XMLTV document.
    /// </summary>
    public async Task StoreAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        using var stagedGuide = await StageAsync(content, cancellationToken);
        stagedGuide.Commit();
    }

    /// <summary>
    /// Writes a guide to a temporary sibling file that can be atomically committed later.
    /// </summary>
    /// <param name="content">The canonical XMLTV document.</param>
    /// <param name="cancellationToken">A token used to cancel staging.</param>
    /// <returns>The staged guide commit.</returns>
    public Task<StagedXmltvGuide> StageAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        return StagedXmltvGuide.CreateAsync(_cachePath, content, cancellationToken);
    }

    /// <summary>
    /// Reads the authoritative canonical guide when one has been published.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the read.</param>
    /// <returns>The canonical guide content, or <see langword="null"/> when no guide exists.</returns>
    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken = default)
    {
        return File.Exists(_cachePath) ? await File.ReadAllBytesAsync(_cachePath, cancellationToken) : null;
    }

    /// <summary>
    /// Determines whether the normalized database has committed the supplied canonical guide generation.
    /// </summary>
    /// <param name="content">The canonical XMLTV content.</param>
    /// <param name="cancellationToken">A token used to cancel the marker read.</param>
    /// <returns><see langword="true"/> when the generation marker matches the content.</returns>
    public async Task<bool> IsGenerationRecordedAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(GenerationPath))
        {
            return false;
        }

        var recordedGeneration = await File.ReadAllTextAsync(GenerationPath, cancellationToken);
        return string.Equals(recordedGeneration.Trim(), CalculateGeneration(content.Span), StringComparison.Ordinal);
    }

    /// <summary>
    /// Records that the normalized database committed the supplied canonical guide generation.
    /// </summary>
    /// <param name="content">The canonical XMLTV content.</param>
    /// <param name="cancellationToken">A token used to cancel marker staging.</param>
    public Task RecordGenerationAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        return WriteAtomicallyAsync(GenerationPath, Encoding.ASCII.GetBytes(CalculateGeneration(content.Span)), cancellationToken);
    }

    private static string CalculateGeneration(ReadOnlySpan<byte> content)
    {
        return Convert.ToHexString(SHA256.HashData(content));
    }

    /// <summary>
    /// Copies the cached canonical XMLTV document to the configured public output path.
    /// </summary>
    public async Task CopyToAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_cachePath))
        {
            throw new InvalidOperationException("No canonical XMLTV guide has been downloaded yet.");
        }

        var content = await File.ReadAllBytesAsync(_cachePath, cancellationToken);
        await WriteAtomicallyAsync(Path.GetFullPath(outputPath), content, cancellationToken);
    }

    /// <summary>
    /// Atomically publishes supplied XMLTV content to a public output path.
    /// </summary>
    /// <param name="outputPath">Destination XMLTV path.</param>
    /// <param name="content">Filtered XMLTV content to publish.</param>
    /// <param name="cancellationToken">Cancels staging before publication.</param>
    public Task PublishAsync(string outputPath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        return WriteAtomicallyAsync(Path.GetFullPath(outputPath), content, cancellationToken);
    }

    private static async Task WriteAtomicallyAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        using var stagedGuide = await StagedXmltvGuide.CreateAsync(path, content, cancellationToken);
        stagedGuide.Commit();
    }

    /// <summary>
    /// Represents a fully written XMLTV file awaiting its atomic commit.
    /// </summary>
    public sealed class StagedXmltvGuide : IDisposable
    {
        private readonly string _destinationPath;
        private string? _temporaryPath;

        private StagedXmltvGuide(string destinationPath, string temporaryPath)
        {
            _destinationPath = destinationPath;
            _temporaryPath = temporaryPath;
        }

        /// <summary>
        /// Writes a guide to a temporary sibling file.
        /// </summary>
        /// <param name="destinationPath">The eventual canonical guide path.</param>
        /// <param name="content">The canonical XMLTV content.</param>
        /// <param name="cancellationToken">A token used to cancel staging.</param>
        /// <returns>The staged guide commit.</returns>
        internal static async Task<StagedXmltvGuide> CreateAsync(string destinationPath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, content.ToArray(), cancellationToken);
                return new StagedXmltvGuide(destinationPath, temporaryPath);
            }
            catch
            {
                File.Delete(temporaryPath);
                throw;
            }
        }

        /// <summary>
        /// Atomically replaces the destination with the staged guide.
        /// </summary>
        public void Commit()
        {
            var temporaryPath = Volatile.Read(ref _temporaryPath);
            ObjectDisposedException.ThrowIf(temporaryPath == null, this);
            File.Move(temporaryPath, _destinationPath, overwrite: true);
            Interlocked.CompareExchange(ref _temporaryPath, null, temporaryPath);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            var temporaryPath = Interlocked.Exchange(ref _temporaryPath, null);
            if (temporaryPath != null)
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
