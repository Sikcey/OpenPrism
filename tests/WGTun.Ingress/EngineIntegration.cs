using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using WGTun.Core;

static class EngineIntegration
{
    static async Task<byte[]> Read(Stream s, int n, CancellationToken t) { var b = new byte[n]; await s.ReadExactlyAsync(b, t); return b; }
    static TcpListener Listen() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); return l; }
    static int Port(TcpListener l) => ((IPEndPoint)l.LocalEndpoint).Port;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static async Task Greet(Stream s, CancellationToken t) { await s.WriteAsync(new byte[] {5,1,0}, t); Check((await Read(s,2,t)).SequenceEqual(new byte[]{5,0}), "SOCKS greeting"); }
    public static async Task RunAsync()
    {
        string exe = Environment.GetEnvironmentVariable("WGTUN_TEST_SINGBOX") ?? Path.GetFullPath("dependencies/sing-box/sing-box-1.14.4-windows-amd64/sing-box.exe");
        Check(File.Exists(exe), "Set WGTUN_TEST_SINGBOX to the pinned sing-box binary");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var t = timeout.Token;
        using var engine = Listen(); using var doh = Listen(); using var target = Listen();
        using var reservation = Listen(); int inbound = Port(reservation); reservation.Stop();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
        using var cert = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.DefaultKeySet);
        string work = Path.GetFullPath("artifacts/ingress-integration"); Directory.CreateDirectory(work);
        string pem = Path.Combine(work,"fixture-cert.pem"); await File.WriteAllTextAsync(pem,cert.ExportCertificatePem(),t);
        var config = JsonNode.Parse(AppBackend.BuildProbeProxyConfiguration("",new UserSettings { Profile=ProfileKind.Dpi,Scope=TrafficScope.All },inbound,null,Port(engine)))!;
        var dns = config["dns"]!["servers"]![0]!;
        dns["server"]="127.0.0.1"; dns["server_port"]=Port(doh);
        dns["tls"] = new JsonObject { ["enabled"]=true,["server_name"]="localhost",["certificate_path"]=pem };
        config["dns"]!["servers"] = new JsonArray(dns.DeepClone());
        config["route"]!["default_domain_resolver"]="dns-upstream";
        string path=Path.Combine(work,"config.json"); await File.WriteAllTextAsync(path,config.ToJsonString(),t);
        var destinations=new ConcurrentBag<int>(); var errors=new ConcurrentBag<Exception>(); var handlers=new ConcurrentBag<Task>();
        int associations=0, closed=0, dnsQueries=0;
        var engineTask=Task.Run(async () => {
            try { while(!t.IsCancellationRequested) { var c=await engine.AcceptTcpClientAsync(t); handlers.Add(Task.Run(async()=> {
                try { using(c) { var s=c.GetStream(); var g=await Read(s,2,t); await Read(s,g[1],t); await s.WriteAsync(new byte[]{5,0},t);
                    var h=await Read(s,4,t); string host=h[3] switch {1=>new IPAddress(await Read(s,4,t)).ToString(),4=>new IPAddress(await Read(s,16,t)).ToString(),3=>Encoding.ASCII.GetString(await Read(s,(await Read(s,1,t))[0],t)),_=>throw new Exception("address")};
                    var p=await Read(s,2,t); int port=p[0]*256+p[1];
                    if(h[1]==3) { Interlocked.Increment(ref associations); int up=((IPEndPoint)udp.Client.LocalEndPoint!).Port; await s.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,(byte)(up>>8),(byte)up},t); while(await s.ReadAsync(new byte[1],t)>0) { } Interlocked.Increment(ref closed); return; }
                    Check(h[1]==1 && host=="127.0.0.1" && (port==Port(doh)||port==Port(target)),"Engine attempted unapproved/poisoned destination "+host+":"+port);
                    destinations.Add(port); using var remote=new TcpClient(); await remote.ConnectAsync(IPAddress.Loopback,port,t); await s.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,0,0},t);
                    using var link=CancellationTokenSource.CreateLinkedTokenSource(t); var a=s.CopyToAsync(remote.GetStream(),link.Token); var b=remote.GetStream().CopyToAsync(s,link.Token); await Task.WhenAny(a,b); link.Cancel(); try { await Task.WhenAll(a,b); } catch(OperationCanceledException) { }
                }} catch(Exception e) when(e is OperationCanceledException or IOException or SocketException) { } catch(Exception e) { errors.Add(e); }
            },t)); }} catch(OperationCanceledException) { }
        },t);
        var dohTask=Task.Run(async()=> { try { while(!t.IsCancellationRequested) { var c=await doh.AcceptTcpClientAsync(t); handlers.Add(Task.Run(async()=> { try { using(c) using(var s=new SslStream(c.GetStream())) {
            await s.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate=cert,ApplicationProtocols=[SslApplicationProtocol.Http11] },t);
            while(!t.IsCancellationRequested) { var header=new List<byte>(); while(header.Count<16384) { header.Add((await Read(s,1,t))[0]); if(header.Count>=4 && header.TakeLast(4).SequenceEqual(new byte[]{13,10,13,10})) break; }
                string text=Encoding.ASCII.GetString(header.ToArray()); Check(text.StartsWith("POST /dns-query "),"DoH HTTP request"); int length=int.Parse(text.Split("\r\n").Single(x=>x.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                byte[] q=await Read(s,length,t); int end=12; while(q[end]!=0) end+=1+q[end]; end++; int kind=q[end]*256+q[end+1]; end+=4;
                byte[] answer=q[..end]; answer[2]=0x81; answer[3]=0x80; answer[6]=0; answer[7]=(byte)(kind==1?1:0); answer[8]=answer[9]=answer[10]=answer[11]=0;
                if(kind==1) answer=[..answer,0xc0,0x0c,0,1,0,1,0,0,0,30,0,4,127,0,0,1];
                Interlocked.Increment(ref dnsQueries); await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/dns-message\r\nContent-Length: {answer.Length}\r\n\r\n"),t); await s.WriteAsync(answer,t);
            }
        }} catch(Exception e) when(e is OperationCanceledException or IOException) {} catch(Exception e) {errors.Add(e);} },t)); }} catch(OperationCanceledException){} },t);
        var echoTask=Task.Run(async()=> { try { while(true) { var packet=await udp.ReceiveAsync(t); await udp.SendAsync(packet.Buffer,packet.RemoteEndPoint,t); }}catch(OperationCanceledException){} },t);
        Process? process=null; Task<string>? diagnostic=null;
        try {
            var start=new ProcessStartInfo(exe) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true }; start.ArgumentList.Add("run"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(path);
            process=Process.Start(start)!; var stderr=process.StandardError.ReadToEndAsync(); diagnostic=stderr; var stdout=process.StandardOutput.ReadToEndAsync();
            bool ready=false; for(int i=0;i<100;i++) { if(process.HasExited) throw new Exception("sing-box exited: "+await stderr); try {using var probe=new TcpClient(); await probe.ConnectAsync(IPAddress.Loopback,inbound,t); ready=true; break;}catch(SocketException){await Task.Delay(25,t);} } Check(ready,"sing-box startup");
            await using var relay=new IngressSocksRelay(inbound);
            var targetTask=Task.Run(async()=> {using var c=await target.AcceptTcpClientAsync(t); using var s=new SslStream(c.GetStream()); await s.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {ServerCertificate=cert},t); Check((await Read(s,4,t)).SequenceEqual("PING"u8.ToArray()),"TLS target payload"); await s.WriteAsync("PONG"u8.ToArray(),t);},t);
            using(var c=new TcpClient()) { await c.ConnectAsync(IPAddress.Loopback,relay.Port,t); var s=c.GetStream(); await Greet(s,t); int tp=Port(target); await s.WriteAsync(new byte[]{5,1,0,1,203,0,113,1,(byte)(tp>>8),(byte)tp},t); Check((await Read(s,10,t))[1]==0,"Ingress connect");
                using var ssl=new SslStream(s,false,(_,presented,_,_)=>presented?.GetCertHashString()==cert.GetCertHashString()); await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {TargetHost="localhost"},t); await ssl.WriteAsync("PING"u8.ToArray(),t); Check((await Read(ssl,4,t)).SequenceEqual("PONG"u8.ToArray()),"TLS target response"); }
            await targetTask; Check(dnsQueries>0 && destinations.Contains(Port(doh)) && destinations.Contains(Port(target)),"DoH and corrected target must both traverse engine");
            using(var c=new TcpClient()) {await c.ConnectAsync(IPAddress.Loopback,relay.Port,t); var s=c.GetStream(); await Greet(s,t); await s.WriteAsync(new byte[]{5,3,0,1,0,0,0,0,0,0},t); var bound=await Read(s,10,t); Check(bound[1]==0,"UDP associate"); var endpoint=new IPEndPoint(new IPAddress(bound[4..8]),bound[8]*256+bound[9]); using var sender=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)); byte[] packet=[0,0,0,1,127,0,0,1,0x13,0x88,..Encoding.ASCII.GetBytes("voice-fixture")]; await sender.SendAsync(packet,endpoint,t); var received=await sender.ReceiveAsync(t); Check(received.Buffer.SequenceEqual(packet),"UDP roundtrip through ingress + sing-box + engine"); }
            // sing-box owns the upstream association; stopping the session must close it.
            await relay.DisposeAsync(); process.Kill(true); await process.WaitForExitAsync(t);
            for(int i=0;i<100 && closed<associations;i++) await Task.Delay(10,t);
            Check(associations>0 && closed==associations,"UDP control lifetime release"); Check(errors.IsEmpty,string.Join(";",errors));
            Console.WriteLine("PASS real sing-box: poisoned IP corrected via SNI, authenticated local DoH AND target through SOCKS engine, UDP roundtrip and session release");
        } finally {if(process!=null){if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); process.Dispose();} if(diagnostic!=null) Console.WriteLine(await diagnostic); foreach(var error in errors) Console.WriteLine(error); timeout.Cancel(); engine.Stop(); doh.Stop(); target.Stop(); udp.Dispose(); try {await Task.WhenAll(engineTask,dohTask,echoTask).WaitAsync(TimeSpan.FromSeconds(2));}catch(Exception e) when(e is OperationCanceledException or ObjectDisposedException or SocketException){} }
    }
}
