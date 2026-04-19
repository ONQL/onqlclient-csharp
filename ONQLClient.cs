using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ONQL
{
    /// <summary>
    /// An asynchronous, concurrent-safe .NET client for the ONQL TCP server.
    /// </summary>
    public class ONQLClient : IDisposable
    {
        private const byte EOM = 0x04;          // End-of-Message
        private const char DELIMITER = '\x1E';  // Record separator

        private TcpClient? _tcp;
        private NetworkStream? _stream;
        private Task? _readerTask;
        private CancellationTokenSource? _cts;

        private readonly ConcurrentDictionary<string, TaskCompletionSource<ONQLResponse>> _pending
            = new ConcurrentDictionary<string, TaskCompletionSource<ONQLResponse>>();

        private readonly int _defaultTimeoutMs;
        private readonly object _writeLock = new object();
        private volatile bool _disposed;

        private ONQLClient(int defaultTimeoutMs)
        {
            _defaultTimeoutMs = defaultTimeoutMs;
        }

        /// <summary>
        /// Create and return a connected ONQLClient.
        /// </summary>
        /// <param name="host">Server hostname (default: "localhost").</param>
        /// <param name="port">Server port (default: 5656).</param>
        /// <param name="timeoutSeconds">Default request timeout in seconds (default: 10).</param>
        public static async Task<ONQLClient> CreateAsync(
            string host = "localhost",
            int port = 5656,
            int timeoutSeconds = 10)
        {
            var client = new ONQLClient(timeoutSeconds * 1000);

            client._tcp = new TcpClient();
            try
            {
                await client._tcp.ConnectAsync(host, port).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException(
                    $"Could not connect to server at {host}:{port}: {ex.Message}", ex);
            }

            client._stream = client._tcp.GetStream();
            client._cts = new CancellationTokenSource();
            client._readerTask = Task.Run(() => client.ResponseReaderLoopAsync(client._cts.Token));

            return client;
        }

        /// <summary>
        /// Send a request and wait for the matching response.
        /// </summary>
        /// <param name="keyword">The ONQL keyword / command.</param>
        /// <param name="payload">The request payload (typically JSON).</param>
        /// <param name="timeoutMs">Per-request timeout in milliseconds, or null to use the default.</param>
        public async Task<ONQLResponse> SendRequestAsync(
            string keyword,
            string payload,
            int? timeoutMs = null)
        {
            if (_disposed || _stream == null)
                throw new InvalidOperationException("Client is not connected.");

            int timeout = timeoutMs ?? _defaultTimeoutMs;
            string rid = GenerateRequestId();

            var tcs = new TaskCompletionSource<ONQLResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[rid] = tcs;

            try
            {
                byte[] frame = BuildFrame(rid, keyword, payload);
                await WriteBytesAsync(frame).ConfigureAwait(false);

                using var delayCts = new CancellationTokenSource(timeout);
                using var reg = delayCts.Token.Register(() =>
                    tcs.TrySetException(new TimeoutException(
                        $"Request {rid} timed out after {timeout} ms.")));

                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                _pending.TryRemove(rid, out _);
            }
        }

        /// <summary>
        /// Gracefully close the connection.
        /// </summary>
        public async Task CloseAsync()
        {
            if (_disposed)
                return;

            _disposed = true;
            _cts?.Cancel();

            if (_stream != null)
            {
                try { _stream.Close(); } catch { }
            }

            if (_tcp != null)
            {
                try { _tcp.Close(); } catch { }
            }

            if (_readerTask != null)
            {
                try { await _readerTask.ConfigureAwait(false); } catch { }
            }

            // Fail all pending requests
            foreach (var kvp in _pending)
            {
                kvp.Value.TrySetException(
                    new InvalidOperationException("Connection closed."));
            }
            _pending.Clear();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _cts?.Cancel();
                _stream?.Dispose();
                _tcp?.Dispose();
                _cts?.Dispose();

                foreach (var kvp in _pending)
                {
                    kvp.Value.TrySetException(
                        new ObjectDisposedException(nameof(ONQLClient)));
                }
                _pending.Clear();
            }
        }

        // ----------------------------------------------------------------
        // Internal helpers
        // ----------------------------------------------------------------

        private async Task ResponseReaderLoopAsync(CancellationToken ct)
        {
            // Read bytes incrementally, splitting on EOM (0x04).
            var buffer = new byte[16 * 1024];
            var messageBuffer = new MemoryStream();

            try
            {
                while (!ct.IsCancellationRequested && _stream != null)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, ct)
                                                  .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (IOException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    if (bytesRead == 0)
                        break; // Connection closed by server

                    // Process each byte; split on EOM
                    for (int i = 0; i < bytesRead; i++)
                    {
                        if (buffer[i] == EOM)
                        {
                            // We have a complete message
                            string message = Encoding.UTF8.GetString(
                                messageBuffer.ToArray());
                            messageBuffer.SetLength(0);

                            HandleMessage(message);
                        }
                        else
                        {
                            messageBuffer.WriteByte(buffer[i]);
                        }
                    }
                }
            }
            catch
            {
                // Reader loop ended due to an unexpected error.
            }
            finally
            {
                // Fail all remaining pending requests
                foreach (var kvp in _pending)
                {
                    kvp.Value.TrySetException(
                        new InvalidOperationException("Connection lost."));
                }
            }
        }

        private void HandleMessage(string message)
        {
            // Split into exactly 3 parts: rid, source, payload
            string[] parts = message.Split(new[] { DELIMITER }, 3);
            if (parts.Length != 3)
                return; // Malformed

            string rid = parts[0];
            string source = parts[1];
            string payload = parts[2];

            // Check pending one-shot request
            if (_pending.TryRemove(rid, out var tcs))
            {
                tcs.TrySetResult(new ONQLResponse(rid, source, payload));
            }
        }

        private static byte[] BuildFrame(string rid, string keyword, string payload)
        {
            string text = rid + DELIMITER + keyword + DELIMITER + payload;
            byte[] textBytes = Encoding.UTF8.GetBytes(text);
            byte[] frame = new byte[textBytes.Length + 1];
            Buffer.BlockCopy(textBytes, 0, frame, 0, textBytes.Length);
            frame[frame.Length - 1] = EOM;
            return frame;
        }

        private async Task WriteBytesAsync(byte[] data)
        {
            if (_stream == null)
                throw new InvalidOperationException("Not connected.");

            // NetworkStream.WriteAsync is not guaranteed thread-safe,
            // so we serialize writes with a SemaphoreSlim for async safety.
            await _writeSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
                await _stream.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _writeSemaphore.Release();
            }
        }

        private readonly SemaphoreSlim _writeSemaphore = new SemaphoreSlim(1, 1);

        private static string GenerateRequestId()
        {
            // 4 random bytes -> 8 hex chars, matching the Python driver's uuid4().hex[:8]
            byte[] bytes = new byte[4];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        // ----------------------------------------------------------------
        // Direct ORM-style API (insert / update / delete / onql / build)
        //
        // `path` is a dotted string:
        //   "mydb.users"        -> table `users` in database `mydb`
        //   "mydb.users.u1"     -> record with id `u1`
        // ----------------------------------------------------------------

        private readonly struct PathParts
        {
            public readonly string Db;
            public readonly string Table;
            public readonly string Id;
            public PathParts(string db, string table, string id) { Db = db; Table = table; Id = id; }
        }

        private static PathParts ParsePath(string path, bool requireId)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException(
                    "Path must be a non-empty string like \"db.table\" or \"db.table.id\"", nameof(path));
            int dot1 = path.IndexOf('.');
            if (dot1 <= 0 || dot1 == path.Length - 1)
                throw new ArgumentException(
                    $"Path \"{path}\" must contain at least \"db.table\"", nameof(path));
            int dot2 = path.IndexOf('.', dot1 + 1);
            string db = path.Substring(0, dot1);
            string table, id;
            if (dot2 == -1)
            {
                table = path.Substring(dot1 + 1);
                id = string.Empty;
            }
            else
            {
                table = path.Substring(dot1 + 1, dot2 - dot1 - 1);
                id = path.Substring(dot2 + 1);
            }
            if (requireId && string.IsNullOrEmpty(id))
                throw new ArgumentException(
                    $"Path \"{path}\" must include a record id: \"db.table.id\"", nameof(path));
            return new PathParts(db, table, id);
        }

        /// <summary>
        /// Parse the standard <c>{"error":"…","data":…}</c> envelope.
        /// Throws <see cref="InvalidOperationException"/> if <c>error</c> is
        /// non-empty. Returns the raw <c>data</c> substring on success.
        /// </summary>
        public static string ProcessResult(string raw)
        {
            if (raw == null)
                throw new InvalidOperationException("null response");
            string error = ExtractValue(raw, "error");
            if (!string.IsNullOrEmpty(error)
                && error != "null" && error != "false" && error != "\"\"")
            {
                if (error.Length >= 2 && error[0] == '"' && error[error.Length - 1] == '"')
                    error = error.Substring(1, error.Length - 2);
                throw new InvalidOperationException(error);
            }
            return ExtractValue(raw, "data");
        }

        /// <summary>
        /// Insert a single record at <paramref name="path"/> (e.g.
        /// <c>"mydb.users"</c>). <paramref name="recordJson"/> is a
        /// pre-serialized JSON object.
        /// </summary>
        public async Task<string> InsertAsync(string path, string recordJson)
        {
            var p = ParsePath(path, requireId: false);
            string payload = "{"
                + "\"db\":"      + JsonEscape(p.Db)    + ","
                + "\"table\":"   + JsonEscape(p.Table) + ","
                + "\"records\":" + recordJson
                + "}";
            var res = await SendRequestAsync("insert", payload).ConfigureAwait(false);
            return ProcessResult(res.Payload);
        }

        /// <summary>
        /// Update the record at <paramref name="path"/> (e.g.
        /// <c>"mydb.users.u1"</c>). Uses <c>protopass = "default"</c>.
        /// </summary>
        public Task<string> UpdateAsync(string path, string recordJson)
            => UpdateAsync(path, recordJson, "default");

        public async Task<string> UpdateAsync(string path, string recordJson, string protopass)
        {
            var p = ParsePath(path, requireId: true);
            string idsJson = "[" + JsonEscape(p.Id) + "]";
            string payload = "{"
                + "\"db\":"        + JsonEscape(p.Db)       + ","
                + "\"table\":"     + JsonEscape(p.Table)    + ","
                + "\"records\":"   + recordJson             + ","
                + "\"query\":\"\","
                + "\"protopass\":" + JsonEscape(protopass)  + ","
                + "\"ids\":"       + idsJson
                + "}";
            var res = await SendRequestAsync("update", payload).ConfigureAwait(false);
            return ProcessResult(res.Payload);
        }

        /// <summary>
        /// Delete the record at <paramref name="path"/> (e.g.
        /// <c>"mydb.users.u1"</c>). Uses <c>protopass = "default"</c>.
        /// </summary>
        public Task<string> DeleteAsync(string path)
            => DeleteAsync(path, "default");

        public async Task<string> DeleteAsync(string path, string protopass)
        {
            var p = ParsePath(path, requireId: true);
            string idsJson = "[" + JsonEscape(p.Id) + "]";
            string payload = "{"
                + "\"db\":"        + JsonEscape(p.Db)       + ","
                + "\"table\":"     + JsonEscape(p.Table)    + ","
                + "\"query\":\"\","
                + "\"protopass\":" + JsonEscape(protopass)  + ","
                + "\"ids\":"       + idsJson
                + "}";
            var res = await SendRequestAsync("delete", payload).ConfigureAwait(false);
            return ProcessResult(res.Payload);
        }

        /// <summary>
        /// Execute a raw ONQL query using defaults (<c>protopass = "default"</c>,
        /// empty context).
        /// </summary>
        public Task<string> OnqlAsync(string query)
            => OnqlAsync(query, "default", "", "[]");

        public async Task<string> OnqlAsync(string query, string protopass,
                                             string ctxkey, string ctxvaluesJson)
        {
            string payload = "{"
                + "\"query\":"     + JsonEscape(query)     + ","
                + "\"protopass\":" + JsonEscape(protopass) + ","
                + "\"ctxkey\":"    + JsonEscape(ctxkey)    + ","
                + "\"ctxvalues\":" + ctxvaluesJson
                + "}";
            var res = await SendRequestAsync("onql", payload).ConfigureAwait(false);
            return ProcessResult(res.Payload);
        }

        /// <summary>
        /// Replace <c>$1</c>, <c>$2</c>, … placeholders in <paramref name="query"/>
        /// with the supplied values. Strings are double-quoted, numbers and booleans
        /// are inlined verbatim.
        /// </summary>
        public string Build(string query, params object?[] values)
        {
            if (values == null) return query;
            for (int i = 0; i < values.Length; i++)
            {
                string placeholder = "$" + (i + 1);
                object? v = values[i];
                string replacement;
                if (v is string s) replacement = "\"" + s + "\"";
                else if (v is bool b) replacement = b ? "true" : "false";
                else if (v == null)  replacement = "null";
                else                 replacement = v.ToString() ?? "";
                query = query.Replace(placeholder, replacement);
            }
            return query;
        }

        /// <summary>
        /// Extract the JSON value for a top-level key. Returns the raw substring
        /// (including surrounding quotes for string values), or <c>""</c> if the
        /// key is missing.
        /// </summary>
        private static string ExtractValue(string raw, string key)
        {
            string pat = "\"" + key + "\"";
            int p = 0;
            while ((p = raw.IndexOf(pat, p, StringComparison.Ordinal)) >= 0)
            {
                int c = p + pat.Length;
                while (c < raw.Length && (raw[c] == ' ' || raw[c] == '\t' ||
                                           raw[c] == '\n' || raw[c] == '\r')) c++;
                if (c < raw.Length && raw[c] == ':')
                {
                    c++;
                    while (c < raw.Length && (raw[c] == ' ' || raw[c] == '\t' ||
                                               raw[c] == '\n' || raw[c] == '\r')) c++;
                    if (c >= raw.Length) return "";
                    int start = c;
                    char ch = raw[c];
                    if (ch == '"')
                    {
                        c++;
                        while (c < raw.Length)
                        {
                            if (raw[c] == '\\' && c + 1 < raw.Length) c += 2;
                            else if (raw[c] == '"') { c++; break; }
                            else c++;
                        }
                    }
                    else if (ch == '{' || ch == '[')
                    {
                        char open = ch, close = (ch == '{') ? '}' : ']';
                        int depth = 1; c++;
                        while (c < raw.Length && depth > 0)
                        {
                            if (raw[c] == '"')
                            {
                                c++;
                                while (c < raw.Length)
                                {
                                    if (raw[c] == '\\' && c + 1 < raw.Length) c += 2;
                                    else if (raw[c] == '"') { c++; break; }
                                    else c++;
                                }
                            }
                            else
                            {
                                if (raw[c] == open)  depth++;
                                if (raw[c] == close) depth--;
                                c++;
                            }
                        }
                    }
                    else
                    {
                        while (c < raw.Length && raw[c] != ',' && raw[c] != '}' &&
                               raw[c] != ']' && raw[c] != ' ' && raw[c] != '\t' &&
                               raw[c] != '\n' && raw[c] != '\r') c++;
                    }
                    return raw.Substring(start, c - start);
                }
                p++;
            }
            return "";
        }

        /// <summary>
        /// Minimal JSON string escaping (no external JSON dependency for netstandard2.1).
        /// </summary>
        private static string JsonEscape(string value)
        {
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20)
                            sb.AppendFormat("\\u{0:x4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
