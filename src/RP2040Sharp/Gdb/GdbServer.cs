// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona
// Portions derived from rp2040js — Copyright (c) 2021 Uri Shaked (MIT).
// See NOTICE.txt.
using System.Globalization;
using RP2040.Core.Cpu;
using static RP2040.Gdb.GdbUtils;

namespace RP2040.Gdb;

/// <summary>
/// RP2040 GDB Remote Serial Protocol server. Ported from rp2040js
/// (src/gdb/gdb-server.ts © 2021 Uri Shaked). Debugs Core 0.
/// </summary>
public class GdbServer
{
    public const string StopReplySigint = "S02";
    public const string StopReplyTrap   = "S05";

    // SYSM values for MRS/MSR special registers (ARMv6-M).
    private const uint SysmMsp     = 8;
    private const uint SysmPsp     = 9;
    private const uint SysmPrimask = 16;
    private const uint SysmControl = 20;

    /* string value: armv6m-none-unknown-eabi */
    private const string LldbTriple = "61726d76366d2d6e6f6e652d756e6b6e6f776e2d65616269";

    private static readonly string[] RegisterInfo =
    [
        "name:r0;bitsize:32;offset:0;encoding:int;format:hex;set:General Purpose Registers;generic:arg1;gcc:0;dwarf:0;",
        "name:r1;bitsize:32;offset:4;encoding:int;format:hex;set:General Purpose Registers;generic:arg2;gcc:1;dwarf:1;",
        "name:r2;bitsize:32;offset:8;encoding:int;format:hex;set:General Purpose Registers;generic:arg3;gcc:2;dwarf:2;",
        "name:r3;bitsize:32;offset:12;encoding:int;format:hex;set:General Purpose Registers;generic:arg4;gcc:3;dwarf:3;",
        "name:r4;bitsize:32;offset:16;encoding:int;format:hex;set:General Purpose Registers;gcc:4;dwarf:4;",
        "name:r5;bitsize:32;offset:20;encoding:int;format:hex;set:General Purpose Registers;gcc:5;dwarf:5;",
        "name:r6;bitsize:32;offset:24;encoding:int;format:hex;set:General Purpose Registers;gcc:6;dwarf:6;",
        "name:r7;bitsize:32;offset:28;encoding:int;format:hex;set:General Purpose Registers;gcc:7;dwarf:7;",
        "name:r8;bitsize:32;offset:32;encoding:int;format:hex;set:General Purpose Registers;gcc:8;dwarf:8;",
        "name:r9;bitsize:32;offset:36;encoding:int;format:hex;set:General Purpose Registers;gcc:9;dwarf:9;",
        "name:r10;bitsize:32;offset:40;encoding:int;format:hex;set:General Purpose Registers;gcc:10;dwarf:10;",
        "name:r11;bitsize:32;offset:44;encoding:int;format:hex;set:General Purpose Registers;generic:fp;gcc:11;dwarf:11;",
        "name:r12;bitsize:32;offset:48;encoding:int;format:hex;set:General Purpose Registers;gcc:12;dwarf:12;",
        "name:sp;bitsize:32;offset:52;encoding:int;format:hex;set:General Purpose Registers;generic:sp;alt-name:r13;gcc:13;dwarf:13;",
        "name:lr;bitsize:32;offset:56;encoding:int;format:hex;set:General Purpose Registers;generic:ra;alt-name:r14;gcc:14;dwarf:14;",
        "name:pc;bitsize:32;offset:60;encoding:int;format:hex;set:General Purpose Registers;generic:pc;alt-name:r15;gcc:15;dwarf:15;",
        "name:cpsr;bitsize:32;offset:64;encoding:int;format:hex;set:General Purpose Registers;generic:flags;alt-name:psr;gcc:16;dwarf:16;",
    ];

    private const string TargetXml = """
<?xml version="1.0"?>
<!DOCTYPE target SYSTEM "gdb-target.dtd">
<target version="1.0">
<architecture>arm</architecture>
<feature name="org.gnu.gdb.arm.m-profile">
<reg name="r0" bitsize="32" regnum="0" save-restore="yes" type="int" group="general"/>
<reg name="r1" bitsize="32" regnum="1" save-restore="yes" type="int" group="general"/>
<reg name="r2" bitsize="32" regnum="2" save-restore="yes" type="int" group="general"/>
<reg name="r3" bitsize="32" regnum="3" save-restore="yes" type="int" group="general"/>
<reg name="r4" bitsize="32" regnum="4" save-restore="yes" type="int" group="general"/>
<reg name="r5" bitsize="32" regnum="5" save-restore="yes" type="int" group="general"/>
<reg name="r6" bitsize="32" regnum="6" save-restore="yes" type="int" group="general"/>
<reg name="r7" bitsize="32" regnum="7" save-restore="yes" type="int" group="general"/>
<reg name="r8" bitsize="32" regnum="8" save-restore="yes" type="int" group="general"/>
<reg name="r9" bitsize="32" regnum="9" save-restore="yes" type="int" group="general"/>
<reg name="r10" bitsize="32" regnum="10" save-restore="yes" type="int" group="general"/>
<reg name="r11" bitsize="32" regnum="11" save-restore="yes" type="int" group="general"/>
<reg name="r12" bitsize="32" regnum="12" save-restore="yes" type="int" group="general"/>
<reg name="sp" bitsize="32" regnum="13" save-restore="yes" type="data_ptr" group="general"/>
<reg name="lr" bitsize="32" regnum="14" save-restore="yes" type="int" group="general"/>
<reg name="pc" bitsize="32" regnum="15" save-restore="yes" type="code_ptr" group="general"/>
<reg name="xPSR" bitsize="32" regnum="16" save-restore="yes" type="int" group="general"/>
</feature>
<feature name="org.gnu.gdb.arm.m-system">
<reg name="msp" bitsize="32" regnum="17" save-restore="yes" type="data_ptr" group="system"/>
<reg name="psp" bitsize="32" regnum="18" save-restore="yes" type="data_ptr" group="system"/>
<reg name="primask" bitsize="1" regnum="19" save-restore="yes" type="int8" group="system"/>
<reg name="basepri" bitsize="8" regnum="20" save-restore="yes" type="int8" group="system"/>
<reg name="faultmask" bitsize="1" regnum="21" save-restore="yes" type="int8" group="system"/>
<reg name="control" bitsize="2" regnum="22" save-restore="yes" type="int8" group="system"/>
</feature>
</target>
""";

    public readonly IGdbTarget Target;
    private readonly HashSet<GdbConnection> _connections = [];
    private readonly HashSet<uint> _breakpoints = [];

    /// <summary>
    /// Addresses armed by GDB through <c>Z0</c>/<c>Z1</c> packets.
    ///
    /// Both software and hardware breakpoint types map to the same set: on an emulator there
    /// is no reason to patch a <c>BKPT</c> into memory, and not patching means breakpoints
    /// work in XIP flash and cost nothing to remove. The execution loop is what enforces
    /// them — it must compare PC against this set <b>before</b> each instruction and call
    /// <see cref="ReportBreakpointHit"/> on a match, which is what gives a breakpoint its
    /// defining semantics: stop before the instruction executes, not after.
    ///
    /// Empty in the common case, so a loop can check <c>Count == 0</c> and stay on its fast path.
    /// </summary>
    public IReadOnlySet<uint> Breakpoints => _breakpoints;

    public GdbServer(IGdbTarget target) => Target = target;

    private CortexM0Plus Core => Target.Machine.Cpu;

    public string? ProcessGdbMessage(string cmd)
    {
        var core = Core;

        if (cmd == "Hg0")
            return GdbMessage("OK");

        switch (cmd[0])
        {
            case '?':
                return GdbMessage(StopReplyTrap);

            case 'q':
                if (cmd.StartsWith("qSupported:"))
                    return GdbMessage("PacketSize=4000;vContSupported+;qXfer:features:read+");
                if (cmd == "qAttached")
                    return GdbMessage("1");
                if (cmd.StartsWith("qXfer:features:read:target.xml"))
                    return GdbMessage("l" + TargetXml);
                if (cmd.StartsWith("qRegisterInfo"))
                {
                    var index = Convert.ToInt32(cmd[13..], 16);
                    return index >= 0 && index < RegisterInfo.Length
                        ? GdbMessage(RegisterInfo[index])
                        : GdbMessage("E45");
                }
                if (cmd == "qHostInfo")
                    return GdbMessage($"triple:{LldbTriple};endian:little;ptrsize:4;");
                if (cmd == "qProcessInfo")
                    return GdbMessage("pid:1;endian:little;ptrsize:4;");
                return GdbMessage("");

            case 'v':
                if (cmd == "vCont?")
                    return GdbMessage("vCont;c;C;s;S");
                if (cmd.StartsWith("vCont;c"))
                {
                    if (!Target.Executing)
                        Target.Execute();
                    return null;
                }
                if (cmd.StartsWith("vCont;s"))
                {
                    core.Step();
                    var status = new List<string>(17);
                    for (var i = 0; i < 17; i++)
                    {
                        var value = i == 16 ? core.Registers.GetxPsr() : core.Registers[i];
                        status.Add($"{EncodeHexByte((byte)i)}:{EncodeHexUint32(value)}");
                    }
                    return GdbMessage($"T05{string.Join(';', status)};reason:trace;");
                }
                break;

            case 'c':
                if (!Target.Executing)
                    Target.Execute();
                return GdbMessage("OK");

            case 'D':
                // Detach: the debugger is leaving, so drop its breakpoints and resume freely.
                _breakpoints.Clear();
                if (!Target.Executing)
                    Target.Execute();
                return GdbMessage("OK");

            case 'Z':
            case 'z':
            {
                // Z/z <type>,<addr>,<kind> — type 0 = software bp, 1 = hardware bp,
                // 2/3/4 = write/read/access watchpoints. Watchpoints would need a hook on
                // every bus access, so they stay unimplemented; the empty reply is the
                // protocol's "unsupported", which makes GDB fall back on its own scheme
                // rather than believe a lie.
                var fields = cmd[1..].Split(',');
                if (fields.Length < 2)
                    return GdbMessage("E22");
                if (fields[0] is not ("0" or "1"))
                    return GdbMessage("");
                if (!uint.TryParse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bpAddress))
                    return GdbMessage("E22");

                if (cmd[0] == 'Z')
                    _breakpoints.Add(bpAddress);
                else
                    _breakpoints.Remove(bpAddress);
                return GdbMessage("OK");
            }

            case 'g':
            {
                Span<byte> buf = stackalloc byte[17 * 4];
                for (var i = 0; i < 16; i++)
                    WriteUint32Le(buf[(i * 4)..], core.Registers[i]);
                WriteUint32Le(buf[(16 * 4)..], core.Registers.GetxPsr());
                return GdbMessage(EncodeHexBuf(buf));
            }

            case 'p':
            {
                var registerIndex = Convert.ToInt32(cmd[1..], 16);
                if (registerIndex is >= 0 and <= 15)
                    return GdbMessage(EncodeHexUint32(core.Registers[registerIndex]));
                switch (registerIndex)
                {
                    case 0x10: return GdbMessage(EncodeHexUint32(core.Registers.GetxPsr()));
                    case 0x11: return GdbMessage(EncodeHexUint32(ReadSpecial(SysmMsp)));
                    case 0x12: return GdbMessage(EncodeHexUint32(ReadSpecial(SysmPsp)));
                    case 0x13: return GdbMessage(EncodeHexUint32(ReadSpecial(SysmPrimask)));
                    case 0x14: return GdbMessage(EncodeHexUint32(0)); // TODO BASEPRI
                    case 0x15: return GdbMessage(EncodeHexUint32(0)); // TODO faultmask
                    case 0x16: return GdbMessage(EncodeHexUint32(ReadSpecial(SysmControl)));
                }
                break;
            }

            case 'P':
            {
                var parts = cmd[1..].Split('=');
                var registerIndex = Convert.ToInt32(parts[0], 16);
                var registerValue = parts[1].Trim();
                var registerBytes = registerIndex > 0x12 ? 1 : 4;
                var decoded = DecodeHexBuf(registerValue);
                if (registerIndex is < 0 or > 0x16 || decoded.Length != registerBytes)
                    return GdbMessage("E00");

                uint value = 0;
                for (var i = 0; i < decoded.Length && i < 4; i++)
                    value |= (uint)decoded[i] << (i * 8);

                switch (registerIndex)
                {
                    case 0x10: core.Registers.SetxPsr(value); break;
                    case 0x11: WriteSpecial(SysmMsp, value); break;
                    case 0x12: WriteSpecial(SysmPsp, value); break;
                    case 0x13: WriteSpecial(SysmPrimask, value); break;
                    case 0x14: break; // TODO BASEPRI
                    case 0x15: break; // TODO faultmask
                    case 0x16: WriteSpecial(SysmControl, value); break;
                    default:   core.Registers[registerIndex] = value; break;
                }
                return GdbMessage("OK");
            }

            case 'm':
            {
                var parts = cmd[1..].Split(',');
                var address = Convert.ToUInt32(parts[0], 16);
                var length = Convert.ToInt32(parts[1], 16);
                var bus = Target.Machine.Bus;
                Span<byte> bytes = length <= 1024 ? stackalloc byte[length] : new byte[length];
                for (var i = 0; i < length; i++)
                    bytes[i] = bus.ReadByte((uint)(address + i));
                return GdbMessage(EncodeHexBuf(bytes));
            }

            case 'M':
            {
                var parts = cmd[1..].Split(',', ':');
                var address = Convert.ToUInt32(parts[0], 16);
                var length = Convert.ToInt32(parts[1], 16);
                var data = DecodeHexBuf(parts[2][..(length * 2)]);
                var bus = Target.Machine.Bus;
                for (var i = 0; i < data.Length; i++)
                    bus.WriteByte((uint)(address + i), data[i]);
                return GdbMessage("OK");
            }
        }

        return GdbMessage("");
    }

    public void AddConnection(GdbConnection connection)
    {
        _connections.Add(connection);
        Core.OnBreakpoint = _ =>
        {
            Target.Stop();
            // Step() advanced PC past the 2-byte BKPT; rewind so GDB reports the BKPT address.
            Core.Registers.PC -= 2;
            foreach (var c in _connections)
                c.OnBreakpoint();
        };
    }

    public void RemoveConnection(GdbConnection connection) => _connections.Remove(connection);

    /// <summary>
    /// Reports that execution reached an address in <see cref="Breakpoints"/>. Call this from
    /// the execution loop with PC still sitting on the breakpoint address — unlike the
    /// <c>BKPT</c> path in <see cref="AddConnection"/>, nothing has executed yet, so PC needs
    /// no rewinding.
    /// </summary>
    public void ReportBreakpointHit()
    {
        Target.Stop();
        foreach (var c in _connections)
            c.OnBreakpoint();
    }

    // ── Special registers (ARMv6-M MRS/MSR semantics) ────────────────────────────

    private uint ReadSpecial(uint sysm)
    {
        var r = Core.Registers;
        var usePsp = r.IPSR == 0 && (r.CONTROL & 2) != 0;
        return sysm switch
        {
            SysmMsp     => usePsp ? r.MSP_Storage : r.SP,
            SysmPsp     => usePsp ? r.SP : r.PSP_Storage,
            SysmPrimask => r.PRIMASK & 1,
            SysmControl => r.CONTROL & 3,
            _           => 0,
        };
    }

    private void WriteSpecial(uint sysm, uint value)
    {
        ref var r = ref Core.Registers;
        var usePsp = r.IPSR == 0 && (r.CONTROL & 2) != 0;
        switch (sysm)
        {
            case SysmMsp:
                if (usePsp) r.MSP_Storage = value; else r.SP = value;
                break;
            case SysmPsp:
                if (usePsp) r.SP = value; else r.PSP_Storage = value;
                break;
            case SysmPrimask: r.PRIMASK = value & 1; break;
            case SysmControl: r.CONTROL = value & 3; break;
        }
    }

    private static void WriteUint32Le(Span<byte> dst, uint value)
    {
        dst[0] = (byte)(value & 0xFF);
        dst[1] = (byte)((value >> 8) & 0xFF);
        dst[2] = (byte)((value >> 16) & 0xFF);
        dst[3] = (byte)((value >> 24) & 0xFF);
    }
}
