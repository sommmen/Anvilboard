using System.Diagnostics;
using System.Text.Json;

namespace Anvilboard.Agent.Tests.Hosting;

public sealed class McpStdoutTests
{
    [Fact]
    public async Task McpMode_WritesOnlyJsonRpcFramesToStdout()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"anvilboard-mcp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = tempDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add(typeof(BoardAgentService).Assembly.Location);
        process.StartInfo.ArgumentList.Add("mcp");
        process.StartInfo.Environment["ANVILBOARD_Database__DatabasePath"] =
            Path.Combine(tempDirectory, "anvilboard.db");
        process.StartInfo.Environment["ANVILBOARD_Database__BackupDirectory"] =
            Path.Combine(tempDirectory, "backups");
        process.StartInfo.Environment["ANVILBOARD_Logging__LogLevel__Default"] = "Information";

        try
        {
            Assert.True(process.Start());
            var stderr = process.StandardError.ReadToEndAsync();

            await process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"stdout-regression-test","version":"1.0"}}}""");
            await process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""");
            await process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");
            await process.StandardInput.FlushAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var frames = new List<JsonDocument>();
            while (!frames.Any(frame =>
                       frame.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == 2))
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.False(string.IsNullOrWhiteSpace(line));

                var frame = JsonDocument.Parse(line);
                frames.Add(frame);
                Assert.Equal("2.0", frame.RootElement.GetProperty("jsonrpc").GetString());
                Assert.True(
                    frame.RootElement.TryGetProperty("id", out _)
                    || frame.RootElement.TryGetProperty("method", out _),
                    $"MCP stdout contained non-JSON-RPC JSON: {line}");
            }

            var toolsResponse = Assert.Single(frames, frame =>
                frame.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == 2);
            Assert.True(toolsResponse.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() > 0);

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);

            var remainingStdout = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            Assert.True(string.IsNullOrWhiteSpace(remainingStdout),
                $"MCP stdout contained output after the final JSON-RPC frame: {remainingStdout}");

            var errorOutput = await stderr.WaitAsync(timeout.Token);
            Assert.False(string.IsNullOrWhiteSpace(errorOutput));
            Assert.Contains("Microsoft.", errorOutput, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            TryDelete(tempDirectory);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
