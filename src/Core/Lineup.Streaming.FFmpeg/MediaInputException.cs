namespace Lineup.Streaming.FFmpeg;

/// <summary>Identifies a terminal upstream media failure independently of presentation readiness.</summary>
public class MediaInputException : IOException
{
    /// <summary>Initializes a terminal upstream failure with a safe description and optional retained cause.</summary>
    public MediaInputException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}

/// <summary>Identifies audio that was advertised or previously observed but is missing from a planned presentation.</summary>
public sealed class MediaAudioDiscoveryException : MediaInputException
{
    /// <summary>Initializes a safe incomplete-audio-discovery failure.</summary>
    public MediaAudioDiscoveryException() : base("The source advertises audio, but audio is missing from the discovered or processed media.")
    {
    }
}
