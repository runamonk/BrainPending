using System.Net;
using System.Text;
using System.Xml.Linq;
using AvaloniaRichEditor.Documents;
using AvaloniaRichEditor.Formatters;

namespace MyNotes.Importing;

internal sealed record ConvertedPage(FlowDocument Document, IReadOnlyList<string> Warnings);

internal static class OneNoteConverter
{
    public static ConvertedPage Convert(string xml)
    {
        var page = XDocument.Parse(xml).Root ?? throw new InvalidDataException("Empty OneNote page.");
        if (page.Name.LocalName != "Page") throw new InvalidDataException("Expected a OneNote page.");
        var warnings = new HashSet<string>();
        var html = new StringBuilder();
        string Encode(string text) => WebUtility.HtmlEncode(text);
        void Render(XElement element)
        {
            switch (element.Name.LocalName)
            {
                case "T": html.Append(element.Value); break; // OneNote stores formatted HTML in CDATA.
                case "OE":
                    var list = element.Elements().FirstOrDefault(e => e.Name.LocalName == "List");
                    var tag = list == null ? "div" : "li";
                    var listTag = list?.Elements().Any(e => e.Name.LocalName == "Number") == true ? "ol" : "ul";
                    if (list != null) html.Append('<').Append(listTag).Append('>');
                    html.Append('<').Append(tag);
                    var style = (string?)element.Attribute("style");
                    if (style != null) html.Append(" style=\"").Append(Encode(style)).Append('"');
                    html.Append('>');
                    foreach (var child in element.Elements()) Render(child);
                    html.Append("</").Append(tag).Append('>');
                    if (list != null) html.Append("</").Append(listTag).Append('>');
                    break;
                case "Table": Wrap("table", element); break;
                case "Row": Wrap("tr", element); break;
                case "Cell": Wrap("td", element); break;
                case "Image":
                    var data = element.Elements().FirstOrDefault(e => e.Name.LocalName == "Data")?.Value;
                    var format = ((string?)element.Attribute("format") ?? "png").ToLowerInvariant();
                    if (data == null || format is not ("png" or "jpg" or "jpeg" or "gif" or "bmp"))
                    { warnings.Add("Some images could not be imported."); html.Append("<p>[Image not imported]</p>"); break; }
                    byte[] bytes;
                    try
                    {
                        bytes = System.Convert.FromBase64String(data);
                        using var stream = new MemoryStream(bytes);
                        using var bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                    }
                    catch (Exception error) when (error is FormatException or ArgumentException or InvalidOperationException)
                    {
                        warnings.Add("An unreadable image was replaced with a placeholder.");
                        html.Append("<p>[Image not imported]</p>");
                        break;
                    }
                    html.Append("<img src=\"data:image/").Append(format).Append(";base64,")
                        .Append(System.Convert.ToBase64String(bytes)).Append('"');
                    var size = element.Elements().FirstOrDefault(e => e.Name.LocalName == "Size");
                    foreach (var dimension in new[] { "width", "height" })
                    {
                        if (double.TryParse((string?)size?.Attribute(dimension), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var points) && double.IsFinite(points) && points > 0)
                            html.Append(' ').Append(dimension).Append("=\"").Append((points * 96 / 72).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('"');
                    }
                    html.Append(" />");
                    break;
                case "InsertedFile": case "InkDrawing": case "Ink": case "MediaFile": case "AudioFile": case "VideoFile":
                    var description = element.Name.LocalName == "InsertedFile" ? "Attachment" : "Ink or media";
                    warnings.Add(description + " could not be imported; retained in OneNote.");
                    html.Append("<p>[").Append(description).Append(" not imported: ")
                        .Append(Encode((string?)element.Attribute("preferredName") ?? element.Name.LocalName)).Append("]</p>");
                    break;
                case "Tag": warnings.Add("OneNote tags and checkbox states were not converted."); break;
                case "Position": case "Size": case "List": case "Meta": case "TagDef":
                case "QuickStyleDef": case "PageSettings": case "Columns": case "Column": break;
                default:
                    foreach (var child in element.Elements()) Render(child);
                    break;
            }
        }
        void Wrap(string tag, XElement element)
        {
            html.Append('<').Append(tag).Append('>');
            foreach (var child in element.Elements()) Render(child);
            html.Append("</").Append(tag).Append('>');
        }
        // The page title becomes the note name and is already shown above the editor.
        // Exclude only title metadata; matching headings within outlines are content.
        foreach (var child in page.Elements().Where(e => e.Name.LocalName != "Title")
            .OrderBy(e => Position(e, "y")).ThenBy(e => Position(e, "x"))) Render(child);
        if (page.Elements().Count(e => e.Name.LocalName == "Outline") > 1)
            warnings.Add("Positioned content was arranged top-to-bottom, then left-to-right.");
        if (html.ToString().Contains("onenote:", StringComparison.OrdinalIgnoreCase))
            warnings.Add("Links to OneNote pages still open the original OneNote pages.");
        return new(HtmlDocumentFormatter.ParseHtml(html.ToString(), allowLocalFileImages: false, allowRemoteImages: false), warnings.ToArray());
    }

    private static double Position(XElement element, string axis) =>
        double.TryParse((string?)element.Elements().FirstOrDefault(e => e.Name.LocalName == "Position")?.Attribute(axis),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;
}
