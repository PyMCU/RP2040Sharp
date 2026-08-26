// SPDX-License-Identifier: BUSL-1.1
// Copyright (c) 2024-2026 Iván Montiel Cardona
using RP2040.Gdb;
using RP2040.Peripherals;

namespace RP2040Sharp.GdbHost;

/// <summary>
/// Execution gate shared by the simulation loop and the GDB server: the loop only advances
/// the machine while <see cref="Executing"/> is true. GDB's <c>continue</c> calls
/// <see cref="Execute"/>; an interrupt or a breakpoint hit calls <see cref="Stop"/>.
/// </summary>
internal sealed class GdbHostTarget(RP2040Machine machine) : IGdbTarget
{
    private volatile bool _executing;

    public RP2040Machine Machine => machine;
    public bool Executing => _executing;
    public void Execute() => _executing = true;
    public void Stop() => _executing = false;
}
