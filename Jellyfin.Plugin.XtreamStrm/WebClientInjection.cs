using System.Text;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.XtreamStrm;

/// <summary>Eklentinin sunucu servislerini kaydeder.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddTransient<IStartupFilter, WebClientInjectionStartupFilter>();
        serviceCollection.AddHostedService<StaleChannelCleanupService>();

        // Jellyfin 12.x kanallari (IChannel) yalnizca servis kaydindan alir.
        serviceCollection.AddSingleton<IChannel, XtreamLiveChannel>();
        serviceCollection.AddSingleton<IChannel, XtreamVodChannel>();
        serviceCollection.AddSingleton<IChannel, XtreamSeriesChannel>();
    }
}

/// <summary>Ara katmani Jellyfin'in istek hattinin en basina yerlestirir.</summary>
public class WebClientInjectionStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<WebClientInjectionMiddleware>();
            app.UseMiddleware<LiveTvZapMiddleware>();
            next(app);
        };
    }
}

/// <summary>
/// Web istemcisinin index.html sayfasina canli TV kumanda betigini ekler. Jellyfin'in
/// dosyalarina dokunmaz; yalnizca giden yanit degistirilir (guncellemeden etkilenmez).
/// LG webOS ve tarayici istemcileri bu sayfayi sunucudan yukledigi icin betik orada calisir.
/// </summary>
public class WebClientInjectionMiddleware
{
    public const string ScriptTag = "<script src=\"../XtreamStrm/LiveTvRemote.js\"></script>";

    private readonly RequestDelegate _next;

    public WebClientInjectionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!ShouldInject(context.Request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Sikistirilmamis ve tam yanit gelsin (304 donerse tarayici eski, betiksiz sayfayi kullanir).
        var headers = context.Request.Headers;
        headers.Remove(HeaderNames.AcceptEncoding);
        headers.Remove(HeaderNames.IfNoneMatch);
        headers.Remove(HeaderNames.IfModifiedSince);
        headers.Remove(HeaderNames.Range);

        var response = context.Response;
        var original = response.Body;
        using var buffer = new MemoryStream();
        response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            response.Body = original;
        }

        buffer.Position = 0;
        var isHtml = response.StatusCode == StatusCodes.Status200OK
            && response.ContentType != null
            && response.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
            && !response.Headers.ContainsKey(HeaderNames.ContentEncoding);

        if (!isHtml)
        {
            await buffer.CopyToAsync(original, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var html = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        if (!html.Contains(ScriptTag, StringComparison.Ordinal))
        {
            var at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            html = at >= 0 ? html.Insert(at, ScriptTag) : html + ScriptTag;
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        response.Headers.Remove(HeaderNames.ETag);
        response.Headers.Remove(HeaderNames.LastModified);
        response.ContentLength = bytes.Length;
        await original.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private static bool ShouldInject(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) || Plugin.Instance?.Configuration is not { } cfg || !(cfg.LiveRemoteControl || cfg.OpenLibraryCard))
        {
            return false;
        }

        var path = request.Path.Value;
        return path != null
            && (path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase));
    }
}
