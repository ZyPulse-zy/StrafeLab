using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
namespace StrafeLab.Core.Gsi;
public sealed class GsiServer : IAsyncDisposable
{
    private WebApplication? _app;
    private readonly string? _token;
    public GsiServer(string? expectedToken=null)=>_token=expectedToken;
    public int Port {get;private set;}=3000;
    public bool IsRunning=>_app!=null;
    public GsiSnapshot? Latest {get;private set;}
    public DateTime? LastReceivedUtc {get;private set;}
    public event EventHandler<GsiSnapshot>? SnapshotReceived;
    public event EventHandler<string>? StatusChanged;
    public async Task<bool> StartAsync(int preferredPort=3000,CancellationToken cancellationToken=default)
    {
        if(_app!=null)return true;
        foreach(int port in Enumerable.Range(preferredPort,10).Concat(Enumerable.Range(33270,10)).Distinct())
        {
            var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o=>{o.AddServerHeader=false;o.Limits.MaxRequestBodySize=1_048_576;o.Limits.RequestHeadersTimeout=TimeSpan.FromSeconds(5);o.Listen(IPAddress.Loopback,port,l=>l.Protocols=HttpProtocols.Http1);});
            var app=builder.Build();app.MapPost("/gsi",Handle);
            try{await app.StartAsync(cancellationToken);_app=app;Port=port;StatusChanged?.Invoke(this,$"GSI 监听 127.0.0.1:{port}");return true;}
            catch(Exception ex) when(ex is System.IO.IOException or System.Net.Sockets.SocketException){await app.DisposeAsync();}
        }
        StatusChanged?.Invoke(this,"GSI 端口不可用");return false;
    }
    private async Task Handle(HttpContext context)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var reader=new System.IO.StreamReader(context.Request.Body,Encoding.UTF8);
            var json=await reader.ReadToEndAsync(timeout.Token);
            if(!ValidateToken(json)){context.Response.StatusCode=401;return;}
            var s=GsiSnapshotParser.Parse(json,TimeUtil.NowMicroseconds());Latest=s;LastReceivedUtc=DateTime.UtcNow;
            SnapshotReceived?.Invoke(this,s);context.Response.StatusCode=200;await context.Response.WriteAsync("ok",timeout.Token);
        }
        catch(OperationCanceledException){context.Response.StatusCode=408;}
        catch(Microsoft.AspNetCore.Http.BadHttpRequestException ex){context.Response.StatusCode=ex.StatusCode;}
        catch(JsonException){context.Response.StatusCode=400;}
        catch(Exception ex){context.Response.StatusCode=400;StatusChanged?.Invoke(this,"GSI: "+ex.Message);}
    }
    private bool ValidateToken(string json)
    {
        if(_token==null)return true;
        using var doc=JsonDocument.Parse(json);var root=doc.RootElement;
        return root.ValueKind==JsonValueKind.Object&&root.TryGetProperty("auth",out var auth)&&auth.ValueKind==JsonValueKind.Object&&
            auth.TryGetProperty("token",out var token)&&token.ValueKind==JsonValueKind.String&&token.GetString()==_token;
    }
    public async Task StopAsync(){if(_app==null)return;var app=_app;_app=null;await app.StopAsync();await app.DisposeAsync();}
    public async ValueTask DisposeAsync()=>await StopAsync();
}
