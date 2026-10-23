using System.Text;

namespace PersonalAI.Core.Services.Ssh;

/// <param name="CanBeApproved">The request is refused only because it is not on the allowed list. Nothing about it is unsafe
/// in itself (it cannot chain, redirect or expand), so a person may approve this exact text; a hard refusal never can be.</param>
public sealed record GuardResult(bool Allowed, string? Error, bool CanBeApproved = false)
{
    public static readonly GuardResult Ok = new(true, null);
    public static GuardResult Deny(string error) => new(false, error);

    /// <summary>Refused because it is not on the allowed list; a person may still approve this exact request.</summary>
    public static GuardResult NotListed(string error) => new(false, error, CanBeApproved: true);
}

/// <summary>Decides whether a command may be sent to a server over SSH. A model proposes the commands, so the
/// rules live here, in code, rather than in a prompt: a read-only command from an allowlist, optionally piped
/// through a few stdin-only filters, with no way to chain, redirect, substitute or expand.
///
/// This is the first line of defense, not the only one: restrict the SSH account on the server too (a read-only
/// user, or <c>restrict</c> in <c>authorized_keys</c>) if you need a hard guarantee.</summary>
public sealed class SshCommandGuard(IReadOnlyList<string> allowlist)
{
    public const int MaxLength = 2000, MaxStages = 6;

    /// <summary>Read-only commands the model may run unless the user edits the list. Deliberately absent: sudo,
    /// shells, interpreters, awk/sed (a quoted program can run code or write files), find, xargs, tee, and
    /// anything that mutates state. An entry is a token prefix; one ending in '/' limits that command's path
    /// arguments to the prefix.</summary>
    public static readonly IReadOnlyList<string> DefaultAllowlist =
    [
        "uptime", "uname", "hostname", "date", "nproc", "whoami", "id",
        "free", "df", "lsblk", "lscpu", "lsmem", "vmstat", "iostat", "mpstat", "top", "sensors",
        "ps", "ss", "ip -br", "ip -s",
        "cat /proc/", "cat /sys/", "cat /etc/os-release", "cat /etc/hostname",
        "systemctl is-active", "systemctl is-enabled", "systemctl is-failed", "systemctl status",
        "systemctl list-units", "systemctl list-unit-files", "systemctl show",
        "journalctl",
        "docker ps", "docker container ls", "docker stats", "docker images", "docker info", "docker version",
        "docker system df", "docker volume ls", "docker network ls",
        "kubectl get", "kubectl top", "kubectl describe", "kubectl version", "kubectl cluster-info",
        "kubectl api-resources", "kubectl config current-context",
        "k3s kubectl get", "k3s kubectl top", "k3s kubectl describe", "k3s kubectl version"
    ];

    public static string DefaultAllowlistText => string.Join('\n', DefaultAllowlist);

    /// <param name="OperandIsPattern">The first operand is a pattern or character set, not a file (grep, tr), unless a flag
    /// supplies the pattern, in which case the operand is a file and must not look like one.</param>
    /// <param name="ValueFlags">Short flags whose next token is their value (`cut -d ' ' -f 1,3`, `sort -k 2,2`), not an operand.</param>
    private sealed record FilterRule(int MaxOperands, int MaxOperandLength, string DeniedShort, string[] DeniedLong, bool OperandIsPattern = false, string ValueFlags = "");

    // Filters read stdin. They may not read files (no path-like operands, no recursion, no -f/-o style flags). GNU tools
    // accept any unambiguous abbreviation of a long option (--out for --output), so a long option is denied when it is a
    // prefix of a denied one, not only when it matches exactly.
    private static readonly Dictionary<string, FilterRule> Filters = new(StringComparer.Ordinal)
    {
        ["grep"] = new(1, int.MaxValue, "rRfd", ["--recursive", "--dereference-recursive", "--file", "--directories", "--exclude-from"], OperandIsPattern: true, ValueFlags: "ABCme"),
        ["head"] = new(0, 0, "", [], ValueFlags: "nc"),
        ["tail"] = new(0, 0, "fF", ["--follow"], ValueFlags: "nc"),
        ["sort"] = new(1, 2, "oT", ["--output", "--temporary-directory", "--files0-from", "--compress-program"], ValueFlags: "ktS"),
        ["uniq"] = new(0, 0, "", [], ValueFlags: "fsw"),
        ["cut"] = new(1, 2, "", [], ValueFlags: "dfcb"),
        ["tr"] = new(2, int.MaxValue, "", [], OperandIsPattern: true),
        ["wc"] = new(0, 0, "", ["--files0-from"])
    };

    /// <summary>Flags that let an allowlisted first command read local files or talk to somewhere else, which a monitoring
    /// command never needs. kubectl and docker use exact long options; journalctl (getopt_long) accepts abbreviations.</summary>
    private sealed record FirstStageRule(string DeniedShort, string[] DeniedLong, bool Abbreviations);

    private static readonly Dictionary<string, FirstStageRule> FirstStageRules = new(StringComparer.Ordinal)
    {
        ["kubectl"] = new("fks", ["--filename", "--kustomize", "--kubeconfig", "--token", "--server", "--certificate-authority",
            "--client-certificate", "--client-key", "--username", "--password", "--as", "--as-group"], false),
        ["docker"] = new("H", ["--host", "--config", "--context"], false),
        ["journalctl"] = new("DM", ["--directory", "--file", "--root", "--image", "--machine", "--vacuum-size", "--vacuum-time", "--vacuum-files",
            "--rotate", "--flush", "--sync", "--relinquish-var", "--smart-relinquish-var", "--cursor-file", "--setup-keys", "--update-catalog",
            "--force", "--verify", "--verify-key"], true),
        ["systemctl"] = new("HM", ["--host", "--machine"], true)
    };

    /// <summary>/proc and /sys entries that lead back to the real filesystem or to secrets: symlinks to the process's
    /// root and working directory, its environment, open files and memory.</summary>
    private static readonly HashSet<string> DeniedPathSegments = new(StringComparer.Ordinal)
    {
        "root", "cwd", "exe", "fd", "fdinfo", "map_files", "environ", "mem", "kcore", "kmsg", "auxv"
    };

    private static bool IsSafeUnder(string path, string prefix)
    {
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal)) return false;
        // A glob could expand to a denied name (ro?t -> root), so none is accepted.
        if (path.IndexOfAny(['*', '?', '[']) >= 0) return false;
        return !path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(DeniedPathSegments.Contains);
    }

    private static GuardResult? CheckFlags(List<string> tokens, int from, string label, string deniedShort, string[] deniedLong, bool abbreviations)
    {
        foreach (var t in tokens.Skip(from))
        {
            if (t.StartsWith("--", StringComparison.Ordinal))
            {
                var name = t.Split('=', 2)[0];
                var denied = deniedLong.Contains(name) || (abbreviations && name.Length > 2 && deniedLong.Any(d => d.StartsWith(name, StringComparison.Ordinal)));
                if (denied) return GuardResult.Deny($"'{label} {name}' is not allowed: it can read files, change state or reach other systems.");
            }
            else if (t.Length > 1 && t[0] == '-')
            {
                // A bundle like -rn: reject if any letter is a denied one (a numeric tail such as -n5 is a value).
                if (t.Skip(1).TakeWhile(char.IsLetter).Any(deniedShort.Contains))
                    return GuardResult.Deny($"'{label} {t}' is not allowed: it can read files, change state or reach other systems.");
            }
        }
        return null;
    }

    /// <summary>Parses allowlist text: one entry per line (or comma-separated), blank lines and #-comments ignored.
    /// Null or blank text means the defaults.</summary>
    public static IReadOnlyList<string> ParseAllowlist(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return DefaultAllowlist;
        var entries = text.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => string.Join(' ', l.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return entries;
    }

    public GuardResult Check(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return GuardResult.Deny("The command is empty.");
        if (command.Length > MaxLength) return GuardResult.Deny($"The command is longer than {MaxLength} characters.");

        var (stages, error) = Tokenize(command);
        if (error is not null) return GuardResult.Deny(error);
        if (stages.Count > MaxStages) return GuardResult.Deny($"At most {MaxStages} piped commands are allowed.");

        // A hard refusal anywhere wins over "not on the list": approving one stage must not smuggle in another.
        GuardResult? notListed = null;
        for (var i = 0; i < stages.Count; i++)
        {
            var result = i == 0 ? CheckFirst(stages[0]) : CheckFilter(stages[i]);
            if (result.Allowed) continue;
            if (!result.CanBeApproved) return result;
            notListed ??= result;
        }
        return notListed ?? GuardResult.Ok;
    }

    // ---- tokenizing ---------------------------------------------------------------

    private static (List<List<string>> Stages, string? Error) Tokenize(string command)
    {
        var stages = new List<List<string>>();
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        char quote = '\0';

        void EndToken()
        {
            if (!inToken) return;
            tokens.Add(current.ToString());
            current.Clear();
            inToken = false;
        }

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (c is '\n' or '\r') return ([], "Newlines are not allowed.");

            if (quote == '\'')
            {
                if (c == '\'') quote = '\0'; else current.Append(c);
                continue;
            }
            if (quote == '"')
            {
                if (c == '"') quote = '\0';
                else if (c is '$' or '`' or '\\') return ([], $"'{c}' is not allowed (it would expand or escape inside double quotes). Use single quotes.");
                else current.Append(c);
                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    inToken = true; // '' is an (empty) token
                    break;
                case ' ' or '\t':
                    EndToken();
                    break;
                case '|':
                    if (i + 1 < command.Length && command[i + 1] == '|') return ([], "'||' is not allowed.");
                    EndToken();
                    if (tokens.Count == 0) return ([], "A piped command is empty.");
                    stages.Add(tokens);
                    tokens = [];
                    break;
                case ';' or '&' or '>' or '<' or '(' or ')' or '{' or '}' or '\\' or '`' or '$':
                    return ([], $"'{c}' is not allowed: commands can't be chained, redirected, substituted or expanded.");
                default:
                    current.Append(c);
                    inToken = true;
                    break;
            }
        }
        if (quote != '\0') return ([], "A quote is not closed.");
        EndToken();
        if (tokens.Count == 0) return ([], stages.Count > 0 ? "A piped command is empty." : "The command is empty.");
        stages.Add(tokens);
        return (stages, null);
    }

    // ---- first stage: the allowlist -------------------------------------------------

    private GuardResult CheckFirst(List<string> tokens)
    {
        var binary = tokens[0];
        if (tokens.Any(t => (binary is "kubectl" || (binary == "k3s" && tokens.Count > 1 && tokens[1] == "kubectl"))
                            && t.Contains("secret", StringComparison.OrdinalIgnoreCase)))
            return GuardResult.Deny("kubectl may not read secrets.");

        var prefixes = new List<string>();
        var exactOnly = new List<string>();
        foreach (var entry in allowlist)
        {
            var e = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (e.Length == 0 || tokens.Count < e.Length) continue;

            var prefixRule = e[^1].EndsWith('/');
            var literal = prefixRule ? e.Length - 1 : e.Length;
            if (!Enumerable.Range(0, literal).All(i => tokens[i] == e[i])) continue;

            if (!prefixRule)
            {
                // An entry that names a file ("cat /etc/hostname") allows exactly that command: anything more would
                // let extra arguments (another file, ~/.ssh/id_rsa) ride along.
                if (e.Any(x => x.StartsWith('/')) && tokens.Count != e.Length)
                {
                    exactOnly.Add(string.Join(' ', e));
                    continue;
                }
                return CheckFirstStageFlags(tokens) ?? CheckArguments(tokens) ?? GuardResult.Ok;
            }

            // "cat /proc/": every operand must live under the prefix and not lead back out of it (.., symlinks such as
            // /proc/self/root, globs that could expand to them).
            var operands = tokens.Skip(literal).Where(t => !t.StartsWith('-')).ToList();
            if (operands.Count > 0 && operands.All(o => IsSafeUnder(o, e[^1])))
                return CheckFirstStageFlags(tokens) ?? CheckArguments(tokens) ?? GuardResult.Ok;
            prefixes.Add(e[^1]);
        }

        if (exactOnly.Count > 0 && prefixes.Count == 0)
            return GuardResult.NotListed($"'{string.Join(' ', exactOnly.Distinct())}' is allowed exactly as written, without extra arguments.");

        // The command is allowed, but only for certain paths, and these arguments aren't under them.
        if (prefixes.Count > 0)
            return GuardResult.NotListed($"'{binary}' may only read paths under {string.Join(" or ", prefixes.Distinct())} (no '..', globs, or /proc/<pid>/root, cwd, exe, fd, environ), and at least one path is needed" +
                (exactOnly.Count > 0 ? $"; or exactly {string.Join(", ", exactOnly.Distinct().Select(x => $"'{x}'"))} as written, without extra arguments." : "."));
        return GuardResult.NotListed($"'{string.Join(' ', tokens.Take(2))}' is not on the allowed command list. Allowed: {Summary()}.");
    }

    /// <summary>For kubectl, docker and journalctl: refuse flags that read local files or point somewhere else.</summary>
    private static GuardResult? CheckFirstStageFlags(List<string> tokens)
    {
        var skip = tokens[0] == "k3s" && tokens.Count > 1 && tokens[1] == "kubectl" ? 2 : 1;
        var key = tokens[skip - 1];
        return FirstStageRules.TryGetValue(key, out var rule)
            ? CheckFlags(tokens, skip, key, rule.DeniedShort, rule.DeniedLong, rule.Abbreviations)
            : null;
    }

    // Listed commands are read-only in their usual form, but several take arguments that change state, switch namespace or read
    // a file ("ip link set", "date -s", "hostname NAME", "ss -K"). Those are not on the list: refused unless the user approves
    // the exact text.
    private static readonly HashSet<string> IpMutatingWords = new(StringComparer.Ordinal)
    {
        "add", "append", "prepend", "change", "chg", "replace", "set", "delete", "del", "flush", "exec", "save", "restore",
        "monitor", "attach", "detach"
    };

    private static readonly HashSet<string> IpOptions = new(StringComparer.Ordinal)
    {
        "-br", "-brief", "-s", "-stats", "-statistics", "-d", "-details", "-4", "-6", "-o", "-oneline", "-j", "-json", "-p", "-pretty", "-h", "-human"
    };

    private static GuardResult? CheckArguments(List<string> tokens)
    {
        var binary = tokens[0];
        var args = tokens.Skip(1).ToList();
        string Not(string why) => $"'{string.Join(' ', tokens.Take(Math.Min(tokens.Count, 3)))}' is not on the allowed command list: {why}.";

        switch (binary)
        {
            case "date":
                // Reading the clock only: -s/--set and -f/--file change it or read a file, and a bare operand other than +FORMAT sets it.
                for (var i = 0; i < args.Count; i++)
                {
                    var t = args[i];
                    if (t.StartsWith("--", StringComparison.Ordinal))
                    {
                        var name = t.Split('=', 2)[0];
                        if (name.Length > 2 && ("--set".StartsWith(name, StringComparison.Ordinal) || "--file".StartsWith(name, StringComparison.Ordinal)))
                            return GuardResult.NotListed(Not("it can set the clock or read a file"));
                        if (!t.Contains('=') && (name is "--date" or "--reference")) i++;
                    }
                    else if (t.Length > 1 && t[0] == '-')
                    {
                        if (t.Skip(1).Any(c => c is 's' or 'f')) return GuardResult.NotListed(Not("it can set the clock or read a file"));
                        if (t[^1] is 'd' or 'r') i++; // -d STRING, -r FILE: the value is not an operand
                    }
                    else if (!t.StartsWith('+'))
                        return GuardResult.NotListed(Not("a bare argument sets the clock"));
                }
                return null;

            case "hostname":
                // Showing the name only: an operand sets it, and -F/--file and -b/--boot set it too.
                foreach (var t in args)
                {
                    var bad = t.StartsWith("--", StringComparison.Ordinal)
                        ? ("--file".StartsWith(t.Split('=', 2)[0], StringComparison.Ordinal) || "--boot".StartsWith(t.Split('=', 2)[0], StringComparison.Ordinal)) && t.Length > 2
                        : t.Length > 1 && t[0] == '-' ? t.Skip(1).Any(c => c is 'F' or 'b') : true;
                    if (bad) return GuardResult.NotListed(Not("it can change the hostname"));
                }
                return null;

            case "ip":
                // Showing state only: unknown options (-b batch files, -n namespaces) and any mutating word are refused.
                foreach (var t in args)
                {
                    if (t.StartsWith('-') && !IpOptions.Contains(t)) return GuardResult.NotListed(Not($"option '{t}' can run batch files or change namespace"));
                    if (IpMutatingWords.Contains(t)) return GuardResult.NotListed(Not($"'{t}' changes network state"));
                }
                return args.Contains("netns") ? GuardResult.NotListed(Not("it works on network namespaces")) : null;

            case "ss":
                // -K kills sockets, -F reads a filter file, -D dumps raw data to a file.
                return CheckFlags(tokens, 1, "ss", "KFD", ["--kill", "--filter", "--diag"], abbreviations: true) is not null
                    ? GuardResult.NotListed(Not("it can kill sockets or read and write files"))
                    : null;

            case "sensors":
                // -s sets limits, -c reads a configuration file.
                return CheckFlags(tokens, 1, "sensors", "sc", ["--set", "--config-file"], abbreviations: true) is not null
                    ? GuardResult.NotListed(Not("it can set limits or read a configuration file"))
                    : null;

            case "kubectl" or "k3s":
                // "cluster-info dump" writes the cluster's state to files on the machine running kubectl.
                return args.Contains("dump") ? GuardResult.NotListed(Not("it writes files")) : null;

            default:
                return null;
        }
    }

    private string Summary()
    {
        var shown = allowlist.Take(80).ToList();
        return string.Join(", ", shown) + (allowlist.Count > shown.Count ? ", …" : "");
    }

    // ---- later stages: stdin-only filters ------------------------------------------

    private static GuardResult CheckFilter(List<string> tokens)
    {
        if (!Filters.TryGetValue(tokens[0], out var rule))
            return GuardResult.NotListed($"'{tokens[0]}' can't be used after a pipe. Allowed filters: {string.Join(", ", Filters.Keys)}.");

        var flagProblem = CheckFlags(tokens, 1, tokens[0], rule.DeniedShort, rule.DeniedLong, abbreviations: true);
        if (flagProblem is not null) return GuardResult.Deny($"{flagProblem.Error} Filters may only read the previous command's output.");

        // grep and tr take a pattern or set as their first operand, which may legitimately start with '.', '/' or '~'
        // ('.service', '/dev/sda'). But when a flag (-e, --regexp) supplies grep's pattern, the operand is a file.
        var patternFromFlag = tokens[0] == "grep" && tokens.Skip(1).Any(t =>
            t.StartsWith("--reg", StringComparison.Ordinal) || (t.Length > 1 && t[0] == '-' && t[1] != '-' && t.Skip(1).TakeWhile(char.IsLetter).Contains('e')));
        var operandIsPattern = rule.OperandIsPattern && !patternFromFlag;

        // With the pattern given by a flag, grep's remaining operands are files: it takes none.
        var maxOperands = patternFromFlag ? 0 : rule.MaxOperands;

        var operands = 0;
        for (var i = 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.StartsWith("--", StringComparison.Ordinal)) continue; // flags: checked above
            if (t.Length > 1 && t[0] == '-')
            {
                // "-f 1,3", "-nrk 2,2": a bare flag cluster ending in a value flag is followed by its value, which is not an operand.
                if (t.Skip(1).All(char.IsLetter) && rule.ValueFlags.Contains(t[^1])) i++;
                continue;
            }
            if (t.Length > 0 && t.TrimStart('+', '-').Length > 0 && t.TrimStart('+', '-').All(char.IsDigit)) continue; // a count: head -n 5, tail -n +2

            if (!operandIsPattern && (t.StartsWith('/') || t.StartsWith('~') || t.StartsWith('.')))
                return GuardResult.Deny($"'{t}' looks like a file path; filters may only read the previous command's output.");
            operands++;
            if (operands > maxOperands || t.Length > rule.MaxOperandLength)
                return GuardResult.Deny($"'{tokens[0]}' takes no file arguments here; it may only filter the previous command's output.");
        }
        return GuardResult.Ok;
    }
}
