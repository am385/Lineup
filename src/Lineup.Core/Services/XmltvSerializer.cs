using System.Xml.Serialization;
using Lineup.Core.Models.Xmltv;

namespace Lineup.Core.Services;

/// <summary>
/// Service for serializing and deserializing XMLTV documents
/// </summary>
public static class XmltvSerializer
{
    private static readonly XmlSerializer _serializer = new(typeof(XmltvDocument));

    /// <summary>
    /// Deserialize an XMLTV document from a file
    /// </summary>
    /// <param name="filePath">Path to the XML file</param>
    /// <returns>Deserialized XMLTV document or null if deserialization fails</returns>
    public static XmltvDocument? DeserializeFromFile(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        return _serializer.Deserialize(stream) as XmltvDocument;
    }

    /// <summary>
    /// Deserialize an XMLTV document from a stream
    /// </summary>
    /// <param name="stream">Stream containing XML data</param>
    /// <returns>Deserialized XMLTV document or null if deserialization fails</returns>
    public static XmltvDocument? DeserializeFromStream(Stream stream)
    {
        return _serializer.Deserialize(stream) as XmltvDocument;
    }

    /// <summary>
    /// Serialize an XMLTV document to a file
    /// </summary>
    /// <param name="document">The XMLTV document to serialize</param>
    /// <param name="filePath">Path where the XML file will be written</param>
    public static void SerializeToFile(XmltvDocument document, string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        _serializer.Serialize(stream, document);
    }

    /// <summary>
    /// Serialize an XMLTV document to a stream
    /// </summary>
    /// <param name="document">The XMLTV document to serialize</param>
    /// <param name="stream">Stream to write XML data to</param>
    public static void SerializeToStream(XmltvDocument document, Stream stream)
    {
        _serializer.Serialize(stream, document);
    }
}
