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
    /// Insère la balise &lt;script&gt; du plugin dans l'index.html du client web,
    /// au moment où la page est servie.
    ///
    /// Rien n'est écrit sur le disque : le dossier web n'a pas besoin d'être
    /// accessible en écriture, et une mise à jour de jellyfin-web n'efface pas
    /// l'injection. Le même code fonctionne sur Jellyfin 10.11 et 12.
    ///
    /// Le filtre est volontairement peureux : il ne touche qu'à index.html, ne
    /// réinjecte pas si la balise est déjà là, et en cas de pépin sert la page
    /// d'origine sans rien casser.
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
                // Enregistré avant le reste du pipeline : en retirant Accept-Encoding
                // ci-dessous, on récupère une réponse non compressée, donc lisible.
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
                || !HttpMethods.IsGet(context.Request.Method)
                || !IsIndexRequest(context.Request.Path.Value))
            {
                await nextMiddleware().ConfigureAwait(false);
                return;
            }

            // Une réponse partielle (206) ou compressée ne se réécrit pas proprement.
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
                // L'échec vient d'en dessous : on jette le tampon (jamais écrit sur
                // le vrai flux) et on relaie, pour que l'hôte rende sa page d'erreur.
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
                        _logger.LogInformation("Seerr Search : script injecté dans index.html à la volée.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Seerr Search : injection impossible, la page d'origine est servie telle quelle.");
            }

            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html;charset=utf-8";
            context.Response.ContentLength = bytes.Length;
            context.Response.Headers.Remove("ETag");
            context.Response.Headers.Remove("Last-Modified");
            context.Response.Headers.Remove("Accept-Ranges");

            await originalBody.WriteAsync(bytes).ConfigureAwait(false);
        }

        // "/web", "/web/" et "/web/index.html" — EndsWith pour rester juste
        // quand Jellyfin est servi sous un préfixe (ex. /jellyfin/web/).
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
