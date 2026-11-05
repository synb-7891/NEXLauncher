using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NEXLauncher.Services;

/// <summary>Minecraft 服务器状态：直接按 Server List Ping 协议连服务器，不依赖任何第三方站点。</summary>
public static class ServerPing
{
    public sealed record Result(bool Online, int Players, int Max, string Version, string Motd, string Error);

    public static async Task<Result> QueryAsync(string address, int timeoutMs = 5000)
    {
        var host = address.Trim();
        var port = 25565;
        var colon = host.LastIndexOf(':');
        if (colon > 0 && int.TryParse(host[(colon + 1)..], out var p)) { port = p; host = host[..colon]; }
        if (string.IsNullOrWhiteSpace(host))
            return new Result(false, 0, 0, "", "", "地址为空");

        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            if (await Task.WhenAny(connect, Task.Delay(timeoutMs)) != connect)
                return new Result(false, 0, 0, "", "", "连接超时（服务器没开或端口不对）");
            await connect;

            using var ns = client.GetStream();
            ns.ReadTimeout = timeoutMs;

            // Handshake：protocol=-1(未知)，next state=1(status)
            using (var ms = new MemoryStream())
            {
                WriteVarInt(ms, 0x00);
                WriteVarInt(ms, -1);
                WriteString(ms, host);
                ms.WriteByte((byte)(port >> 8));
                ms.WriteByte((byte)(port & 0xFF));
                WriteVarInt(ms, 1);
                await SendPacketAsync(ns, ms.ToArray());
            }
            // Status Request
            await SendPacketAsync(ns, new byte[] { 0x00 });

            var length = await ReadVarIntAsync(ns);
            if (length <= 0) return new Result(false, 0, 0, "", "", "服务器没有返回数据");
            var buf = new byte[length];
            var read = 0;
            while (read < length)
            {
                var n = await ns.ReadAsync(buf.AsMemory(read, length - read));
                if (n <= 0) break;
                read += n;
            }
            using var rms = new MemoryStream(buf, 0, read);
            await ReadVarIntAsync(rms); // packet id
            var jsonLen = await ReadVarIntAsync(rms);
            var jsonBuf = new byte[jsonLen];
            var got = 0;
            while (got < jsonLen)
            {
                var n = await rms.ReadAsync(jsonBuf.AsMemory(got, jsonLen - got));
                if (n <= 0) break;
                got += n;
            }
            var json = Encoding.UTF8.GetString(jsonBuf, 0, got);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var players = 0; var max = 0;
            if (root.TryGetProperty("players", out var pl))
            {
                if (pl.TryGetProperty("online", out var o) && o.TryGetInt32(out var ov)) players = ov;
                if (pl.TryGetProperty("max", out var m) && m.TryGetInt32(out var mv)) max = mv;
            }
            var version = root.TryGetProperty("version", out var v) && v.TryGetProperty("name", out var vn)
                ? (vn.GetString() ?? "") : "";
            var motd = ExtractText(root.TryGetProperty("description", out var d) ? d : default);
            return new Result(true, players, max, version, motd, "");
        }
        catch (Exception ex)
        {
            return new Result(false, 0, 0, "", "", ex.Message);
        }
    }

    private static string ExtractText(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Undefined) return "";
        if (e.ValueKind == JsonValueKind.String) return e.GetString() ?? "";
        var sb = new StringBuilder();
        if (e.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) sb.Append(t.GetString());
        if (e.TryGetProperty("extra", out var ex) && ex.ValueKind == JsonValueKind.Array)
            foreach (var c in ex.EnumerateArray()) sb.Append(ExtractText(c));
        return sb.ToString();
    }

    private static async Task SendPacketAsync(NetworkStream ns, byte[] payload)
    {
        using var ms = new MemoryStream();
        WriteVarInt(ms, payload.Length);
        ms.Write(payload, 0, payload.Length);
        var data = ms.ToArray();
        await ns.WriteAsync(data);
        await ns.FlushAsync();
    }

    private static void WriteVarInt(Stream s, int value)
    {
        var v = (uint)value;
        do
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) b |= 0x80;
            s.WriteByte(b);
        } while (v != 0);
    }

    private static void WriteString(Stream s, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        WriteVarInt(s, bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }

    private static async Task<int> ReadVarIntAsync(Stream s)
    {
        var result = 0; var shift = 0;
        while (true)
        {
            var one = new byte[1];
            var n = await s.ReadAsync(one.AsMemory(0, 1));
            if (n <= 0) throw new EndOfStreamException("服务器提前断开连接");
            var b = one[0];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 35) throw new InvalidDataException("VarInt 过长");
        }
        return result;
    }
}
