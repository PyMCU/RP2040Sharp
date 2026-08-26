// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona

using RP2040.TestKit;
using RP2040.TestKit.Boards;

namespace RP2040Sharp.Wireless.Cyw43;

/// <summary>
/// A Raspberry Pi Pico W: a <see cref="PicoSimulation"/> with the CYW43439 radio already wired to it.
///
/// <para>Bringing the radio up by hand takes four steps that are easy to get wrong and fail silently
/// when missed — the PIO must read pad levels rather than SIO's, pad changes must be published so the
/// gSPI slave sees the bit-banged clock, the chip must be constructed against IO_BANK0, and the board
/// must be joined to a network. Getting any of them wrong leaves the guest stuck in cyw43_init with no
/// diagnostic. This does all four.</para>
///
/// <example><code>
/// using var net = new VirtualNet();
/// using var board = PicoWSimulation.Create(firmware, net);
/// board.OfferAp("HomeNet", password: "hunter2");
/// </code></example>
/// </summary>
public sealed class PicoWSimulation : IDisposable
{
    /// <summary>The board itself — CPU, peripherals, UART/USB probes.</summary>
    public PicoSimulation Board { get; }

    /// <summary>The emulated CYW43439 attached to this board.</summary>
    public Cyw43439Device Radio { get; }

    /// <summary>The radio's WLAN/BT command layer (scan results, join state, packet queues).</summary>
    public Sdpcm Wifi => Radio.Sdpcm;

    private PicoWSimulation(PicoSimulation board, Cyw43439Device radio)
    {
        Board = board;
        Radio = radio;
    }

    /// <summary>
    /// Build a Pico W running <paramref name="flashImage"/>, optionally joined to <paramref name="net"/>
    /// (the same instance for several boards puts them on one LAN, each with its own MAC and lease).
    /// </summary>
    public static PicoWSimulation Create(ReadOnlySpan<byte> flashImage, VirtualNet? net = null)
    {
        var board = new PicoSimulation();
        board.LoadFlash(flashImage);

        // The gSPI lines are bit-banged by the PIO and sampled off the pads, so both have to look at
        // IO_BANK0 rather than at SIO, and every pad change has to be announced.
        board.Rp2040.Pio0.ReadGpioIn = () => board.Rp2040.IoBank0.GetInputWord();
        board.Rp2040.Pio1.ReadGpioIn = () => board.Rp2040.IoBank0.GetInputWord();
        board.Rp2040.Sio.OnGpioChanged += mask => board.Rp2040.IoBank0.NotifyPads(mask);

        var radio = new Cyw43439Device(board.Rp2040.IoBank0);
        net?.AddDevice(radio.Sdpcm);
        return new PicoWSimulation(board, radio);
    }

    /// <summary>Put an access point in this board's air so its scans and joins can find it.</summary>
    public PicoWSimulation OfferAp(string ssid, string? password = null, int channel = 6, int rssi = -55)
    {
        Wifi.VisibleAps.Add(new Sdpcm.VirtualAp(ssid, [0x02, 0, 0x5E, 0, 4, (byte)Wifi.VisibleAps.Count],
            channel, rssi, Secured: password is not null, Passphrase: password));
        return this;
    }

    /// <summary>
    /// Run the board for one scheduling slice. Slices are short while the core is executing and longer
    /// while it sits in WFE, so a fleet spends its budget where work is actually happening.
    /// </summary>
    public void Step(int instructions = 512) =>
        Board.Rp2040.Run(Board.Rp2040.Core0Waiting ? instructions * 3 : instructions);

    public void Dispose() => Board.Dispose();
}
