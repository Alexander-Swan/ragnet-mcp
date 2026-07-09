namespace RagNet.Mcp.Workspace.Interfaces;

public interface IWorkspaceTransferService
{
    Task<WorkspaceExportResult> ExportWorkspaceAsync(
        string workspace,
        string outputDirectory,
        CancellationToken cancellationToken = default);

    Task<WorkspaceExportResult> ExportGroupAsync(
        string group,
        string outputDirectory,
        CancellationToken cancellationToken = default);

    Task<WorkspaceImportResult> ImportAsync(
        string inputDirectory,
        IReadOnlyDictionary<string, string> pathMap,
        string? workspaceRoot = null,
        string? expectedKind = null,
        CancellationToken cancellationToken = default);

    Task<WorkspaceMigrationResult> MigrateAsync(CancellationToken cancellationToken = default);

    Task<WorkspaceCollectionStatusResult> ResolveCollectionsAsync(
        string? workspace = null,
        string? group = null,
        string? path = null,
        CancellationToken cancellationToken = default);
}

public sealed record WorkspaceExportResult(
    string Kind,
    string OutputDirectory,
    string ManifestPath,
    IReadOnlyList<WorkspaceCollectionExport> Workspaces,
    IReadOnlyList<string> Groups);

public sealed record WorkspaceCollectionExport(
    string WorkspaceRoot,
    string WorkspaceId,
    string CollectionName,
    int PointsExported,
    string PointsPath);

public sealed record WorkspaceImportResult(
    string InputDirectory,
    IReadOnlyList<ImportedWorkspaceResult> Workspaces,
    IReadOnlyList<string> Groups);

public sealed record ImportedWorkspaceResult(
    string WorkspaceRoot,
    string WorkspaceId,
    string CollectionName,
    int PointsImported);

public sealed record WorkspaceMigrationResult(
    int WorkspacesScanned,
    int WorkspacesUpdated,
    IReadOnlyList<string> Warnings);

public sealed record WorkspaceCollectionStatusResult(
    IReadOnlyList<WorkspaceCollectionMapping> Workspaces);

public sealed record WorkspaceCollectionMapping(
    string WorkspaceRoot,
    string WorkspaceId,
    string CollectionName,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> IndexedTargets);
