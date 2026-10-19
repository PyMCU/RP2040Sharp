using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SiliconTwin.Native;

namespace RP2040Sharp.Native.Tests;

/// <summary>
/// The st_rp2040_* exports called the way a C host calls them: through unmanaged function pointers, with
/// unmanaged callbacks. Everything a test does through this class crosses the same boundary as Proteus.
/// </summary>
internal static unsafe class Abi
{
    /// <summary>st_rp2040_create_ex flags: ST_RP2040_BUS_I2C / SPI / UART / ALL and ST_RP2040_CYW43.</summary>
    public const uint BusI2c = 1, BusSpi = 2, BusUart = 4, BusAll = 7, Cyw43 = 0x100;

    public static string Version() => Marshal.PtrToStringAnsi((nint)((delegate* unmanaged[Cdecl]<byte*>)&Exports.Version)())!;
    public static uint ClockHz() => ((delegate* unmanaged[Cdecl]<uint>)&Exports.ClockHz)();
    public static nint Create(uint flash) => ((delegate* unmanaged[Cdecl]<uint, nint>)&Exports.Create)(flash);
    public static nint CreateEx(uint flash, uint flags) => ((delegate* unmanaged[Cdecl]<uint, uint, nint>)&Exports.CreateEx)(flash, flags);
    public static void Destroy(nint h) => ((delegate* unmanaged[Cdecl]<nint, void>)&Exports.Destroy)(h);
    public static int Status(nint h) => ((delegate* unmanaged[Cdecl]<nint, int>)&Exports.Status)(h);
    public static string LastError(nint h) => Marshal.PtrToStringAnsi((nint)((delegate* unmanaged[Cdecl]<nint, byte*>)&Exports.LastError)(h))!;

    public static int LoadUf2(nint h, byte[]? data)
    {
        var f = (delegate* unmanaged[Cdecl]<nint, byte*, nuint, int>)&Exports.LoadUf2;
        if (data is null) return f(h, null, 0);
        fixed (byte* p = data) return f(h, p, (nuint)data.Length);
    }

    public static ulong Run(nint h, uint n) => ((delegate* unmanaged[Cdecl]<nint, uint, ulong>)&Exports.Run)(h, n);
    public static ulong RunUntilEdge(nint h, uint max, uint guard, uint watch) =>
        ((delegate* unmanaged[Cdecl]<nint, uint, uint, uint, ulong>)&Exports.RunUntilEdge)(h, max, guard, watch);
    public static ulong CyclesUntilEdge(nint h, uint watch) => ((delegate* unmanaged[Cdecl]<nint, uint, ulong>)&Exports.CyclesUntilEdge)(h, watch);
    public static ulong Cycles(nint h) => ((delegate* unmanaged[Cdecl]<nint, ulong>)&Exports.Cycles)(h);
    public static ulong TimePs(nint h) => ((delegate* unmanaged[Cdecl]<nint, ulong>)&Exports.TimePs)(h);
    public static ulong CycleToPs(nint h, ulong c) => ((delegate* unmanaged[Cdecl]<nint, ulong, ulong>)&Exports.CycleToPs)(h, c);
    public static uint ClkSysHz(nint h) => ((delegate* unmanaged[Cdecl]<nint, uint>)&Exports.ClkSysHz)(h);
    public static ulong Instructions(nint h, int core) => ((delegate* unmanaged[Cdecl]<nint, int, ulong>)&Exports.Instructions)(h, core);

    public static int PinGet(nint h, int gpio, out int oe)
    {
        int o = 7;
        int r = ((delegate* unmanaged[Cdecl]<nint, int, int*, int>)&Exports.PinGet)(h, gpio, &o);
        oe = o;
        return r;
    }

    public static int PinGetPull(nint h, int gpio) => ((delegate* unmanaged[Cdecl]<nint, int, int>)&Exports.PinGetPull)(h, gpio);
    public static void PinSetInput(nint h, int gpio, int level) => ((delegate* unmanaged[Cdecl]<nint, int, int, void>)&Exports.PinSetInput)(h, gpio, level);
    public static void AdcSetVoltage(nint h, int ch, double v) => ((delegate* unmanaged[Cdecl]<nint, int, double, void>)&Exports.AdcSetVoltage)(h, ch, v);

    public static void SerialWrite(nint h, int channel, byte[]? data, int len = -1)
    {
        var f = (delegate* unmanaged[Cdecl]<nint, int, byte*, nuint, void>)&Exports.SerialWrite;
        if (data is null) { f(h, channel, null, (nuint)Math.Max(len, 0)); return; }
        fixed (byte* p = data) f(h, channel, p, (nuint)(len < 0 ? data.Length : len));
    }

    public static void SerialWrite(nint h, int channel, string text) => SerialWrite(h, channel, Encoding.ASCII.GetBytes(text));

#if ST_ENABLE_TEST_HOOKS
    public static int TestInjectFault(nint h, int mode) => ((delegate* unmanaged[Cdecl]<nint, int, int>)&Exports.TestInjectFault)(h, mode);
#endif

    // ---- callbacks ---------------------------------------------------------------------------

    /// <summary>Installs <paramref name="host"/> as the pin and serial callback of <paramref name="h"/>.</summary>
    public static void Attach(nint h, Host host)
    {
        var user = GCHandle.ToIntPtr(host.Handle);
        ((delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, int, int, int, ulong, void>, nint, void>)&Exports.SetPinCallback)(h, &OnPin, user);
        ((delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, int, byte*, nuint, void>, nint, void>)&Exports.SetSerialCallback)(h, &OnSerial, user);
    }

    public static void DetachPin(nint h) =>
        ((delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, int, int, int, ulong, void>, nint, void>)&Exports.SetPinCallback)(h, null, 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPin(nint user, int gpio, int level, int oe, ulong cycle)
    {
        var host = (Host)GCHandle.FromIntPtr(user).Target!;
        host.Pins.Add((gpio, level, oe, cycle));
        host.OnPinHook?.Invoke(gpio, level, oe, cycle);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSerial(nint user, int channel, byte* data, nuint len)
    {
        var host = (Host)GCHandle.FromIntPtr(user).Target!;
        var text = Encoding.Latin1.GetString(data, (int)len);
        host.Serial[channel].Append(text);
        if (host.Chip != 0) host.SerialLog.Add((Cycles(host.Chip), channel, text));
    }
}

/// <summary>What a host sees through the callbacks.</summary>
internal sealed class Host : IDisposable
{
    public GCHandle Handle;
    public readonly List<(int Gpio, int Level, int Oe, ulong Cycle)> Pins = [];
    public readonly StringBuilder[] Serial = [new(), new(), new()];
    public Action<int, int, int, ulong>? OnPinHook;

    /// <summary>When set, every serial chunk is also logged with the chip's cycle count when it arrived.</summary>
    public nint Chip;
    public readonly List<(ulong Cycle, int Channel, string Data)> SerialLog = [];

    public Host() => Handle = GCHandle.Alloc(this);

    public string Cdc => Serial[2].ToString();

    public void Dispose() { if (Handle.IsAllocated) Handle.Free(); }
}
