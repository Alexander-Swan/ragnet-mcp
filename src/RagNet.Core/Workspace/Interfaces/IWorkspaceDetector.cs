namespace RagNet.Mcp.Workspace.Interfaces;

public interface IWorkspaceDetector
{
    Task<WorkspaceInfo> DetectAsync(string filePath, CancellationToken cancellationToken = default);
}
