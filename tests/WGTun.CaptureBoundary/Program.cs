using WGTun.Core;
using System.Text.Json.Nodes;
static void Require(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
string key=Convert.ToBase64String(new byte[32]);
string wg=$"[Interface]\nPrivateKey = {key}\nAddress = 10.0.0.2/32\n[Peer]\nPublicKey = {key}\nEndpoint = 192.0.2.1:51820\nAllowedIPs = 0.0.0.0/0, ::/0\n";
var egress=new ZapretEgress("fixture",7,System.Net.IPAddress.Parse("192.0.2.10"));
Directory.CreateDirectory("artifacts/config-checks");
foreach(var profile in Enum.GetValues<ProfileKind>())foreach(var scope in Enum.GetValues<TrafficScope>()){
 var settings=new UserSettings{Profile=profile,Scope=scope,RuleSchemaVersion=2,
  Rules=[new(){Type=RuleType.FolderPath,Value=Path.GetTempPath()},
   new(){Type=RuleType.ProcessPath,Value=Environment.ProcessPath!,Action=RuleAction.Direct,Protocol="TCP"},
   new(){Type=RuleType.IPv4Address,Value="192.0.2.8",Action=RuleAction.Direct},
   new(){Type=RuleType.IPv6Range,Value="2001:db8::/32",Protocol="UDP"}]};
 CaptureBoundary.EnsureSupported(settings);
 var config=JsonNode.Parse(AppBackend.BuildSelectiveTunConfiguration(wg,settings,12080,egress))!;
 Require(config["inbounds"]!.AsArray().Count==0,$"no OS TUN or listener / {profile} / {scope}");
 Require(!config.ToJsonString().Contains("auto_route")&&!config.ToJsonString().Contains("strict_route"),"no default routes or global DNS filtering");
 var rules=config["route"]!["rules"]!.AsArray();
 Require(!rules.Any(r=>r?["action"]?.GetValue<string>()=="hijack-dns"),"system DNS remains native in every scope");
 Require(rules.Any(r=>r?["port"]?.ToJsonString()=="53"&&r?["outbound"]?.GetValue<string>()=="direct"),"port 53 stays direct");
 Require(rules.Any(r=>r?["process_path"]!=null&&r?["network"]?.GetValue<string>()=="tcp"&&r?["outbound"]?.GetValue<string>()=="direct"),"exact exe/protocol direct exemption supported");
 Require(rules[0]!["process_name"]!.ToJsonString().Contains("WGTun.Tun.exe"),"embedded engine cannot capture itself");
 File.WriteAllText($"artifacts/config-checks/packet-{profile}-{scope}.json",config.ToJsonString());
}
var bad=new UserSettings{Rules=[new(){Type=RuleType.IPv4Address,Value="not-an-ip"}]};
try{CaptureBoundary.EnsureSupported(bad);throw new Exception("invalid policy accepted");}catch(ArgumentException){Console.WriteLine("PASS invalid rule rejected before capture");}
