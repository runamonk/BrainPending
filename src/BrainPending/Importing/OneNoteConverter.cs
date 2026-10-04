using System.Net;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using AvaloniaRichEditor.Documents;
using AvaloniaRichEditor.Formatters;
using BrainPending.Core;

namespace BrainPending.Importing;

internal sealed record ConvertedPage(FlowDocument Document, IReadOnlyList<string> Warnings);

internal static class OneNoteConverter
{
    public static ConvertedPage Convert(string xml, Func<XElement, StoredAttachment>? saveAttachment = null)
    {
        var page = XDocument.Parse(xml).Root ?? throw new InvalidDataException("Empty OneNote page.");
        if (page.Name.LocalName != "Page") throw new InvalidDataException("Expected a OneNote page.");
        var warnings = new HashSet<string>();
        var html = new StringBuilder();
        var styles = page.Elements().Where(e => e.Name.LocalName == "QuickStyleDef")
            .ToDictionary(e => (string)e.Attribute("index")!, e => e);
        string Encode(string text) => WebUtility.HtmlEncode(text);
        string TextStyle(XElement element)
        {
            var declarations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ancestor in element.AncestorsAndSelf().Reverse())
            {
                if ((string?)ancestor.Attribute("quickStyleIndex") is { } index && styles.TryGetValue(index, out var definition))
                {
                    foreach (var (attribute, property) in new[] { ("font", "font-family"), ("fontSize", "font-size"),
                        ("fontColor", "color"), ("highlightColor", "background-color") })
                        if ((string?)definition.Attribute(attribute) is { } value)
                            declarations[property] = value == "automatic" ? (property == "color" ? "#000000" : "transparent")
                                : value + (attribute == "fontSize" ? "pt" : "");
                    bool Enabled(string name) => (string?)definition.Attribute(name) is "true" or "1";
                    declarations["font-weight"] = Enabled("bold") ? "bold" : "normal";
                    declarations["font-style"] = Enabled("italic") ? "italic" : "normal";
                    declarations["text-decoration"] = string.Join(' ',
                        new[] { Enabled("underline") ? "underline" : "", Enabled("strikethrough") ? "line-through" : "" }).Trim();
                }
                foreach (var declaration in ((string?)ancestor.Attribute("style") ?? "").Split(';'))
                {
                    var pair = declaration.Split(':', 2);
                    if (pair.Length == 2) declarations[pair[0].Trim()] = pair[1].Trim();
                }
            }
            return string.Join(';', declarations.Select(d => d.Key + ":" + d.Value));
        }
        (string Tag, string Style)? ListStyle(XElement element)
        {
            var list = element.Elements().FirstOrDefault(e => e.Name.LocalName == "List");
            if (list == null) return null;
            var number = list.Elements().FirstOrDefault(e => e.Name.LocalName == "Number");
            if (number == null)
            {
                var bullet = list.Elements().FirstOrDefault(e => e.Name.LocalName == "Bullet");
                return ("ul", (string?)bullet?.Attribute("bullet") == "3" ? "circle" : "disc");
            }
            var marker = (string?)number.Attribute("numberSequence") switch
            {
                "2" => "lower-roman", "3" => "upper-alpha", "4" => "lower-alpha",
                _ => "decimal"
            };
            if ((string?)number.Attribute("numberSequence") is { } sequence && sequence is not ("0" or "2" or "3" or "4"))
                warnings.Add("Some number styles were converted to decimal numbering.");
            return ("ol", marker);
        }
        void RenderChildren(XElement element)
        {
            (string Tag, string Style)? active = null;
            foreach (var child in element.Elements())
            {
                var next = ListStyle(child);
                if (active != next)
                {
                    if (active is { } previous) html.Append("</").Append(previous.Tag).Append('>');
                    if (next is { } current)
                        html.Append('<').Append(current.Tag).Append(" style=\"list-style-type:").Append(current.Style).Append("\">");
                    active = next;
                }
                Render(child);
            }
            if (active is { } last) html.Append("</").Append(last.Tag).Append('>');
        }
        void Render(XElement element)
        {
            switch (element.Name.LocalName)
            {
                case "T":
                    // Put inherited formatting on an inline wrapper, where the HTML reader applies it.
                    html.Append("<span data-are-fg=\"1\" style=\"").Append(Encode(TextStyle(element))).Append("\">")
                        .Append(element.Value).Append("</span>");
                    break;
                case "OEChildren": RenderChildren(element); break;
                case "OE":
                    var tag = ListStyle(element) == null ? "div" : "li";
                    html.Append('<').Append(tag);
                    var number = element.Elements().FirstOrDefault(e => e.Name.LocalName == "List")?
                        .Elements().FirstOrDefault(e => e.Name.LocalName == "Number");
                    if (int.TryParse((string?)number?.Attribute("restartNumberingAt"), out var restart) && restart > 0)
                        html.Append(" value=\"").Append(restart).Append('"');
                    if ((string?)element.Attribute("alignment") is { } alignment)
                        html.Append(" style=\"text-align:").Append(Encode(alignment)).Append('"');
                    html.Append('>');
                    foreach (var child in element.Elements()) Render(child);
                    html.Append("</").Append(tag).Append('>');
                    break;
                case "Table": Wrap("table", element); break;
                case "Row": Wrap("tr", element); break;
                case "Cell":
                    html.Append("<td");
                    if ((string?)element.Attribute("shadingColor") is { } shading && shading != "automatic")
                        html.Append(" style=\"background-color:").Append(Encode(shading)).Append('"');
                    html.Append('>');
                    foreach (var child in element.Elements()) Render(child);
                    html.Append("</td>");
                    break;
                case "Columns":
                    html.Append("<colgroup>");
                    foreach (var column in element.Elements().OrderBy(e => (int?)e.Attribute("index") ?? 0))
                    {
                        html.Append("<col");
                        if (double.TryParse((string?)column.Attribute("width"), NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
                            && double.IsFinite(width) && width > 0)
                            html.Append(" style=\"width:").Append((width * 96 / 72).ToString(CultureInfo.InvariantCulture)).Append("px\"");
                        html.Append('>');
                    }
                    html.Append("</colgroup>");
                    break;
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
                case "InsertedFile":
                    var name = (string?)element.Attribute("preferredName") ?? "attachment.bin";
                    if (saveAttachment == null)
                    {
                        html.Append("<p>[Attachment: ").Append(Encode(name)).Append("]</p>");
                        warnings.Add("Attachments will be copied when you import. Unavailable files will be reported.");
                        break;
                    }
                    try
                    {
                        var attachment = saveAttachment(element);
                        html.Append("<p><a href=\"").Append(Encode(attachment.Link)).Append("\">Attachment: ")
                            .Append(Encode(attachment.Name)).Append(" (").Append(attachment.Size.ToString("N0"))
                            .Append(" bytes)</a></p>");
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        warnings.Add($"Attachment '{name}' could not be imported: {error.Message}");
                        html.Append("<p>[Attachment not imported: ").Append(Encode(name)).Append("]</p>");
                    }
                    break;
                case "InkDrawing": case "Ink": case "MediaFile": case "AudioFile": case "VideoFile":
                    var description = "Ink or media";
                    warnings.Add(description + " could not be imported; retained in OneNote.");
                    html.Append("<p>[").Append(description).Append(" not imported: ")
                        .Append(Encode((string?)element.Attribute("preferredName") ?? element.Name.LocalName)).Append("]</p>");
                    break;
                case "Tag": warnings.Add("OneNote tags and checkbox states were not converted."); break;
                case "Position": case "Size": case "List": case "Meta": case "TagDef":
                case "QuickStyleDef": case "PageSettings": case "Column": break;
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
        // The page title becomes the thought name and is already shown above the editor.
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
