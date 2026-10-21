# Wi-Fi behind the C ABI (Pico W)

A chip created with `ST_RP2040_CYW43` (`st_rp2040_create_ex`) has the Pico W's CYW43439 gSPI host interface on
GPIO23/24/25/29, translated into a **virtual network**: one access point the host configures
(`st_rp2040_wifi_set_ap`), and a gateway at `192.168.4.1/24` with ARP, DHCP (the station gets `192.168.4.2`) and DNS.
The radio firmware the guest downloads is parked, never executed: there is no RF and no chip internals. The guest's
own driver and lwIP run unmodified. The join state, the status struct and the events are described in
[native-abi.md](native-abi.md#wi-fi-pico-w); the model, the compatibility rules (the struct only grows at the end,
enums are open, new behaviour comes as new exports) and the plans in RP2350Sharp's `docs/wireless.md`, which this
core follows value for value with `st_rp2040_` names.

## Port forwarding (W2)

`st_rp2040_wifi_forward_tcp(chip, 8080, 80)` makes the library listen on `127.0.0.1:8080` and relay every connection
accepted there to `192.168.4.2:80`, the way a home router's port forward does: a browser on the PC opens
`http://127.0.0.1:8080` and talks to a MicroPython (socket or Microdot) server in the guest. The guest sees the
connections come from the gateway, `192.168.4.1`.

| Call | Does |
|---|---|
| `int st_rp2040_wifi_forward_tcp(chip, host_port, guest_port)` | Listens on `127.0.0.1:host_port` and relays each connection to the station's `guest_port`. `host_port` 0: a free port. `guest_port` 0 removes the forward of `host_port` (its connections are reset). Returns the port listened on (1..65535), 0 for a removal, or -1 with `last_error`: no radio, a port out of range, already forwarded on this chip, in use by another program, more than 16 forwards. |
| `int st_rp2040_wifi_poll(chip)` | Moves what the host sockets delivered (connections, bytes, closes) into the guest now, runs the forwarding timers, then delivers the Wi-Fi events. Returns the forwarded connections open, or -1. Not from inside a callback. |

The status struct grew at its end (offset 112; a library older than this fills `size` = 112): `tcp_forwards`,
`tcp_open` (accepted and not finished), `tcp_accepted`, `tcp_failed` (refused), `tcp_to_guest`, `tcp_from_guest`
(payload bytes), and `tcp_host_port`, `tcp_guest_port`, `tcp_peer_port`: the connection of the last TCP event (the
forward, and the client's own port). New events: `ST_RP2040_WIFI_EV_TCP_OPEN` (a connection reached the guest's server),
`ST_RP2040_WIFI_EV_TCP_CLOSE` (it ended, closed by either side or reset) and `ST_RP2040_WIFI_EV_TCP_REFUSED` (given up: nothing listened on the
guest port within 10 s of simulated time). They are delivered like the others, after the run call or the poll.

- **Loopback only.** The listener binds `127.0.0.1`, never `0.0.0.0`: nothing outside the PC can reach the guest.
- **The guest side is ordinary TCP** that the gateway opens as a client (`TcpForwarder`, `VirtualNet.Forwarder`):
  SYN with an MSS option, retried while nothing listens yet (MicroPython answers RST until its server reaches
  `listen()`; a connection waits up to 10 s of simulated time, then is reset and counted in `tcp_failed`), the
  guest's MSS and window respected, at most 8 segments in flight, go-back-N retransmission on a timeout counted in
  simulated time (50 ms, doubling to 2 s), zero-window probes, FIN both ways with half-close (a client that sends
  its request and shuts its side still gets the answer), RST both ways. Several connections at once (a browser
  opens 2 to 6), up to 64 per chip.
- **Threads.** The socket I/O (accept, receive, send) runs on background tasks that only fill or drain queues. The
  guest side, every TCP state change and every frame into the guest, runs on the caller's thread: in
  `st_rp2040_wifi_poll`, or in the run calls, which pump the forwards by themselves at most once per millisecond of host
  time (a chip without forwards pays one null check, so the idle fast paths are untouched). Proteus calls
  `wifi_poll` once per step. A frame into the guest raises the host-wake line (GPIO24), which wakes a sleeping core
  through its GPIO interrupt, as any received frame does.
- **ACKs at the next pump.** The guest's data is acknowledged when the forwards are next pumped, not from inside
  the guest's transmission: the cyw43 driver drops any frame that arrives while it waits for SDPCM bus credit, and
  reads it into the buffer of the frame it is about to send. For the same reason the virtual network now grants bus
  credit 16 packets ahead (it was 4: about one segment in eight of a 20 kB response was lost that way, each costing
  the guest a retransmission timeout).
- **Lifetime.** `st_rp2040_destroy` closes the listeners and resets the connections: the port is free again when it
  returns (a Proteus restart reuses it).

Measured with MicroPython v1.28.0 driven as Proteus drives it (`run_until_edge(30000, 75)` + `wifi_poll`) on the
development machine: a GET answered by a socket server in 5 to 10 ms of wall time, three parallel GETs in about
0.1 s, a 20 kB response in about 0.1 s wall; from C through the NativeAOT library about 10 ms per request.

Tests: `tests/RP2040Sharp.Native.Tests/PicoWForwardTests.cs` (a real `HttpClient` / `TcpClient` on 127.0.0.1 while the test thread runs the chip: sequential and
three parallel GETs, a POST that switches GP15 seen by the pin callback, a 20 kB response, a 6 kB request body, close
and reopen, counters and events, the port free after destroy, argument checks) and `abi_selftest.c` (the same from C
with a socket of the test process).

## Outbound bridges (W3)

`st_rp2040_wifi_bridge_tcp(chip, 1883, "127.0.0.1", 1883)` relays every connection the guest opens to the gateway's port
1883 (`192.168.4.1:1883`) to `127.0.0.1:1883` on the PC, the way a NAT router relays a station's connection: a
MicroPython `umqtt.simple` client on the board talks to a Mosquitto (or any) broker on the PC.
`st_rp2040_wifi_dns_add(chip, "broker.local", "192.168.4.1")` gives the gateway's DNS a name for it, so the firmware can
dial the bridge by name.

| Call | Does |
|---|---|
| `int st_rp2040_wifi_bridge_tcp(chip, gateway_port, host, port)` | Relays the guest's connections to `192.168.4.1:gateway_port` to `host:port`. `host`: a name or an IPv4 / IPv6 literal (brackets allowed around IPv6), at most 253 bytes, resolved by the PC at each connection. Ports 1..65535; `port` 0 removes the bridge of `gateway_port` (its connections are reset; `host` is then ignored and may be NULL). Returns 0, or -1 with `last_error`: no radio, a port out of range, a bad host, already bridged on this chip, not bridged (a removal), more than 16 bridges. |
| `int st_rp2040_wifi_dns_add(chip, name, ipv4)` | The gateway's DNS answers `name` with `ipv4` (a dotted quad); `ipv4` NULL or `""` removes the name. `name`: 1..253 bytes of letters, digits, `-`, `_` and `.`, labels of 1..63, compared without case, a trailing `.` dropped. Adding a name again replaces its address; at most 64 names. Returns 0, or -1 with `last_error`. |

- **The guest reaches only what the host configures.** A gateway port is relayed to its one `host:port`; nothing the
  guest sends chooses the destination, segments to any address other than `192.168.4.1` never reach a bridge, and the
  rest of the PC's network stays out of reach as before. Without a bridge nothing changes.
- **Handshake.** The guest's SYN starts the host connection (non-blocking, checked at every pump, so it never waits on
  the thread pool; only a name is resolved on a background task) and is answered with a SYN-ACK only once that
  connection is up, so the guest's `connect()` succeeds when its peer is really there. When it cannot be
  (refused, no answer within 10 s of host time, a name the PC cannot resolve, 64 bridged connections open) the guest
  gets a RST|ACK and its `connect()` fails at once (MicroPython: `OSError: ECONNRESET`), counted in `bridge_failed`
  with the reason in `bridge_error` and `ST_RP2040_WIFI_EV_BRIDGE_FAILED`. To a host on this PC (a loopback address) no SYN is
  retried, so a closed port is refused in milliseconds instead of Windows' 2 s of retries, by which time a guest that
  runs faster than real time may have given up by itself.
- **Past the handshake** a bridged connection is the same TCP as a forward's (`TcpForwarder`): the guest's MSS and
  window, at most 8 segments in flight, go-back-N retransmission on simulated time, zero-window probes (a guest that
  answers them with a closed window is kept, however long it does not read), FIN both ways with half-close, RST
  both ways, ACKs at the next pump, up to 64 bridged connections per chip.
- **Pumped with the forwards**: by `st_rp2040_wifi_poll` and by the run calls at most once per millisecond of host time;
  a chip without forwards or bridges still pays one null check. `st_rp2040_destroy` resets the bridged connections.
- **DNS and mDNS.** lwIP sends the names ending in `.local` to mDNS (`224.0.0.251:5353`) instead of the DNS server;
  the gateway answers both for the names added (an mDNS query for another name gets no answer, as on a LAN without
  that host; a DNS query gets NXDOMAIN, as before).
- **Status** (the struct grew at its end to 208 bytes; a library older than this fills `size` = 160): `bridges`,
  `bridge_open` (opened by the guest and not finished, including those still connecting), `bridge_connected`,
  `bridge_failed`, `bridge_to_host`, `bridge_to_guest` (payload bytes), and `bridge_gateway_port`, `bridge_host_port`,
  `bridge_guest_port` (the connection of the last bridge event) and `bridge_error`
  (`ST_RP2040_WIFI_BRIDGE_ERR_REFUSED` 1, `_UNREACHABLE` 2, `_NO_HOST` 3, `_FULL` 4). Events: `ST_RP2040_WIFI_EV_BRIDGE_OPEN` (10: the
  relay is up both ways), `ST_RP2040_WIFI_EV_BRIDGE_CLOSE` (11: it ended, by either side or reset; none for a connection that
  never opened) and `ST_RP2040_WIFI_EV_BRIDGE_FAILED` (12).
- `VirtualNet.BridgeTcp` (the managed API, several stations on one LAN) now goes through the same relay; it used to
  connect synchronously on the guest's transmission and relay without retransmission.

Tests: `tests/RP2040Sharp.Native.Tests/PicoWBridgeTests.cs` (MicroPython v1.28.0 `RPI_PICO_W` run as Proteus runs it: an 8000-byte echo both ways with a real
TCP echo server on 127.0.0.1 reached by a DNS name, a refused host port, NXDOMAIN; `umqtt.simple` from micropython-lib,
pinned to commit 5e49b1bd41d312d9d2a8e4f19d7bd6a918896abc, connecting to `broker.local:1883`, publishing telemetry to an
MQTT 3.1.1 broker of the test on 127.0.0.1, subscribing, and switching GP15 on a message the test publishes; argument
checks, the 208-byte layout and a 160-byte caller) and `abi_selftest.c` (the same from C: an echo server of the test
process and a refused port).
