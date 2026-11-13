using System.Diagnostics;
using System.Globalization;
using System.Text;
using Typlo.Core.Layouts;

var iterations = int.Parse(args[1], CultureInfo.InvariantCulture);
var cases = File.ReadAllLines(args[0]).Select(line =>
{
    var fields = line.Split('\t', 2);
    var pair = fields[0].Split('-');
    return (Source: KeyboardLayouts.Find(pair[0])!, Target: KeyboardLayouts.Find(pair[1])!, Text: fields[1]);
}).ToArray();

static string ConvertCase((KeyboardLayout Source, KeyboardLayout Target, string Text) item) =>
    LayoutConverter.Convert(item.Text, item.Source, item.Target, forceSource: true);

var checksum = 0xcbf29ce484222325UL;
foreach (var item in cases)
    foreach (var value in Encoding.UTF8.GetBytes(ConvertCase(item) + "\n"))
        checksum = unchecked((checksum ^ value) * 0x100000001b3UL);

for (var i = 0; i < 500; i++)
    foreach (var item in cases)
        GC.KeepAlive(ConvertCase(item));

var watch = Stopwatch.StartNew();
for (var i = 0; i < iterations; i++)
    foreach (var item in cases)
        GC.KeepAlive(ConvertCase(item));
watch.Stop();
Console.WriteLine(FormattableString.Invariant($"cases={cases.Length} iterations={iterations} elapsed_ms={watch.Elapsed.TotalMilliseconds:F3} checksum={checksum:x16}"));
