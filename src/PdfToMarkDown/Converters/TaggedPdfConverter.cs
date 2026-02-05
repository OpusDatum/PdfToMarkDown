using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace PdfToMarkDown.Converters;

public class TaggedPdfConverter : IPdfToMarkdownConverter
{
    private static readonly Regex MultiSpaceRegex = new(@" {2,}", RegexOptions.Compiled);

    // Per-page MCID lookup: pageNumber -> (mcid -> MarkedContentElement)
    private Dictionary<int, Dictionary<int, MarkedContentElement>> _mcidLookup = new();

    public string Convert(string pdfPath)
    {
        var md = new MarkdownBuilder();

        using var document = PdfDocument.Open(pdfPath);

        // Step 1: Read the structure tree
        var root = StructureTreeReader.Read(document);
        if (root == null)
            return string.Empty;

        // Step 2: Build per-page MCID lookup from content stream
        BuildMcidLookup(document);

        // Step 3: Walk the structure tree and generate Markdown
        foreach (var child in root.Children)
        {
            ProcessNode(child, md);
        }

        return md.ToString();
    }

    private void BuildMcidLookup(PdfDocument document)
    {
        _mcidLookup = new Dictionary<int, Dictionary<int, MarkedContentElement>>();

        foreach (var page in document.GetPages())
        {
            var pageLookup = new Dictionary<int, MarkedContentElement>();
            var markedContents = page.GetMarkedContents();
            IndexMarkedContents(markedContents, pageLookup);
            _mcidLookup[page.Number] = pageLookup;
        }
    }

    private void IndexMarkedContents(IReadOnlyList<MarkedContentElement> elements, Dictionary<int, MarkedContentElement> lookup)
    {
        foreach (var element in elements)
        {
            if (element.MarkedContentIdentifier >= 0)
            {
                lookup[element.MarkedContentIdentifier] = element;
            }

            // Recurse into children to find nested MCIDs
            if (element.Children.Count > 0)
            {
                IndexMarkedContents(element.Children, lookup);
            }
        }
    }

    private void ProcessNode(StructureNode node, MarkdownBuilder md)
    {
        var tag = node.Tag.ToUpperInvariant();

        switch (tag)
        {
            case "H1":
                md.AppendHeading(1, ExtractNodeText(node));
                return;
            case "H2":
                md.AppendHeading(2, ExtractNodeText(node));
                return;
            case "H3":
                md.AppendHeading(3, ExtractNodeText(node));
                return;
            case "H4":
                md.AppendHeading(4, ExtractNodeText(node));
                return;
            case "H5":
                md.AppendHeading(5, ExtractNodeText(node));
                return;
            case "H6":
                md.AppendHeading(6, ExtractNodeText(node));
                return;

            case "P":
                var pText = ExtractNodeText(node);
                if (!string.IsNullOrWhiteSpace(pText))
                    md.AppendParagraph(pText);
                return;

            case "L":
                ProcessList(node, md);
                return;

            case "TABLE":
                ProcessTable(node, md);
                return;

            case "FIGURE":
                md.AppendFigurePlaceholder("Figure");
                return;

            case "LINK":
            case "SPAN":
                // Inline elements - extract text and emit as paragraph
                var inlineText = ExtractNodeText(node);
                if (!string.IsNullOrWhiteSpace(inlineText))
                    md.AppendParagraph(inlineText);
                return;

            case "NOTE":
            case "FOOTNOTE":
                var noteText = ExtractNodeText(node);
                if (!string.IsNullOrWhiteSpace(noteText))
                    md.AppendParagraph(noteText);
                return;

            case "ARTIFACT":
            case "HEADER":
            case "FOOTER":
                // Skip artifacts and header/footer
                return;

            case "DOCUMENT":
            case "SECT":
            case "PART":
            case "DIV":
            case "ART":
            case "BLOCKQUOTE":
            case "TOC":
            case "TOCI":
            default:
                // Structural containers or unknown tags - recurse into children
                foreach (var child in node.Children)
                {
                    ProcessNode(child, md);
                }

                // If no children but has direct MCID refs, emit as paragraph
                if (node.Children.Count == 0 && node.McidRefs.Count > 0)
                {
                    var text = ExtractNodeText(node);
                    if (!string.IsNullOrWhiteSpace(text))
                        md.AppendParagraph(text);
                }
                return;
        }
    }

    private void ProcessList(StructureNode listNode, MarkdownBuilder md)
    {
        md.EnsureBlankLine();
        int itemNumber = 0;
        bool isOrdered = false;

        foreach (var child in listNode.Children)
        {
            var childTag = child.Tag.ToUpperInvariant();
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
            else
            {
                // Nested structure within list - recurse
                ProcessNode(child, md);
            }
        }
    }

    private string ExtractListItemText(StructureNode liNode, out string label)
    {
        label = string.Empty;
        var bodyParts = new List<string>();

        foreach (var child in liNode.Children)
        {
            var childTag = child.Tag.ToUpperInvariant();
            switch (childTag)
            {
                case "LBL":
                    label = ExtractNodeText(child);
                    break;
                case "LBODY":
                    var text = ExtractNodeText(child);
                    if (!string.IsNullOrWhiteSpace(text))
                        bodyParts.Add(text);
                    break;
                default:
                    var fallback = ExtractNodeText(child);
                    if (!string.IsNullOrWhiteSpace(fallback))
                        bodyParts.Add(fallback);
                    break;
            }
        }

        if (bodyParts.Count == 0)
        {
            // Try direct MCID refs on the LI node
            var directText = ExtractTextFromMcidRefs(liNode.McidRefs);
            if (!string.IsNullOrWhiteSpace(directText))
                bodyParts.Add(directText);
        }

        return string.Join(" ", bodyParts);
    }

    private void ProcessTable(StructureNode tableNode, MarkdownBuilder md)
    {
        var rows = new List<List<string>>();
        bool hasHeader = false;

        foreach (var child in tableNode.Children)
        {
            var childTag = child.Tag.ToUpperInvariant();
            if (childTag == "THEAD")
            {
                hasHeader = true;
                foreach (var row in child.Children)
                {
                    if (row.Tag.ToUpperInvariant() == "TR")
                        rows.Add(ExtractTableRow(row));
                }
            }
            else if (childTag == "TBODY")
            {
                foreach (var row in child.Children)
                {
                    if (row.Tag.ToUpperInvariant() == "TR")
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

        // First row is header
        md.AppendTableHeader(rows[0]);

        int startIndex = hasHeader ? GetHeaderRowCount(tableNode) : 1;
        for (int i = startIndex; i < rows.Count; i++)
        {
            md.AppendTableRow(rows[i]);
        }
    }

    private int GetHeaderRowCount(StructureNode tableNode)
    {
        int count = 0;
        foreach (var child in tableNode.Children)
        {
            if (child.Tag.ToUpperInvariant() == "THEAD")
            {
                foreach (var row in child.Children)
                {
                    if (row.Tag.ToUpperInvariant() == "TR")
                        count++;
                }
            }
        }
        return count > 0 ? count : 1;
    }

    private List<string> ExtractTableRow(StructureNode rowNode)
    {
        var cells = new List<string>();
        foreach (var cell in rowNode.Children)
        {
            cells.Add(ExtractNodeText(cell));
        }

        // If no children but has direct MCID refs
        if (cells.Count == 0 && rowNode.McidRefs.Count > 0)
        {
            cells.Add(ExtractTextFromMcidRefs(rowNode.McidRefs));
        }

        return cells;
    }

    /// <summary>
    /// Extract text for a structure node by collecting letters from all its MCID refs
    /// and recursively from child nodes.
    /// </summary>
    private string ExtractNodeText(StructureNode node)
    {
        var allLetters = new List<Letter>();
        CollectLettersFromNode(node, allLetters);

        if (allLetters.Count == 0)
            return string.Empty;

        return LettersToText(allLetters);
    }

    private void CollectLettersFromNode(StructureNode node, List<Letter> letters)
    {
        // Collect from direct MCID refs
        foreach (var mcidRef in node.McidRefs)
        {
            var element = LookupMcid(mcidRef.PageNumber, mcidRef.Mcid);
            if (element != null)
            {
                CollectLettersFromElement(element, letters);
            }
        }

        // Collect from children
        foreach (var child in node.Children)
        {
            CollectLettersFromNode(child, letters);
        }
    }

    private void CollectLettersFromElement(MarkedContentElement element, List<Letter> letters)
    {
        letters.AddRange(element.Letters);

        foreach (var child in element.Children)
        {
            if (!child.IsArtifact)
                CollectLettersFromElement(child, letters);
        }
    }

    private string ExtractTextFromMcidRefs(List<McidReference> mcidRefs)
    {
        var allLetters = new List<Letter>();

        foreach (var mcidRef in mcidRefs)
        {
            var element = LookupMcid(mcidRef.PageNumber, mcidRef.Mcid);
            if (element != null)
            {
                CollectLettersFromElement(element, allLetters);
            }
        }

        if (allLetters.Count == 0)
            return string.Empty;

        return LettersToText(allLetters);
    }

    private MarkedContentElement? LookupMcid(int pageNumber, int mcid)
    {
        if (_mcidLookup.TryGetValue(pageNumber, out var pageLookup))
        {
            if (pageLookup.TryGetValue(mcid, out var element))
                return element;
        }
        return null;
    }

    private string LettersToText(List<Letter> allLetters)
    {
        if (allLetters.Count == 0)
            return string.Empty;

        var words = NearestNeighbourWordExtractor.Instance.GetWords(allLetters);
        var wordList = words.ToList();

        if (wordList.Count == 0)
            return string.Empty;

        // Build a lookup: for each letter, find its index in the collection order
        var letterIndex = new Dictionary<Letter, int>();
        for (int i = 0; i < allLetters.Count; i++)
            letterIndex[allLetters[i]] = i;

        // Order words by the minimum content stream index of any of their letters
        var orderedWords = wordList
            .Select(w => new
            {
                Word = w,
                StreamIndex = w.Letters.Min(l => letterIndex.GetValueOrDefault(l, int.MaxValue))
            })
            .OrderBy(x => x.StreamIndex)
            .Select(x => x.Word)
            .ToList();

        var sb = new System.Text.StringBuilder();

        for (int i = 0; i < orderedWords.Count; i++)
        {
            if (i > 0)
                sb.Append(' ');
            sb.Append(orderedWords[i].Text);
        }

        return MultiSpaceRegex.Replace(sb.ToString().Trim(), " ");
    }
}
