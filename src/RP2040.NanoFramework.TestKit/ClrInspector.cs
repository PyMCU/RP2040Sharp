using System.Text;
using RP2040.Peripherals;

namespace RP2040.NanoFramework.TestKit;

/// <summary>
/// Reads managed state out of a running nanoCLR by walking its in-RAM data structures with a
/// <see cref="ClrLayout"/>. Currently resolves a managed <c>static</c> field by name — assembly
/// (<c>g_CLR_RT_TypeSystem.m_assemblies</c>) → type/field (the <c>.pe</c> metadata tables) → the
/// cross-reference slot → the <c>CLR_RT_HeapBlock</c> that holds the value.
/// </summary>
public sealed class ClrInspector
{
    private readonly RP2040Machine _m;
    private readonly ClrLayout _l;
    private readonly uint _typeSystem;

    public ClrInspector(RP2040Machine machine, uint typeSystemAddress, ClrLayout? layout = null)
    {
        _m = machine;
        _typeSystem = typeSystemAddress;
        _l = layout ?? ClrLayout.Default;
    }

    private uint Rd(uint a) => _m.Bus.ReadWord(a);
    private ushort Rh(uint a) => _m.Bus.ReadHalfWord(a);
    private byte Rb(uint a) => _m.Bus.ReadByte(a);

    private string CStr(uint a)
    {
        var sb = new StringBuilder();
        for (byte c = Rb(a); c != 0; c = Rb(++a))
        {
            sb.Append((char)c);
        }

        return sb.ToString();
    }

    /// <summary>The CLR_RT_Assembly pointer for the loaded assembly named <paramref name="name"/> (0 if none).</summary>
    public uint FindAssembly(string name)
    {
        uint max = Rd(_typeSystem + _l.TS_AssembliesMax);
        for (uint i = 0; i < max && i < 64; i++)
        {
            uint asm = Rd(_typeSystem + _l.TS_Assemblies + i * 4);
            if (asm == 0)
            {
                continue;
            }

            uint namePtr = Rd(asm + _l.ASM_Name);
            if (namePtr != 0 && CStr(namePtr) == name)
            {
                return asm;
            }
        }

        return 0;
    }

    /// <summary>
    /// Resolves the static-field slot (the cross-reference offset into <c>m_pStaticFields</c>) for the
    /// static field named <paramref name="fieldName"/> in assembly <paramref name="asm"/>. Searches every
    /// type's static fields; pass <paramref name="typeName"/> to disambiguate a repeated field name.
    /// </summary>
    public bool TryResolveStaticSlot(uint asm, string fieldName, out int slot, string? typeName = null)
    {
        slot = -1;
        uint header = Rd(asm + _l.ASM_Header);
        uint sot = header + _l.SOT;
        uint typeDefTable = header + Rd(sot + (uint)_l.TBL_TypeDef * 4);
        uint fieldDefTable = header + Rd(sot + (uint)_l.TBL_FieldDef * 4);
        uint stringHeap = header + Rd(sot + (uint)_l.TBL_Strings * 4);

        uint xref = Rd(asm + _l.ASM_XrefFieldDef);
        if (xref == 0 || Rd(asm + _l.ASM_StaticFields) == 0)
        {
            return false; // the CLR has not finished allocating this assembly's statics yet
        }

        // TypeDef record count = byte span to the next table / record size (tables are contiguous).
        uint typeDefBytes = Rd(sot + (uint)(_l.TBL_TypeDef + 1) * 4) - Rd(sot + (uint)_l.TBL_TypeDef * 4);
        int typeCount = (int)(typeDefBytes / _l.TD_Size);

        for (int t = 0; t < typeCount; t++)
        {
            uint td = typeDefTable + (uint)t * _l.TD_Size;
            if (typeName != null && CStr(stringHeap + Rh(td + _l.TD_Name)) != typeName)
            {
                continue;
            }

            int first = Rh(td + _l.TD_SFieldsFirst);
            int num = Rb(td + _l.TD_SFieldsNum);
            for (int fi = first; fi < first + num; fi++)
            {
                uint fd = fieldDefTable + (uint)fi * _l.FD_Size;
                if (CStr(stringHeap + Rh(fd + _l.FD_Name)) == fieldName)
                {
                    slot = Rh(xref + (uint)fi * 2); // CLR_RT_FieldDef_CrossReference[fi].m_offset
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>A static field's stored cell: its nanoCLR data type and the raw 32 bits of value.</summary>
    public readonly record struct HeapValue(byte DataType, uint Raw)
    {
        public int AsInt32 => (int)Raw;
        public uint AsUInt32 => Raw;
        public bool AsBoolean => Raw != 0;
    }

    /// <summary>Reads the static field's storage cell (its data type byte + the value's low 32 bits).</summary>
    public HeapValue ReadStatic(uint asm, int slot)
    {
        uint heapBlock = Rd(asm + _l.ASM_StaticFields) + (uint)slot * _l.HB_Size;
        byte dataType = Rb(heapBlock); // CLR_RT_HeapBlock.m_id.dataType @ 0
        return new HeapValue(dataType, Rd(heapBlock + _l.HB_Data));
    }

    /// <summary>Reads a managed <c>static int</c> field as a 32-bit value.</summary>
    public int ReadStaticInt32(uint asm, int slot) => ReadStatic(asm, slot).AsInt32;

    /// <summary>Reads a managed <c>static long</c> field as a 64-bit value (the cell's 8 data bytes).</summary>
    public long ReadStaticInt64(uint asm, int slot)
    {
        uint hb = Rd(asm + _l.ASM_StaticFields) + (uint)slot * _l.HB_Size;
        ulong lo = Rd(hb + _l.HB_Data);
        ulong hi = Rd(hb + _l.HB_Data + 4);
        return (long)(lo | (hi << 32));
    }

    // ---- instance fields (by name) ------------------------------------

    /// <summary>
    /// Resolves the instance-field slot for <paramref name="fieldName"/> of <paramref name="typeName"/>
    /// (the offset, in heap blocks, of the field within an object of that type).
    /// </summary>
    public bool TryResolveInstanceSlot(uint asm, string typeName, string fieldName, out int slot)
    {
        slot = -1;
        uint header = Rd(asm + _l.ASM_Header);
        uint sot = header + _l.SOT;
        uint typeDefTable = header + Rd(sot + (uint)_l.TBL_TypeDef * 4);
        uint fieldDefTable = header + Rd(sot + (uint)_l.TBL_FieldDef * 4);
        uint stringHeap = header + Rd(sot + (uint)_l.TBL_Strings * 4);
        uint xref = Rd(asm + _l.ASM_XrefFieldDef);
        if (xref == 0)
        {
            return false;
        }

        uint typeDefBytes = Rd(sot + (uint)(_l.TBL_TypeDef + 1) * 4) - Rd(sot + (uint)_l.TBL_TypeDef * 4);
        int typeCount = (int)(typeDefBytes / _l.TD_Size);

        for (int t = 0; t < typeCount; t++)
        {
            uint td = typeDefTable + (uint)t * _l.TD_Size;
            if (CStr(stringHeap + Rh(td + _l.TD_Name)) != typeName)
            {
                continue;
            }

            int first = Rh(td + _l.TD_IFieldsFirst);
            int num = Rb(td + _l.TD_IFieldsNum);
            for (int fi = first; fi < first + num; fi++)
            {
                uint fd = fieldDefTable + (uint)fi * _l.FD_Size;
                if (CStr(stringHeap + Rh(fd + _l.FD_Name)) == fieldName)
                {
                    slot = Rh(xref + (uint)fi * 2);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Reads instance field <paramref name="slot"/> of the object at <paramref name="obj"/>. The slot is
    /// the field's cross-reference m_offset, which already counts past the object header (the nanoCLR does
    /// <c>res = Dereference(); res += m_offset</c>), so it is <em>not</em> additionally offset by the header.
    /// </summary>
    public HeapValue ReadInstance(uint obj, int slot)
    {
        uint hb = obj + (uint)slot * _l.HB_Size;
        return new HeapValue(Rb(hb), Rd(hb + _l.HB_Data));
    }

    // ---- methods (stack frames) ---------------------------------------

    private uint StringHeapOf(uint asm)
    {
        uint header = Rd(asm + _l.ASM_Header);
        return header + Rd(header + _l.SOT + (uint)_l.TBL_Strings * 4);
    }

    /// <summary>
    /// The managed method a <c>CLR_RT_StackFrame</c> is running, as "Assembly!Method"
    /// (m_call.m_assm.m_szName + m_call.m_target's name). "" if <paramref name="stackFrame"/> is not one.
    /// </summary>
    public string MethodAt(uint stackFrame)
    {
        uint asm = Rd(stackFrame + _l.SF_CallAssm);
        if (asm == 0)
        {
            return string.Empty;
        }

        uint namePtr = Rd(asm + _l.ASM_Name);
        uint md = Rd(stackFrame + _l.SF_CallTarget);
        if (namePtr == 0 || md == 0)
        {
            return string.Empty;
        }

        string asmName = CStr(namePtr);
        string method = CStr(StringHeapOf(asm) + Rh(md + _l.MD_Name));
        return asmName + "!" + method;
    }

    // ---- instance heap objects (arrays) -------------------------------

    /// <summary>Element count of a managed array object (its <c>CLR_RT_HeapBlock_Array</c> header).</summary>
    public uint ReadArrayLength(uint arrayObject) => Rd(arrayObject + _l.ARR_NumElems);

    /// <summary>Reads element <paramref name="index"/> of a managed <c>int[]</c> object.</summary>
    public int ReadArrayInt32(uint arrayObject, int index) => (int)Rd(arrayObject + _l.ARR_DataOff + (uint)index * 4);
}
