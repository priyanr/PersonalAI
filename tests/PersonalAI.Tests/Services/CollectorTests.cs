using System.Text.Json.Nodes;
using FluentAssertions;
using PersonalAI.Core.Services.Monitoring.Collectors;
using PersonalAI.Core.Services.Ssh;

namespace PersonalAI.Tests.Services;

public class SshCommandGuardTests
{
    private static readonly SshCommandGuard Guard = new(SshCommandGuard.DefaultAllowlist);

    [Theory]
    [InlineData("uptime")]
    [InlineData("df -P -B1")]
    [InlineData("free -m")]
    [InlineData("top -bn1")]
    [InlineData("cat /proc/meminfo")]
    [InlineData("cat /proc/stat /proc/loadavg")]
    [InlineData("cat /sys/class/thermal/thermal_zone0/temp")]
    [InlineData("cat /etc/os-release")]
    [InlineData("systemctl is-active kubelet")]
    [InlineData("systemctl list-units --type=service --state=running --no-legend")]
    [InlineData("docker ps -a --format '{{.Names}}|{{.State}}|{{.Status}}'")]
    [InlineData("docker stats --no-stream --format '{{json .}}'")]
    [InlineData("kubectl get pods -A -o json")]
    [InlineData("kubectl top nodes")]
    [InlineData("k3s kubectl get nodes -o wide")]
    [InlineData("ss -tln | grep LISTEN | head -n 5")]
    [InlineData("ps -eo pid,comm,%cpu --sort=-%cpu | head -n 10")]
    [InlineData("journalctl -u kubelet --since '10 min ago' | tail -n 50")]
    [InlineData("df -P | sort -k 5 | tail -n 3")]
    [InlineData("kubectl get pods -A | grep -v Running | wc -l")]
    [InlineData("df -h | tr -s ' ' | cut -d ' ' -f 5")]
    // quoted patterns that start with '.', '/' or '~' are patterns, not files
    [InlineData("systemctl list-units | grep '.service'")]
    [InlineData("df | grep '/dev/sda'")]
    [InlineData("df | tr -d '.'")]
    [InlineData("df | tr -s ' ' | cut -d ' ' -f 5")]
    // tail -n +2 is the standard way to skip a header
    [InlineData("df -P | tail -n +2")]
    [InlineData("ps -eo pid,comm | tail -n +2 | head -n 5")]
    [InlineData("df | head -5")]
    [InlineData("cat /etc/hostname")]
    [InlineData("cat /etc/os-release")]
    [InlineData("cat /proc/net/dev")]
    [InlineData("cat /sys/class/thermal/thermal_zone0/temp")]
    [InlineData("kubectl get pods -A -o wide --no-headers")]
    [InlineData("kubectl get pods -n kube-system -l app=coredns")]
    [InlineData("kubectl top nodes --no-headers")]
    [InlineData("docker ps --format '{{.Names}}' --no-trunc")]
    [InlineData("journalctl -u kubelet --since '10 min ago' --no-pager")]
    public void Allows_ReadOnlyCommandsAndStdinFilters(string command) =>
        Guard.Check(command).Should().Be(GuardResult.Ok, command);

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("rm -rf /", "not on the allowed")]
    [InlineData("sudo df", "not on the allowed")]
    [InlineData("bash -c 'df'", "not on the allowed")]
    [InlineData("python3 -c 'print(1)'", "not on the allowed")]
    [InlineData("awk '{print $1}' /etc/passwd", "not on the allowed")]
    [InlineData("df; rm -rf /tmp/x", "not allowed")]
    [InlineData("df && reboot", "not allowed")]
    [InlineData("df & ", "not allowed")]
    [InlineData("df || ls", "'||'")]
    [InlineData("df > /tmp/out", "not allowed")]
    [InlineData("df < /etc/passwd", "not allowed")]
    [InlineData("df $(whoami)", "not allowed")]
    [InlineData("df `id`", "not allowed")]
    [InlineData("df $HOME", "not allowed")]
    [InlineData("df \"$HOME\"", "double quotes")]
    [InlineData("df (x)", "not allowed")]
    [InlineData("df \\; ls", "not allowed")]
    [InlineData("df\nreboot", "Newlines")]
    [InlineData("df 'unterminated", "not closed")]
    [InlineData("df |", "empty")]
    [InlineData("| df", "empty")]
    [InlineData("FOO=bar df", "not on the allowed")]
    [InlineData("cat /etc/passwd", "may only read paths under /proc/ or /sys/")]
    [InlineData("cat /etc/shadow", "may only read paths under /proc/ or /sys/")]
    [InlineData("cat /proc/../etc/passwd", "no '..'")]
    [InlineData("cat /proc/stat /etc/shadow", "may only read paths under /proc/ or /sys/")]
    [InlineData("cat", "not on the allowed")]
    [InlineData("kubectl get secrets -A", "secrets")]
    [InlineData("kubectl get pods -o yaml --field-selector=metadata.name=secret-store", "secrets")]
    [InlineData("k3s kubectl get secret x", "secrets")]
    [InlineData("kubectl delete pod x", "not on the allowed")]
    [InlineData("kubectl exec x -- sh", "not on the allowed")]
    [InlineData("docker exec x sh", "not on the allowed")]
    [InlineData("docker run alpine", "not on the allowed")]
    [InlineData("docker inspect x", "not on the allowed")]
    [InlineData("systemctl restart kubelet", "not on the allowed")]
    [InlineData("df | tee /tmp/x", "can't be used after a pipe")]
    [InlineData("df | sh", "can't be used after a pipe")]
    [InlineData("df | awk '{print $1}'", "can't be used after a pipe")]
    [InlineData("df | grep -r root /etc", "not allowed")]
    [InlineData("df | grep -R root", "not allowed")]
    [InlineData("df | grep -rn root", "not allowed")]
    [InlineData("df | grep -f /etc/passwd", "not allowed")]
    [InlineData("df | grep root /etc/passwd", "file arguments")]
    [InlineData("df | head /etc/passwd", "file path")]
    [InlineData("df | tail -f /var/log/syslog", "not allowed")]
    [InlineData("df | sort -o /tmp/x", "not allowed")]
    [InlineData("df | sort --output=/tmp/x", "not allowed")]
    [InlineData("df | uniq in out", "file arguments")]
    [InlineData("df | cut -d , notes.txt", "file arguments")]
    [InlineData("df | wc --files0-from=/etc/x", "not allowed")]
    // extra arguments on a literal path entry
    [InlineData("cat /etc/hostname ~/.ssh/id_rsa", "as written")]
    [InlineData("cat /etc/os-release /etc/passwd", "as written")]
    // /proc symlinks, environment, globs that could expand to them
    [InlineData("cat /proc/self/root/etc/passwd", "may only read paths under")]
    [InlineData("cat /proc/1/root/home/u/.ssh/id_rsa", "may only read paths under")]
    [InlineData("cat /proc/self/cwd/secrets.txt", "may only read paths under")]
    [InlineData("cat /proc/1/environ", "may only read paths under")]
    [InlineData("cat /proc/self/fd/3", "may only read paths under")]
    [InlineData("cat /proc/*/root/etc/passwd", "may only read paths under")]
    [InlineData("cat /proc/self/ro?t/etc/passwd", "may only read paths under")]
    [InlineData("cat /proc/self/r[o]ot/etc/passwd", "may only read paths under")]
    [InlineData("cat /proc/kcore", "may only read paths under")]
    // abbreviated long options (GNU getopt accepts any unambiguous prefix)
    [InlineData("df | sort --out=/tmp/x", "not allowed")]
    [InlineData("df | sort --outp=/tmp/x", "not allowed")]
    [InlineData("df | sort --compress-program=sh", "not allowed")]
    [InlineData("df | sort --compress=sh", "not allowed")]
    [InlineData("df | sort --files0=x", "not allowed")]
    [InlineData("df | wc --files0=x", "not allowed")]
    [InlineData("df | grep --rec root", "not allowed")]
    [InlineData("df | grep --dereference-rec root", "not allowed")]
    [InlineData("df | tail --fol", "not allowed")]
    [InlineData("docker ps --format 'ssh-ed25519 AAAA' | sort --out=/home/u/.ssh/authorized_keys", "not allowed")]
    // flags on the first command that read files or reach elsewhere
    [InlineData("kubectl get pods -f /etc/passwd", "not allowed")]
    [InlineData("kubectl get pods --filename=/etc/passwd", "not allowed")]
    [InlineData("kubectl get pods --kubeconfig=/x/config", "not allowed")]
    [InlineData("kubectl get pods --token=abc", "not allowed")]
    [InlineData("kubectl get pods -s https://evil:6443", "not allowed")]
    [InlineData("k3s kubectl get pods --server=https://evil", "not allowed")]
    [InlineData("docker ps -H tcp://evil:2375", "not allowed")]
    [InlineData("docker ps --host=tcp://evil:2375", "not allowed")]
    [InlineData("journalctl --directory=/var/x", "not allowed")]
    [InlineData("journalctl --dir=/var/x", "not allowed")]
    [InlineData("journalctl -D /var/x", "not allowed")]
    [InlineData("journalctl --file=/root/x.journal", "not allowed")]
    // a pattern from -e is followed by a file operand
    [InlineData("df | grep -e root /etc/passwd", "file")]
    [InlineData("df | grep --regexp=root /etc/passwd", "file path")]
    public void Denies(string command, string reasonContains)
    {
        var result = Guard.Check(command);

        result.Allowed.Should().BeFalse(command);
        result.Error.Should().Contain(reasonContains, command);
    }

    [Fact]
    public void Denies_TooLong_AndTooManyStages()
    {
        Guard.Check("df " + new string('a', SshCommandGuard.MaxLength)).Error.Should().Contain("longer than");
        Guard.Check("df | head | head | head | head | head | head").Error.Should().Contain("At most 6");
    }

    [Fact]
    public void QuotedMetacharacters_AreLiteralData_NotSyntax()
    {
        // '|', ';', '>' and '$' inside single quotes are just text for grep to look for
        Guard.Check("df | grep 'a;b>c$d|e'").Should().Be(GuardResult.Ok);
    }

    [Fact]
    public void ErrorMessage_ListsWhatIsAllowed_SoAModelCanSelfCorrect()
    {
        var result = Guard.Check("rm -rf /");

        result.Error.Should().Contain("df").And.Contain("kubectl get").And.Contain("docker ps");
    }

    [Fact]
    public void ParseAllowlist_HandlesNewlinesCommasCommentsAndBlank()
    {
        var list = SshCommandGuard.ParseAllowlist("""
            # monitoring
            df
            free  -m , uptime

            kubectl   get
            """);

        list.Should().Equal("df", "free -m", "uptime", "kubectl get");
        SshCommandGuard.ParseAllowlist(null).Should().BeEquivalentTo(SshCommandGuard.DefaultAllowlist);
        SshCommandGuard.ParseAllowlist("  \n ").Should().BeEquivalentTo(SshCommandGuard.DefaultAllowlist);
    }

    [Fact]
    public void CustomAllowlist_IsTheOnlySourceOfFirstStageCommands()
    {
        var guard = new SshCommandGuard(["df", "docker ps"]);

        guard.Check("df -h").Allowed.Should().BeTrue();
        guard.Check("free").Allowed.Should().BeFalse();
        guard.Check("kubectl get pods").Allowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("ls /var/log")]
    [InlineData("rm -rf /tmp/x")]
    [InlineData("cat /etc/passwd")]
    [InlineData("df | awk")]
    [InlineData("cat /proc/self/root/etc/shadow")]
    public void NotOnTheList_IsRefused_ButCanBeApprovedAsWritten(string command)
    {
        var result = Guard.Check(command);

        result.Allowed.Should().BeFalse();
        result.CanBeApproved.Should().BeTrue();
    }

    [Theory]
    [InlineData("df; rm -rf /")]
    [InlineData("df > /tmp/x")]
    [InlineData("df $(whoami)")]
    [InlineData("df\nls")]
    [InlineData("kubectl get secrets")]
    [InlineData("kubectl get pods --kubeconfig=/x")]
    [InlineData("df | grep -r x")]
    [InlineData("ls /var/log | grep -r x")]
    [InlineData("")]
    public void HardRefusals_CanNeverBeApproved(string command)
    {
        var result = Guard.Check(command);

        result.Allowed.Should().BeFalse();
        result.CanBeApproved.Should().BeFalse();
    }

    // Listed commands in their read-only forms stay allowed; the same commands with arguments that change state, switch
    // namespace or read a file are not on the list (the user can approve them as written).
    [Theory]
    [InlineData("date")]
    [InlineData("date +%s")]
    [InlineData("date -u +%FT%T")]
    [InlineData("date -d yesterday +%F")]
    [InlineData("hostname")]
    [InlineData("hostname -I")]
    [InlineData("hostname -f")]
    [InlineData("ip -br addr")]
    [InlineData("ip -s link")]
    [InlineData("ip -br addr show")]
    [InlineData("ip -s link show eth0")]
    [InlineData("ss -tulpn")]
    [InlineData("ss -s")]
    [InlineData("sensors")]
    [InlineData("journalctl -u nginx -n 50 --no-pager")]
    [InlineData("systemctl status nginx")]
    [InlineData("kubectl cluster-info")]
    public void ReadOnlyForms_OfListedCommands_StayAllowed(string command) => Guard.Check(command).Allowed.Should().BeTrue();

    [Theory]
    [InlineData("date -s 2020-01-01")]
    [InlineData("date 010112002020")]
    [InlineData("date --set=2020-01-01")]
    [InlineData("date -f dates.txt")]
    [InlineData("hostname newname")]
    [InlineData("hostname -F /etc/hostname")]
    [InlineData("ip -br link set eth0 down")]
    [InlineData("ip -s netns exec x sh")]
    [InlineData("ip -b batchfile")]
    [InlineData("ip -n blue addr")]
    [InlineData("ip -br addr add 10.0.0.9/24 dev eth0")]
    [InlineData("ss -K")]
    [InlineData("ss --kill")]
    [InlineData("ss -F filter.txt")]
    [InlineData("sensors -s")]
    [InlineData("sensors -c /etc/sensors3.conf")]
    [InlineData("kubectl cluster-info dump")]
    public void MutatingOrFileReadingForms_OfListedCommands_AreNotListed_ButApprovable(string command)
    {
        var result = Guard.Check(command);

        result.Allowed.Should().BeFalse();
        result.CanBeApproved.Should().BeTrue();
    }

    [Theory]
    [InlineData("journalctl --vacuum-size=1K")]
    [InlineData("journalctl --vacuum=1K")]
    [InlineData("journalctl --rotate")]
    [InlineData("journalctl --flush")]
    [InlineData("journalctl --cursor-file=/tmp/x")]
    [InlineData("journalctl -M other")]
    [InlineData("systemctl status -H root@other")]
    [InlineData("systemctl status --host=other")]
    public void JournalAndSystemctl_FlagsThatChangeStateOrReachOtherMachines_AreHardRefusals(string command)
    {
        var result = Guard.Check(command);

        result.Allowed.Should().BeFalse();
        result.CanBeApproved.Should().BeFalse();
    }

    [Theory]
    [InlineData("ps | grep -e foo")]
    [InlineData("ps | grep -ie foo")]
    [InlineData("df | cut -d ' ' -f 1,3")]
    [InlineData("df | cut -f 1-3")]
    [InlineData("df | sort -k 2,2")]
    [InlineData("df | sort -nrk 2,2")]
    [InlineData("df -P | tr -s ' ' | cut -d ' ' -f 1,5")]
    [InlineData("cat /proc/stat | head -n 3")]
    [InlineData("ps | uniq -f 1")]
    public void FiltersWithValueFlags_AreAllowed_TheValueIsNotAFileOperand(string command) => Guard.Check(command).Allowed.Should().BeTrue();

    [Theory]
    [InlineData("ps | grep --regexp=. Documents/secrets.txt")]
    [InlineData("ps | grep -eX bin/token")]
    [InlineData("ps | grep -e foo bar.txt")]
    [InlineData("ps | cut -d ' ' -f 1 notes.txt")]
    public void AFilterNeverReadsAFile_EvenWhenGrepsPatternComesFromAFlag(string command)
    {
        var result = Guard.Check(command);

        result.Allowed.Should().BeFalse();
        result.CanBeApproved.Should().BeFalse();
    }

    [Fact]
    public void AnEmptyAllowlist_AllowsNothing() =>
        new SshCommandGuard([]).Check("df").Allowed.Should().BeFalse();
}

public class CollectorParserTests
{
    private static ParserSpec Table(params (string Name, string? Type)[] columns) =>
        new("table", 1, [.. columns.Select(c => new ColumnSpec(c.Name, c.Type))], true, null, null, null);

    private const string DfOutput = """
        Filesystem     1B-blocks        Used   Available Capacity Mounted on
        /dev/sda1    100000000000 40000000000 60000000000      40% /
        /dev/sdb1    500000000000 450000000000 50000000000     90% /mnt/data disk
        tmpfs                100           0         100       0% /dev/shm
        """;

    [Fact]
    public void Table_TypedColumns_PercentAndTrailingColumnWithSpaces()
    {
        var spec = Table(("fs", null), ("size", "number"), ("used", "number"), ("avail", "number"), ("pct", "number"), ("mount", null));

        var result = CollectorParser.Parse(spec, DfOutput);

        result.Diagnostic.Should().BeNull();
        result.Rows.Should().HaveCount(3);
        result.Rows[1]["pct"]!.GetValue<double>().Should().Be(90);
        result.Rows[1]["mount"]!.GetValue<string>().Should().Be("/mnt/data disk");
        result.Rows[0]["size"]!.GetValue<double>().Should().Be(100000000000);
    }

    [Fact]
    public void Table_ShortLinesAreSkipped_AndAnAllShortOutputExplainsItself()
    {
        var spec = Table(("a", null), ("b", null), ("c", null));

        var result = CollectorParser.Parse(spec with { SkipLines = 0 }, "only two\nalso two");

        result.Rows.Should().BeEmpty();
        result.Diagnostic.Should().Contain("No line had 3 columns").And.Contain("only two");
    }

    [Fact]
    public void Table_ValuesThatDontParseAreLeftOut_NotStoredWrong()
    {
        var spec = Table(("name", null), ("cpu", "number")) with { SkipLines = 0 };

        var result = CollectorParser.Parse(spec, "web 12.5\ndb n/a");

        result.Rows[0]["cpu"]!.GetValue<double>().Should().Be(12.5);
        result.Rows[1].ContainsKey("cpu").Should().BeFalse();
        result.Rows[1]["name"]!.GetValue<string>().Should().Be("db");
    }

    private const string PodsJson = """
        {"items":[
          {"metadata":{"name":"coredns-1","namespace":"kube-system"},"status":{"phase":"Running","containerStatuses":[{"restartCount":2,"ready":true}]}},
          {"metadata":{"name":"app-7","namespace":"default"},"status":{"phase":"Pending","containerStatuses":[{"restartCount":0,"ready":false}]}}
        ]}
        """;

    [Fact]
    public void Json_PathToArray_WithFieldPaths_IncludingArrayIndexes()
    {
        var spec = new ParserSpec("json", null, null, null, "items", new()
        {
            ["name"] = new("metadata.name", null),
            ["namespace"] = new("metadata.namespace", null),
            ["phase"] = new("status.phase", null),
            ["restarts"] = new("status.containerStatuses.0.restartCount", "number"),
            ["ready"] = new("status.containerStatuses.0.ready", "bool")
        }, null);

        var result = CollectorParser.Parse(spec, PodsJson);

        result.Diagnostic.Should().BeNull();
        result.Rows.Should().HaveCount(2);
        result.Rows[0]["name"]!.GetValue<string>().Should().Be("coredns-1");
        result.Rows[0]["restarts"]!.GetValue<double>().Should().Be(2);
        result.Rows[1]["ready"]!.GetValue<bool>().Should().BeFalse();
        result.Rows[1]["phase"]!.GetValue<string>().Should().Be("Pending");
    }

    [Fact]
    public void Json_RootObjectWithoutFields_KeepsScalarsOnly()
    {
        var result = CollectorParser.Parse(new ParserSpec("json", null, null, null, null, null, null),
            """{"cpu": 12.5, "name":"x","nested":{"a":1},"list":[1,2]}""");

        var row = result.Rows.Single();
        row.Select(p => p.Key).Should().BeEquivalentTo("cpu", "name");
    }

    [Fact]
    public void Json_Problems_ComeBackAsDiagnosticsTheModelCanActOn()
    {
        var spec = new ParserSpec("json", null, null, null, "itemz", null, null);

        CollectorParser.Parse(spec, PodsJson).Diagnostic.Should().Contain("'itemz' was not found").And.Contain("items");
        CollectorParser.Parse(spec, "Error from server (Forbidden)").Diagnostic.Should().Contain("not valid JSON").And.Contain("Forbidden");
        CollectorParser.Parse(spec with { Path = null }, "").Diagnostic.Should().Contain("not valid JSON");
    }

    [Fact]
    public void KeyValue_NumberWithUnitSuffix_IsLeftOut_AndPlainValuesParse()
    {
        var spec = new ParserSpec("kv", null, [new("load", "number"), new("ok", "bool")], null, null, null, null);

        var row = CollectorParser.Parse(spec, "load=1.5\nok=yes\nhost-name = box1\nbad line").Rows.Single();

        row["load"]!.GetValue<double>().Should().Be(1.5);
        row["ok"]!.GetValue<bool>().Should().BeTrue();
        row["host_name"]!.GetValue<string>().Should().Be("box1");
    }

    [Fact]
    public void Regex_NamedGroupsPerLine_WithTypes()
    {
        var spec = new ParserSpec("regex", null, [new("cpu", "number")], null, null, null, @"^(?<name>\S+)\s+(?<cpu>[\d.]+)%");

        var result = CollectorParser.Parse(spec, "web 12.5%\nheader line\ndb 80%");

        result.Rows.Should().HaveCount(2);
        result.Rows[1]["name"]!.GetValue<string>().Should().Be("db");
        result.Rows[1]["cpu"]!.GetValue<double>().Should().Be(80);
    }

    [Fact]
    public void EmptyOutput_ExplainsItself()
    {
        CollectorParser.Parse(Table(("a", null)), "").Diagnostic.Should().Contain("No rows parsed");
    }

    [Fact]
    public void RowCount_IsCapped_WithAWarning()
    {
        var spec = Table(("a", null)) with { SkipLines = 0 };
        var output = string.Join("\n", Enumerable.Range(0, CollectorSpecs.MaxRows + 50).Select(i => $"row{i}"));

        var result = CollectorParser.Parse(spec, output);

        result.Rows.Should().HaveCount(CollectorSpecs.MaxRows);
        result.Diagnostic.Should().Contain("only the first");
    }

    [Theory]
    [InlineData("""{"type":"table"}""", "columns is required")]
    [InlineData("""{"type":"table","columns":[{"name":"a"},{"name":"a"}]}""", "duplicate")]
    [InlineData("""{"type":"table","columns":[{"name":"1bad"}]}""", "not a valid field name")]
    [InlineData("""{"type":"table","columns":[{"name":"a","type":"int"}]}""", "string, number or bool")]
    [InlineData("""{"type":"yaml"}""", "must be one of")]
    [InlineData("""{"type":"regex","pattern":"(unclosed"}""", "not a valid regular expression")]
    [InlineData("""{"type":"regex","pattern":"\\d+"}""", "named group")]
    [InlineData("""{"type":"json","fields":{"a":{}}}""", "path is required")]
    [InlineData("""{"type":"builtin"}""", "parser.type must be one of")]
    public void Validate_RejectsBadSpecs_WithAnActionableMessage(string json, string expected)
    {
        var (spec, error) = CollectorSpecs.Parse<ParserSpec>(json, "parser");
        error.Should().BeNull();

        CollectorSpecs.Validate(spec!).Should().Contain(e => e.Contains(expected));
    }

    [Fact]
    public void Parse_UnknownProperty_IsRejectedByName()
    {
        var (spec, error) = CollectorSpecs.Parse<ParserSpec>("""{"type":"table","skipLine":1}""", "parser");

        spec.Should().BeNull();
        error.Should().Contain("skipLine");
    }

    [Fact]
    public void Validate_AcceptsGoodSpecs()
    {
        CollectorSpecs.Validate(Table(("a", "number"))).Should().BeEmpty();
    }

    [Theory]
    [InlineData("pods", null, true)]
    [InlineData("k8s-pods", "snapshot", true)]
    [InlineData("Pods", null, false)]
    [InlineData("1pods", null, false)]
    [InlineData("", null, false)]
    [InlineData("pods", "weird", false)]
    public void DatasetSpec_NameAndKindRules(string name, string? kind, bool valid) =>
        (CollectorSpecs.Validate(new DatasetSpec(name, kind, null)).Count == 0).Should().Be(valid);
}
