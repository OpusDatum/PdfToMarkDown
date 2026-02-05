using System.Text;

namespace PdfToMarkDown;

public class MarkdownBuilder
{
    private readonly StringBuilder _sb = new();
    private bool _needsBlankLine;

    public void AppendHeading(int level, string text)
    {
        EnsureBlankLine();
        _sb.Append(new string('#', level));
        _sb.Append(' ');
        _sb.AppendLine(text.Trim());
        _needsBlankLine = true;
    }

    public void AppendParagraph(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        EnsureBlankLine();
        _sb.AppendLine(text.Trim());
        _needsBlankLine = true;
    }

    public void AppendBulletItem(string text)
    {
        _sb.Append("- ");
        _sb.AppendLine(text.Trim());
        _needsBlankLine = true;
    }

    public void AppendNumberedItem(int number, string text)
    {
        _sb.Append(number);
        _sb.Append(". ");
        _sb.AppendLine(text.Trim());
        _needsBlankLine = true;
    }

    public void AppendTableHeader(IReadOnlyList<string> headers)
    {
        EnsureBlankLine();
        _sb.Append('|');
        foreach (var h in headers)
        {
            _sb.Append(' ');
            _sb.Append(h.Trim());
            _sb.Append(" |");
        }
        _sb.AppendLine();

        _sb.Append('|');
        foreach (var _ in headers)
        {
            _sb.Append(" --- |");
        }
        _sb.AppendLine();
    }

    public void AppendTableRow(IReadOnlyList<string> cells)
    {
        _sb.Append('|');
        foreach (var c in cells)
        {
            _sb.Append(' ');
            _sb.Append(c.Trim());
            _sb.Append(" |");
        }
        _sb.AppendLine();
        _needsBlankLine = true;
    }

    public void AppendRaw(string text)
    {
        _sb.Append(text);
    }

    public void AppendLine(string text = "")
    {
        _sb.AppendLine(text);
    }

    public void AppendFigurePlaceholder(string altText = "Figure")
    {
        EnsureBlankLine();
        _sb.AppendLine($"![{altText}]()");
        _needsBlankLine = true;
    }

    public void EnsureBlankLine()
    {
        if (_needsBlankLine)
        {
            _sb.AppendLine();
            _needsBlankLine = false;
        }
    }

    public override string ToString() => _sb.ToString().TrimEnd() + Environment.NewLine;
}
