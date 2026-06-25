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
    /// Resolves the static-field slot (the cross-reference offset into <c>m_pStaticFields</c>) for
    /// <paramref name="fieldName"/> of <paramref name="typeName"/> in assembly <paramref name="asm"/>.
    /// </summary>
    public bool TryResolveStaticSlot(uint asm, string typeName, string fieldName, out int slot)
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
            if (CStr(stringHeap + Rh(td + _l.TD_Name)) != typeName)
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

    /// <summary>Reads a managed <c>static int</c> field as a 32-bit value.</summary>
    public int ReadStaticInt32(uint asm, int slot)
    {
        uint heapBlock = Rd(asm + _l.ASM_StaticFields) + (uint)slot * _l.HB_Size;
        return (int)Rd(heapBlock + _l.HB_Data);
    }
}
