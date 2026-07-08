namespace RagNet.Mcp.Tests;

internal sealed class TemporaryWorkspace : IDisposable
{
    public string RootPath { get; } = Path.Combine(Path.GetTempPath(), $"ragnet-tests-{Guid.NewGuid():N}");

    public TemporaryWorkspace()
    {
        Directory.CreateDirectory(RootPath);
    }

    public string WriteFile(string relativePath, string content)
    {
        var file = Path.Combine(RootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        return file;
    }

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
