using System.Collections.Concurrent;

namespace Lineup.Web.Services;

/// <summary>
/// Owns transient, contained WebVTT sidecars for active Watch sessions.
/// </summary>
public sealed class SubtitleSidecarService
{
    /// <summary>Gets the application-owned transient subtitle directory name.</summary>
    public const string DirectoryName = "lineup-subtitles";

    private readonly ITransientDataStore _transientStore;
    private readonly string _root;
    private readonly ConcurrentDictionary<string, string> _sessions = new(StringComparer.Ordinal);

    /// <summary>Initializes an isolated store beneath the configured transient root.</summary>
    /// <param name="transientStore">Configured transient data store.</param>
    public SubtitleSidecarService(ITransientDataStore transientStore)
    {
        _transientStore = transientStore;
        _root = transientStore.ResetSubtitleWorkspace();
    }

    /// <summary>Initializes an isolated store and removes stale sidecars from the supplied test root.</summary>
    /// <param name="rootDirectory">Application-owned root used by tests.</param>
    internal SubtitleSidecarService(string rootDirectory)
        : this(new TransientDataStore(rootDirectory))
    {
    }

    /// <summary>Creates and registers a contained path for a validated session identifier.</summary>
    public string Create(string sessionId)
    {
        ValidateSessionId(sessionId);
        var path = _transientStore.GetFilePath(_root, $"{sessionId}.vtt");
        _sessions[sessionId] = path;
        return path;
    }

    /// <summary>Opens the current sidecar with sharing enabled for FFmpeg.</summary>
    public Stream? OpenRead(string sessionId)
    {
        ValidateSessionId(sessionId);
        return _sessions.TryGetValue(sessionId, out var path) ? _transientStore.OpenRead(path) : null;
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

        if (!_sessions.TryGetValue(sessionId, out var path))
        {
            return null;
        }

        using var stream = _transientStore.OpenRead(path);
        if (stream == null)
        {
            return null;
        }
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
            _transientStore.DeleteFile(path);
        }
    }

    /// <summary>Removes every transient subtitle artifact.</summary>
    public void Clear()
    {
        _sessions.Clear();
        _transientStore.DeleteDirectory(_root);
        _ = _transientStore.ResetSubtitleWorkspace();
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
