namespace RagNet.Mcp.Analyzers;

public sealed record AnalyzerMatch(bool CanAnalyze, double Confidence, string Reason)
{
    public static AnalyzerMatch No { get; } = new(false, 0, "not_supported");

    public static AnalyzerMatch Supported(double confidence = 0.5, string reason = "extension")
        => new(true, Clamp(confidence), reason);

    public static double Clamp(double confidence)
        => Math.Max(0, Math.Min(1, confidence));
}
