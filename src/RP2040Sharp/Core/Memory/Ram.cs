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
    public readonly byte* BasePtr;
    public uint Size { get; }

    private bool _disposed;

    public RandomAccessMemory(int size)
    {
        // AllocZeroed preserves the zero-initialised semantics of `new byte[size]`.
        BasePtr = (byte*)NativeMemory.AllocZeroed((nuint)size);
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
