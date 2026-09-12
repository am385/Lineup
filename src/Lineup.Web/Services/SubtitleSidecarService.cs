using System.Collections.Concurrent;

namespace Lineup.Web.Services;

/// <summary>
/// Owns transient, contained WebVTT sidecars for active Watch sessions.
/// </summary>
public sealed class SubtitleSidecarService
{
    /// <summary>Gets the application-owned transient subtitle directory name.</summary>
    public const string DirectoryName = "lineup-subtitles";

    private readonly string _root;
    private readonly ConcurrentDictionary<string, string> _sessions = new(StringComparer.Ordinal);

    /// <summary>Initializes the store and removes stale sidecars left by an earlier process.</summary>
    /// <param name="rootDirectory">Optional application-owned root used by tests.</param>
    public SubtitleSidecarService(string? rootDirectory = null)
    {
        _root = Path.GetFullPath(rootDirectory ?? Path.Combine(Path.GetTempPath(), DirectoryName));
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        Directory.CreateDirectory(_root);
    }

    /// <summary>Creates and registers a contained path for a validated session identifier.</summary>
    public string Create(string sessionId)
    {
        ValidateSessionId(sessionId);
        var path = Path.Combine(_root, $"{sessionId}.vtt");
        _sessions[sessionId] = path;
        return path;
    }

    /// <summary>Opens the current sidecar with sharing enabled for FFmpeg.</summary>
    public Stream? OpenRead(string sessionId)
    {
        ValidateSessionId(sessionId);
        return _sessions.TryGetValue(sessionId, out var path) && File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
            : null;
    }

    /// <summary>Reads only bytes appended at or after the requested sidecar offset.</summary>
    /// <param name="sessionId">The validated active subtitle session.</param>
    /// <param name="offset">The zero-based byte offset previously returned to the client.</param>
    /// <returns>The available bytes and the next offset, or <see langword="null"/> when the sidecar is not available.</returns>
    public SubtitleSidecarChunk? ReadFrom(string sessionId, long offset)
    {
        ValidateSessionId(sessionId);
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "The subtitle offset cannot be negative.");
        }

        if (!_sessions.TryGetValue(sessionId, out var path) || !File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var effectiveOffset = Math.Min(offset, stream.Length);
        stream.Position = effectiveOffset;
        var data = new byte[(int)Math.Min(stream.Length - effectiveOffset, 64 * 1024)];
        stream.ReadExactly(data);
        return new SubtitleSidecarChunk(data, effectiveOffset + data.Length);
    }

    /// <summary>Removes the sidecar and unregisters its session.</summary>
    public void Remove(string sessionId)
    {
        ValidateSessionId(sessionId);
        if (_sessions.TryRemove(sessionId, out var path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Removes every transient subtitle artifact.</summary>
    public void Clear()
    {
        _sessions.Clear();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        Directory.CreateDirectory(_root);
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (sessionId.Length is < 1 or > 64 || sessionId.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException("The subtitle session identifier is invalid.", nameof(sessionId));
        }
    }
}

/// <summary>
/// Contains one bounded append-only read from a WebVTT sidecar.
/// </summary>
/// <param name="Data">The bytes available after the requested offset.</param>
/// <param name="NextOffset">The offset to use for the next read.</param>
public sealed record SubtitleSidecarChunk(byte[] Data, long NextOffset);
