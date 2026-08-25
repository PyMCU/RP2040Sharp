using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RP2040Sharp.IntegrationTests.Infrastructure;

/// <summary>
/// A just-enough MQTT 3.1.1 broker on localhost: CONNECT/SUBSCRIBE/PUBLISH(QoS 0-1)/PINGREQ, with
/// fan-out to subscribers by exact topic. It exists so a fleet test exercises the emulator's network
/// stack rather than the public internet — bridging to a real broker works too, and this keeps the
/// suite deterministic and offline.
/// </summary>
public sealed class MiniMqttBroker : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<(TcpClient Client, HashSet<string> Topics)> _clients = [];
    private readonly Lock _gate = new();

    public int Port { get; }

    public MiniMqttBroker()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            client.NoDelay = true;
            var entry = (client, new HashSet<string>(StringComparer.Ordinal));
            lock (_gate) _clients.Add(entry);
            _ = Task.Run(() => ClientLoop(entry.client, entry.Item2));
        }
    }

    private void ClientLoop(TcpClient client, HashSet<string> topics)
    {
        try
        {
            var stream = client.GetStream();
            while (client.Connected)
            {
                var header = stream.ReadByte();
                if (header < 0) return;
                var length = ReadVarInt(stream);
                if (length < 0) return;
                var body = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var n = stream.Read(body, read, length - read);
                    if (n <= 0) return;
                    read += n;
                }
                Handle(stream, (byte)header, body, topics);
            }
        }
        catch (IOException) { /* client vanished */ }
        catch (ObjectDisposedException) { }
        finally { lock (_gate) _clients.RemoveAll(c => c.Client == client); }
    }

    private void Handle(NetworkStream stream, byte header, byte[] body, HashSet<string> topics)
    {
        switch (header >> 4)
        {
            case 1:  // CONNECT
                stream.Write([0x20, 0x02, 0x00, 0x00]);
                break;

            case 8:  // SUBSCRIBE: u16 packet id, then (topic, qos)+
            {
                var packetId = (body[0] << 8) | body[1];
                var o = 2;
                byte granted = 0;
                while (o + 2 <= body.Length)
                {
                    var len = (body[o] << 8) | body[o + 1];
                    o += 2;
                    if (o + len > body.Length) break;
                    topics.Add(Encoding.UTF8.GetString(body, o, len));
                    o += len;
                    granted = o < body.Length ? body[o] : (byte)0;
                    o++;
                }
                stream.Write([0x90, 0x03, (byte)(packetId >> 8), (byte)packetId, granted]);
                break;
            }

            case 3:  // PUBLISH
            {
                var qos = (header >> 1) & 3;
                var len = (body[0] << 8) | body[1];
                var topic = Encoding.UTF8.GetString(body, 2, len);
                var o = 2 + len;
                var packetId = 0;
                if (qos > 0) { packetId = (body[o] << 8) | body[o + 1]; o += 2; }
                var payload = body[o..];
                if (qos == 1)
                    stream.Write([0x40, 0x02, (byte)(packetId >> 8), (byte)packetId]);   // PUBACK
                Fanout(topic, payload);
                break;
            }

            case 12: // PINGREQ
                stream.Write([0xD0, 0x00]);
                break;
        }
        stream.Flush();
    }

    private void Fanout(string topic, byte[] payload)
    {
        var topicBytes = Encoding.UTF8.GetBytes(topic);
        var remaining = 2 + topicBytes.Length + payload.Length;
        var packet = new List<byte> { 0x30 };
        WriteVarInt(packet, remaining);
        packet.Add((byte)(topicBytes.Length >> 8));
        packet.Add((byte)topicBytes.Length);
        packet.AddRange(topicBytes);
        packet.AddRange(payload);
        var bytes = packet.ToArray();

        (TcpClient Client, HashSet<string> Topics)[] snapshot;
        lock (_gate) snapshot = [.. _clients];
        foreach (var (client, subs) in snapshot)
        {
            if (!subs.Contains(topic)) continue;
            try { client.GetStream().Write(bytes); client.GetStream().Flush(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private static int ReadVarInt(NetworkStream s)
    {
        int value = 0, multiplier = 1;
        for (var i = 0; i < 4; i++)
        {
            var b = s.ReadByte();
            if (b < 0) return -1;
            value += (b & 0x7F) * multiplier;
            if ((b & 0x80) == 0) return value;
            multiplier *= 128;
        }
        return -1;
    }

    private static void WriteVarInt(List<byte> to, int value)
    {
        do
        {
            var b = (byte)(value % 128);
            value /= 128;
            if (value > 0) b |= 0x80;
            to.Add(b);
        } while (value > 0);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        lock (_gate) { foreach (var (c, _) in _clients) { try { c.Close(); } catch { } } _clients.Clear(); }
        _cts.Dispose();
    }
}
