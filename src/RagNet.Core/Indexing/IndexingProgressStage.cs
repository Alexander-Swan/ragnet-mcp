namespace RagNet.Mcp.Indexing;

public enum IndexingProgressStage
{
    Starting,
    ScanningFiles,
    ComparingState,
    DeletingVectors,
    AnalyzingFiles,
    CreatingEmbeddings,
    UpsertingVectors,
    SavingState,
    Completed
}
