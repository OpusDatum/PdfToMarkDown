using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace PdfToMarkDown.Converters;

public class TaggedPdfConverter : IPdfToMarkdownConverter
{
    public string Convert(string pdfPath)
    {
        var md = new MarkdownBuilder();

        using var document = PdfDocument.Open(pdfPath);

        foreach (var page in document.GetPages())
        {
            var markedContents = page.GetMarkedContents();

            foreach (var element in markedContents)
            {
                ProcessElement(element, md, new ConversionState());
            }
        }

        return md.ToString();
    }

    private void ProcessElement(MarkedContentElement element, MarkdownBuilder md, ConversionState state)
    {
        if (element.IsArtifact)
            return;

        var tag = element.Tag?.ToUpperInvariant() ?? string.Empty;

        switch (tag)
        {
            case "H1":
                md.AppendHeading(1, ExtractText(element));
                return;
            case "H2":
                md.AppendHeading(2, ExtractText(element));
                return;
            case "H3":
                md.AppendHeading(3, ExtractText(element));
                return;
            case "H4":
                md.AppendHeading(4, ExtractText(element));
                return;
            case "H5":
                md.AppendHeading(5, ExtractText(element));
                return;
            case "H6":
                md.AppendHeading(6, ExtractText(element));
                return;
            case "P":
            case "SPAN":
                var text = ExtractText(element);
                if (!string.IsNullOrWhiteSpace(text))
                    md.AppendParagraph(text);
                return;
            case "L":
                ProcessList(element, md);
                return;
            case "TABLE":
                ProcessTable(element, md);
                return;
            case "FIGURE":
                var alt = element.AlternateDescription ?? "Figure";
                md.AppendFigurePlaceholder(alt);
                return;
        }

        // For unknown tags or structural containers, recurse into children
        foreach (var child in element.Children)
        {
            ProcessElement(child, md, state);
        }

        // If no children but has letters directly, emit as paragraph
        if (element.Children.Count == 0 && element.Letters.Count > 0)
        {
            var directText = ExtractText(element);
            if (!string.IsNullOrWhiteSpace(directText))
                md.AppendParagraph(directText);
        }
    }

    private void ProcessList(MarkedContentElement listElement, MarkdownBuilder md)
    {
        md.EnsureBlankLine();
        int itemNumber = 0;
        bool isOrdered = false;

        foreach (var child in listElement.Children)
        {
            var childTag = child.Tag?.ToUpperInvariant() ?? string.Empty;
            if (childTag == "LI")
            {
                itemNumber++;
                var bodyText = ExtractListItemText(child, out var label);

                // Detect ordered list by label pattern
                if (itemNumber == 1 && !string.IsNullOrEmpty(label))
                {
                    var trimmedLabel = label.Trim().TrimEnd('.', ')');
                    isOrdered = int.TryParse(trimmedLabel, out _);
                }

                if (isOrdered)
                    md.AppendNumberedItem(itemNumber, bodyText);
                else
                    md.AppendBulletItem(bodyText);
            }
        }
    }

    private string ExtractListItemText(MarkedContentElement liElement, out string label)
    {
        label = string.Empty;
        var bodyParts = new List<string>();

        foreach (var child in liElement.Children)
        {
            var childTag = child.Tag?.ToUpperInvariant() ?? string.Empty;
            switch (childTag)
            {
                case "LBL":
                    label = ExtractText(child);
                    break;
                case "LBODY":
                    var text = ExtractText(child);
                    if (!string.IsNullOrWhiteSpace(text))
                        bodyParts.Add(text);
                    break;
                default:
                    var fallback = ExtractText(child);
                    if (!string.IsNullOrWhiteSpace(fallback))
                        bodyParts.Add(fallback);
                    break;
            }
        }

        if (bodyParts.Count == 0)
        {
            // Try direct letters on the LI element
            var directText = ExtractText(liElement);
            if (!string.IsNullOrWhiteSpace(directText))
                bodyParts.Add(directText);
        }

        return string.Join(" ", bodyParts);
    }

    private void ProcessTable(MarkedContentElement tableElement, MarkdownBuilder md)
    {
        var rows = new List<List<string>>();
        bool hasHeader = false;

        foreach (var child in tableElement.Children)
        {
            var childTag = child.Tag?.ToUpperInvariant() ?? string.Empty;
            if (childTag == "THEAD")
            {
                hasHeader = true;
                foreach (var row in child.Children)
                {
                    var rowTag = row.Tag?.ToUpperInvariant() ?? string.Empty;
                    if (rowTag == "TR")
                        rows.Add(ExtractTableRow(row));
                }
            }
            else if (childTag == "TBODY")
            {
                foreach (var row in child.Children)
                {
                    var rowTag = row.Tag?.ToUpperInvariant() ?? string.Empty;
                    if (rowTag == "TR")
                        rows.Add(ExtractTableRow(row));
                }
            }
            else if (childTag == "TR")
            {
                rows.Add(ExtractTableRow(child));
            }
        }

        if (rows.Count == 0)
            return;

        // Normalize column count
        int maxCols = rows.Max(r => r.Count);
        foreach (var row in rows)
        {
            while (row.Count < maxCols)
                row.Add(string.Empty);
        }

        // First row is header if THEAD was found, or use first row as header
        var headerRow = rows[0];
        md.AppendTableHeader(headerRow);

        int startIndex = hasHeader ? GetHeaderRowCount(tableElement) : 1;
        for (int i = startIndex; i < rows.Count; i++)
        {
            md.AppendTableRow(rows[i]);
        }
    }

    private int GetHeaderRowCount(MarkedContentElement tableElement)
    {
        int count = 0;
        foreach (var child in tableElement.Children)
        {
            var tag = child.Tag?.ToUpperInvariant() ?? string.Empty;
            if (tag == "THEAD")
            {
                foreach (var row in child.Children)
                {
                    var rowTag = row.Tag?.ToUpperInvariant() ?? string.Empty;
                    if (rowTag == "TR")
                        count++;
                }
            }
        }
        return count > 0 ? count : 1;
    }

    private List<string> ExtractTableRow(MarkedContentElement rowElement)
    {
        var cells = new List<string>();
        foreach (var cell in rowElement.Children)
        {
            cells.Add(ExtractText(cell));
        }

        // If no children, try the row's own letters
        if (cells.Count == 0 && rowElement.Letters.Count > 0)
        {
            cells.Add(ExtractText(rowElement));
        }

        return cells;
    }

    private string ExtractText(MarkedContentElement element)
    {
        // Prefer ActualText replacement if available
        if (!string.IsNullOrEmpty(element.ActualText))
            return element.ActualText;

        // Collect all letters from this element and its children
        var allLetters = CollectLetters(element);

        if (allLetters.Count == 0)
            return string.Empty;

        // Sort by reading order: top-to-bottom, then left-to-right
        var sorted = allLetters
            .OrderByDescending(l => l.GlyphRectangle.Top)
            .ThenBy(l => l.GlyphRectangle.Left)
            .ToList();

        var sb = new System.Text.StringBuilder();
        Letter? prev = null;

        foreach (var letter in sorted)
        {
            if (prev != null)
            {
                // Detect line break: significant vertical gap
                double verticalGap = Math.Abs(prev.GlyphRectangle.Top - letter.GlyphRectangle.Top);
                if (verticalGap > prev.GlyphRectangle.Height * 0.5)
                {
                    sb.Append(' ');
                }
                else
                {
                    // Detect word spacing: horizontal gap
                    double horizontalGap = letter.GlyphRectangle.Left - prev.GlyphRectangle.Right;
                    double spaceThreshold = prev.GlyphRectangle.Width * 0.3;
                    if (horizontalGap > spaceThreshold)
                    {
                        sb.Append(' ');
                    }
                }
            }

            sb.Append(letter.Value);
            prev = letter;
        }

        return sb.ToString().Trim();
    }

    private List<Letter> CollectLetters(MarkedContentElement element)
    {
        var letters = new List<Letter>(element.Letters);

        foreach (var child in element.Children)
        {
            if (!child.IsArtifact)
                letters.AddRange(CollectLetters(child));
        }

        return letters;
    }

    /// <summary>
    /// Checks whether a page has meaningful tagged structure (not just Artifact markers).
    /// </summary>
    public static bool HasMeaningfulTags(IReadOnlyList<MarkedContentElement> markedContents)
    {
        foreach (var element in markedContents)
        {
            if (HasStructureTag(element))
                return true;
        }
        return false;
    }

    private static readonly HashSet<string> StructureTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "H1", "H2", "H3", "H4", "H5", "H6",
        "P", "SPAN", "L", "LI", "LBL", "LBODY",
        "TABLE", "TR", "TD", "TH", "THEAD", "TBODY",
        "FIGURE", "SECT", "DIV", "BLOCKQUOTE",
        "DOCUMENT", "PART", "ART", "TOC", "TOCI"
    };

    private static bool HasStructureTag(MarkedContentElement element)
    {
        if (element.IsArtifact)
            return false;

        var tag = element.Tag ?? string.Empty;
        if (StructureTags.Contains(tag))
            return true;

        foreach (var child in element.Children)
        {
            if (HasStructureTag(child))
                return true;
        }

        return false;
    }

    private class ConversionState
    {
        // Reserved for future state tracking during recursive processing
    }
}
