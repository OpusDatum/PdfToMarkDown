using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace PdfToMarkDown.Converters;

public class LayoutAnalysisConverter : IPdfToMarkdownConverter
{
    private static readonly Regex BulletPattern = new(@"^[\u2022\u2023\u25E6\u2043\u2219\-\*\xB7]\s*", RegexOptions.Compiled);
    private static readonly Regex NumberedPattern = new(@"^\d+[\.\)]\s+", RegexOptions.Compiled);

    public string Convert(string pdfPath)
    {
        var md = new MarkdownBuilder();

        using var document = PdfDocument.Open(pdfPath);

        // First pass: determine the median font size across all pages for heading detection
        double medianFontSize = ComputeMedianFontSize(document);

        foreach (var page in document.GetPages())
        {
            var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);
            var wordList = words.ToList();

            if (wordList.Count == 0)
                continue;

            var segmenter = new RecursiveXYCut();
            var blocks = segmenter.GetBlocks(wordList);
            var blockList = blocks.ToList();

            if (blockList.Count == 0)
                continue;

            UnsupervisedReadingOrderDetector.Instance.Get(blockList);
            var orderedBlocks = blockList.OrderBy(b => b.ReadingOrder).ToList();

            foreach (var block in orderedBlocks)
            {
                ProcessBlock(block, md, medianFontSize);
            }
        }

        return md.ToString();
    }

    private double ComputeMedianFontSize(PdfDocument document)
    {
        var fontSizes = new List<double>();

        foreach (var page in document.GetPages())
        {
            foreach (var letter in page.Letters)
            {
                if (letter.PointSize > 0)
                    fontSizes.Add(letter.PointSize);
            }
        }

        if (fontSizes.Count == 0)
            return 12.0;

        fontSizes.Sort();
        int mid = fontSizes.Count / 2;
        return fontSizes.Count % 2 == 0
            ? (fontSizes[mid - 1] + fontSizes[mid]) / 2.0
            : fontSizes[mid];
    }

    private void ProcessBlock(TextBlock block, MarkdownBuilder md, double medianFontSize)
    {
        if (block.TextLines.Count == 0)
            return;

        // Check if block looks like a heading
        int headingLevel = DetectHeadingLevel(block, medianFontSize);
        if (headingLevel > 0)
        {
            md.AppendHeading(headingLevel, block.Text.Replace("\n", " "));
            return;
        }

        // Check if block is a list
        if (TryProcessAsList(block, md))
            return;

        // Default: paragraph
        var text = block.Text.Replace("\n", " ").Trim();
        if (!string.IsNullOrWhiteSpace(text))
            md.AppendParagraph(text);
    }

    private int DetectHeadingLevel(TextBlock block, double medianFontSize)
    {
        // Get the predominant font size and style of the block
        var letters = block.TextLines
            .SelectMany(tl => tl.Words)
            .SelectMany(w => w.Letters)
            .ToList();

        if (letters.Count == 0)
            return 0;

        double blockFontSize = GetPredominantFontSize(letters);
        bool isBold = IsPredominantlyBold(letters);

        // Short text blocks that are significantly larger than body text are headings
        double ratio = blockFontSize / medianFontSize;

        // Only consider headings if text is relatively short (< ~200 chars) or is significantly larger
        bool isShortEnough = block.Text.Length < 200;

        if (ratio >= 1.8 && isShortEnough)
            return 1;
        if (ratio >= 1.4 && isShortEnough)
            return 2;
        if (ratio >= 1.15 && isShortEnough && isBold)
            return 3;
        if (isBold && isShortEnough && block.TextLines.Count <= 2)
            return 4;

        return 0;
    }

    private double GetPredominantFontSize(List<Letter> letters)
    {
        // Return the most common font size (mode)
        return letters
            .Where(l => l.PointSize > 0)
            .GroupBy(l => Math.Round(l.PointSize, 1))
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault(12.0);
    }

    private bool IsPredominantlyBold(List<Letter> letters)
    {
        if (letters.Count == 0)
            return false;

        int boldCount = letters.Count(l =>
        {
            var fontName = l.FontName?.ToLowerInvariant() ?? string.Empty;
            return fontName.Contains("bold") || fontName.Contains("heavy") || fontName.Contains("black");
        });

        return boldCount > letters.Count / 2;
    }

    private bool TryProcessAsList(TextBlock block, MarkdownBuilder md)
    {
        var lines = block.TextLines;
        if (lines.Count < 2)
        {
            // Single line can still be a list item
            var singleLine = GetLineText(lines[0]);
            if (BulletPattern.IsMatch(singleLine))
            {
                md.EnsureBlankLine();
                md.AppendBulletItem(BulletPattern.Replace(singleLine, ""));
                return true;
            }
            if (NumberedPattern.IsMatch(singleLine))
            {
                md.EnsureBlankLine();
                md.AppendNumberedItem(1, NumberedPattern.Replace(singleLine, ""));
                return true;
            }
            return false;
        }

        // Check if most lines start with bullet or number patterns
        int bulletCount = 0;
        int numberedCount = 0;

        foreach (var line in lines)
        {
            var text = GetLineText(line);
            if (BulletPattern.IsMatch(text)) bulletCount++;
            else if (NumberedPattern.IsMatch(text)) numberedCount++;
        }

        if (bulletCount >= lines.Count / 2)
        {
            md.EnsureBlankLine();
            foreach (var line in lines)
            {
                var text = GetLineText(line);
                md.AppendBulletItem(BulletPattern.Replace(text, ""));
            }
            return true;
        }

        if (numberedCount >= lines.Count / 2)
        {
            md.EnsureBlankLine();
            int num = 0;
            foreach (var line in lines)
            {
                num++;
                var text = GetLineText(line);
                md.AppendNumberedItem(num, NumberedPattern.Replace(text, ""));
            }
            return true;
        }

        return false;
    }

    private string GetLineText(TextLine line)
    {
        return string.Join(" ", line.Words.Select(w => w.Text));
    }
}
