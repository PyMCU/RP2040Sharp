// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona
// Portions derived from rp2040js — Copyright (c) 2021 Uri Shaked (MIT).
// See NOTICE.txt.
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RP2040.Gdb;

/// <summary>
/// Exposes a <see cref="GdbServer"/> over TCP so <c>arm-none-eabi-gdb</c> can connect with
/// <c>target remote :3333</c>. Ported from rp2040js (src/gdb/gdb-tcp-server.ts).
/// One connection is served at a time, matching a typical debug session.
/// </summary>
public sealed class GdbTcpServer : GdbServer, IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }

    /// <summary>Optional sink for connection/lifecycle messages (connected, disconnected, errors).</summary>
    public Action<string>? OnLog;

    public GdbTcpServer(IGdbTarget target, int port = 3333) : base(target)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    /// <summary>Begin accepting connections on a background task.</summary>
    public void Start()
    {
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { OnLog?.Invoke($"GDB accept error: {e.Message}"); return; }

            _ = HandleConnectionAsync(client, ct);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        OnLog?.Invoke("GDB connected");
        client.NoDelay = true;
        var stream = client.GetStream();

        var connection = new GdbConnection(this, data =>
        {
            var bytes = Encoding.ASCII.GetBytes(data);
            lock (stream)
                stream.Write(bytes, 0, bytes.Length);
        });

        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0)
                    break;
                connection.FeedData(Encoding.ASCII.GetString(buffer, 0, read));
            }
        }
        catch (Exception e)
        {
            OnLog?.Invoke($"GDB socket error: {e.Message}");
        }
        finally
        {
            RemoveConnection(connection);
            client.Dispose();
            OnLog?.Invoke("GDB disconnected");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
