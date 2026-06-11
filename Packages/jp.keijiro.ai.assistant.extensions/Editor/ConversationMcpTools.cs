using System;
using System.IO;
using System.Linq;
using System.Text;
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

} // namespace AIAssistantExtensions
