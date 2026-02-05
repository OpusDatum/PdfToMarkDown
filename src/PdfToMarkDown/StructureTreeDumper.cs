using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;

namespace PdfToMarkDown;

/// <summary>
/// Reads the StructTreeRoot from a PDF's catalog and dumps the logical structure hierarchy.
/// This reveals the semantic tags (H1, H2, P, L, Table, etc.) that PdfPig's GetMarkedContents() doesn't expose.
/// </summary>
public static class StructureTreeDumper
{
    public static string Dump(string pdfPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PDF Structure Tree (StructTreeRoot)");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine($"File: {Path.GetFileName(pdfPath)}");
        sb.AppendLine();

        using var document = PdfDocument.Open(pdfPath);

        var catalog = document.Structure.Catalog.CatalogDictionary;

        if (!catalog.TryGet(NameToken.Create("StructTreeRoot"), out var structTreeRootToken))
        {
            sb.AppendLine("No StructTreeRoot found - this PDF is not tagged.");
            return sb.ToString();
        }

        // Resolve the StructTreeRoot reference
        var structTreeRoot = ResolveToken(structTreeRootToken, document);

        if (structTreeRoot is not DictionaryToken structTreeDict)
        {
            sb.AppendLine("StructTreeRoot is not a dictionary.");
            return sb.ToString();
        }

        // Collect tag frequency
        var tagCounts = new Dictionary<string, int>();

        // Check for RoleMap
        if (structTreeDict.TryGet(NameToken.Create("RoleMap"), out var roleMapToken))
        {
            var roleMap = ResolveToken(roleMapToken, document);
            if (roleMap is DictionaryToken roleMapDict)
            {
                sb.AppendLine("Role Mapping (custom tags -> standard tags):");
                sb.AppendLine(new string('-', 40));
                foreach (var kvp in roleMapDict.Data)
                {
                    var value = ResolveToken(kvp.Value, document);
                    var mappedTo = value is NameToken nt ? nt.Data : value?.ToString() ?? "?";
                    sb.AppendLine($"  {kvp.Key} -> {mappedTo}");
                }
                sb.AppendLine();
            }
        }

        // Walk the K (kids) array
        sb.AppendLine("Structure Tree Hierarchy:");
        sb.AppendLine(new string('-', 60));

        if (structTreeDict.TryGet(NameToken.Create("K"), out var kidsToken))
        {
            var kids = ResolveToken(kidsToken, document);
            WalkStructureNode(kids, document, sb, 0, tagCounts);
        }

        // Tag frequency summary
        sb.AppendLine();
        sb.AppendLine("Tag Frequency Summary:");
        sb.AppendLine(new string('-', 40));
        foreach (var kvp in tagCounts.OrderByDescending(k => k.Value))
        {
            sb.AppendLine($"  {kvp.Key,-25} {kvp.Value,6} occurrences");
        }

        return sb.ToString();
    }

    private static void WalkStructureNode(IToken token, PdfDocument document, StringBuilder sb, int depth, Dictionary<string, int> tagCounts)
    {
        token = ResolveToken(token, document);

        if (token is ArrayToken array)
        {
            foreach (var item in array.Data)
            {
                WalkStructureNode(item, document, sb, depth, tagCounts);
            }
            return;
        }

        if (token is DictionaryToken dict)
        {
            // Get the structure type (S entry)
            string structType = "(unknown)";
            if (dict.TryGet(NameToken.Create("S"), out var sToken))
            {
                var resolved = ResolveToken(sToken, document);
                if (resolved is NameToken nameToken)
                    structType = nameToken.Data;
            }

            // Track tag frequency
            if (!tagCounts.ContainsKey(structType))
                tagCounts[structType] = 0;
            tagCounts[structType]++;

            var indent = new string(' ', depth * 2);

            // Check for MCID (leaf node referencing marked content)
            int? mcid = null;
            if (dict.TryGet(NameToken.Create("MCID"), out var mcidToken))
            {
                var resolvedMcid = ResolveToken(mcidToken, document);
                if (resolvedMcid is NumericToken numToken)
                    mcid = numToken.Int;
            }

            // Check for page reference
            string pageInfo = "";
            if (dict.TryGet(NameToken.Create("Pg"), out var pgToken))
            {
                // We won't resolve page number here for simplicity
            }

            if (mcid.HasValue)
            {
                sb.AppendLine($"{indent}{structType} [MCID={mcid.Value}]");
            }
            else
            {
                sb.AppendLine($"{indent}{structType}");
            }

            // Recurse into K (kids)
            if (dict.TryGet(NameToken.Create("K"), out var kidsToken))
            {
                var kids = ResolveToken(kidsToken, document);

                // K can be a single integer (MCID), a single dict, or an array
                if (kids is NumericToken numKid)
                {
                    sb.AppendLine($"{indent}  (MCID={numKid.Int})");
                }
                else
                {
                    WalkStructureNode(kids, document, sb, depth + 1, tagCounts);
                }
            }

            return;
        }

        if (token is NumericToken mcidDirect)
        {
            // Direct MCID reference (not wrapped in a dict)
            var indent = new string(' ', depth * 2);
            sb.AppendLine($"{indent}(MCID={mcidDirect.Int})");
        }
    }

    private static IToken ResolveToken(IToken token, PdfDocument document)
    {
        while (token is IndirectReferenceToken refToken)
        {
            if (document.Structure.GetObject(refToken.Data) is ObjectToken objToken)
                token = objToken.Data;
            else
                break;
        }
        return token;
    }
}
