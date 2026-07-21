using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using RagNet.Mcp.Configuration;

namespace RagNet.Mcp.Source;

public interface ITfsCommandRunner
{
    Task<TfsCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

public sealed class TfsCommandRunner(IOptions<RagNetOptions> options) : ITfsCommandRunner
{
    private const int TfsTimeoutMilliseconds = 15_000;
    private readonly RagNetOptions _options = options.Value;

    public async Task<TfsCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        using var timeout = new CancellationTokenSource(TfsTimeoutMilliseconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(_options.SourceControl.TfCommand)
                    ? "tf"
                    : _options.SourceControl.TfCommand,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new TfsCommandResult(-1, string.Empty, ex.Message);
        }

        var output = process.StandardOutput.ReadToEndAsync(linked.Token);
        var error = process.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new TfsCommandResult(-1, string.Empty, "TF command timed out.");
        }

        return new TfsCommandResult(process.ExitCode, await output, await error);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}

public sealed record TfsCommandResult(int ExitCode, string Output, string Error);
