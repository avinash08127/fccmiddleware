using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Files;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DppMiddleWareService
{
    public class DashboardService : IHostedService
    {
        private readonly ILogger<DashboardService> _logger;
        private WebServer? _server;

        public DashboardService(ILogger<DashboardService> logger)
        {
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting dashboard service...");

            // Create webroot folder and index.html if missing
            var webRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            Directory.CreateDirectory(webRoot);

            var htmlPath = Path.Combine(webRoot, "index.html");
            File.WriteAllText(htmlPath, DashboardHtml);

            // Start EmbedIO server
            _server = new WebServer(o => o
                    .WithUrlPrefix("http://localhost:8080/")
                    .WithMode(HttpListenerMode.EmbedIO))
                .WithLocalSessionManager()
                .WithStaticFolder("/", webRoot, true);
                //.HandleHttpException(async (ctx, ex) =>
                //{
                //    // Force all 404s to return the same index.html
                //    if (ex.StatusCode == 404)
                //    {
                //        ctx.Response.ContentType = "text/html";
                //        await ctx.SendFileAsync(Path.Combine(webRoot, "index.html"));
                //    }
                //    else
                //    {
                //        throw ex;
                //    }
                //});

            _server.RunAsync(cancellationToken);
            _logger.LogInformation("Dashboard running at http://localhost:8080/sitemanager");

            // Open browser
            OpenBrowser("http://localhost:8080/sitemanager");

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping dashboard service...");
            _server?.Dispose();
            return Task.CompletedTask;
        }

        private void OpenBrowser(string url)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    Process.Start("xdg-open", url);
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    Process.Start("open", url);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open browser");
            }
        }

        // --- HTML Page with WebSocket URL using 0.0.0.0 ---
        private const string DashboardHtml = @"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='UTF-8'>
<title>WebSocket Dashboard</title>
<style>
  body { font-family:Segoe UI, sans-serif; background:#121212; color:#eee; padding:20px; }
  .status { padding:6px 12px; border-radius:8px; font-weight:bold; margin-left:10px; }
  .online { background:#2e7d32; color:white; }
  .offline { background:#c62828; color:white; }
  #log { background:#1e1e1e; border-radius:8px; padding:10px; margin-top:20px; height:350px; overflow-y:auto; font-family:monospace; }
</style>
</head>
<body>
<h2>WebSocket Dashboard <span id='status' class='status offline'>Offline</span></h2>
<div id='endpoint'></div>
<div id='log'></div>
<script>
const statusEl=document.getElementById('status');
const logEl=document.getElementById('log');
const endpointEl=document.getElementById('endpoint');

// Always connect to 0.0.0.0:2004 with the same path
const path = window.location.pathname || '/sitemanager';
const wsUrl = `ws://0.0.0.0:2004${path}`;
endpointEl.textContent = 'Connecting to: ' + wsUrl;

const log = (msg) => {
  const ts = new Date().toLocaleTimeString();
  logEl.textContent += `[${ts}] ${msg}\n`;
  logEl.scrollTop = logEl.scrollHeight;
};

const setStatus = (online) => {
  if (online) {
    statusEl.textContent = 'Online';
    statusEl.classList.add('online');
    statusEl.classList.remove('offline');
  } else {
    statusEl.textContent = 'Offline';
    statusEl.classList.add('offline');
    statusEl.classList.remove('online');
  }
};

let socket;
function connect() {
  socket = new WebSocket(wsUrl);
  socket.onopen = () => {
    setStatus(true);
    log('✅ Connected to ' + wsUrl);
    setInterval(() => {
      if (socket.readyState === WebSocket.OPEN) {
        socket.send(JSON.stringify({ command: 'ping', source: 'browser' }));
        log('📤 Sent ping');
      }
    }, 10000);
  };
  socket.onmessage = (e) => log('📩 ' + e.data);
  socket.onclose = () => {
    setStatus(false);
    log('❌ Disconnected — retrying in 3s...');
    setTimeout(connect, 3000);
  };
  socket.onerror = (err) => log('⚠️ WebSocket error: ' + err.message);
}
connect();
</script>
</body>
</html>";
    }
}
