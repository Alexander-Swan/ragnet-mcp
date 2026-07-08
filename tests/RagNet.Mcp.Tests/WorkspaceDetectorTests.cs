namespace RagNet.Mcp.Tests;

public sealed class WorkspaceDetectorTests
{
    [Fact]
    public async Task DetectAsync_UsesNearestSolutionAsWorkspaceRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ragnet-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "src", "Sample");
        Directory.CreateDirectory(projectDirectory);

        try
        {
            var solutionPath = Path.Combine(root, "Sample.sln");
            var projectPath = Path.Combine(projectDirectory, "Sample.csproj");
            var filePath = Path.Combine(projectDirectory, "Program.cs");

            await File.WriteAllTextAsync(solutionPath, string.Empty);
            await File.WriteAllTextAsync(projectPath, "<Project />");
            await File.WriteAllTextAsync(filePath, "class Program { }");

            var detector = new Workspace.WorkspaceDetector();
            var workspace = await detector.DetectAsync(filePath);

            Assert.Equal(root, workspace.RootPath);
            Assert.Equal(solutionPath, workspace.SolutionPath);
            Assert.Equal(projectPath, workspace.ProjectPath);
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
