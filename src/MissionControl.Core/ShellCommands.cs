using System.Text;
using System.Text.RegularExpressions;

namespace MissionControl.Core;

/// <summary>
/// Reads which commands a shell call executes, the way a shell would: the text is split into pipelines at ;, &amp;&amp;,
/// ||, &amp;, newlines and grouping; quoted text, comments and heredoc bodies are data, never commands; and scripts
/// handed to bash -c, sh -c, powershell -Command, cmd /c and wsl are read in turn. Bash and PowerShell share enough
/// syntax for this. A call whose output also carries file contents or search results cannot have that output
/// attributed to its commands, so it yields no operations.
/// </summary>
internal static class ShellCommands
{
    private const int MaximumDepth = 3;
    private static readonly string[] Order = ["pr", "commit", "push", "test"];
    // Programs that print existing text: a log, a file or search results.
    private static readonly HashSet<string> Readers = new(StringComparer.Ordinal)
    {
        "cat", "tac", "type", "gc", "get-content", "more", "less", "head", "tail", "bat", "rg", "grep", "egrep", "fgrep", "zgrep",
        "ag", "ack", "findstr", "select-string", "sls", "sed", "awk", "gawk", "jq", "yq", "strings", "od", "xxd", "hexdump", "nl", "cut", "sort", "uniq"
    };
    private static readonly HashSet<string> Shells = new(StringComparer.Ordinal) { "bash", "sh", "zsh", "dash", "ksh" };
    private static readonly Regex CodeModeCommand = new(@"\bcmd\s*:\s*(?:""((?:[^""\\]|\\.)*)""|'((?:[^'\\]|\\.)*)'|`((?:[^`\\]|\\.)*)`)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    private static readonly Regex Assignment = new(@"^[A-Za-z_][A-Za-z0-9_]*=", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>The shell commands a Codex code-mode script runs through exec_command, one per line.</summary>
    internal static string CodeModeCommands(string script)
    {
        try
        {
            return string.Join('\n', CodeModeCommand.Matches(script).Select(match =>
                Unescape(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value)));
        }
        catch (RegexMatchTimeoutException) { return ""; }
    }

    /// <summary>"pr", "commit", "push" and "test" in that order, for the operations the script runs; "" when none or when its output cannot be attributed.</summary>
    internal static string Operations(string script)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var reads = false;
        // Files a fact-producing command wrote its output to: reading them afterwards reads that command's output.
        var written = new HashSet<string>(StringComparer.Ordinal);
        // Simple variables (PHP=/path/php.exe, $php = "C:\php\php.exe") so "$PHP" resolves to the program it names.
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Script(script, 0);
        return reads ? "" : string.Join(' ', Order.Where(found.Contains));

        // Each returns whether it ran a fact-producing operation.
        bool Script(string text, int depth)
        {
            var any = false;
            foreach (var pipeline in Parse(text))
            {
                var producing = false;
                foreach (var command in pipeline)
                    if (Command(command, depth, producing)) producing = any = true;
            }
            return any;
        }
        bool Command(IReadOnlyList<string> words, int depth, bool filtering)
        {
            if (words.All(word => Assignment.IsMatch(word)))
            {
                foreach (var word in words) variables[word[..word.IndexOf('=')]] = word[(word.IndexOf('=') + 1)..];
                return false;
            }
            if (words.Count == 3 && words[0].StartsWith('$') && words[1] == "=") { variables[words[0][1..]] = words[2]; return false; }
            var (program, arguments, script, command) = Resolve(words, Expand);
            if (script is not null) return depth < MaximumDepth && Script(script, depth + 1);
            if (command is not null) return depth < MaximumDepth && Command(command, depth + 1, filtering);
            if (program is null) return false;
            if (Classify(program, arguments) is { } operation)
            {
                found.Add(operation);
                foreach (var target in Targets(arguments)) written.Add(target);
                return true;
            }
            // tee on a fact-producing command's output also writes that output.
            if (filtering && program == "tee") foreach (var target in arguments.Where(word => !word.StartsWith('-'))) written.Add(target);
            // A filter on a fact-producing command's own output is fine, and so is reading a file it wrote; any other
            // reader mixes in other text.
            if (!filtering && Reads(program, arguments) && !arguments.Any(written.Contains)) reads = true;
            return false;
        }
        // Only a word that is exactly one variable ($NAME or ${NAME}) is replaced.
        string Expand(string word)
        {
            if (!word.StartsWith('$')) return word;
            var name = word.StartsWith("${", StringComparison.Ordinal) && word.EndsWith('}') ? word[2..^1] : word[1..];
            return variables.TryGetValue(name, out var value) ? value : word;
        }
    }

    // Where a command's output was redirected: "> file", ">file", ">> file", "&> file" (not "2>&1").
    private static IEnumerable<string> Targets(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var word = arguments[i];
            var start = word.StartsWith("&>", StringComparison.Ordinal) ? 2 : word.StartsWith("1>", StringComparison.Ordinal) ? 2 : word.StartsWith('>') ? 1 : -1;
            if (start < 0) continue;
            if (start < word.Length && word[start] == '>') start++;
            var target = start < word.Length ? word[start..] : i + 1 < arguments.Count ? arguments[++i] : "";
            if (target.Length > 0 && !target.StartsWith('&')) yield return target;
        }
    }

    /// <summary>Pipelines of simple commands, each a list of words with quoting removed.</summary>
    internal static List<List<List<string>>> Parse(string script)
    {
        var pipelines = new List<List<List<string>>>();
        var pipeline = new List<List<string>>();
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        var heredocs = new Queue<string>();
        void EndWord() { if (inWord) { words.Add(word.ToString()); word.Clear(); inWord = false; } }
        void EndCommand() { EndWord(); if (words.Count > 0) { pipeline.Add(words); words = []; } }
        void EndPipeline() { EndCommand(); if (pipeline.Count > 0) { pipelines.Add(pipeline); pipeline = []; } }
        char At(int index) => index < script.Length ? script[index] : '\0';
        for (var i = 0; i < script.Length; i++)
        {
            var c = script[i];
            if (c == '\'')
            {
                var end = script.IndexOf('\'', i + 1);
                if (end < 0) end = script.Length;
                word.Append(script, i + 1, end - i - 1); inWord = true; i = end;
            }
            else if (c == '"')
            {
                var j = i + 1;
                for (; j < script.Length && script[j] != '"'; j++)
                {
                    // Bash escapes with \, PowerShell with `; a Windows path's other backslashes stay as they are.
                    if (script[j] is '\\' or '`' && At(j + 1) is '"' or '\\' or '`' or '$') j++;
                    word.Append(script[j]);
                }
                inWord = true; i = j;
            }
            else if (c == '$' && At(i + 1) == '{')
            {
                var end = script.IndexOf('}', i);
                if (end < 0) end = script.Length - 1;
                word.Append(script, i, end - i + 1); inWord = true; i = end;
            }
            else if (c == '@' && !inWord && At(i + 1) is '\'' or '"' && At(i + 2) is '\n' or '\r')
            {
                // A PowerShell here-string is data up to its closing '@ or "@ line.
                var close = script.IndexOf("\n" + At(i + 1) + "@", i, StringComparison.Ordinal);
                i = close < 0 ? script.Length : close + 2;
                inWord = true;
            }
            else if (c == '\\' && At(i + 1) == '\n') i++;
            else if (c is ' ' or '\t' or '\r') EndWord();
            else if (c == '\n')
            {
                EndPipeline();
                // A heredoc's body is data up to its delimiter line.
                while (heredocs.TryDequeue(out var delimiter))
                {
                    var next = i + 1;
                    while (next < script.Length)
                    {
                        var lineEnd = script.IndexOf('\n', next);
                        if (lineEnd < 0) lineEnd = script.Length;
                        var line = script[next..lineEnd].Trim(' ', '\t', '\r');
                        next = lineEnd + 1;
                        if (line == delimiter) break;
                    }
                    i = next - 1;
                }
            }
            else if (c == '#' && !inWord)
            {
                var end = script.IndexOf('\n', i);
                i = (end < 0 ? script.Length : end) - 1;
            }
            else if (c == ';' || c is '(' or ')' or '{' or '}') EndPipeline();
            else if (c == '&' && At(i + 1) == '>') { word.Append(c); inWord = true; }
            else if (c == '&' && (!inWord || At(i + 1) == '&' || At(i - 1) is ' ' or '\t'))
            {
                if (At(i + 1) == '&') i++;
                EndPipeline();
            }
            else if (c == '|')
            {
                if (At(i + 1) == '|') { i++; EndPipeline(); }
                else EndCommand();
            }
            else if (c == '<' && At(i + 1) == '<' && At(i + 2) != '<')
            {
                EndWord();
                i += 2;
                if (At(i) is '-' or '~') i++;
                while (At(i) is ' ' or '\t') i++;
                var delimiter = new StringBuilder();
                for (; i < script.Length && script[i] is not (' ' or '\t' or '\r' or '\n' or ';' or '|' or '&' or ')'); i++)
                    if (script[i] is not ('\'' or '"')) delimiter.Append(script[i]);
                if (delimiter.Length > 0) heredocs.Enqueue(delimiter.ToString());
                i--;
            }
            else { word.Append(c); inWord = true; }
        }
        EndPipeline();
        return pipelines;
    }

    // The program a command runs and its arguments, past assignments and wrappers such as sudo, npx or uv run; or a
    // script it hands to another shell; or, for wsl, the command it runs.
    private static (string? Program, IReadOnlyList<string> Arguments, string? Script, IReadOnlyList<string>? Command) Resolve(IReadOnlyList<string> source, Func<string, string> expand)
    {
        IReadOnlyList<string> words = source.Select(expand).ToArray();
        var i = 0;
        for (var guard = 0; guard < 8 && i < words.Count; guard++)
        {
            while (i < words.Count && Assignment.IsMatch(words[i])) i++;
            // PowerShell assignment: $result = command ...
            if (i + 1 < words.Count && words[i].StartsWith('$') && words[i + 1] == "=") { i += 2; continue; }
            if (i >= words.Count) break;
            var name = Program(words[i]);
            if (name is "sudo" or "env" or "time" or "nohup" or "command" or "exec" or "nice" or "call" or "npx" or "pnpx" or "bunx")
                i = SkipOptions(words, i + 1);
            else if (name == "timeout") i = Math.Min(words.Count, SkipOptions(words, i + 1) + 1);
            else if (name is "npm" or "pnpm" or "yarn" or "bun" && i + 1 < words.Count && words[i + 1] is "exec" or "dlx" or "x")
                i = SkipOptions(words, i + 2);
            else if (name is "uv" or "poetry" or "pipenv" or "hatch" or "pdm" or "rye" && i + 1 < words.Count && words[i + 1] == "run")
                i = SkipOptions(words, i + 2);
            else break;
        }
        if (i >= words.Count) return (null, [], null, null);
        var program = Program(words[i]);
        var arguments = words.Skip(i + 1).ToArray();
        if (Shells.Contains(program))
        {
            var flag = Array.FindIndex(arguments, word => word.Length > 1 && word[0] == '-' && word[1] != '-' && word.Contains('c'));
            return flag >= 0 && flag + 1 < arguments.Length ? (null, [], arguments[flag + 1], null) : (program, arguments, null, null);
        }
        // PowerShell and cmd join the remaining arguments into one command line, as Windows does.
        if (program is "pwsh" or "powershell")
        {
            var flag = Array.FindIndex(arguments, word => word.Length >= 2 && "-command".StartsWith(word, StringComparison.OrdinalIgnoreCase));
            return flag >= 0 ? (null, [], string.Join(' ', arguments.Skip(flag + 1)), null) : (program, arguments, null, null);
        }
        if (program == "cmd")
        {
            var flag = Array.FindIndex(arguments, word => word.Equals("/c", StringComparison.OrdinalIgnoreCase) || word.Equals("/k", StringComparison.OrdinalIgnoreCase));
            return flag >= 0 ? (null, [], string.Join(' ', arguments.Skip(flag + 1)), null) : (program, arguments, null, null);
        }
        if (program == "wsl")
        {
            var k = 0;
            while (k < arguments.Length && arguments[k].StartsWith('-'))
            {
                if (arguments[k] is "-e" or "--exec" or "--") { k++; break; }
                k += arguments[k] is "-d" or "--distribution" or "-u" or "--user" or "--cd" or "--shell-type" ? 2 : 1;
            }
            return (null, [], null, arguments.Skip(k).ToArray());
        }
        return (program, arguments, null, null);
    }

    // The fact-producing operation a program run performs, if any. Dry runs perform nothing.
    private static string? Classify(string program, IReadOnlyList<string> arguments)
    {
        var first = arguments.Count > 0 ? arguments[0] : "";
        switch (program)
        {
            case "git":
            {
                var k = 0;
                while (k < arguments.Count && arguments[k].StartsWith('-'))
                    k += arguments[k] is "-C" or "-c" or "--git-dir" or "--work-tree" or "--namespace" ? 2 : 1;
                if (k >= arguments.Count) return null;
                var rest = arguments.Skip(k + 1).ToArray();
                var dryRun = rest.Contains("--dry-run");
                return arguments[k] switch
                {
                    "commit" when !dryRun => "commit",
                    "push" when !dryRun && !rest.Any(word => word.Length > 1 && word[0] == '-' && word[1] != '-' && word.Contains('n')) => "push",
                    _ => null
                };
            }
            case "gh":
                return first == "pr" && arguments.Count > 1 && arguments[1] == "create" && !arguments.Contains("--dry-run") ? "pr" : null;
            case "dotnet":
                return first == "test" ? "test" : null;
            case "pytest" or "py.test" or "jest" or "vitest" or "phpunit" or "pest":
                return "test";
            case "python" or "python3" or "py":
            {
                var module = arguments.ToList().IndexOf("-m");
                return module >= 0 && module + 1 < arguments.Count && arguments[module + 1] is "pytest" or "unittest" ? "test" : null;
            }
            case "cargo":
            {
                var sub = arguments.FirstOrDefault(word => !word.StartsWith('+') && !word.StartsWith('-')) ?? "";
                return sub is "test" or "nextest" ? "test" : null;
            }
            case "npm" or "pnpm" or "yarn" or "bun":
            {
                var words = arguments.Where(word => !word.StartsWith('-')).ToArray();
                if (words.Length == 0) return null;
                if (words[0] is "test" or "t" || words[0] is "run" or "run-script" && words.Length > 1 && IsTestScript(words[1])) return "test";
                return program != "npm" && (IsTestScript(words[0]) || words[0] is "jest" or "vitest") ? "test" : null;
            }
            case "php":
            {
                // Interpreter options come first: php -d memory_limit=2G vendor/bin/pest
                var k = 0;
                while (k < arguments.Count && arguments[k].StartsWith('-')) k += arguments[k] is "-d" or "-c" or "-z" ? 2 : 1;
                var script = k < arguments.Count ? arguments[k] : "";
                return script == "artisan" && k + 1 < arguments.Count && arguments[k + 1] == "test" || Program(script) is "phpunit" or "pest" ? "test" : null;
            }
            case "artisan":
                return first == "test" ? "test" : null;
            case "composer":
                return first == "test" || first is "run" or "run-script" && arguments.Count > 1 && IsTestScript(arguments[1]) ? "test" : null;
            default:
                return null;
        }
    }

    // Prints existing text to the output, unless its output goes to a file or it only reports matches or counts.
    private static bool Reads(string program, IReadOnlyList<string> arguments)
    {
        if (!Readers.Contains(program)) return false;
        if (arguments.Any(word => word.StartsWith('>') || word.StartsWith("1>", StringComparison.Ordinal))) return false;
        if (program is "sed" && arguments.Any(word => word.StartsWith("-i", StringComparison.Ordinal) || word == "--in-place")) return false;
        if (program is "grep" or "egrep" or "fgrep" or "rg" or "zgrep" or "findstr" or "select-string" or "sls"
            && arguments.Any(word => word is "-q" or "--quiet" or "-l" or "-c" or "--count" or "--files-with-matches" || word.Equals("-Quiet", StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    private static bool IsTestScript(string name) => name == "test" || name.StartsWith("test:", StringComparison.Ordinal);

    private static int SkipOptions(IReadOnlyList<string> words, int index)
    {
        while (index < words.Count && (words[index].StartsWith('-') || Assignment.IsMatch(words[index]))) index++;
        return index;
    }

    // "C:/Program Files/Git/cmd/git.exe" and "./vendor/bin/pest" name git and pest.
    private static string Program(string word)
    {
        var name = word[(Math.Max(word.LastIndexOf('/'), word.LastIndexOf('\\')) + 1)..].ToLowerInvariant();
        foreach (var extension in new[] { ".exe", ".cmd", ".bat", ".ps1" })
            if (name.EndsWith(extension, StringComparison.Ordinal)) return name[..^extension.Length];
        return name;
    }

    // A JavaScript string literal's escapes: \n, \t, \r, \uXXXX, \xXX; any other escaped character stands for itself.
    private static string Unescape(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 == text.Length) { result.Append(text[i]); continue; }
            var c = text[++i];
            if (c == 'u' && i + 4 < text.Length && int.TryParse(text.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var unicode)) { result.Append((char)unicode); i += 4; }
            else if (c == 'x' && i + 2 < text.Length && int.TryParse(text.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var hex)) { result.Append((char)hex); i += 2; }
            else result.Append(c switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => c });
        }
        return result.ToString();
    }
}
