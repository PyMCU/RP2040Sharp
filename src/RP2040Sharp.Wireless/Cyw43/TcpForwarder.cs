// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace RP2040Sharp.Wireless.Cyw43;

/// <summary>
/// Host port forwarding into the guest: a real listening socket on <c>127.0.0.1:hostPort</c> (loopback only, never
/// another interface) per forward, and every connection it accepts relayed to a TCP server on the station
/// (<see cref="VirtualNet.ClientIp"/>:guestPort) through the gateway, which opens the guest side as an ordinary TCP
/// client: SYN (retried while nothing listens yet), MSS, the guest's window, go-back-N retransmission, FIN both ways
/// (half-close kept) and RST. A browser on the PC can then open a page served by MicroPython + Microdot.
///
/// Threads: socket I/O runs on background tasks that only fill or drain thread-safe queues; every TCP state change and
/// every frame into the guest happens on the simulation thread, in <see cref="Pump"/> (the run loop's turn, at a host-time
/// cadence) or while the guest transmits (<see cref="HandleGuestSegment"/>). Retransmission timers count simulated time
/// (<see cref="Clock"/>), the guest's own time base.
/// </summary>
public sealed class TcpForwarder : IDisposable
{
    public enum Event { Open = 1, Close = 2, Refused = 3 }

    /// <summary>The connection an event is about: the forward's ports and the host client's (browser's) port.</summary>
    public readonly record struct ConnInfo(ushort HostPort, ushort GuestPort, ushort PeerPort);

    public const int MaxForwards = 16;
    public const int MaxConnections = 64;

    // Gateway-side ports of the forwarded connections; below VirtualNet.TcpConnect's default 50000.
    private const ushort FirstPort = 40000, PortSpan = 9000;
    private const int OurMss = 1460, DefaultMss = 536;
    private const int MaxInFlightSegments = 8;     // never more than this ahead of the guest's ACKs
    private const int SendBufCap = 256 * 1024;      // host bytes held for the guest per connection
    private const int InboxCap = 64 * 1024;         // the reader pauses past this
    private const int WindowMax = 65535;            // no window scaling
    // A short first timeout: on this wire the round trip is well under a millisecond, and lwIP refuses the data of a
    // connection its application has not accept()ed yet, so the first request bytes often need a second copy.
    private const double InitialRto = 0.05, MaxSynRto = 1.0, MaxRto = 2.0;
    /// <summary>Simulated seconds a connection may wait for the guest's server (not joined yet, not listening yet).</summary>
    public const double ConnectDeadline = 10.0;
    private const int MaxRetries = 12;
    private const byte FIN = 0x01, SYN = 0x02, RST = 0x04, PSH = 0x08, ACK = 0x10;

    private readonly VirtualNet _net;
    private readonly List<Listener> _listeners = [];
    private readonly ConcurrentQueue<(Listener L, Socket S)> _accepted = new();
    private readonly Dictionary<ushort, Conn> _conns = new();
    private readonly List<Conn> _scratch = [];
    private int _signal;
    private ushort _nextPort;
    private uint _nextIss = 0x4000_0000;
    private volatile bool _disposed;

    /// <summary>Seconds on the guest's clock (the chip's simulated time). Defaults to host time.</summary>
    public Func<double> Clock { get; set; } = () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>A forwarded connection reached the guest's server, closed, or was refused (nothing listened before
    /// <see cref="ConnectDeadline"/>). Raised on the simulation thread.</summary>
    public Action<Event, ConnInfo>? OnEvent;

    public int Forwards => _listeners.Count;
    /// <summary>Connections accepted from the host and not finished yet.</summary>
    public int OpenConnections => _conns.Count;
    public long Accepted { get; private set; }
    public long Failed { get; private set; }
    /// <summary>Payload bytes from the host clients delivered into the guest (first transmissions).</summary>
    public long ToGuestBytes { get; private set; }
    /// <summary>Payload bytes the guest's servers sent back to the host clients.</summary>
    public long FromGuestBytes { get; private set; }

    /// <summary>True when a pump would have something to do: host traffic queued, or connections alive.</summary>
    public bool Busy => Volatile.Read(ref _signal) != 0 || _conns.Count > 0;

    internal TcpForwarder(VirtualNet net) => _net = net;

    private sealed class Listener(ushort hostPort, ushort guestPort, Socket sock)
    {
        public readonly ushort HostPort = hostPort, GuestPort = guestPort;
        public readonly Socket Sock = sock;
    }

    private enum State { SynSent, Established }

    private sealed class Conn(Listener l, Socket sock, ushort port, ushort peer)
    {
        public readonly Listener L = l;
        public readonly Socket Sock = sock;
        public readonly ushort Port = port, Peer = peer;
        public State St;
        public byte[]? Mac;
        public uint Iss, SndUna, SndNxt, SndMax, RcvNxt, FinSeq;
        public int Mss = DefaultMss, GuestWnd, LastAdvWnd = WindowMax, Retries;
        public bool SynSent, HostFin, FinSent, FinSeqSet, FinAcked, GuestFin, RtoArmed, Probe, AckDue;
        public double Deadline, TimerAt, Rto = InitialRto;
        // host → guest: unacknowledged and unsent bytes, starting at SndUna
        public byte[] Buf = new byte[4096];
        public int Len;
        // background reader → simulation thread
        public readonly ConcurrentQueue<byte[]> Inbox = new();
        public int InboxBytes;
        public volatile bool HostEof, HostReset, Closed;
        // simulation thread → background writer
        public readonly Channel<byte[]> Out = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        public int OutPending;
        private int _halves;

        public ConnInfo Info => new(L.HostPort, L.GuestPort, Peer);

        public void HalfDone()
        {
            if (Interlocked.Increment(ref _halves) == 2) try { Sock.Dispose(); } catch { /* gone */ }
        }

        public void Abort()
        {
            Closed = true;
            Out.Writer.TryComplete();
            try { Sock.LingerState = new LingerOption(true, 0); } catch { /* gone */ }
            try { Sock.Dispose(); } catch { /* gone */ }
        }
    }

    // ---- forwards ------------------------------------------------------------------------------------------------

    /// <summary>Listen on 127.0.0.1:<paramref name="hostPort"/> (0: a free port) and relay each connection to the guest's
    /// <paramref name="guestPort"/>. Returns the port listened on. Throws <see cref="InvalidOperationException"/> with a
    /// one-line reason when the port is taken, already forwarded, or the limit is reached.</summary>
    public int Listen(ushort hostPort, ushort guestPort)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (guestPort == 0) throw new InvalidOperationException("guest_port 0 is not a port");
        if (hostPort != 0 && _listeners.Exists(l => l.HostPort == hostPort))
            throw new InvalidOperationException("127.0.0.1:" + hostPort + " is already forwarded on this chip");
        if (_listeners.Count >= MaxForwards) throw new InvalidOperationException("at most " + MaxForwards + " forwards per chip");
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            s.Bind(new IPEndPoint(IPAddress.Loopback, hostPort));   // loopback only: nothing outside the PC can reach it
            s.Listen(16);
        }
        catch (SocketException e)
        {
            s.Dispose();
            throw new InvalidOperationException(e.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? "127.0.0.1:" + hostPort + " is in use by another program"
                : "cannot listen on 127.0.0.1:" + hostPort + ": " + e.SocketErrorCode);
        }
        var l = new Listener((ushort)((IPEndPoint)s.LocalEndPoint!).Port, guestPort, s);
        _listeners.Add(l);
        _ = AcceptLoop(l);
        return l.HostPort;
    }

    /// <summary>Stop forwarding <paramref name="hostPort"/>: the listener closes and its connections are reset.</summary>
    public bool Unlisten(ushort hostPort)
    {
        var l = _listeners.Find(x => x.HostPort == hostPort);
        if (l is null) return false;
        _listeners.Remove(l);
        try { l.Sock.Dispose(); } catch { /* gone */ }
        foreach (var c in Snapshot())
            if (c.L == l) Finish(c, reset: true, Event.Close);
        return true;
    }

    private async Task AcceptLoop(Listener l)
    {
        try
        {
            while (!_disposed)
            {
                var s = await l.Sock.AcceptAsync().ConfigureAwait(false);
                if (_disposed) { Reset(s); break; }
                s.NoDelay = true;
                _accepted.Enqueue((l, s));
                Signal();
            }
        }
        catch (Exception) { /* listener closed */ }
    }

    private static void Reset(Socket s)
    {
        try { s.LingerState = new LingerOption(true, 0); } catch { /* gone */ }
        try { s.Dispose(); } catch { /* gone */ }
    }

    private void Signal() => Volatile.Write(ref _signal, 1);

    private async Task ReadLoop(Conn c)
    {
        var buf = new byte[4096];
        try
        {
            while (!c.Closed)
            {
                while (Volatile.Read(ref c.InboxBytes) > InboxCap && !c.Closed) await Task.Delay(1).ConfigureAwait(false);
                var n = await c.Sock.ReceiveAsync(buf, SocketFlags.None).ConfigureAwait(false);
                if (n == 0) { c.HostEof = true; break; }
                c.Inbox.Enqueue(buf[..n]);
                Interlocked.Add(ref c.InboxBytes, n);
                Signal();
            }
        }
        catch (Exception) { if (!c.Closed) c.HostReset = true; }
        finally { Signal(); c.HalfDone(); }
    }

    private async Task WriteLoop(Conn c)
    {
        try
        {
            await foreach (var chunk in c.Out.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                for (var off = 0; off < chunk.Length;)
                    off += await c.Sock.SendAsync(chunk.AsMemory(off), SocketFlags.None).ConfigureAwait(false);
                Interlocked.Add(ref c.OutPending, -chunk.Length);
                Signal();   // the window may reopen
            }
            if (!c.Closed) c.Sock.Shutdown(SocketShutdown.Send);   // the guest closed: EOF to the host client
        }
        catch (Exception) { if (!c.Closed) { c.HostReset = true; Signal(); } }
        finally { c.HalfDone(); }
    }

    // ---- the simulation thread's turn ----------------------------------------------------------------------------

    /// <summary>Take in the connections and bytes the host sockets delivered, run the timers, send what the guest's
    /// window allows. Simulation thread only.</summary>
    public void Pump()
    {
        if (_disposed) return;
        Volatile.Write(ref _signal, 0);
        var now = Clock();
        while (_accepted.TryDequeue(out var a)) Open(a.L, a.S, now);
        if (_conns.Count == 0) return;
        foreach (var c in Snapshot()) Service(c, now);
    }

    private List<Conn> Snapshot()
    {
        _scratch.Clear();
        _scratch.AddRange(_conns.Values);
        return _scratch;
    }

    private void Open(Listener l, Socket s, double now)
    {
        if (!_listeners.Contains(l) || _conns.Count >= MaxConnections) { Reset(s); Failed++; return; }
        ushort peer = 0;
        try { peer = (ushort)((IPEndPoint)s.RemoteEndPoint!).Port; } catch { /* already gone */ }
        var c = new Conn(l, s, AllocPort(), peer) { Deadline = now + ConnectDeadline, TimerAt = now };
        c.Iss = _nextIss;
        _nextIss += 0x0100_0000;
        c.SndUna = c.SndNxt = c.SndMax = c.Iss;
        _conns[c.Port] = c;
        Accepted++;
        _ = ReadLoop(c);
        _ = WriteLoop(c);
    }

    private ushort AllocPort()
    {
        for (;;)
        {
            var p = (ushort)(FirstPort + _nextPort);
            _nextPort = (ushort)((_nextPort + 1) % PortSpan);
            if (!_conns.ContainsKey(p)) return p;
        }
    }

    private void Service(Conn c, double now)
    {
        bool reset = c.HostReset, eof = c.HostEof;   // read before draining: what they cover is queued already
        while (c.Len < SendBufCap && c.Inbox.TryDequeue(out var chunk))
        {
            Append(c, chunk);
            Interlocked.Add(ref c.InboxBytes, -chunk.Length);
        }
        if (reset)
        {
            if (c.St == State.Established || c.SynSent) SendRst(c);
            Finish(c, reset: true, Event.Close);
            return;
        }
        if (eof && c.Inbox.IsEmpty) c.HostFin = true;

        if (c.St == State.SynSent)
        {
            if (c.HostFin && c.Len == 0)
            {
                // The client gave up (or preconnected and closed) before the guest answered.
                if (c.SynSent) SendRst(c);
                Finish(c, reset: false, Event.Close);
                return;
            }
            if (now >= c.Deadline)
            {
                Failed++;
                if (c.SynSent) SendRst(c);
                Finish(c, reset: true, Event.Refused);
                return;
            }
            if (now >= c.TimerAt)
            {
                if (_net.StationMac() is { } mac)   // no station with a lease yet: wait for it
                {
                    c.Mac = mac;
                    c.SynSent = true;
                    Send(c, SYN, c.Iss, [], withMss: true);
                }
                c.TimerAt = now + c.Rto;
                c.Rto = Math.Min(c.Rto * 2, MaxSynRto);
            }
            return;
        }

        if (c.RtoArmed && now >= c.TimerAt)
        {
            c.RtoArmed = false;
            if (++c.Retries > MaxRetries) { SendRst(c); Finish(c, reset: true, Event.Close); return; }
            c.SndNxt = c.SndUna;            // go back: resend from the oldest unacknowledged byte
            c.FinSent = false;
            c.Rto = Math.Min(c.Rto * 2, MaxRto);
            if (c.GuestWnd == 0) c.Probe = true;
        }
        var w = Window(c);
        if (!c.GuestFin && c.LastAdvWnd < 2 * OurMss && w >= c.LastAdvWnd + 2 * OurMss) c.AckDue = true;   // window update
        TrySend(c, now);
        if (c.AckDue) SendAck(c);
        if (c.GuestFin && c.FinAcked) Finish(c, reset: false, Event.Close);
    }

    private static void Append(Conn c, byte[] chunk)
    {
        if (c.Len + chunk.Length > c.Buf.Length)
            Array.Resize(ref c.Buf, Math.Max(c.Buf.Length * 2, c.Len + chunk.Length));
        Buffer.BlockCopy(chunk, 0, c.Buf, c.Len, chunk.Length);
        c.Len += chunk.Length;
    }

    private static int Window(Conn c) => Math.Clamp(WindowMax - Volatile.Read(ref c.OutPending), 0, WindowMax);

    private void TrySend(Conn c, double now)
    {
        if (c.St != State.Established || c.FinSent) return;
        var sent = (int)(c.SndNxt - c.SndUna);   // bytes in flight, also the offset of the first unsent byte
        var wnd = Math.Min(c.GuestWnd, c.Mss * MaxInFlightSegments);
        if (c.Probe && wnd == 0 && sent < c.Len) wnd = sent + 1;   // zero-window probe: one byte past the edge
        c.Probe = false;
        while (sent < c.Len && sent < wnd)
        {
            var n = Math.Min(Math.Min(c.Mss, c.Len - sent), wnd - sent);
            Send(c, PSH | ACK, c.SndNxt, c.Buf.AsSpan(sent, n));
            var end = c.SndNxt + (uint)n;
            if ((int)(end - c.SndMax) > 0) { ToGuestBytes += (int)(end - c.SndMax); c.SndMax = end; }
            c.SndNxt = end;
            sent += n;
        }
        if (c.HostFin && sent == c.Len && !c.FinAcked)
        {
            Send(c, FIN | ACK, c.SndNxt, []);
            c.FinSeq = c.SndNxt;
            c.FinSeqSet = true;
            c.SndNxt += 1;
            if ((int)(c.SndNxt - c.SndMax) > 0) c.SndMax = c.SndNxt;
            c.FinSent = true;
        }
        if (!c.RtoArmed && (c.SndNxt != c.SndUna || (sent < c.Len && c.GuestWnd == 0)))
        {
            c.RtoArmed = true;
            c.TimerAt = now + c.Rto;
        }
    }

    /// <summary>A TCP segment from the guest. True when it belongs to a forwarded connection (consumed).</summary>
    internal bool HandleGuestSegment(byte[] f, int ipOff, int tcpOff)
    {
        if (_conns.Count == 0) return false;
        var dst = Be16(f, tcpOff + 2);
        if (!_conns.TryGetValue(dst, out var c) || Be16(f, tcpOff) != c.L.GuestPort) return false;
        for (var i = 0; i < 4; i++) if (f[ipOff + 12 + i] != _net.ClientIp[i]) return false;
        var seq = Be32(f, tcpOff + 4);
        var ack = Be32(f, tcpOff + 8);
        var flags = f[tcpOff + 13];
        var win = (int)Be16(f, tcpOff + 14);
        var dataOff = (f[tcpOff + 12] >> 4) * 4;
        var payloadOff = tcpOff + dataOff;
        var payloadLen = Math.Max(0, Math.Min(ipOff + Be16(f, ipOff + 2), f.Length) - payloadOff);
        var now = Clock();

        if ((flags & RST) != 0)
        {
            // Before the handshake: nothing listens on the guest port yet; the SYN timer tries again.
            if (c.St == State.Established) Finish(c, reset: true, Event.Close);
            return true;
        }
        if (c.St == State.SynSent)
        {
            if ((flags & (SYN | ACK)) != (SYN | ACK) || ack != c.Iss + 1) return true;
            c.RcvNxt = seq + 1;
            c.SndUna = c.SndNxt = c.SndMax = c.Iss + 1;
            c.Mss = Math.Min(OurMss, ParseMss(f, tcpOff, dataOff));
            c.GuestWnd = win;
            c.St = State.Established;
            c.Rto = InitialRto;
            c.Retries = 0;
            c.RtoArmed = false;
            SendAck(c);
            OnEvent?.Invoke(Event.Open, c.Info);
            TrySend(c, now);
            return true;
        }
        if ((flags & SYN) != 0) { SendAck(c); return true; }   // a repeated SYN-ACK: our ACK did not land

        if ((flags & ACK) != 0)
        {
            var adv = (int)(ack - c.SndUna);
            if (adv > 0 && (int)(ack - c.SndMax) <= 0)
            {
                var data = Math.Min(adv, c.Len);
                Buffer.BlockCopy(c.Buf, data, c.Buf, 0, c.Len - data);
                c.Len -= data;
                c.SndUna = ack;
                if ((int)(c.SndNxt - ack) < 0) c.SndNxt = ack;
                if (c.FinSeqSet && c.Len == 0 && ack == c.FinSeq + 1) c.FinAcked = true;
                c.Retries = 0;
                c.Rto = InitialRto;
                c.RtoArmed = false;
            }
            c.GuestWnd = win;
        }
        if (payloadLen > 0)
        {
            if (seq == c.RcvNxt && !c.GuestFin)
            {
                c.RcvNxt += (uint)payloadLen;
                FromGuestBytes += payloadLen;
                Interlocked.Add(ref c.OutPending, payloadLen);
                c.Out.Writer.TryWrite(f[payloadOff..(payloadOff + payloadLen)]);
            }
            DelayAck(c);   // in order: ACK; anything else: a duplicate ACK
        }
        if ((flags & FIN) != 0)
        {
            if (!c.GuestFin && seq + (uint)payloadLen == c.RcvNxt)
            {
                c.RcvNxt += 1;
                c.GuestFin = true;
                c.Out.Writer.TryComplete();   // the writer flushes, then shuts the host socket's send side
            }
            DelayAck(c);
        }
        TrySend(c, now);
        if (c.GuestFin && c.FinAcked) Finish(c, reset: false, Event.Close);
        return true;
    }

    private static int ParseMss(byte[] f, int tcpOff, int dataOff)
    {
        for (var o = tcpOff + 20; o < tcpOff + dataOff && o < f.Length;)
        {
            var kind = f[o];
            if (kind == 0) break;
            if (kind == 1) { o++; continue; }
            if (o + 1 >= f.Length) break;
            var len = f[o + 1];
            if (len < 2) break;
            if (kind == 2 && len == 4 && o + 3 < f.Length) return Math.Max(64, (int)Be16(f, o + 2));
            o += len;
        }
        return DefaultMss;
    }

    private void Finish(Conn c, bool reset, Event ev)
    {
        if (!_conns.Remove(c.Port)) return;
        if (reset) c.Abort();
        else c.Out.Writer.TryComplete();   // graceful: the tasks flush and close the socket
        OnEvent?.Invoke(ev, c.Info);
    }

    private void SendAck(Conn c) => Send(c, ACK, c.SndNxt, []);

    // The guest's data is acknowledged at the next pump, not from inside its transmission: the cyw43 driver drops any
    // Ethernet frame it reads while it waits for bus credit to send (cyw43_sdpcm_send_common), so an ACK answered in the
    // middle of a burst of segments would mostly be lost and the guest would sit out its retransmission timeout.
    private void DelayAck(Conn c)
    {
        c.AckDue = true;
        Signal();
    }

    private void SendRst(Conn c) => Send(c, RST | ACK, c.St == State.Established ? c.SndNxt : c.Iss + 1, []);

    private void Send(Conn c, byte flags, uint seq, ReadOnlySpan<byte> payload, bool withMss = false)
    {
        if (c.Mac is null) return;
        var hl = withMss ? 24 : 20;
        var tcp = new byte[hl + payload.Length];
        tcp[0] = (byte)(c.Port >> 8); tcp[1] = (byte)c.Port;
        tcp[2] = (byte)(c.L.GuestPort >> 8); tcp[3] = (byte)c.L.GuestPort;
        WriteBe32(tcp, 4, seq);
        WriteBe32(tcp, 8, (flags & ACK) != 0 ? c.RcvNxt : 0);
        tcp[12] = (byte)((hl / 4) << 4);
        tcp[13] = flags;
        var w = (flags & SYN) != 0 ? WindowMax : Window(c);
        tcp[14] = (byte)(w >> 8); tcp[15] = (byte)w;
        if (withMss) { tcp[20] = 2; tcp[21] = 4; tcp[22] = OurMss >> 8; tcp[23] = OurMss & 0xFF; }
        payload.CopyTo(tcp.AsSpan(hl));
        c.LastAdvWnd = w;
        if ((flags & ACK) != 0) c.AckDue = false;
        _net.SendToStation(c.Mac, tcp);
    }

    private static ushort Be16(byte[] b, int o) => (ushort)(b[o] << 8 | b[o + 1]);
    private static uint Be32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
    private static void WriteBe32(byte[] b, int o, uint v)
    { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    /// <summary>Close every listener and reset every connection: the ports are free again when this returns.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var l in _listeners) try { l.Sock.Dispose(); } catch { /* gone */ }
        _listeners.Clear();
        foreach (var c in _conns.Values) c.Abort();
        _conns.Clear();
        while (_accepted.TryDequeue(out var a)) Reset(a.S);
    }
}
