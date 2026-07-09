# Hosted Indexing Design

RagNet supports local indexing today. Hosted/team indexing should reuse the same analyzer, chunking, embedding, and vector storage pipeline while replacing only the source acquisition and job execution layer.

## Goals

- Accept GitHub, GitLab, Azure DevOps, or generic webhook events.
- Resolve each event to a repository, branch/ref, commit SHA, and changed/deleted paths.
- Checkout or update a working copy in an isolated worker directory.
- Reindex only relevant files when enough change information is available.
- Fall back to full workspace or solution-scoped indexing when the event is incomplete.
- Persist job status, warnings, retry state, and indexing results so agents and operators can inspect progress.
- Write vectors, index state, workspace registry, and groups to shared Qdrant for teammate reuse.

## Job Lifecycle

1. **Intake**
   - Validate webhook signatures and map the sender to an allowed repository.
   - Normalize provider payloads into `IndexTriggerRequest`.
   - Record `repositoryUrl`, `repositoryProvider`, `ref`, `commitSha`, `changedFiles`, and `deletedFiles`.

2. **Queue**
   - Store a durable job record with status `queued`.
   - Coalesce events by repository/ref when newer commits supersede older queued work.
   - Keep the current in-memory queue only for local/dev mode.

3. **Checkout**
   - Clone missing repositories into a worker cache.
   - Fetch and checkout the requested commit or branch.
   - Record the resolved repository root and commit SHA in source metadata.

4. **Plan**
   - Detect workspace roots, solution scopes, and configured product groups.
   - Use changed/deleted paths for incremental planning when possible.
   - If changes cross project metadata, package files, analyzers, or unsupported paths, widen the plan to the affected solution or workspace.

5. **Index**
   - Run the existing `WorkspaceIndexer` pipeline against the checked-out path.
   - Preserve hosted source identity in chunk payloads: repository URL, provider, commit SHA, relative path, and line range.
   - Store index state and workspace registry records in shared Qdrant.

6. **Finalize**
   - Mark the job `completed`, `failed`, or `cancelled`.
   - Keep warnings, file/chunk counts, changed/deleted file counts, and elapsed timing.
   - Expose recent jobs through MCP tools and HTTP endpoints.

## Durable Job State

The hosted worker should persist:

- job id
- status
- repository URL/provider
- ref and commit SHA
- workspace root or group
- requested profile
- changed/deleted paths
- retry count and next retry time
- accepted/started/completed timestamps
- files scanned
- chunks embedded
- total indexed chunks
- warnings and error message

Qdrant can hold this initially as an operational collection, but a relational store may be better if job querying grows.

## Backup And Restore Planning

Use Qdrant snapshots as the first backup/restore mechanism for hosted or shared environments. Snapshots preserve collections, vectors, and payloads in Qdrant's native format, which is safer than inventing a RagNet-specific vector export format before restore requirements are stable.

Planned backup/restore work:

- schedule snapshots for workspace vector collections and operational collections such as workspace registry, groups, and index state;
- document snapshot retention, storage location, and restore drills per environment;
- add restore validation that checks collection prefix, registered workspaces, index-state count, and approximate vector count after restore;
- defer workspace export/import until there is a clear need to move logical workspaces across Qdrant deployments without full collection snapshots.

## Incremental Rules

- Path-only changes should reindex matching files and delete removed files.
- Solution/project metadata changes should reindex the containing solution scope.
- Shared configuration changes should reindex the affected workspace or configured product group.
- Missing or unreliable provider payloads should force a safe full reindex.

## Open TODO

- Add a durable `IIndexingJobStore`.
- Add a hosted `IRepositoryCheckoutService`.
- Add provider-specific webhook mappers.
- Add job status/list/retry/cancel MCP tools.
- Add a worker process mode separate from the HTTP/MCP server.
- Add Qdrant snapshot backup/restore automation and only then evaluate workspace export/import.
