using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using WGTun.Core;

static void Assert(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
byte[] query = [1,2,1,0,0,1,0,0,0,0,0,0,1,97,0,0,1,0,1];
var handler = new Fake();
int relayPort = 0;
await using (var relay = new DnsSocksRelay(new Uri("https://test.invalid/dns-query"), handler, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(100))) {
 relayPort = relay.Port;
 await relay.CheckReadyAsync();
 using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, relay.Port); var stream = tcp.GetStream();
 await stream.WriteAsync(new byte[] {5,1,0}); byte[] greeting = new byte[2]; await stream.ReadExactlyAsync(greeting); Assert(greeting.SequenceEqual(new byte[] {5,0}), "SOCKS greeting");
 await stream.WriteAsync(new byte[] {5,1,0,1,8,8,8,8,0,53}); byte[] reply = new byte[10]; await stream.ReadExactlyAsync(reply); Assert(reply[1] == 0, "SOCKS CONNECT");
 await stream.WriteAsync(new byte[] {0,(byte)query.Length}); await stream.WriteAsync(query); byte[] len = new byte[2]; await stream.ReadExactlyAsync(len); byte[] answer = new byte[len[1]]; await stream.ReadExactlyAsync(answer); Assert((answer[2] & 128) != 0, "TCP framed DoH answer");
 using var control = new TcpClient(); await control.ConnectAsync(IPAddress.Loopback, relay.Port); var cs = control.GetStream(); await cs.WriteAsync(new byte[] {5,1,0}); await cs.ReadExactlyAsync(greeting); await cs.WriteAsync(new byte[] {5,3,0,1,0,0,0,0,0,0}); await cs.ReadExactlyAsync(reply);
 using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); byte[] packet = [0,0,0,1,8,8,8,8,0,53,..query]; await udp.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, (reply[8] << 8) | reply[9])); var got = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2)); Assert((got.Buffer[12] & 128) != 0, "UDP associate DoH answer");
 handler.Mode = 1; try { await relay.CheckReadyAsync(); throw new Exception("malformed accepted"); } catch (IOException) { Console.WriteLine("PASS malformed response rejected"); }
 handler.Mode = 2; try { await relay.CheckReadyAsync(); throw new Exception("timeout accepted"); } catch (OperationCanceledException) { Console.WriteLine("PASS upstream timeout"); }
 await Task.Delay(1000); Assert(!relay.IsRunning, "sustained failure marks relay unhealthy");
}
var rebound = new TcpListener(IPAddress.Loopback, relayPort); rebound.Server.ExclusiveAddressUse = true; rebound.Start(); rebound.Stop(); Console.WriteLine("PASS listener closed on disposal");
using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); await using var relay = new DnsSocksRelay(new Uri("https://test.invalid"), new Fake()); try { await relay.CheckReadyAsync(cancelled.Token); throw new Exception("cancellation ignored"); } catch (OperationCanceledException) { Console.WriteLine("PASS startup cancellation"); } }
sealed class Fake : HttpMessageHandler {
 public volatile int Mode;
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
  token.ThrowIfCancellationRequested();
  if (Mode == 2) await Task.Delay(Timeout.Infinite, token);
  byte[] data = await request.Content!.ReadAsByteArrayAsync(token); data[2] |= 128; if (Mode == 1) data[0]++;
  var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }; response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message"); return response;
 }
}
