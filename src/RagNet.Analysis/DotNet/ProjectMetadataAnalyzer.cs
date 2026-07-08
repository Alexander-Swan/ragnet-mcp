using RagNet.Mcp.Analyzers.Common;
using RagNet.Mcp.Analyzers.Interfaces;
using RagNet.Mcp.Indexing;

namespace RagNet.Mcp.Analyzers.DotNet;

public sealed class ProjectMetadataAnalyzer : ICodeAnalyzer
{
    private static readonly HashSet<string> ExactMetadataFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".editorconfig",
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "Dockerfile",
        "NuGet.config",
        "azure-pipelines.yaml",
        "azure-pipelines.yml",
        "compose.yaml",
        "compose.yml",
        "docker-compose.yaml",
        "docker-compose.yml",
        "global.json",
        "launchSettings.json"
    };

    public bool CanAnalyze(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (ExactMetadataFiles.Contains(fileName))
        {
            return true;
        }

        if (fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetExtension(fileName), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (fileName.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsCiWorkflow(filePath))
        {
            return true;
        }

        if (fileName.StartsWith("deployment", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("publish", StringComparison.OrdinalIgnoreCase))
        {
            return IsSupportedDeploymentExtension(fileName);
        }

        return IsDotNetMetadataFile(fileName);
    }

    public async Task<IReadOnlyList<CodeChunk>> AnalyzeAsync(string workspaceRoot, string filePath, CancellationToken cancellationToken = default)
    {
        var source = await File.ReadAllTextAsync(filePath, cancellationToken);
        var lines = TextChunkBuilder.NormalizeNewlines(source).Split('\n');
        var profile = GetProfile(filePath);
        var content = string.Join(
            Environment.NewLine,
            $"Metadata file: {Path.GetRelativePath(workspaceRoot, filePath)}",
            $"Role: {profile.SymbolKind}",
            string.Empty,
            source);

        return TextChunkBuilder.Split(
            workspaceRoot,
            filePath,
            profile.Language,
            Path.GetFileName(filePath),
            profile.SymbolKind,
            1,
            Math.Max(1, lines.Length),
            content,
            IndexedContentTypes.ProjectMetadata);
    }

    private static MetadataProfile GetProfile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var extension = Path.GetExtension(fileName);

        if (string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return new MetadataProfile("solution", "DotNetSolution");
        }

        if (string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return new MetadataProfile("xml", "DotNetProject");
        }

        if (string.Equals(fileName, "Directory.Packages.props", StringComparison.OrdinalIgnoreCase))
        {
            return new MetadataProfile("xml", "CentralPackageManagement");
        }

        if (fileName.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".runsettings", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".pubxml", StringComparison.OrdinalIgnoreCase))
        {
            return new MetadataProfile("xml", "MSBuildMetadata");
        }

        if (string.Equals(fileName, "NuGet.config", StringComparison.OrdinalIgnoreCase))
        {
            return new MetadataProfile("xml", "NuGetConfig");
        }

        if (string.Equals(fileName, "global.json", StringComparison.OrdinalIgnoreCase))
        {
            return new MetadataProfile("json", "DotNetSdkConfig");
        }

        if (fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
        {
            return new MetadataProfile("json", "ApplicationSettings");
        }

        if (string.Equals(fileName, "launchSettings.json", StringComparison.OrdinalIgnoreCase))
        {
            return new MetadataProfile("json", "LaunchSettings");
        }

        if (string.Equals(fileName, ".editorconfig", StringComparison.OrdinalIgnoreCase))
        {
            return new MetadataProfile("editorconfig", "EditorConfig");
        }

        if (fileName.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase))
        {
            return new MetadataProfile("dockerfile", "DockerMetadata");
        }

        if (IsYaml(fileName))
        {
            return IsCiWorkflow(filePath)
                ? new MetadataProfile("yaml", "CiWorkflow")
                : new MetadataProfile("yaml", "DeploymentMetadata");
        }

        if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            return new MetadataProfile("json", "ProjectMetadata");
        }

        return new MetadataProfile("text", "ProjectMetadata");
    }

    private static bool IsDotNetMetadataFile(string fileName)
        => fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".runsettings", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".pubxml", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedDeploymentExtension(string fileName)
        => IsYaml(fileName) ||
            fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    private static bool IsCiWorkflow(string filePath)
    {
        var normalized = filePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.Contains($"{Path.DirectorySeparatorChar}.github{Path.DirectorySeparatorChar}workflows{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
            IsYaml(Path.GetFileName(filePath));
    }

    private static bool IsYaml(string fileName)
        => fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);

    private sealed record MetadataProfile(string Language, string SymbolKind);
}
