namespace RP2040Sharp.Native.Tests;

/// <summary>
/// The circuit side of a co-simulation through the C ABI, as a lock-step host (Proteus) runs it: after every
/// st_rp2040_run_until_edge it looks at the pad changes the call reported and answers through
/// st_rp2040_pin_set_input, at the chip's time when the call returned. Wires copy a driven pad to another pad's input
/// (SPI MOSI to MISO, UART TX to RX); an I2C bus resolves SDA / SCL as open-drain lines with a pull-up between the
/// chip's pads and a bit-level slave (<see cref="I2cBitSlave"/>).
/// </summary>
internal sealed class BusHost
{
    private readonly nint _h;
    private readonly Host _host;
    private int _seen;
    private readonly bool[] _oe = new bool[30], _level = new bool[30];
    private readonly int?[] _input = new int?[30];
    private readonly List<(int From, int To)> _wires = [];
    private (int Sda, int Scl, I2cBitSlave Slave)? _i2c;

    /// <summary>Every input the host drove: (core-0 cycle when driven, gpio, level).</summary>
    public readonly List<(ulong Cycle, int Gpio, int Level)> Inputs = [];

    public BusHost(nint h, Host host)
    {
        _h = h;
        _host = host;
        for (int g = 0; g < 30; g++)
        {
            _level[g] = Abi.PinGet(h, g, out var oe) != 0;
            _oe[g] = oe != 0;
        }
    }

    /// <summary>Drive <paramref name="to"/> with the level <paramref name="from"/> is driven to.</summary>
    public BusHost Wire(int from, int to) { _wires.Add((from, to)); return this; }

    /// <summary>An open-drain bus on <paramref name="sda"/> / <paramref name="scl"/> with a pull-up and <paramref name="slave"/> on it.</summary>
    public BusHost I2c(int sda, int scl, I2cBitSlave slave)
    {
        _i2c = (sda, scl, slave);
        Drive(sda, 1);
        Drive(scl, 1);
        return this;
    }

    /// <summary>Drive a pad from the host (only when the level changes, as a circuit would).</summary>
    public BusHost Drive(int gpio, int level)
    {
        if (_input[gpio] == level) return this;
        _input[gpio] = level;
        Inputs.Add((Abi.Cycles(_h), gpio, level));
        Abi.PinSetInput(_h, gpio, level);
        return this;
    }

    /// <summary>One call of the chip, then the circuit's answers to what it reported.</summary>
    public ulong Step(uint max, uint guard, uint watch)
    {
        var c = Abi.RunUntilEdge(_h, max, guard, watch);
        Assert.Equal(0, Abi.Status(_h));
        Answer();
        return c;
    }

    /// <summary>React to the pad events reported since the last call, in order.</summary>
    public void Answer()
    {
        var pins = _host.Pins;
        for (; _seen < pins.Count; _seen++)
        {
            var (g, level, oe, cycle) = pins[_seen];
            _oe[g] = oe != 0;
            _level[g] = level != 0;
            foreach (var (from, to) in _wires)
                if (from == g && oe != 0) Drive(to, level);
            if (_i2c is var (sda, scl, slave) && (g == sda || g == scl)) Resolve(sda, scl, slave, (long)cycle);
        }
    }

    private bool ChipLow(int g) => _oe[g] && !_level[g];

    private void Resolve(int sda, int scl, I2cBitSlave slave, long cycle)
    {
        // The slave sees the wired-AND of the chip and itself, and what it pulls is part of what it sees next.
        for (int i = 0; i < 4; i++)
        {
            bool s = slave.SdaLow, c = slave.SclLow;
            slave.Update(!(ChipLow(sda) || slave.SdaLow), !(ChipLow(scl) || slave.SclLow), cycle);
            if (slave.SdaLow == s && slave.SclLow == c) break;
        }
        Drive(sda, slave.SdaLow ? 0 : 1);
        Drive(scl, slave.SclLow ? 0 : 1);
    }
}

/// <summary>
/// A bit-level I2C slave (RP2350Sharp's TestKit probe, trimmed): it sees only the SDA and SCL line levels, detects
/// START / repeated START / STOP, shifts the address and data bits on the SCL edges, ACKs its own 7-bit address and
/// every written byte and serves reads from a register file whose pointer is the first byte of a write.
/// </summary>
internal sealed class I2cBitSlave(byte address, byte[]? registers = null)
{
    public enum EventKind { Start, RepeatedStart, Stop, AddressAck, AddressNack, WriteByte, ReadByte }

    public readonly record struct BusEvent(long Cycle, EventKind Kind, int Value);

    private enum State { Idle, Addr, RxByte, TxByte }

    private readonly byte[] _regs = registers ?? new byte[256];
    private State _state = State.Idle;
    private bool _prevSda = true, _prevScl = true;
    private int _n, _shift, _ptr;
    private bool _rw, _haveRegPointer, _masterAck;
    private byte _txByte;

    public bool SdaLow { get; private set; }
    public bool SclLow { get; private set; }
    public byte[] Registers => _regs;
    public List<BusEvent> Events { get; } = [];

    public void Update(bool sda, bool scl, long cycle)
    {
        bool sdaChanged = sda != _prevSda, sclChanged = scl != _prevScl;
        if (!sdaChanged && !sclChanged) return;
        if (sdaChanged && scl && _prevScl)
        {
            if (_prevSda && !sda)
            {
                Events.Add(new BusEvent(cycle, _state == State.Idle ? EventKind.Start : EventKind.RepeatedStart, 0));
                _state = State.Addr; _n = 0; _shift = 0; SdaLow = false; _haveRegPointer = false;
            }
            else if (!_prevSda && sda)
            {
                Events.Add(new BusEvent(cycle, EventKind.Stop, 0));
                _state = State.Idle; _n = 0; SdaLow = false;
            }
        }
        else if (sclChanged && scl) OnRise(sda);
        else if (sclChanged && !scl) OnFall(cycle);
        _prevSda = sda; _prevScl = scl;
    }

    private void OnRise(bool sda)
    {
        if (_state == State.Idle) return;
        _n++;
        if (_n <= 8 && _state is State.Addr or State.RxByte) _shift = (_shift << 1) | (sda ? 1 : 0);
        else if (_n == 9 && _state == State.TxByte) _masterAck = !sda;
    }

    private void OnFall(long cycle)
    {
        if (_state == State.Idle) return;
        if (_n == 8)
        {
            switch (_state)
            {
                case State.Addr:
                    if ((_shift >> 1) == address)
                    {
                        _rw = (_shift & 1) != 0;
                        SdaLow = true;
                        Events.Add(new BusEvent(cycle, EventKind.AddressAck, _shift));
                    }
                    else { Events.Add(new BusEvent(cycle, EventKind.AddressNack, _shift)); _state = State.Idle; }
                    break;
                case State.RxByte:
                    Events.Add(new BusEvent(cycle, EventKind.WriteByte, _shift));
                    if (!_haveRegPointer) { _ptr = _shift; _haveRegPointer = true; }
                    else _regs[_ptr++ & 0xFF] = (byte)_shift;
                    SdaLow = true;
                    break;
                case State.TxByte:
                    SdaLow = false;   // the master drives the ACK / NACK
                    break;
            }
        }
        else if (_n == 9)
        {
            SdaLow = false;
            var wasAddr = _state == State.Addr;
            _n = 0; _shift = 0;
            if (wasAddr) _state = _rw ? State.TxByte : State.RxByte;
            if (_state == State.TxByte)
            {
                if (!wasAddr && !_masterAck) { _state = State.Idle; return; }   // NACK: the master is done
                _txByte = _regs[_ptr++ & 0xFF];
                Events.Add(new BusEvent(cycle, EventKind.ReadByte, _txByte));
                SdaLow = ((_txByte >> 7) & 1) == 0;
            }
        }
        else if (_state == State.TxByte && _n is >= 1 and <= 7)
            SdaLow = ((_txByte >> (7 - _n)) & 1) == 0;
    }
}
