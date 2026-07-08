using Microsoft.Extensions.Logging.Abstractions;
using RagNet.Mcp.Source;

namespace RagNet.Mcp.Tests;

public sealed class GitSourceIdentityResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsLocalIdentityWhenWorkspaceIsNotGitRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ragnet-source-{Guid.NewGuid():N}");
        var filePath = Path.Combine(root, "src", "Sample.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        try
        {
            await File.WriteAllTextAsync(filePath, "class Sample { }");

            var resolver = new GitSourceIdentityResolver(NullLogger<GitSourceIdentityResolver>.Instance);
            var identity = await resolver.ResolveAsync(root, filePath);

            Assert.Equal(root, identity.WorkspaceRoot);
            Assert.Equal(root, identity.RepositoryRoot);
            Assert.Equal("src/Sample.cs", identity.RelativePath);
            Assert.False(identity.IsGitRepository);
            Assert.Null(identity.RemoteUrl);
            Assert.Null(identity.Branch);
            Assert.Null(identity.CommitSha);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
