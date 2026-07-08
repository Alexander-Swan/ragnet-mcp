using System.Text.RegularExpressions;
using RagNet.Mcp.Storage;

namespace RagNet.Mcp.Tests;

public sealed class QdrantCollectionNamingTests
{
    [Fact]
    public void GetWorkspaceId_IsStableForEquivalentRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), "ragnet-qdrant", "sample");
        var withTrailingSeparator = root + Path.DirectorySeparatorChar;

        var first = QdrantCollectionNaming.GetWorkspaceId(root);
        var second = QdrantCollectionNaming.GetWorkspaceId(withTrailingSeparator);

        Assert.Equal(first, second);
        Assert.Matches("^[a-f0-9]{32}$", first);
    }

    [Fact]
    public void GetCollectionName_SanitizesPrefixAndDoesNotIncludeRawPath()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "ragnet qdrant", "workspace");
        var collectionName = QdrantCollectionNaming.GetCollectionName("Rag Net:Local/Dev", workspaceRoot);

        Assert.StartsWith("rag-net-local-dev-", collectionName);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, collectionName);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, collectionName);
        Assert.DoesNotContain("ragnet qdrant", collectionName);
        Assert.Matches(new Regex("^[a-z0-9_.-]+-[a-f0-9]{32}$", RegexOptions.CultureInvariant), collectionName);
    }

    [Fact]
    public void GetCollectionName_UsesDefaultPrefixWhenConfiguredPrefixIsEmptyAfterSanitization()
    {
        var collectionName = QdrantCollectionNaming.GetCollectionName(":///", Path.GetTempPath());

        Assert.StartsWith("ragnet-", collectionName);
    }
}
