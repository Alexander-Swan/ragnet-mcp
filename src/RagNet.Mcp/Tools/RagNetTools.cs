using System.ComponentModel;
using ModelContextProtocol.Server;
using RagNet.Mcp.Indexing;
using RagNet.Mcp.Indexing.Interfaces;

namespace RagNet.Mcp.Tools;

[McpServerToolType]
public sealed class RagNetTools(IWorkspaceIndexer indexer)
{
    [McpServerTool(Name = "index_workspace"), Description("Index or re-index a local workspace for semantic code search.")]
    public Task<IndexWorkspaceResult> IndexWorkspace(
        [Description("A file or directory inside the workspace to index.")] string workspace_path,
        [Description("Additional directory names or workspace-relative directory paths to exclude for this indexing run.")] string[]? exclude_directories = null,
        [Description("When true, clears existing vectors/state for the workspace and reindexes all files.")] bool force = false,
        [Description("Index profile: all, code, docs, metadata, frontend, or tests. Default is all.")] string? index_profile = null,
        CancellationToken cancellationToken = default)
        => indexer.IndexAsync(workspace_path, exclude_directories, force, index_profile, cancellationToken);

    [McpServerTool(Name = "index_workspace_group"), Description("Index all configured workspace roots in a named workspace group.")]
    public Task<IReadOnlyList<IndexWorkspaceResult>> IndexWorkspaceGroup(
        [Description("Configured workspace group name from RagNet:WorkspaceGroups.")] string workspace_group,
        [Description("Additional directory names or workspace-relative directory paths to exclude for this indexing run.")] string[]? exclude_directories = null,
        [Description("When true, clears existing vectors/state for each workspace and reindexes all files.")] bool force = false,
        [Description("Index profile: all, code, docs, metadata, frontend, or tests. Default is all.")] string? index_profile = null,
        CancellationToken cancellationToken = default)
        => indexer.IndexGroupAsync(workspace_group, exclude_directories, force, index_profile, cancellationToken);

    [McpServerTool(Name = "get_index_status"), Description("Return persisted index metadata for a workspace.")]
    public Task<IndexStatusResult> GetIndexStatus(
        [Description("A file or directory inside the workspace to inspect.")] string workspace_path,
        CancellationToken cancellationToken = default)
        => indexer.GetStatusAsync(workspace_path, cancellationToken);

    [McpServerTool(Name = "trigger_indexing"), Description("Agent-friendly indexing trigger for either a workspace path or a configured workspace group.")]
    public async Task<IReadOnlyList<IndexWorkspaceResult>> TriggerIndexing(
        [Description("A file or directory inside the workspace to index. Required when workspace_group is not provided.")] string? workspace_path = null,
        [Description("Configured workspace group name from RagNet:WorkspaceGroups. When provided, indexes the whole group.")] string? workspace_group = null,
        [Description("Additional directory names or workspace-relative directory paths to exclude for this indexing run.")] string[]? exclude_directories = null,
        [Description("When true, clears existing vectors/state and reindexes all files.")] bool force = false,
        [Description("Index profile: all, code, docs, metadata, frontend, or tests. Default is all.")] string? index_profile = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(workspace_group))
        {
            return await indexer.IndexGroupAsync(workspace_group, exclude_directories, force, index_profile, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(workspace_path))
        {
            throw new ArgumentException("Either workspace_path or workspace_group is required.");
        }

        return [await indexer.IndexAsync(workspace_path, exclude_directories, force, index_profile, cancellationToken)];
    }

    [McpServerTool(Name = "search_code"), Description("Semantic search over indexed product context for the workspace containing file_path.")]
    public Task<IReadOnlyList<SearchResult>> SearchCode(
        [Description("A file path inside the target workspace. Required for default current_workspace scope.")] string? file_path,
        [Description("Natural language search query.")] string query,
        [Description("Search scope: current_workspace, explicit_workspace_root, named_workspace_group, or all_indexed_workspaces. Default is current_workspace.")] string? scope = null,
        [Description("Explicit workspace root when scope is explicit_workspace_root.")] string? workspace_root = null,
        [Description("Configured group name when scope is named_workspace_group.")] string? workspace_group = null,
        [Description("Content filter: code, documentation, markup, project_metadata, or all. Default is all.")] string? content_type = null,
        [Description("Ranking mode: balanced, docs_first, or code_first. Default is balanced.")] string? retrieval_mode = null,
        [Description("Search profile: all, code, docs, metadata, frontend, or tests. Default is all.")] string? search_profile = null,
        [Description("When true, current_workspace or explicit_workspace_root searches also search every workspace in any configured group containing that workspace. Default is false.")] bool include_grouped_workspaces = false,
        [Description("Maximum number of results to return.")] int limit = 10,
        CancellationToken cancellationToken = default)
        => indexer.SearchAsync(
            file_path,
            query,
            Math.Clamp(limit, 1, 50),
            hybrid: false,
            scope,
            workspace_root,
            workspace_group,
            content_type,
            retrieval_mode,
            search_profile,
            cancellationToken,
            include_grouped_workspaces);

    [McpServerTool(Name = "hybrid_search"), Description("Hybrid semantic and keyword search over indexed product context for the workspace containing file_path.")]
    public Task<IReadOnlyList<SearchResult>> HybridSearch(
        [Description("A file path inside the target workspace. Required for default current_workspace scope.")] string? file_path,
        [Description("Natural language query and optional exact identifiers.")] string query,
        [Description("Search scope: current_workspace, explicit_workspace_root, named_workspace_group, or all_indexed_workspaces. Default is current_workspace.")] string? scope = null,
        [Description("Explicit workspace root when scope is explicit_workspace_root.")] string? workspace_root = null,
        [Description("Configured group name when scope is named_workspace_group.")] string? workspace_group = null,
        [Description("Content filter: code, documentation, markup, project_metadata, or all. Default is all.")] string? content_type = null,
        [Description("Ranking mode: balanced, docs_first, or code_first. Default is balanced.")] string? retrieval_mode = null,
        [Description("Search profile: all, code, docs, metadata, frontend, or tests. Default is all.")] string? search_profile = null,
        [Description("When true, current_workspace or explicit_workspace_root searches also search every workspace in any configured group containing that workspace. Default is false.")] bool include_grouped_workspaces = false,
        [Description("Maximum number of results to return.")] int limit = 10,
        CancellationToken cancellationToken = default)
        => indexer.SearchAsync(
            file_path,
            query,
            Math.Clamp(limit, 1, 50),
            hybrid: true,
            scope,
            workspace_root,
            workspace_group,
            content_type,
            retrieval_mode,
            search_profile,
            cancellationToken,
            include_grouped_workspaces);

    [McpServerTool(Name = "get_code_context"), Description("Return code lines around a specific file location.")]
    public Task<string> GetCodeContext(
        [Description("File path to read.")] string file_path,
        [Description("1-based line number.")] int line,
        [Description("Number of lines before the target line.")] int before = 20,
        [Description("Number of lines after the target line.")] int after = 20,
        CancellationToken cancellationToken = default)
        => indexer.GetCodeContextAsync(file_path, line, before, after, cancellationToken);

    [McpServerTool(Name = "get_symbol_details"), Description("Return the full code chunk for a named symbol in a file.")]
    public Task<string?> GetSymbolDetails(
        [Description("File path containing the symbol.")] string file_path,
        [Description("Class, method, record, enum, property, field, or constructor name.")] string symbol_name,
        CancellationToken cancellationToken = default)
        => indexer.GetSymbolDetailsAsync(file_path, symbol_name, cancellationToken);
}
