using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace PdfToMarkDown;

/// <summary>
/// Represents one node in the PDF's logical structure tree.
/// </summary>
public class StructureNode
{
    public string Tag { get; set; } = string.Empty;
    public List<StructureNode> Children { get; } = new();
    public List<McidReference> McidRefs { get; } = new();
}

/// <summary>
/// A reference to a marked content sequence on a specific page.
/// </summary>
public record McidReference(int PageNumber, int Mcid);

/// <summary>
/// Reads the StructTreeRoot from a PDF's catalog and builds a logical structure tree
/// with MCID references that link structure elements to their content on pages.
/// </summary>
public static class StructureTreeReader
{
    /// <summary>
    /// Returns the root StructureNode tree, or null if no StructTreeRoot.
    /// </summary>
    public static StructureNode? Read(PdfDocument document)
    {
        var catalog = document.Structure.Catalog.CatalogDictionary;

        if (!catalog.TryGet(NameToken.Create("StructTreeRoot"), out var structTreeRootToken))
            return null;

        var resolved = ResolveToken(structTreeRootToken, document);
        if (resolved is not DictionaryToken structTreeDict)
            return null;

        // Build page reference map: IndirectReference -> 1-indexed page number
        var pageMap = BuildPageMap(document);

        // Parse RoleMap
        var roleMap = ParseRoleMap(structTreeDict, document);

        // Walk K (kids) to build the tree
        var root = new StructureNode { Tag = "StructTreeRoot" };

        if (structTreeDict.TryGet(NameToken.Create("K"), out var kidsToken))
        {
            var kids = ResolveToken(kidsToken, document);
            WalkKids(kids, document, root, pageMap, roleMap, parentPageNumber: 0);
        }

        return root;
    }

    /// <summary>
    /// Returns true if the PDF has a StructTreeRoot with at least one structure element.
    /// </summary>
    public static bool HasStructureTree(PdfDocument document)
    {
        var catalog = document.Structure.Catalog.CatalogDictionary;

        if (!catalog.TryGet(NameToken.Create("StructTreeRoot"), out var structTreeRootToken))
            return false;

        var resolved = ResolveToken(structTreeRootToken, document);
        if (resolved is not DictionaryToken structTreeDict)
            return false;

        if (!structTreeDict.TryGet(NameToken.Create("K"), out var kidsToken))
            return false;

        var kids = ResolveToken(kidsToken, document);

        // Check that there's at least one structure element child
        return HasStructureElements(kids, document);
    }

    private static bool HasStructureElements(IToken token, PdfDocument document)
    {
        token = ResolveToken(token, document);

        if (token is ArrayToken array)
        {
            foreach (var item in array.Data)
            {
                if (HasStructureElements(item, document))
                    return true;
            }
            return false;
        }

        if (token is DictionaryToken dict)
        {
            // If it has an S entry, it's a structure element
            if (dict.TryGet(NameToken.Create("S"), out _))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Build a map from page object IndirectReference to 1-indexed page number.
    /// We walk the Pages tree in the catalog to do this.
    /// </summary>
    private static Dictionary<IndirectReference, int> BuildPageMap(PdfDocument document)
    {
        var map = new Dictionary<IndirectReference, int>();
        var catalog = document.Structure.Catalog.CatalogDictionary;

        if (!catalog.TryGet(NameToken.Create("Pages"), out var pagesToken))
            return map;

        // We need to find all page object references and their ordering.
        // Walk the Pages tree collecting leaf Page nodes.
        var pageRefs = new List<IndirectReference>();
        CollectPageReferences(pagesToken, document, pageRefs);

        for (int i = 0; i < pageRefs.Count; i++)
        {
            map[pageRefs[i]] = i + 1; // 1-indexed
        }

        return map;
    }

    private static void CollectPageReferences(IToken token, PdfDocument document, List<IndirectReference> pageRefs)
    {
        // If it's an indirect reference, remember it before resolving
        IndirectReference? currentRef = null;
        if (token is IndirectReferenceToken refToken)
        {
            currentRef = refToken.Data;
            if (document.Structure.GetObject(refToken.Data) is ObjectToken objToken)
                token = objToken.Data;
            else
                return;
        }

        if (token is not DictionaryToken dict)
            return;

        // Determine if this is a Page or Pages node
        string type = "Pages"; // default if Type is missing
        if (dict.TryGet(NameToken.Create("Type"), out var typeToken))
        {
            var resolvedType = ResolveToken(typeToken, document);
            if (resolvedType is NameToken nameType)
                type = nameType.Data;
        }

        if (type == "Page")
        {
            // Leaf page node
            if (currentRef.HasValue)
                pageRefs.Add(currentRef.Value);
            return;
        }

        if (type == "Pages")
        {
            // Intermediate node, recurse into Kids
            if (dict.TryGet(NameToken.Create("Kids"), out var kidsToken))
            {
                var kids = ResolveToken(kidsToken, document);
                if (kids is ArrayToken kidsArray)
                {
                    foreach (var kid in kidsArray.Data)
                    {
                        CollectPageReferences(kid, document, pageRefs);
                    }
                }
            }
        }
    }

    private static Dictionary<string, string> ParseRoleMap(DictionaryToken structTreeDict, PdfDocument document)
    {
        var roleMap = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!structTreeDict.TryGet(NameToken.Create("RoleMap"), out var roleMapToken))
            return roleMap;

        var resolved = ResolveToken(roleMapToken, document);
        if (resolved is not DictionaryToken roleMapDict)
            return roleMap;

        foreach (var kvp in roleMapDict.Data)
        {
            var value = ResolveToken(kvp.Value, document);
            if (value is NameToken nameToken)
                roleMap[kvp.Key] = nameToken.Data;
        }

        return roleMap;
    }

    /// <summary>
    /// Recursively walk K (kids) entries and populate the parent node's Children/McidRefs.
    /// </summary>
    private static void WalkKids(
        IToken token,
        PdfDocument document,
        StructureNode parent,
        Dictionary<IndirectReference, int> pageMap,
        Dictionary<string, string> roleMap,
        int parentPageNumber)
    {
        token = ResolveToken(token, document);

        if (token is ArrayToken array)
        {
            foreach (var item in array.Data)
            {
                WalkKids(item, document, parent, pageMap, roleMap, parentPageNumber);
            }
            return;
        }

        if (token is NumericToken numToken)
        {
            // Bare integer MCID - inherits page from parent
            parent.McidRefs.Add(new McidReference(parentPageNumber, numToken.Int));
            return;
        }

        if (token is not DictionaryToken dict)
            return;

        // Determine the page number for this node
        int nodePageNumber = parentPageNumber;
        if (dict.TryGet(NameToken.Create("Pg"), out var pgToken))
        {
            nodePageNumber = ResolvePageNumber(pgToken, document, pageMap, parentPageNumber);
        }

        // Check if this is a structure element (has S entry)
        if (dict.TryGet(NameToken.Create("S"), out var sToken))
        {
            var resolvedS = ResolveToken(sToken, document);
            string tag = resolvedS is NameToken nameToken ? nameToken.Data : "Unknown";

            // Apply RoleMap
            if (roleMap.TryGetValue(tag, out var mappedTag))
                tag = mappedTag;

            var childNode = new StructureNode { Tag = tag };
            parent.Children.Add(childNode);

            // Recurse into K
            if (dict.TryGet(NameToken.Create("K"), out var kidsToken))
            {
                var kids = ResolveToken(kidsToken, document);
                WalkKids(kids, document, childNode, pageMap, roleMap, nodePageNumber);
            }

            return;
        }

        // Check if this is a marked content reference (MCR) - has MCID entry but no S
        if (dict.TryGet(NameToken.Create("MCID"), out var mcidToken))
        {
            var resolvedMcid = ResolveToken(mcidToken, document);
            if (resolvedMcid is NumericToken mcidNum)
            {
                parent.McidRefs.Add(new McidReference(nodePageNumber, mcidNum.Int));
            }
            return;
        }
    }

    private static int ResolvePageNumber(
        IToken pgToken,
        PdfDocument document,
        Dictionary<IndirectReference, int> pageMap,
        int fallback)
    {
        if (pgToken is IndirectReferenceToken refToken)
        {
            if (pageMap.TryGetValue(refToken.Data, out int pageNumber))
                return pageNumber;
        }

        return fallback;
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
