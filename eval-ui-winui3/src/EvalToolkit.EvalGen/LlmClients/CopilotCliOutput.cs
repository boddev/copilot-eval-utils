using System.Text;
using System.Text.Json;

namespace EvalToolkit.EvalGen.LlmClients;

/// <summary>
/// Extracts the assistant's textual answer from the GitHub Copilot CLI's
/// <c>--output-format json</c> stream (JSONL: one JSON event object per line).
///
/// <para>The CLI emits many event types (session/MCP/skill status, reasoning,
/// turn markers, a final <c>result</c>). The model's answer lives in
/// <c>assistant.message</c> events under <c>data.content</c>. Selecting those
/// events isolates the answer from the startup banners and tool/MCP noise that
/// the user's extensions print, which is far more robust than scraping plain
/// text stdout.</para>
/// </summary>
internal static class CopilotCliOutput
{
    /// <summary>
    /// Concatenate the <c>data.content</c> of every <c>assistant.message</c>
    /// event, in order. If no such event is found (e.g. an older CLI that does
    /// not speak JSONL), the raw stdout is returned unchanged so the caller's
    /// JSON parser can still attempt extraction.
    /// </summary>
    public static string ExtractAssistantText(string stdout)
    {
        if (string.IsNullOrEmpty(stdout)) return stdout ?? string.Empty;

        var sb = new StringBuilder();
        bool any = false;

        foreach (string rawLine in stdout.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] != '{') continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("type", out JsonElement typeEl)
                    || typeEl.ValueKind != JsonValueKind.String
                    || typeEl.GetString() != "assistant.message")
                {
                    continue;
                }

                if (!root.TryGetProperty("data", out JsonElement dataEl)
                    || dataEl.ValueKind != JsonValueKind.Object
                    || !dataEl.TryGetProperty("content", out JsonElement contentEl)
                    || contentEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                sb.Append(contentEl.GetString());
                any = true;
            }
        }

        return any ? sb.ToString() : stdout;
    }
}
