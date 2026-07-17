// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona
// Portions derived from rp2040js — Copyright (c) 2021 Uri Shaked (MIT).
// See NOTICE.txt.
using static RP2040.Gdb.GdbUtils;

namespace RP2040.Gdb;

/// <summary>
/// A single GDB client connection: frames RSP packets out of an incoming byte stream,
/// validates checksums, and dispatches to <see cref="GdbServer"/>. Transport-agnostic —
/// responses are delivered through the <c>onResponse</c> callback. Ported from rp2040js
/// (src/gdb/gdb-connection.ts).
/// </summary>
public sealed class GdbConnection
{
    private readonly GdbServer _server;
    private readonly Action<string> _onResponse;
    private string _buf = "";

    public GdbConnection(GdbServer server, Action<string> onResponse)
    {
        _server = server;
        _onResponse = onResponse;
        server.AddConnection(this);
        onResponse("+");
    }

    public void FeedData(string data)
    {
        if (data.Length > 0 && data[0] == 3)   // Ctrl-C interrupt
        {
            _server.Target.Stop();
            _onResponse(GdbMessage(GdbServer.StopReplySigint));
            data = data[1..];
        }

        _buf += data;
        while (true)
        {
            var dolla = _buf.IndexOf('$');
            if (dolla < 0)
                return;
            var hash = _buf.IndexOf('#', dolla + 1);
            if (hash < 0 || hash + 2 >= _buf.Length)   // need both checksum chars after '#'
                return;

            var cmd = _buf.Substring(dolla + 1, hash - dolla - 1);
            var cksum = _buf.Substring(hash + 1, 2);
            _buf = _buf[(hash + 3)..];

            if (GdbChecksum(cmd) != cksum)
            {
                _onResponse("-");
            }
            else
            {
                _onResponse("+");
                var response = _server.ProcessGdbMessage(cmd);
                if (response != null)
                    _onResponse(response);
            }
        }
    }

    public void OnBreakpoint()
    {
        try
        {
            _onResponse(GdbMessage(GdbServer.StopReplyTrap));
        }
        catch
        {
            _server.RemoveConnection(this);
        }
    }
}
