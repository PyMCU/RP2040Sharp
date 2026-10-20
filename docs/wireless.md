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
