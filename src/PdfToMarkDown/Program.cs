using System.Diagnostics;
using PdfToMarkDown.Converters;
using UglyToad.PdfPig;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: PdfToMarkDown <pdf-file-path>");
    Console.Error.WriteLine("  Converts a PDF file to Markdown format.");
    return 1;
}

var pdfPath = args[0];

if (!File.Exists(pdfPath))
{
    Console.Error.WriteLine($"Error: File not found: {pdfPath}");
    return 1;
}

if (!pdfPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Error: Input file must be a .pdf file.");
    return 1;
}

var outputPath = Path.ChangeExtension(pdfPath, ".md");

Console.WriteLine($"Input:  {pdfPath}");
Console.WriteLine($"Output: {outputPath}");

var sw = Stopwatch.StartNew();

// Determine which strategy to use
bool useTagged = false;
int pageCount;

using (var probe = PdfDocument.Open(pdfPath))
{
    pageCount = probe.NumberOfPages;

    if (pageCount > 0)
    {
        var firstPage = probe.GetPage(1);
        var markedContents = firstPage.GetMarkedContents();
        useTagged = TaggedPdfConverter.HasMeaningfulTags(markedContents);
    }
}

string strategyName;
IPdfToMarkdownConverter converter;

if (useTagged)
{
    strategyName = "Tagged PDF (structured markup)";
    converter = new TaggedPdfConverter();
}
else
{
    strategyName = "Layout Analysis (font-size/position heuristics)";
    converter = new LayoutAnalysisConverter();
}

Console.WriteLine($"Strategy: {strategyName}");
Console.WriteLine($"Pages: {pageCount}");
Console.WriteLine("Converting...");

var markdown = converter.Convert(pdfPath);

File.WriteAllText(outputPath, markdown);

sw.Stop();

Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:F1}s");
Console.WriteLine($"Output written to: {outputPath}");

return 0;
