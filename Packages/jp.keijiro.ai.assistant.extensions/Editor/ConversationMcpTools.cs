using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Unity.AI.MCP.Editor.Helpers;
using Unity.AI.MCP.Editor.ToolRegistry;
using UnityEngine;

namespace AIAssistantExtensions {

// MCP tools that expose the Conversation Extractor over the Unity MCP bridge, so an
// AI client can list and export AI Assistant conversations without the editor window.
// All require an AI Assistant window to be open (the live provider only exists then).
// Results use the standard Response envelope ({ success, message, data }).

public class ListConversationsParams { }

[McpTool("aia_list_conversations",
  "List the Unity AI Assistant conversations available for extraction. Returns id, title, " +
  "last-message timestamp (Unix ms) and favorite flag. Requires an AI Assistant window to be open.",
  EnabledByDefault = true)]
public class ListConversationsTool : IUnityMcpTool<ListConversationsParams>
{
    public async Task<object> ExecuteAsync(ListConversationsParams parameters)
    {
        try
        {
            var list = await new ConversationExtractorCore().ListConversationsAsync();
            return Response.Success(
              $"{list.Count} conversation(s) available.",
              new
              {
                  count = list.Count,
                  conversations = list.Select(c => new
                  {
                      id = c.Id,
                      title = c.Title,
                      timestamp = c.Timestamp,
                      favorite = c.Favorite
                  }).ToArray()
              });
        }
        catch (Exception e)
        {
            return Response.Error(e.Message);
        }
    }
}

public class ExtractConversationParams
{
    [McpDescription("Conversation id obtained from aia_list_conversations", Required = true)]
    public string ConversationId { get; set; }

    [McpDescription("Include tool-call sections (arguments and results) in the output")]
    public bool IncludeToolCalls { get; set; } = true;

    [McpDescription("Output file path, absolute or relative to the project root. " +
      "If omitted or a directory, an auto-generated name is used under that directory (default: Logs/).")]
    public string OutputPath { get; set; }
}

[McpTool("aia_extract_conversation",
  "Extract a Unity AI Assistant conversation to a Markdown file on disk and return only its path " +
  "and metadata (the content is written to the file, not returned, to avoid bloating context). " +
  "Requires an AI Assistant window to be open.",
  EnabledByDefault = true)]
public class ExtractConversationTool : IUnityMcpTool<ExtractConversationParams>
{
    public async Task<object> ExecuteAsync(ExtractConversationParams parameters)
    {
        try
        {
            var includeToolCalls = parameters?.IncludeToolCalls ?? true;
            var conversation = await new ConversationExtractorCore()
              .LoadConversationByIdAsync(parameters?.ConversationId);

            var markdown = ConversationExtractorCore.BuildMarkdown(conversation, includeToolCalls);
            var path = ResolveOutputPath(parameters?.OutputPath, conversation);

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, markdown, new UTF8Encoding(false));

            var messages = ConversationExtractorCore.MessageCount(conversation);
            return Response.Success(
              $"Saved {messages} message(s) to {path}",
              new { path, bytes = new FileInfo(path).Length, messages });
        }
        catch (Exception e)
        {
            return Response.Error(e.Message);
        }
    }

    // Resolves the destination: an auto-named file under Logs/ by default; an explicit
    // file path as given; or an auto-named file inside an explicit directory.
    static string ResolveOutputPath(string outputPath, object conversation)
    {
        var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
        var autoName = ConversationExtractorCore.BuildFileName(conversation) + ".md";

        if (string.IsNullOrWhiteSpace(outputPath))
            return Path.Combine(projectRoot, "Logs", autoName);

        var path = Path.IsPathRooted(outputPath) ? outputPath : Path.Combine(projectRoot, outputPath);

        // Treat an existing directory or an extension-less path as a target directory.
        if (Directory.Exists(path) || string.IsNullOrEmpty(Path.GetExtension(path)))
            return Path.Combine(path, autoName);

        return path;
    }
}

public class ReexportDirectoryParams
{
    [McpDescription("Directory holding old-format .md logs to re-export. Absolute or relative to the project root.", Required = true)]
    public string Directory { get; set; }

    [McpDescription("Include tool-call sections (arguments and results) in the output")]
    public bool IncludeToolCalls { get; set; } = true;
}

[McpTool("aia_reexport_directory",
  "Re-export every old-format AI Assistant log (.md) in a directory to the new Markdown format. " +
  "Each file is matched to a conversation by its title (the '# ' heading) and, when titles collide, " +
  "by the date/time in its filename. New-format files are written into the same directory. Files that " +
  "match no available conversation (e.g. from another account) are skipped. Files already in the new " +
  "format are ignored. Returns a summary only. Requires an AI Assistant window to be open.",
  EnabledByDefault = true)]
public class ReexportDirectoryTool : IUnityMcpTool<ReexportDirectoryParams>
{
    // Matches the new-format file name: yyyyMMdd-HHmm-<title>-<8 hex>.md
    static readonly Regex NewFormatName = new(@"^\d{8}-\d{4}-.*-[0-9a-fA-F]{8}\.md$", RegexOptions.Compiled);
    // Matches the old-format file-name date prefix: yyyy-MM-dd HH-mm
    static readonly Regex OldFormatDate = new(@"^(\d{4})-(\d{2})-(\d{2}) (\d{2})-(\d{2}) ", RegexOptions.Compiled);

    // The assistant reuses generic titles (and even identical opening prompts) across
    // projects and accounts, so a title match alone is ambiguous. The old filename's
    // time tracks the conversation's last-message time within minutes, so we only accept
    // a same-title candidate whose timestamp is within this window of the filename's.
    const long MaxTimestampSkewMs = 2L * 24 * 60 * 60 * 1000; // 2 days

    public async Task<object> ExecuteAsync(ReexportDirectoryParams parameters)
    {
        try
        {
            var includeToolCalls = parameters?.IncludeToolCalls ?? true;

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            var inputDir = parameters?.Directory ?? "";
            var dir = Path.IsPathRooted(inputDir) ? inputDir : Path.Combine(projectRoot, inputDir);
            if (!Directory.Exists(dir))
                return Response.Error($"Directory not found: {dir}");

            var core = new ConversationExtractorCore();
            var list = await core.ListConversationsAsync();
            var byTitle = list
              .GroupBy(c => c.Title ?? "")
              .ToDictionary(g => g.Key, g => g.ToList());

            var written = new List<object>();
            var skipped = new List<object>();

            foreach (var file in Directory.GetFiles(dir, "*.md").OrderBy(f => f))
            {
                var name = Path.GetFileName(file);
                if (NewFormatName.IsMatch(name))
                    continue; // already converted

                var title = ReadTitle(file);
                if (string.IsNullOrEmpty(title))
                {
                    skipped.Add(new { source = name, reason = "no title" });
                    continue;
                }

                if (!byTitle.TryGetValue(title, out var candidates) || candidates.Count == 0)
                {
                    skipped.Add(new { source = name, reason = "no matching conversation (title)" });
                    continue;
                }

                var hint = ParseFileTimestamp(name);
                if (hint <= 0)
                {
                    skipped.Add(new { source = name, reason = "no date in filename to disambiguate" });
                    continue;
                }

                // Pick the closest-in-time same-title conversation, then require it to be
                // within the skew window; otherwise it belongs to a different conversation
                // that merely shares the (generic) title.
                var item = candidates.OrderBy(c => Math.Abs(c.Timestamp - hint)).First();
                var skewMs = Math.Abs(item.Timestamp - hint);
                if (skewMs > MaxTimestampSkewMs)
                {
                    skipped.Add(new { source = name, reason = $"no matching conversation (closest title match is {skewMs / 86400000} day(s) off)" });
                    continue;
                }

                var conversation = await core.LoadConversationAsync(item);
                var markdown = ConversationExtractorCore.BuildMarkdown(conversation, includeToolCalls);
                var outName = ConversationExtractorCore.BuildFileName(conversation) + ".md";
                File.WriteAllText(Path.Combine(dir, outName), markdown, new UTF8Encoding(false));

                written.Add(new { source = name, output = outName, id = item.Id });
            }

            return Response.Success(
              $"Re-exported {written.Count} file(s); skipped {skipped.Count}.",
              new { directory = dir, written, skipped, total = written.Count + skipped.Count });
        }
        catch (Exception e)
        {
            return Response.Error(e.Message);
        }
    }

    // Reads the conversation title from the file's first '# ' heading (BOM-tolerant).
    static string ReadTitle(string file)
    {
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.TrimStart('﻿').Trim();
            if (line.Length == 0) continue;
            return line.StartsWith("# ") ? line.Substring(2).Trim() : null;
        }
        return null;
    }

    // Parses the old-format filename date prefix to a Unix-ms hint (local time), or 0.
    static long ParseFileTimestamp(string name)
    {
        var m = OldFormatDate.Match(name);
        if (!m.Success) return 0;
        try
        {
            var dt = new DateTime(
              int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
              int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
              int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
              int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
              int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture),
              0, DateTimeKind.Local);
            return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
        }
        catch
        {
            return 0;
        }
    }
}

} // namespace AIAssistantExtensions
