using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PiNet.Abstractions.Models;

namespace PiNet.AgentCore.Tools;

public static class BuiltInTools
{
    private const int MaxOutputChars = 50_000;

    public static IReadOnlyList<AgentTool> All() => [ReadFile, WriteFile, EditFile, Bash];

    public static AgentTool ReadFile { get; } = new()
    {
        Name = "read_file",
        Label = "Read File",
        Description = "Read a file's contents and return them as text. Fails if the file does not exist.",
        Parameters = new ToolParameterSchema
        {
            Type = "object",
            Properties = new Dictionary<string, ToolParameterSchema>
            {
                ["path"] = new() { Type = "string", Description = "Absolute or relative file path to read" }
            },
            Required = ["path"]
        },
        Execute = async (callId, args, ct, progress) =>
        {
            var path = args.TryGetProperty("path", out var p) ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(path))
                return ToolResult.Error("Missing required parameter: path");

            if (!File.Exists(path))
                return ToolResult.Error($"File not found: {path}");

            var content = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
            if (content.Length > MaxOutputChars)
                content = content[..MaxOutputChars] + "\n\n[... output truncated at 50000 chars ...]";

            return ToolResult.Ok(content);
        }
    };

    public static AgentTool WriteFile { get; } = new()
    {
        Name = "write_file",
        Label = "Write File",
        Description = "Write content to a file, creating parent directories as needed. Overwrites existing files.",
        Parameters = new ToolParameterSchema
        {
            Type = "object",
            Properties = new Dictionary<string, ToolParameterSchema>
            {
                ["path"] = new() { Type = "string", Description = "File path to write to" },
                ["content"] = new() { Type = "string", Description = "Full content to write" }
            },
            Required = ["path", "content"]
        },
        Execute = async (callId, args, ct, progress) =>
        {
            var path = args.TryGetProperty("path", out var p) ? p.GetString() : null;
            var content = args.TryGetProperty("content", out var c) ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(path))
                return ToolResult.Error("Missing required parameter: path");
            content ??= string.Empty;

            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(full, content, Encoding.UTF8, ct);
            return ToolResult.Ok($"Wrote {Encoding.UTF8.GetByteCount(content)} bytes to {full}");
        }
    };

    public static AgentTool EditFile { get; } = new()
    {
        Name = "edit_file",
        Label = "Edit File",
        Description = "Replace a single exact string occurrence in a file. Fails if the match is not found or appears more than once.",
        Parameters = new ToolParameterSchema
        {
            Type = "object",
            Properties = new Dictionary<string, ToolParameterSchema>
            {
                ["path"] = new() { Type = "string", Description = "File path to edit" },
                ["old_string"] = new() { Type = "string", Description = "Exact text to replace" },
                ["new_string"] = new() { Type = "string", Description = "Replacement text" }
            },
            Required = ["path", "old_string", "new_string"]
        },
        Execute = async (callId, args, ct, progress) =>
        {
            var path = args.TryGetProperty("path", out var p) ? p.GetString() : null;
            var oldString = args.TryGetProperty("old_string", out var o) ? o.GetString() : null;
            var newString = args.TryGetProperty("new_string", out var n) ? n.GetString() : null;

            if (string.IsNullOrWhiteSpace(path))
                return ToolResult.Error("Missing required parameter: path");
            if (oldString is null)
                return ToolResult.Error("Missing required parameter: old_string");
            newString ??= string.Empty;
            if (oldString.Length == 0)
                return ToolResult.Error("old_string must not be empty");

            if (!File.Exists(path))
                return ToolResult.Error($"File not found: {path}");

            var text = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
            var count = CountOccurrences(text, oldString);

            if (count == 0)
                return ToolResult.Error($"Could not find the text to replace in {path}");
            if (count > 1)
                return ToolResult.Error($"Found {count} occurrences of the text in {path}; edit_file requires exactly one match. Include more surrounding context in old_string.");

            var edited = text.Replace(oldString, newString);
            await File.WriteAllTextAsync(path, edited, Encoding.UTF8, ct);
            return ToolResult.Ok($"Edited {path}: replaced one occurrence.");
        }
    };

    public static AgentTool Bash { get; } = new()
    {
        Name = "bash",
        Label = "Bash",
        Description = "Run a shell command and capture its standard output and error output. On Windows uses cmd.exe; otherwise /bin/bash.",
        Parameters = new ToolParameterSchema
        {
            Type = "object",
            Properties = new Dictionary<string, ToolParameterSchema>
            {
                ["command"] = new() { Type = "string", Description = "Shell command to run" }
            },
            Required = ["command"]
        },
        Execute = async (callId, args, ct, progress) =>
        {
            var command = args.TryGetProperty("command", out var c) ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(command))
                return ToolResult.Error("Missing required parameter: command");

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            var isWindows = OperatingSystem.IsWindows();
            var psi = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/bash",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory
            };
            if (isWindows)
            {
                psi.Arguments = $"/d /s /c \"{command}\"";
            }
            else
            {
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(command);
            }

            using var process = new Process { StartInfo = psi };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(120));

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                return ToolResult.Error($"Failed to start command: {ex.Message}");
            }

            var readStdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var readStderr = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                try { process.Kill(true); } catch { /* ignore */ }
                return ToolResult.Error("Command timed out after 120 seconds.");
            }

            var outText = await readStdout;
            var errText = await readStderr;

            var output = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(outText))
                output.Append(outText);
            if (!string.IsNullOrWhiteSpace(errText))
            {
                if (output.Length > 0)
                    output.Append('\n');
                output.Append("[stderr]\n").Append(errText);
            }

            var result = output.ToString();
            if (result.Length > MaxOutputChars)
                result = result[..MaxOutputChars] + "\n\n[... output truncated ...]";

            return process.ExitCode == 0
                ? ToolResult.Ok(string.IsNullOrWhiteSpace(result) ? "(command completed with no output)" : result)
                : ToolResult.Error($"[exit code {process.ExitCode}]\n{result}");
        }
    };

    private static int CountOccurrences(string text, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
            if (count > 1)
                break;
        }
        return count;
    }
}