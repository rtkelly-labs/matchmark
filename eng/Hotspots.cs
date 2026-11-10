// Where a speedscope profile (dotnet-trace --format Speedscope) spends its time, as Markdown:
//   dotnet run eng/Hotspots.cs -- profile.speedscope.json [rows]
// Busy time is every thread's time not parked in native code (a leaf of UNMANAGED_CODE_TIME): what the work cost,
// across threads, so a parallel compile and the thread waiting on it aren't both counted.
#:property PublishAot=false
using System.Text.Json;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: dotnet run eng/Hotspots.cs -- <profile.speedscope.json> [rows]");
    return 2;
}

var rows = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 30;
using var stream = File.OpenRead(args[0]);
using var doc = JsonDocument.Parse(stream);
var frames = doc.RootElement.GetProperty("shared").GetProperty("frames").EnumerateArray().Select(f => f.GetProperty("name").GetString() ?? "?").ToArray();
var idle = Array.IndexOf(frames, "UNMANAGED_CODE_TIME");
var cpu = Array.IndexOf(frames, "CPU_TIME");

var self = new double[frames.Length];
var inclusive = new double[frames.Length];
double busy = 0, parked = 0;
var threads = new List<(string Name, double Busy)>();
var seen = new int[frames.Length];
var stamp = 0;

void Charge(IReadOnlyList<int> stack, double dt, ref double threadBusy)
{
    if (stack.Count == 0 || dt <= 0)
    {
        return;
    }

    if (stack[^1] == idle)
    {
        parked += dt;
        return;
    }

    busy += dt;
    threadBusy += dt;
    // A thread-time sample ends in a CPU_TIME pseudo-frame: the self time is the method above it.
    self[stack.Count > 1 && stack[^1] == cpu ? stack[^2] : stack[^1]] += dt;
    stamp++;
    foreach (var f in stack)
    {
        if (seen[f] != stamp)
        {
            seen[f] = stamp;
            inclusive[f] += dt;
        }
    }
}

foreach (var profile in doc.RootElement.GetProperty("profiles").EnumerateArray())
{
    double threadBusy = 0;
    var name = profile.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
    if (profile.GetProperty("type").GetString() == "evented")
    {
        var stack = new List<int>();
        double last = profile.GetProperty("startValue").GetDouble();
        foreach (var e in profile.GetProperty("events").EnumerateArray())
        {
            var at = e.GetProperty("at").GetDouble();
            Charge(stack, at - last, ref threadBusy);
            last = at;
            var frame = e.GetProperty("frame").GetInt32();
            if (e.GetProperty("type").GetString() == "O")
            {
                stack.Add(frame);
            }
            else if (stack.LastIndexOf(frame) is var i and >= 0)
            {
                stack.RemoveRange(i, stack.Count - i);
            }
        }
    }
    else
    {
        var weights = profile.GetProperty("weights").EnumerateArray().Select(w => w.GetDouble()).ToArray();
        var k = 0;
        foreach (var sample in profile.GetProperty("samples").EnumerateArray())
        {
            Charge(sample.EnumerateArray().Select(f => f.GetInt32()).ToList(), weights[k++], ref threadBusy);
        }
    }

    threads.Add((name, threadBusy));
}

string Pct(double v) => busy == 0 ? "0" : (100 * v / busy).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
string Ms(double v) => TimeSpan.FromMilliseconds(v).ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
string Cell(string s) => (s.Length > 140 ? s[..140] + "…" : s).Replace("|", "\\|", StringComparison.Ordinal);

Console.WriteLine($"Busy {Ms(busy)} across {threads.Count(t => t.Busy > 0)} threads; parked in native code {Ms(parked)}.");
Console.WriteLine();
Console.WriteLine("#### Busiest threads");
Console.WriteLine("| Thread | Busy | % |");
Console.WriteLine("|---|---|---|");
foreach (var t in threads.OrderByDescending(t => t.Busy).Take(8))
{
    Console.WriteLine($"| {Cell(t.Name)} | {Ms(t.Busy)} | {Pct(t.Busy)} |");
}

Console.WriteLine();
Console.WriteLine("#### Self time by namespace (first three segments)");
Console.WriteLine("| Namespace | % |");
Console.WriteLine("|---|---|");
foreach (var g in Enumerable.Range(0, frames.Length).GroupBy(i => Namespace(frames[i])).Select(g => (g.Key, Time: g.Sum(i => self[i]))).OrderByDescending(g => g.Time).Take(15))
{
    Console.WriteLine($"| {Cell(g.Key)} | {Pct(g.Time)} |");
}

foreach (var (title, values) in new[] { ("Self time", self), ("Inclusive time", inclusive) })
{
    Console.WriteLine();
    Console.WriteLine($"#### {title}, top {rows}");
    Console.WriteLine("| % | Frame |");
    Console.WriteLine("|---|---|");
    foreach (var i in Enumerable.Range(0, frames.Length).OrderByDescending(i => values[i]).Take(rows))
    {
        Console.WriteLine($"| {Pct(values[i])} | `{Cell(frames[i])}` |");
    }
}

return 0;

// "Module!Namespace.Type.Method(...)" or "Namespace.Type.Method(...)" to its first three dotted segments.
static string Namespace(string frame)
{
    var name = frame[(frame.IndexOf('!', StringComparison.Ordinal) + 1)..];
    var paren = name.IndexOf('(', StringComparison.Ordinal);
    var parts = (paren < 0 ? name : name[..paren]).Split('.');
    return parts.Length <= 1 ? name : string.Join('.', parts.Take(Math.Min(3, parts.Length - 1)));
}
