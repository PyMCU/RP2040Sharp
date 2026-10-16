using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RP2040.Core.Memory;

public sealed unsafe class RandomAccessMemory : IMemoryMappedDevice, IDisposable
{
    // Backing store is unmanaged native memory (not a pinned managed array). This keeps the
    // multi-megabyte RAM/Flash/BootROM blocks out of the GC heap entirely — a pinned array of
    // this size would fragment the heap and resist compaction for its whole lifetime. Mirrors
    // the unmanaged-buffer pattern used by InstructionDecoder.
    /// <summary>Slack allocated past the end of every backing store. The bus masks an address into the
    /// buffer but then reads/writes a whole halfword/word from there, so an unaligned access at the last
    /// 1-3 bytes spills past the masked end. Without the slack that was a host heap overflow reachable from
    /// guest code (LDR/STR/LDM/STM, exception stacking with a hostile SP). Wider than any single access.</summary>
    public const int TailPad = 16;

    public readonly byte* BasePtr;
    public uint Size { get; }

    private bool _disposed;

    public RandomAccessMemory(int size)
    {
        // AllocZeroed preserves the zero-initialised semantics of `new byte[size]`.
        BasePtr = (byte*)NativeMemory.AllocZeroed((nuint)size + TailPad);
        Size = (uint)size;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte(uint address)
    {
        return BasePtr[address];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadHalfWord(uint address)
    {
        return Unsafe.ReadUnaligned<ushort>(BasePtr + address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadWord(uint address)
    {
        return Unsafe.ReadUnaligned<uint>(BasePtr + address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(uint address, byte value) => BasePtr[address] = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHalfWord(uint address, ushort value) =>
        Unsafe.WriteUnaligned(BasePtr + address, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteWord(uint address, uint value) =>
        Unsafe.WriteUnaligned(BasePtr + address, value);

    [ExcludeFromCodeCoverage]
    ~RandomAccessMemory()
    {
        Free();
    }

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    private void Free()
    {
        if (_disposed)
            return;
        NativeMemory.Free(BasePtr);
        _disposed = true;
    }
}
