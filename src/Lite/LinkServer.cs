using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VusaLinkApp
{
    // The local link between vUSALink-Lite and the EDST page (in a CRC browser tab or any browser).
    //
    // Listens on http://localhost:47613/ only (never the network). WebSocket /link accepts vatflow pages only:
    // the Origin header must be one of AllowedOrigins, so no other website can connect and receive what is typed
    // in CRC. Messages are JSON:
    //   Lite -> page:  {t:"hello", helper, version, protocol}   {t:"cmd", id, text}   {t:"cids", map}
    //   page -> Lite:  {t:"hello", page:"edst", api, list}      {t:"result", id, ok, action, error, flid, ...}
    // Commands go to the most recently connected main EDST page (not a LIST window); CIDs go to every page.
    class LinkServer
    {
        public const int Port = 47613;
        public const int Protocol = 1;

        static readonly string[] AllowedOrigins =
        {
            "https://vatflow.io", "https://www.vatflow.io", "https://vatflow.net", "https://www.vatflow.net"
        };

        class Client
        {
            public int Id;
            public WebSocket Ws;
            public string Origin;
            public bool Hello, List;
            public int Api;
            public DateTime ActiveAt;   // last focus / click / became visible (or when it said hello)
            public readonly SemaphoreSlim SendLock = new SemaphoreSlim(1, 1);
        }

        readonly List<Client> clients = new List<Client>();
        readonly Dictionary<int, TaskCompletionSource<Dictionary<string, object>>> pending =
            new Dictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        readonly object sync = new object();
        readonly string version;
        readonly string[] extraOrigins;
        HttpListener listener;
        int nextClient, nextCmd;

        public event Action ConnectionsChanged;          // fires off the UI thread
        public Func<Dictionary<string, object>> CurrentCids;   // sent to each page when it says hello
        public string StartError;

        public LinkServer(string version)
        {
            this.version = version;
            // For local testing only: VUSALINK_LITE_ORIGINS=http://localhost:8765
            extraOrigins = (Environment.GetEnvironmentVariable("VUSALINK_LITE_ORIGINS") ?? "")
                .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        public bool Start()
        {
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add("http://localhost:" + Port + "/");
                listener.Start();
                Log.Info("Link: listening on http://localhost:" + Port + "/");
                var th = new Thread(AcceptLoop) { IsBackground = true, Name = "LinkServer" };
                th.Start();
                return true;
            }
            catch (Exception ex)
            {
                StartError = ex.Message;
                Log.Info("Link: could not listen on port " + Port + ": " + ex.Message);
                return false;
            }
        }

        // The page that takes commands: a main EDST (not a LIST window) that has said hello; with several, the one
        // used most recently (focus, click or became visible), then the newest.
        Client Target()
        {
            lock (sync) return clients.Where(c => c.Hello && !c.List && c.Ws.State == WebSocketState.Open)
                .OrderByDescending(c => c.ActiveAt).ThenByDescending(c => c.Id).FirstOrDefault();
        }

        public bool HasEdst { get { return Target() != null; } }
        public int ApiVersion { get { var t = Target(); return t != null ? t.Api : 0; } }
        public int PageCount { get { lock (sync) return clients.Count(c => c.Hello && c.Ws.State == WebSocketState.Open); } }

        void AcceptLoop()
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { break; }
                Task.Run(() => Handle(ctx));
            }
        }

        bool Allowed(string origin)
        {
            return origin != null && (AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)
                || extraOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase));
        }

        async Task Handle(HttpListenerContext ctx)
        {
            var origin = ctx.Request.Headers["Origin"];
            try
            {
                if (!Allowed(origin))
                {
                    Log.Info("Link: refused " + ctx.Request.HttpMethod + " " + ctx.Request.Url.AbsolutePath + " from origin " + (origin ?? "(none)"));
                    ctx.Response.StatusCode = 403;
                    ctx.Response.Close();
                    return;
                }
                if (!ctx.Request.IsWebSocketRequest || ctx.Request.Url.AbsolutePath != "/link")
                {
                    // Small status answer (lets a page check for Lite with fetch).
                    var r = ctx.Response;
                    r.Headers["Access-Control-Allow-Origin"] = origin;
                    r.Headers["Access-Control-Allow-Private-Network"] = "true";
                    if (ctx.Request.HttpMethod == "OPTIONS") { r.StatusCode = 204; r.Close(); return; }
                    var b = Encoding.UTF8.GetBytes(Json.Write(new Dictionary<string, object>
                        { { "helper", "vUSALink-Lite" }, { "version", version }, { "protocol", (double)Protocol } }));
                    r.ContentType = "application/json";
                    r.OutputStream.Write(b, 0, b.Length);
                    r.Close();
                    return;
                }
                var wsc = await ctx.AcceptWebSocketAsync(null);
                var c = new Client { Ws = wsc.WebSocket, Origin = origin };
                lock (sync) { c.Id = ++nextClient; clients.Add(c); }
                Log.Info("Link: page connected #" + c.Id + " from " + origin);
                await SendTo(c, new Dictionary<string, object>
                    { { "t", "hello" }, { "helper", "vUSALink-Lite" }, { "version", version }, { "protocol", (double)Protocol } });
                await ReceiveLoop(c);
            }
            catch (Exception ex) { Log.Info("Link: " + ex.Message); }
        }

        async Task ReceiveLoop(Client c)
        {
            var buf = new byte[64 * 1024];
            try
            {
                while (c.Ws.State == WebSocketState.Open)
                {
                    var sb = new StringBuilder();
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await c.Ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                    } while (!r.EndOfMessage);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    OnMessage(c, sb.ToString());
                }
            }
            catch (Exception ex) { Log.Info("Link: page #" + c.Id + " receive ended: " + ex.Message); }
            finally
            {
                lock (sync) clients.Remove(c);
                try { c.Ws.Dispose(); } catch { }
                Log.Info("Link: page #" + c.Id + " disconnected");
                Changed();
            }
        }

        void OnMessage(Client c, string text)
        {
            var o = Json.Parse(text) as Dictionary<string, object>;
            if (o == null) return;
            var t = Json.Str(o, "t");
            if (t == "hello")
            {
                c.Hello = true;
                c.Api = (int)Json.Num(o, "api", 0);
                c.List = Json.Bool(o, "list") == true;
                // A page that is in the foreground when it connects counts as just used; a background one doesn't
                // take over from the page you're working in.
                c.ActiveAt = Json.Bool(o, "active") == true ? DateTime.UtcNow : DateTime.MinValue.AddTicks(c.Id);
                Log.Info("Link: page #" + c.Id + " is " + (c.List ? "a LIST window" : "the EDST") + ", host API v" + c.Api);
                var cids = CurrentCids != null ? CurrentCids() : null;
                if (cids != null) { var ignored = SendTo(c, new Dictionary<string, object> { { "t", "cids" }, { "map", cids } }); }
                Changed();
            }
            else if (t == "active")
            {
                c.ActiveAt = DateTime.UtcNow;
            }
            else if (t == "result")
            {
                int id = (int)Json.Num(o, "id", -1);
                TaskCompletionSource<Dictionary<string, object>> tcs;
                lock (sync) { if (pending.TryGetValue(id, out tcs)) pending.Remove(id); }
                if (tcs != null) tcs.TrySetResult(o);
            }
        }

        void Changed() { if (ConnectionsChanged != null) ConnectionsChanged(); }

        // Runs a command in the EDST (as vUSALinkEDST.command(text, {source:'crc'})) and returns its result,
        // or null if no EDST is connected or it didn't answer in time.
        public async Task<Dictionary<string, object>> SendCommand(string text)
        {
            var c = Target();
            if (c == null) return null;
            var tcs = new TaskCompletionSource<Dictionary<string, object>>();
            int id;
            lock (sync) { id = ++nextCmd; pending[id] = tcs; }
            await SendTo(c, new Dictionary<string, object> { { "t", "cmd" }, { "id", (double)id }, { "text", text } });
            var done = await Task.WhenAny(tcs.Task, Task.Delay(15000));
            lock (sync) pending.Remove(id);
            return done == tcs.Task ? tcs.Task.Result : null;
        }

        public void SendCids(Dictionary<string, object> map)
        {
            List<Client> all;
            lock (sync) all = clients.Where(x => x.Hello).ToList();
            foreach (var c in all) { var ignored = SendTo(c, new Dictionary<string, object> { { "t", "cids" }, { "map", map } }); }
        }

        async Task SendTo(Client c, Dictionary<string, object> msg)
        {
            var b = Encoding.UTF8.GetBytes(Json.Write(msg));
            await c.SendLock.WaitAsync();
            try
            {
                if (c.Ws.State == WebSocketState.Open)
                    await c.Ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex) { Log.Info("Link: send to page #" + c.Id + " failed: " + ex.Message); }
            finally { c.SendLock.Release(); }
        }
    }
}
