using System.Xml.Linq;
using System.Xml;

namespace Lineup.Core;

/// <summary>
/// Creates and reads compact internal envelopes for unmodeled XMLTV metadata.
/// </summary>
internal sealed class XmltvSupplementalMetadata
{
    private static readonly XNamespace EnvelopeNamespace = "urn:lineup:xmltv-supplemental:v1";
    private readonly XElement _root;

    private XmltvSupplementalMetadata(XElement root)
    {
        _root = root;
    }

    /// <summary>
    /// Gets the preserved attributes.
    /// </summary>
    public IReadOnlyList<XAttribute> Attributes => _root.Element(EnvelopeNamespace + "attributes")?
        .Elements(EnvelopeNamespace + "attribute")
        .Select(element => new XAttribute(
            XName.Get((string?)element.Attribute("name") ?? throw new InvalidDataException("Supplemental XMLTV attribute name is missing."),
                (string?)element.Attribute("namespace") ?? string.Empty),
            (string?)element.Attribute("value") ?? string.Empty))
        .ToArray() ?? [];

    /// <summary>
    /// Gets preserved complete XMLTV child elements.
    /// </summary>
    public IReadOnlyList<XElement> Elements => _root.Element(EnvelopeNamespace + "elements")?
        .Elements()
        .Select(element => new XElement(element))
        .ToArray() ?? [];

    /// <summary>
    /// Creates an envelope, returning <see langword="null"/> when it would be empty.
    /// </summary>
    public static string? Create(
        IEnumerable<XAttribute>? attributes = null,
        IEnumerable<XElement>? elements = null,
        IEnumerable<(string Key, XElement Element)>? ownedElements = null)
    {
        var attributeElements = attributes?
            .Where(attribute => !attribute.IsNamespaceDeclaration)
            .Select(attribute => new XElement(
                EnvelopeNamespace + "attribute",
                new XAttribute("name", attribute.Name.LocalName),
                new XAttribute("namespace", attribute.Name.NamespaceName),
                new XAttribute("value", attribute.Value)))
            .ToArray() ?? [];
        var preservedElements = elements?.Select(element => new XElement(element)).ToArray() ?? [];
        var owned = ownedElements?
            .Select(item => new XElement(
                EnvelopeNamespace + "owned",
                new XAttribute("key", item.Key),
                new XElement(item.Element)))
            .ToArray() ?? [];
        if (attributeElements.Length == 0 && preservedElements.Length == 0 && owned.Length == 0)
        {
            return null;
        }

        var root = new XElement(EnvelopeNamespace + "supplemental");
        if (attributeElements.Length > 0)
        {
            root.Add(new XElement(EnvelopeNamespace + "attributes", attributeElements));
        }
        if (preservedElements.Length > 0)
        {
            root.Add(new XElement(EnvelopeNamespace + "elements", preservedElements));
        }
        root.Add(owned);
        return root.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Parses a persisted envelope.
    /// </summary>
    public static XmltvSupplementalMetadata? Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using var textReader = new StringReader(content);
            using var reader = XmlReader.Create(textReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            var root = XElement.Load(reader, LoadOptions.None);
            if (root.Name != EnvelopeNamespace + "supplemental")
            {
                throw new InvalidDataException("Supplemental XMLTV metadata has an unsupported format.");
            }
            return new XmltvSupplementalMetadata(root);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidDataException("Supplemental XMLTV metadata is invalid.", ex);
        }
    }

    /// <summary>
    /// Gets an attribute-only shell for a typed XMLTV element.
    /// </summary>
    public XElement? GetOwnedElement(string key)
    {
        var owned = _root.Elements(EnvelopeNamespace + "owned")
            .FirstOrDefault(element => string.Equals((string?)element.Attribute("key"), key, StringComparison.Ordinal));
        return owned?.Elements().Select(element => new XElement(element)).SingleOrDefault();
    }
}
