namespace PdfToMarkDown.Converters;

public interface IPdfToMarkdownConverter
{
    string Convert(string pdfPath);
}
