using System.Xml.Linq;

namespace LucidCartographer.Services.Import;

/// <summary>
/// Shared XML parsing helpers for XML-based importers (GPX, KML, etc.).
/// Tries a namespaced lookup first, then falls back to local name only.
/// </summary>
internal static class XmlParsingHelpers
{
    /// <summary>
    /// Finds a direct child element, trying the namespaced name first,
    /// then falling back to the local name without namespace.
    /// </summary>
    public static XElement? FindElement(XElement parent, XNamespace ns, string localName) => parent.Element(ns + localName) ?? parent.Element(localName);

    /// <summary>
    /// Finds a descendant element, trying the namespaced name first,
    /// then falling back to the local name without namespace.
    /// </summary>
    public static XElement? FindDescendant(XElement parent, XNamespace ns, string localName)
    {
        return parent.Descendants(ns + localName).FirstOrDefault()
               ?? parent.Descendants(localName).FirstOrDefault();
    }
}
