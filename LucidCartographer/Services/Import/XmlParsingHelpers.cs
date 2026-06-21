using System.Xml.Linq;

namespace LucidCartographer.Services.Import;

/// <summary>
/// Shared XML parsing helpers for XML-based importers (GPX, KML, etc.).
/// Tries a namespaced lookup first, then falls back to local name only.
/// </summary>
internal static class XmlParsingHelpers
{
    public static XElement? FindElement(XElement parent, XNamespace ns, string localName) => parent.Element(ns + localName) ?? parent.Element(localName);

    public static XElement? FindDescendant(XElement parent, XNamespace ns, string localName)
    {
        return parent.Descendants(ns + localName).FirstOrDefault()
               ?? parent.Descendants(localName).FirstOrDefault();
    }
}
