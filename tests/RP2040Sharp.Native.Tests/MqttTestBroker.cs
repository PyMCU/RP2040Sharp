using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// A just-enough MQTT 3.1.1 broker on 127.0.0.1 for the bridge tests: CONNECT, SUBSCRIBE (with + and #), PUBLISH QoS 0
/// and 1 (PUBACK), PINGREQ, DISCONNECT, fan-out to the subscribers, and <see cref="Publish"/> for the test itself, so a
/// guest's umqtt.simple can publish and receive without anything outside the test process.
/// </summary>
internal sealed class MqttTestBroker : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<(TcpClient Client, List<string> Filters)> _clients = [];
    private readonly Lock _gate = new();
    private readonly List<(string Topic, byte[] Payload)> _published = [];

    public int Port { get; }
    public int Connects { get; private set; }

    public MqttTestBroker()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>Every PUBLISH a client sent, in order.</summary>
    public (string Topic, byte[] Payload)[] Published { get { lock (_gate) return [.. _published]; } }

    /// <summary>Topic filters subscribed so far, over all clients.</summary>
    public string[] Subscriptions { get { lock (_gate) return [.. _clients.SelectMany(c => c.Filters)]; } }

    /// <summary>Publish from the test (QoS 0) to the subscribers whose filter matches.</summary>
    public void Publish(string topic, string payload) => Fanout(topic, Encoding.UTF8.GetBytes(payload));

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { return; }
            client.NoDelay = true;
            var entry = (client, new List<string>());
            lock (_gate) _clients.Add(entry);
            _ = Task.Run(() => ClientLoop(entry.client, entry.Item2));
        }
    }

    private void ClientLoop(TcpClient client, List<string> filters)
    {
        try
        {
            var stream = client.GetStream();
            for (;;)
            {
                var header = stream.ReadByte();
                if (header < 0) return;
                var length = ReadVarInt(stream);
                if (length < 0) return;
                var body = new byte[length];
                for (var read = 0; read < length;)
                {
                    var n = stream.Read(body, read, length - read);
                    if (n <= 0) return;
                    read += n;
                }
                if (!Handle(stream, (byte)header, body, filters)) return;
            }
        }
        catch (Exception) { /* the client vanished */ }
        finally
        {
            lock (_gate) _clients.RemoveAll(c => c.Client == client);
            client.Dispose();
        }
    }

    private bool Handle(NetworkStream stream, byte header, byte[] body, List<string> filters)
    {
        switch (header >> 4)
        {
            case 1:  // CONNECT
                lock (_gate) Connects++;
                Write(stream, [0x20, 0x02, 0x00, 0x00]);
                break;
            case 8:  // SUBSCRIBE: packet id, then (filter, qos)+
            {
                var id = body[0] << 8 | body[1];
                var granted = new List<byte>();
                for (var o = 2; o + 2 <= body.Length;)
                {
                    var len = body[o] << 8 | body[o + 1];
                    o += 2;
                    if (o + len + 1 > body.Length) break;
                    lock (_gate) filters.Add(Encoding.UTF8.GetString(body, o, len));
                    o += len + 1;
                    granted.Add(0);
                }
                Write(stream, [0x90, (byte)(2 + granted.Count), (byte)(id >> 8), (byte)id, .. granted]);
                break;
            }
            case 3:  // PUBLISH
            {
                var qos = header >> 1 & 3;
                var len = body[0] << 8 | body[1];
                var topic = Encoding.UTF8.GetString(body, 2, len);
                var o = 2 + len;
                var id = 0;
                if (qos > 0) { id = body[o] << 8 | body[o + 1]; o += 2; }
                var payload = body[o..];
                lock (_gate) _published.Add((topic, payload));
                if (qos == 1) Write(stream, [0x40, 0x02, (byte)(id >> 8), (byte)id]);
                Fanout(topic, payload);
                break;
            }
            case 12: // PINGREQ
                Write(stream, [0xD0, 0x00]);
                break;
            case 14: // DISCONNECT
                return false;
        }
        return true;
    }

    private static void Write(NetworkStream s, byte[] b) { lock (s) { s.Write(b); s.Flush(); } }

    private void Fanout(string topic, byte[] payload)
    {
        var t = Encoding.UTF8.GetBytes(topic);
        var packet = new List<byte> { 0x30 };
        for (var v = 2 + t.Length + payload.Length; ; )
        {
            var b = (byte)(v % 128);
            v /= 128;
            packet.Add(v > 0 ? (byte)(b | 0x80) : b);
            if (v == 0) break;
        }
        packet.Add((byte)(t.Length >> 8));
        packet.Add((byte)t.Length);
        packet.AddRange(t);
        packet.AddRange(payload);
        var bytes = packet.ToArray();
        (TcpClient Client, List<string> Filters)[] snapshot;
        lock (_gate) snapshot = [.. _clients.Select(c => (c.Client, c.Filters.ToList()))];
        foreach (var (client, subs) in snapshot)
        {
            if (!subs.Any(f => Matches(f, topic))) continue;
            try { Write(client.GetStream(), bytes); } catch (Exception) { /* gone */ }
        }
    }

    internal static bool Matches(string filter, string topic)
    {
        var f = filter.Split('/');
        var t = topic.Split('/');
        for (var i = 0; i < f.Length; i++)
        {
            if (f[i] == "#") return true;
            if (i >= t.Length) return false;
            if (f[i] != "+" && f[i] != t[i]) return false;
        }
        return f.Length == t.Length;
    }

    private static int ReadVarInt(NetworkStream s)
    {
        int value = 0, mul = 1;
        for (var i = 0; i < 4; i++)
        {
            var b = s.ReadByte();
            if (b < 0) return -1;
            value += (b & 0x7F) * mul;
            if ((b & 0x80) == 0) return value;
            mul *= 128;
        }
        return -1;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* stopped */ }
        lock (_gate) { foreach (var (c, _) in _clients) try { c.Close(); } catch { /* gone */ } _clients.Clear(); }
    }
}
