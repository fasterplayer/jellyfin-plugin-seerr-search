using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrSearch.Services
{
    /// <summary>
    /// Inserts the plugin's &lt;script&gt; tag into the web client's index.html at the
    /// moment the page is served.
    ///
    /// Nothing is written to disk: the web folder does not need to be writable, and a
    /// jellyfin-web update does not erase the injection. The same code works on
    /// Jellyfin 10.11 and 12.
    ///
    /// The filter is deliberately timid: it only touches index.html, does not inject
    /// twice, does nothing until Seerr is configured, and serves the original page
    /// untouched if anything goes wrong.
    /// </summary>
    public class ScriptInjectionStartupFilter : IStartupFilter
    {
        private readonly ILogger<ScriptInjectionStartupFilter> _logger;
        private int _loggedOnce;

        public ScriptInjectionStartupFilter(ILogger<ScriptInjectionStartupFilter> logger)
        {
            _logger = logger;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                // Registered before the rest of the pipeline: by removing Accept-Encoding
                // below, we get an uncompressed, readable response.
                app.Use(InvokeAsync);
                next(app);
            };
        }

        private async Task InvokeAsync(HttpContext context, Func<Task> nextMiddleware)
        {
            var config = Plugin.Instance?.Configuration;

            if (config == null
                || !config.Enabled
                || config.DisableScriptInjection
                || !SeerrClient.IsConfigured
                || !HttpMethods.IsGet(context.Request.Method)
                || !IsIndexRequest(context.Request.Path.Value))
            {
                await nextMiddleware().ConfigureAwait(false);
                return;
            }

            // A partial (206) or compressed response cannot be rewritten cleanly.
            context.Request.Headers.Remove("Accept-Encoding");
            context.Request.Headers.Remove("Range");
            context.Request.Headers.Remove("If-Range");

            var originalBody = context.Response.Body;
            using var buffer = new MemoryStream();
            context.Response.Body = buffer;

            try
            {
                await nextMiddleware().ConfigureAwait(false);
            }
            catch
            {
                // The failure comes from below: drop the buffer (never written to the real
                // stream) and rethrow, so the host renders its own error page.
                context.Response.Body = originalBody;
                throw;
            }

            context.Response.Body = originalBody;
            buffer.Seek(0, SeekOrigin.Begin);

            var isHtml = context.Response.StatusCode == 200
                && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);

            if (!isHtml)
            {
                await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
                return;
            }

            string html;
            using (var reader = new StreamReader(buffer, Encoding.UTF8, true, 1024, leaveOpen: true))
            {
                html = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            try
            {
                var alreadyInjected = html.Contains("/" + Plugin.PluginRoute + "/script", StringComparison.OrdinalIgnoreCase);
                var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);

                if (!alreadyInjected && bodyClose >= 0 && Plugin.Instance != null)
                {
                    html = html[..bodyClose] + Plugin.Instance.BuildScriptTag() + "\n" + html[bodyClose..];

                    if (System.Threading.Interlocked.Exchange(ref _loggedOnce, 1) == 0)
                    {
                        _logger.LogInformation("Seerr Search: script injected into index.html on the fly.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Seerr Search: injection failed, the original page is served unchanged.");
            }

            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html;charset=utf-8";
            context.Response.ContentLength = bytes.Length;
            context.Response.Headers.Remove("ETag");
            context.Response.Headers.Remove("Last-Modified");
            context.Response.Headers.Remove("Accept-Ranges");

            await originalBody.WriteAsync(bytes).ConfigureAwait(false);
        }

        // "/web", "/web/" and "/web/index.html" — EndsWith to stay correct when
        // Jellyfin is served under a prefix (e.g. /jellyfin/web/).
        private static bool IsIndexRequest(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            return path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/web", StringComparison.OrdinalIgnoreCase);
        }
    }
}
