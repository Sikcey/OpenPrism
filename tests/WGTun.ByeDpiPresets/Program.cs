using WGTun.Core;

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

string[] expected = ["--split", "1", "--disorder", "3+s", "--mod-http=h,d", "--auto=torst", "--tlsrec", "1+s"];
var preset = ByeDpiStrategies.All.Single(p => p.Id == "byedpi-user-split-disorder");
Assert(preset.Engine == "byedpi", "Preset engine");
Assert(preset.Arguments.SequenceEqual(expected), "Exact user-provided arguments and order");
Assert(ByeDpiStrategies.Resolve(new DpiSettings { PresetId = preset.Id }).SequenceEqual(expected), "Fixed ID resolves to expected arguments");
Assert(ByeDpiStrategies.Resolve(new DpiSettings()).SequenceEqual(new[] { "--tlsrec", "1" }), "Default remains unchanged");
Assert(ByeDpiStrategies.All.Count(p => p.Id == preset.Id) == 1, "Preset ID is unique");
try { ByeDpiStrategies.Resolve(new DpiSettings { PresetId = preset.Id, CustomArguments = "--ip 0.0.0.0" }); throw new Exception("Custom arguments accepted"); }
catch (InvalidOperationException) { }
Console.WriteLine("ByeDPI preset checks passed.");
string custom = "--split 1 --disorder 3+s --mod-http=h,d --auto=torst --tlsrec 1+s";
Assert(ByeDpiStrategies.Resolve(new DpiSettings { CustomArguments = custom }).SequenceEqual(new[] { "--split=1", "--disorder=3+s", "--mod-http=h,d", "--auto=torst", "--tlsrec=1+s" }), "Custom SplitWire strategy");
Assert(DpiCustomArguments.Parse("zapret", "--filter-tcp=80,443 --dpi-desync=fake --dpi-desync-ttl=4 --new --filter-udp=443 --dpi-desync=fake").Length == 6, "Zapret strategy");
foreach (var bad in new[] { "--ip 0.0.0.0", "--port 1080", "--split '1", "--split 1;whoami", "--split 1 & calc", "--fake-file foo", "--split 1 --unknown 2", new string('x', 4097) })
{
    bool rejected = false; try { DpiCustomArguments.Parse("byedpi", bad); } catch (InvalidOperationException) { rejected = true; }
    Assert(rejected, "Rejected " + bad[..Math.Min(40, bad.Length)]);
}
foreach (var bad in new[] { "--wf-raw=true", "--filter-tcp=22", "--filter-udp=80", "--dpi-desync-fake-tls=C:/x", "--dpi-desync=fake --new --wf-tcp=80" })
{
    bool rejected = false; try { DpiCustomArguments.Parse("zapret", bad); } catch (InvalidOperationException) { rejected = true; }
    Assert(rejected, "Rejected " + bad);
}
