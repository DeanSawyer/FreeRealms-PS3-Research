using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
namespace FreeRealms.Bootstrap;
public sealed class BootstrapHost
{
    public event Action<string>? Log;
    private WebApplication? app;
    private UdpClient? dns;
    private CancellationTokenSource? stop;
    private Task? dnsTask;
    private static readonly HashSet<string> KnownHosts = new(StringComparer.OrdinalIgnoreCase) { "lpconfig.station.sony.com", "frps3.patch.station.sony.com", "ps3.freerealms.com", "fr.patch.station.sony.com" };
    private const string DigestPath="/patch/freerealms/ps3scelive/ps3-web/FreeRealms.soe";
    private const string AssetPrefix="/patch/freerealms/ps3scelive/ps3-web/FreeRealms.zs/";
    public async Task StartAsync(string client, string bind, string advertise, int port, bool enableDns)
    {
        if (stop is not null) throw new InvalidOperationException("Already running");
        stop=new CancellationTokenSource();
        try {
            if (enableDns) {
                dns = new UdpClient(new IPEndPoint(IPAddress.Parse(bind),53));
                dnsTask = Task.Run(() => DnsLoop(IPAddress.Parse(advertise),stop.Token));
                Log?.Invoke($"DNS listening {bind}:53 → {advertise}");
            }
            var builder=WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls($"http://{bind}:{port}");
            app=builder.Build();
            app.Run(async context => {
                context.Response.Headers["Cache-Control"]="no-cache";
                context.Response.Headers["Connection"]="close";
                var path=context.Request.Path.Value??"/";
                Log?.Invoke($"{context.Request.Method} {path}");
                if (path.Equals("/app/appconfig.php",StringComparison.OrdinalIgnoreCase)) {
                    string xml=$"<AppConfig>\n<Login>\n<URL type=\"access\">http://{advertise}/LaunchPad/lpadAPI/login_v2</URL>\n</Login>\n<GameList>\n<Game env=\"live\">\n<Labels/>\n<Versions>\n<Version id=\"scea-live-tails\">\n<Digest>http://frps3.patch.station.sony.com{DigestPath}</Digest>\n</Version>\n</Versions>\n</Game>\n</GameList>\n</AppConfig>";
                    context.Response.ContentType="text/xml; charset=utf-8";
                    context.Response.ContentLength=Encoding.UTF8.GetByteCount(xml);
                    if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.WriteAsync(xml); return;
                }
                if (path.Equals(DigestPath,StringComparison.OrdinalIgnoreCase)) {
                    await SendFile(context,Path.Combine(client,"USRDIR","Digests","NPUA30048.soe")); return;
                }
                if (path.StartsWith(AssetPrefix,StringComparison.OrdinalIgnoreCase)) {
                    var relative=Uri.UnescapeDataString(path.Substring(AssetPrefix.Length)).Replace('\\','/');
                    if (relative.Length==0 || relative.Split('/').Any(part => part==".." || part=="." || part.Length==0)) { context.Response.StatusCode=400; return; }
                    var root=Path.GetFullPath(Path.Combine(client,"USRDIR"))+Path.DirectorySeparatorChar;
                    var target=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(root,StringComparison.OrdinalIgnoreCase)) {context.Response.StatusCode=403;return;}
                    if (File.Exists(target)) {await SendFile(context,target);return;}
                    Log?.Invoke("Missing asset: "+relative);
                    context.Response.StatusCode=404;return;
                }
                if (HttpMethods.IsPost(context.Request.Method) && path.StartsWith("/ps3ws/",StringComparison.OrdinalIgnoreCase)) {
                    string xml="<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Response><Status>0</Status><StatusMessage>Success</StatusMessage></Response>\r\n";
                    if (path.Equals("/ps3ws/game/serverList.action",StringComparison.OrdinalIgnoreCase))
                        xml="<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<ServerListReply><Status>0</Status><StatusMessage>Success</StatusMessage><Servers><Server><Name>NorthForge</Name><Online>1</Online><Locked>0</Locked></Server></Servers></ServerListReply>\r\n";
                    context.Response.ContentType="text/xml; charset=utf-8";
                    context.Response.ContentLength=Encoding.UTF8.GetByteCount(xml);
                    await context.Response.WriteAsync(xml);return;
                }
                context.Response.StatusCode=404;
            });
            await app.StartAsync();
            Log?.Invoke($"HTTP listening on {bind}:{port} (experimental preview)");
        } catch { Stop(); throw; }
    }
    private static async Task SendFile(HttpContext ctx,string file) {
        if (!File.Exists(file)){ctx.Response.StatusCode=404;return;}
        ctx.Response.Headers["Cache-Control"]="no-cache";
        ctx.Response.Headers["Connection"]="close";
        ctx.Response.ContentType="application/octet-stream";
        ctx.Response.ContentLength=new FileInfo(file).Length;
        if (!HttpMethods.IsHead(ctx.Request.Method)) await ctx.Response.SendFileAsync(file);
    }
    private async Task DnsLoop(IPAddress advertise,CancellationToken token)
    {
        while (!token.IsCancellationRequested) {
            try {
                var packet=await dns!.ReceiveAsync(token);
                var data=packet.Buffer;
                if (data.Length<17) continue;
                int offset=12; var labels=new List<string>();
                while (offset<data.Length && data[offset]!=0) {
                    int len=data[offset++]; if(len>63||offset+len>data.Length)throw new FormatException("DNS label");
                    labels.Add(Encoding.ASCII.GetString(data,offset,len));offset+=len;
                }
                if (++offset+4>data.Length)continue;
                int questionEnd=offset+4;
                int type=(data[offset]<<8)|data[offset+1];
                string domain=string.Join(".",labels).ToLowerInvariant();
                bool known=KnownHosts.Contains(domain);
                if (!known) {
                    using var upstream=new UdpClient();
                    await upstream.SendAsync(data,data.Length,new IPEndPoint(IPAddress.Parse("1.1.1.1"),53));
                    var upstreamTask=upstream.ReceiveAsync(token).AsTask();
                    var completed=await Task.WhenAny(upstreamTask,Task.Delay(3000,token));
                    if (completed==upstreamTask) await dns.SendAsync((await upstreamTask).Buffer,packet.RemoteEndPoint,token);
                    else Log?.Invoke($"DNS upstream timeout: {domain}");
                    continue;
                }
                using var ms=new MemoryStream();
                ms.Write(data,0,2);
                ms.Write(new byte[]{0x81,0x80,0,1,0,(byte)(known&&type==1?1:0),0,0,0,0});
                ms.Write(data,12,questionEnd-12);
                if(known&&type==1) {ms.Write(new byte[]{0xC0,0x0C,0,1,0,1,0,0,0,30,0,4});ms.Write(advertise.GetAddressBytes());}
                var response=ms.ToArray();
                await dns.SendAsync(response,packet.RemoteEndPoint,token);
                Log?.Invoke($"DNS {domain} type={type} {(known?"local":"no answer")}");
            } catch(OperationCanceledException){break;} catch(ObjectDisposedException){break;} catch(Exception ex){Log?.Invoke("DNS: "+ex.Message);}
        }
    }
    public void Stop()
    {
        stop?.Cancel(); dns?.Dispose(); dns=null;
        if (app is not null) { try { app.StopAsync().GetAwaiter().GetResult(); app.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch {} app=null; }
        stop?.Dispose();stop=null; Log?.Invoke("Services stopped");
    }
}