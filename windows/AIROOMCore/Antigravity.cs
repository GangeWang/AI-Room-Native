using System.Text;
using System.Text.Json;

namespace AIROOMCore;

/// <summary>
/// The Antigravity CLI (`agy`) runs one turn per call and prints newline-delimited
/// JSON events. It signs in through its own credentials, shared with the
/// Antigravity app; AIROOM never reads or passes those credentials.
/// (The headless doc page omits them, but `agy --help` confirms --mode,
/// --settings and --disable-slash-commands exist on Windows too.)
/// </summary>
public static class Antigravity
{
    /// <summary>Settings for this run only, so a looser choice saved for `agy`
    /// itself can't widen what a member may do. Actions that need approval are
    /// refused: headless runs have nobody to ask — and an auto-denied tool ends
    /// the turn with no reply, so the read-only tools a desk prompt needs are
    /// allowlisted explicitly. Writes stay gated by the mode (plan / accept-edits).</summary>
    const string RunSettings =
        """{"toolPermission":"request-review","allowNonWorkspaceAccess":false,"permissions":{"allow":["list_dir","view_file","grep_search","find_by_name","read_url_content","read_resource"]}}""";

    /// <summary>Plan mode can't change files, and the terminal sandbox holds in
    /// every mode. The prompt arrives on stdin, so it never shows in the process
    /// list. Note: agy warns that --mode has no effect while slash command
    /// expansion is disabled, so --disable-slash-commands stays off.</summary>
    public static List<string> Arguments(ExecutionAccess access, CLIRunOptions options)
    {
        var agy = new List<string>
        {
            "--input-format", "stream-json", "--output-format", "stream-json", "--sandbox",
            "--settings", RunSettings,
            "--mode", access == ExecutionAccess.StagedWrite ? "accept-edits" : "plan",
        };
        var model = ModelCatalog.Sanitize(options.Model);
        if (model.Length > 0) agy.AddRange(["--model", model]);
        if (options.Effort.Length > 0) agy.AddRange(["--effort", options.Effort]);
        if (options.Session?.ResumeID is { Length: > 0 } resumeID) agy.AddRange(["--conversation", resumeID]);
        return agy;
    }

    /// <summary>One stream-json user message, on one line.</summary>
    public static byte[] UserMessage(string prompt)
    {
        var message = new Dictionary<string, object>
        {
            ["event"] = "user",
            ["message"] = new Dictionary<string, object> { ["content"] = prompt },
        };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            JsonSerializer.Serialize(writer, message);
        }
        stream.WriteByte(0x0A);
        return stream.ToArray();
    }

    /// <summary>`agy models` prints "slug<TAB>Title" lines, after a progress line.</summary>
    public static List<ModelChoice> ParseModels(string output)
    {
        var choices = new List<ModelChoice>();
        foreach (var rawLine in output.Split('\n'))
        {
            var parts = rawLine.Trim().Split('\t', 2);
            if (parts.Length != 2) continue;
            var slug = parts[0].Trim();
            if (slug.Length == 0 || slug.Contains(' ')) continue;
            choices.Add(new ModelChoice(slug, parts[1].Trim(), []));
        }
        return choices;
    }

    /// <summary>`agy` has no status command: it lists models only once signed in.</summary>
    public static OfficialCLIStatus Status(string modelsOutput) =>
        ParseModels(modelsOutput).Count == 0 ? OfficialCLIStatus.NotLoggedIn : OfficialCLIStatus.Authenticated;

    /// <summary>A conversation id `agy` doesn't know starts a new conversation with
    /// only a warning, so a member would carry on without their context.</summary>
    public static bool LostConversation(string? resumeID, string? reported)
    {
        if (resumeID is null) return false;
        return reported != resumeID;
    }
}
