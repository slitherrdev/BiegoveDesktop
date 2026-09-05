using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Biegove.controllers
{
    public class HTTPServer
    {
        public int port = 8080;
        private TcpListener? listener;
        private DateTime lastPing = DateTime.MinValue;
        private bool _blocked = false;

        public Action<List<(int id, string name, long createdAt)>, List<(int numer, long czas, int runId)>>? OnSync { get; set; }
        public Action<long, List<(int numer, int elapsed, string? note)>, string>? OnSyncMobile { get; set; }
        public event Action? OnConnectionChanged;

        public bool IsPhoneConnected => !_blocked && (DateTime.Now - lastPing).TotalSeconds < 15;

        public void Disconnect()
        {
            _blocked = true;
            lastPing = DateTime.MinValue;
            OnConnectionChanged?.Invoke();
        }

        public void init()
        {
            for (int p = port; p < port + 10; p++)
            {
                try
                {
                    listener = new TcpListener(IPAddress.Any, p);
                    listener.Start();
                    port = p;
                    listener.BeginAcceptTcpClient(OnAccept, null);
                    EnsureFirewallRule(p);
                    return;
                }
                catch { listener = null; }
            }
            throw new Exception("Cannot bind port");
        }

        private static void EnsureFirewallRule(int p)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("netsh",
                    $"advfirewall firewall add rule name=\"Biegove\" dir=in action=allow protocol=TCP localport={p}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit(3000);
            }
            catch { }
        }

        private void OnAccept(IAsyncResult ar)
        {
            if (listener == null) return;
            TcpClient? client = null;
            try { client = listener.EndAcceptTcpClient(ar); } catch { }
            try { listener.BeginAcceptTcpClient(OnAccept, null); } catch { }
            if (client == null) return;
            try { Handle(client); } catch { }
            finally { try { client.Close(); } catch { } }
        }

        private void Handle(TcpClient client)
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            var stream = client.GetStream();

            var buf = new MemoryStream();
            uint marker = 0;
            while (true)
            {
                int b = stream.ReadByte();
                if (b == -1) return;
                buf.WriteByte((byte)b);
                marker = (marker << 8) | (uint)b;
                if (marker == 0x0D0A0D0A) break;
            }

            var headerStr = Encoding.ASCII.GetString(buf.ToArray());
            var lines = headerStr.Split('\n');
            var req = lines[0].Trim().Split(' ');
            var path = req.Length > 1 ? req[1].TrimEnd('/').ToLower() : "";

            int contentLength = 0;
            foreach (var l in lines)
                if (l.Trim().StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(l.Split(':')[1].Trim(), out contentLength);

            string body = "";
            if (contentLength > 0)
            {
                var bodyBuf = new byte[contentLength];
                int read = 0;
                while (read < contentLength)
                {
                    int n = stream.Read(bodyBuf, read, contentLength - read);
                    if (n == 0) break;
                    read += n;
                }
                body = Encoding.UTF8.GetString(bodyBuf, 0, read);
            }

            string responseText;
            int statusCode;

            switch (path)
            {
                case "/handshake":
                    _blocked = false;
                    lastPing = DateTime.Now;
                    OnConnectionChanged?.Invoke();
                    responseText = "{\"status\":\"connected\",\"name\":\"Biegove\"}";
                    statusCode = 200;
                    break;

                case "/ping":
                    if (!_blocked)
                    {
                        lastPing = DateTime.Now;
                        OnConnectionChanged?.Invoke();
                    }
                    responseText = "{\"status\":\"ok\"}";
                    statusCode = 200;
                    break;

                case "/sync":
                    if (_blocked)
                    {
                        responseText = "{\"error\":\"disconnected\"}";
                        statusCode = 403;
                        break;
                    }
                    try
                    {
                        var doc = JsonDocument.Parse(body);

                        if (doc.RootElement.TryGetProperty("startTime", out var startTimeProp))
                        {
                            var startTime = startTimeProp.GetInt64();
                            var raceName = doc.RootElement.TryGetProperty("name", out var rn) ? rn.GetString() ?? "" : "";
                            var entries = new List<(int, int, string?)>();
                            foreach (var item in doc.RootElement.GetProperty("entries").EnumerateArray())
                            {
                                string? note = item.TryGetProperty("note", out var noteProp) && noteProp.ValueKind != JsonValueKind.Null
                                    ? noteProp.GetString() : null;
                                entries.Add((
                                    item.GetProperty("number").GetInt32(),
                                    item.GetProperty("elapsed").GetInt32(),
                                    note
                                ));
                            }
                            OnSyncMobile?.Invoke(startTime, entries, raceName);
                        }
                        else
                        {
                            var runs = new List<(int, string, long)>();
                            if (doc.RootElement.TryGetProperty("runs", out var runsEl))
                            {
                                foreach (var r in runsEl.EnumerateArray())
                                {
                                    runs.Add((
                                        r.GetProperty("id").GetInt32(),
                                        r.GetProperty("name").GetString() ?? "",
                                        r.GetProperty("createdAt").GetInt64()
                                    ));
                                }
                            }

                            var fullEntries = new List<(int, long, int)>();
                            foreach (var item in doc.RootElement.GetProperty("entries").EnumerateArray())
                            {
                                int runId = item.TryGetProperty("runId", out var rp) ? rp.GetInt32() : 0;
                                fullEntries.Add((
                                    item.GetProperty("number").GetInt32(),
                                    item.GetProperty("timestamp").GetInt64(),
                                    runId
                                ));
                            }
                            OnSync?.Invoke(runs, fullEntries);
                        }

                        lastPing = DateTime.Now;
                        OnConnectionChanged?.Invoke();
                        responseText = "{\"status\":\"ok\"}";
                        statusCode = 200;
                    }
                    catch
                    {
                        responseText = "{\"error\":\"Invalid JSON\"}";
                        statusCode = 400;
                    }
                    break;

                default:
                    responseText = "{\"error\":\"Unknown endpoint\"}";
                    statusCode = 404;
                    break;
            }

            var respBody = Encoding.UTF8.GetBytes(responseText);
            var respHeader = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {statusCode} {(statusCode == 200 ? "OK" : statusCode == 403 ? "Forbidden" : statusCode == 400 ? "Bad Request" : "Not Found")}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {respBody.Length}\r\nConnection: close\r\n\r\n");
            stream.Write(respHeader, 0, respHeader.Length);
            stream.Write(respBody, 0, respBody.Length);
            stream.Flush();
        }
    }
}
