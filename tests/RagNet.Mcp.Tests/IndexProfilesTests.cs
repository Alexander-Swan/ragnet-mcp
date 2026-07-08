using RagNet.Mcp.Indexing;

namespace RagNet.Mcp.Tests;

public sealed class IndexProfilesTests
{
    [Fact]
    public void Normalize_AcceptsAliases()
    {
        Assert.Equal(IndexProfiles.All, IndexProfiles.Normalize(null));
        Assert.Equal(IndexProfiles.Documentation, IndexProfiles.Normalize("documentation"));
        Assert.Equal(IndexProfiles.Metadata, IndexProfiles.Normalize("project_metadata"));
        Assert.Equal(IndexProfiles.Tests, IndexProfiles.Normalize("test"));
        Assert.Equal(IndexProfiles.Frontend, IndexProfiles.Normalize("ui"));
        Assert.Equal(IndexProfiles.Frontend, IndexProfiles.Normalize("web"));
    }

    [Fact]
    public void NormalizeFilter_ReturnsNullForAll()
    {
        Assert.Null(IndexProfiles.NormalizeFilter("all"));
        Assert.Equal(IndexProfiles.Code, IndexProfiles.NormalizeFilter("code"));
    }

    [Fact]
    public void Normalize_RejectsUnknownProfiles()
    {
        var exception = Assert.Throws<ArgumentException>(() => IndexProfiles.Normalize("unknown"));

        Assert.Contains("Unsupported profile", exception.Message);
    }
}
