// The large public specs' copies, against upstream.tsv. Run from anywhere:
//   dotnet run samples/large/Refresh.cs -- --verify   each copy's SHA-256 is the one pinned (no network)
//   dotnet run samples/large/Refresh.cs -- --check    whether upstream has changed a spec since its pinned commit
//   dotnet run samples/large/Refresh.cs               move every pin to upstream's latest commit, and copy the specs
// --check prints a Markdown report and exits 3 when upstream has moved, so CI can raise it without failing on it.
// GITHUB_TOKEN or GH_TOKEN, when set, raises GitHub's API rate limit.
#:property PublishAot=false
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

var mode = args.FirstOrDefault() ?? "--refresh";
var here = Here();
var manifest = Path.Combine(here, "upstream.tsv");
var lines = File.ReadAllLines(manifest);
var sources = lines.Where(l => l.Length > 0 && !l.StartsWith('#')).Select(Source.Parse).ToList();

using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("matchmark-large-specs", "1"));
http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
if ((Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GH_TOKEN")) is { Length: > 0 } token)
{
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}

switch (mode)
{
    case "--verify":
        var bad = sources.Where(s => Sha256(s.File(here)) != s.Sha256).ToList();
        foreach (var s in sources)
        {
            Console.WriteLine($"{s.Name}: {(bad.Contains(s) ? "DIFFERS from" : "matches")} {s.Repository}@{s.Commit[..7]}");
        }

        return bad.Count == 0 ? 0 : 1;

    case "--check":
        var moved = 0;
        Console.WriteLine("| Spec | Pinned | Upstream's latest | |");
        Console.WriteLine("|---|---|---|---|");
        foreach (var s in sources)
        {
            var (latest, date) = await Latest(s);
            var same = latest == s.Commit;
            moved += same ? 0 : 1;
            var compare = same ? "current" : $"[changed](https://github.com/{s.Repository}/compare/{s.Commit}...{latest})";
            Console.WriteLine($"| {s.Name} | `{s.Commit[..7]}` | `{latest[..7]}` ({date:yyyy-MM-dd}) | {compare} |");
        }

        return moved == 0 ? 0 : 3;

    case "--refresh":
        var updated = new List<Source>();
        foreach (var s in sources)
        {
            var (latest, _) = await Latest(s);
            var bytes = await http.GetByteArrayAsync(new Uri($"https://raw.githubusercontent.com/{s.Repository}/{latest}/{s.Path}"));
            await File.WriteAllBytesAsync(s.File(here), bytes);
            var next = s with { Commit = latest, Sha256 = Sha256(s.File(here)) };
            Console.WriteLine($"{s.Name}: {s.Commit[..7]} -> {latest[..7]}");
            updated.Add(next);
        }

        File.WriteAllLines(manifest, lines.Where(l => l.StartsWith('#')).Concat(updated.Select(s => s.ToString())));
        return 0;

    default:
        Console.Error.WriteLine("usage: dotnet run samples/large/Refresh.cs [-- --verify | --check]");
        return 2;
}

// The last commit on the branch that touched the spec, so unrelated upstream commits don't count as a change.
async Task<(string Sha, DateTimeOffset Date)> Latest(Source s)
{
    var url = new Uri($"https://api.github.com/repos/{s.Repository}/commits?sha={s.Branch}&path={s.Path}&per_page=1");
    using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
    var commit = doc.RootElement[0];
    return (commit.GetProperty("sha").GetString()!, commit.GetProperty("commit").GetProperty("committer").GetProperty("date").GetDateTimeOffset());
}

static string Sha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
}

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

internal sealed record Source(string Name, string Repository, string Branch, string Path, string Commit, string Sha256)
{
    public static Source Parse(string line)
    {
        var f = line.Split('\t');
        return new Source(f[0], f[1], f[2], f[3], f[4], f[5]);
    }

    public string File(string directory) => System.IO.Path.Combine(directory, Name + ".json");

    public override string ToString() => string.Join('\t', Name, Repository, Branch, Path, Commit, Sha256);
}
