using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanguageDataProbe;
using Microsoft.Data.Sqlite;

if (args.Length < 2) { Console.Error.WriteLine("Usage: LanguageDataProbe <lookup|verify|bench> words.sqlite3 [words...]"); return 2; }
var path = Path.GetFullPath(args[1]);
if (args[0] == "verify")
{
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA cache_size=-128; PRAGMA mmap_size=0;"; command.ExecuteNonQuery();
    command.CommandText = "PRAGMA integrity_check";
    if ((string?)command.ExecuteScalar() != "ok") throw new InvalidDataException("Integrity check failed");
    command.CommandText = "SELECT word,weights FROM words ORDER BY word COLLATE BINARY";
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    long count = 0;
    using (var rows = command.ExecuteReader())
        while (rows.Read())
        {
            var word = rows.GetString(0); var weights = (byte[])rows[1];
            if (word != DiskLexicon.Normalize(word) || weights.Length != 8 || weights.All(x => x == 0)) throw new InvalidDataException("Invalid word/vector");
            hash.AppendData(Encoding.UTF8.GetBytes(word)); hash.AppendData([0]); hash.AppendData(weights); count++;
        }
    var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
    using var manifest = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path, ".manifest.json")));
    if (actual != manifest.RootElement.GetProperty("content_sha256").GetString()) throw new InvalidDataException("Content checksum mismatch");
    using var lexicon = new DiskLexicon(path);
    foreach (var pair in new[] { ("en", "hello"), ("ru", "привет"), ("uk", "дякую"), ("fr", "bonjour"), ("es", "gracias"), ("it", "buongiorno"), ("de", "danke"), ("pt", "obrigado") })
    {
        var index = lexicon.Languages.ToList().IndexOf(pair.Item1);
        if (lexicon.Lookup(pair.Item2)?[index] is not > 0) throw new InvalidDataException("Missing sample: " + pair);
    }
    foreach (var word in new[] { "chat", "так" })
        if (lexicon.Lookup(word)?.Count(x => x > 0) is not > 1) throw new InvalidDataException("Lost ambiguity: " + word);
    if (lexicon.Lookup("xqzvnotaword987") is not null) throw new InvalidDataException("False positive");
    if (!(lexicon.Lookup("CAFÉ") ?? []).SequenceEqual(lexicon.Lookup("cafe\u0301") ?? [])) throw new InvalidDataException("Normalization mismatch");
    Console.WriteLine($"PASS {count} rows, eight languages, .NET/Python normalization, ambiguous/unknown words, SHA-256={actual}");
    return 0;
}
if (args[0] == "bench")
{
    // Build the workload before the memory baseline. SQLite/native library and filesystem cache
    // are warm, as in Typlo (which already uses SQLite); this is not a cold-disk benchmark.
    var samples = new List<string>();
    using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
    {
        db.Open(); using var select = db.CreateCommand();
        select.CommandText = "PRAGMA cache_size=-128; PRAGMA mmap_size=0;"; select.ExecuteNonQuery();
        select.CommandText = "SELECT word FROM words ORDER BY word COLLATE BINARY";
        using var rows = select.ExecuteReader(); var row = 0;
        while (rows.Read()) if (row++ % 251 == 0) samples.Add(rows.GetString(0));
    }
    samples.AddRange(Enumerable.Range(0, 256).Select(i => "typlo-unknown-" + i));
    var durations = new double[100_000];
    var random = new Random(42);
    var requests = Enumerable.Range(0, durations.Length).Select(_ => random.Next(samples.Count)).ToArray();
    using var process = Process.GetCurrentProcess();
    long managedBaseline = GC.GetTotalMemory(true); process.Refresh();
    var privateBaseline = process.PrivateMemorySize64;
    using var lexicon = new DiskLexicon(path);
    if (lexicon.IsOpen) throw new InvalidOperationException("Reader must open lazily");
    var first = Stopwatch.StartNew(); lexicon.Lookup("hello"); first.Stop();
    process.Refresh(); var firstPrivateDelta = process.PrivateMemorySize64 - privateBaseline;
    var hitCount = 0;
    for (var i = 0; i < requests.Length; i++)
    {
        var stamp = Stopwatch.GetTimestamp();
        if (lexicon.Lookup(samples[requests[i]]) is not null) hitCount++;
        durations[i] = Stopwatch.GetElapsedTime(stamp).TotalMicroseconds;
    }
    var retained = GC.GetTotalMemory(true) - managedBaseline; process.Refresh();
    var privateDelta = process.PrivateMemorySize64 - privateBaseline;
    GC.KeepAlive(samples); GC.KeepAlive(requests); GC.KeepAlive(durations); GC.KeepAlive(lexicon);
    Array.Sort(durations);
    Console.WriteLine($"file_bytes={new FileInfo(path).Length}; queries={requests.Length}; distinct_keys={samples.Count}; hits={hitCount}");
    Console.WriteLine($"first_open_lookup_ms={first.Elapsed.TotalMilliseconds:F3}; median_us={durations[durations.Length/2]:F3}; p95_us={durations[(int)(durations.Length*0.95)]:F3}; p99_us={durations[(int)(durations.Length*0.99)]:F3}");
    Console.WriteLine($"first_open_private_delta_bytes={firstPrivateDelta}; retained_managed_delta_bytes={retained}; stress_private_commit_delta_bytes={privateDelta}; sqlite_page_cache_target_kib=128; mmap=0; pooling=false");
    return 0;
}
if (args[0] != "lookup") return 2;
using (var lexicon = new DiskLexicon(path))
    foreach (var token in args.Skip(2))
    {
        var weights = lexicon.Lookup(token);
        Console.WriteLine(token + ": " + (weights is null ? "unknown" : string.Join(", ", lexicon.Languages.Select((lang,i) => (lang,weight:weights[i])).Where(x => x.weight > 0).Select(x => $"{x.lang}={x.weight}"))));
    }
return 0;
