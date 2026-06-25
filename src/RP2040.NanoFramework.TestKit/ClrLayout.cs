namespace RP2040.NanoFramework.TestKit;

/// <summary>
/// In-RAM struct offsets of a specific nanoCLR build, used to read managed state (e.g. a static
/// field) out of the emulator's memory. The values come from the firmware's DWARF
/// (<c>arm-none-eabi-gdb -batch nanoCLR.elf -ex 'ptype /o struct …'</c>) and are therefore tied to
/// the build — keep them next to the firmware. <see cref="Default"/> matches the vendored RP_PICO build.
/// </summary>
public sealed record ClrLayout
{
    // CLR_RT_TypeSystem (g_CLR_RT_TypeSystem)
    public uint TS_Assemblies { get; init; }       // m_assemblies[64]
    public uint TS_AssembliesMax { get; init; }    // m_assembliesMax

    // CLR_RT_Assembly
    public uint ASM_Header { get; init; }          // m_header (-> CLR_RECORD_ASSEMBLY in flash)
    public uint ASM_Name { get; init; }            // m_szName (const char*)
    public uint ASM_StaticFields { get; init; }    // m_pStaticFields (CLR_RT_HeapBlock[])
    public uint ASM_XrefFieldDef { get; init; }    // m_pCrossReference_FieldDef (CLR_RT_FieldDef_CrossReference[])

    // CLR_RT_HeapBlock
    public uint HB_Size { get; init; }             // sizeof(CLR_RT_HeapBlock)
    public uint HB_Data { get; init; }             // m_data (numeric value lives here)

    // CLR_RECORD_ASSEMBLY (.pe header)
    public uint SOT { get; init; }                 // startOfTables[16] (CLR_OFFSET_LONG, 4 bytes each)

    // metadata table indices
    public int TBL_TypeDef { get; init; }
    public int TBL_FieldDef { get; init; }
    public int TBL_Strings { get; init; }

    // CLR_RECORD_TYPEDEF
    public uint TD_Size { get; init; }
    public uint TD_Name { get; init; }             // CLR_STRING (offset into the string heap)
    public uint TD_SFieldsFirst { get; init; }     // first static FieldDef index
    public uint TD_SFieldsNum { get; init; }       // static field count (1 byte)

    // CLR_RECORD_FIELDDEF
    public uint FD_Size { get; init; }
    public uint FD_Name { get; init; }             // CLR_STRING

    // CLR_RT_Assembly.m_pTablesSize[16] — per-table record counts.
    public uint ASM_TablesSize { get; init; }

    /// <summary>Offsets for the vendored RP_PICO (RP2040) nanoCLR build.</summary>
    public static ClrLayout Default { get; } = new()
    {
        TS_Assemblies = 0,
        TS_AssembliesMax = 256,
        ASM_Header = 20,
        ASM_Name = 24,
        ASM_TablesSize = 32,
        ASM_StaticFields = 96,
        ASM_XrefFieldDef = 128,
        HB_Size = 12,
        HB_Data = 4,
        SOT = 40,
        TBL_TypeDef = 4,
        TBL_FieldDef = 5,
        TBL_Strings = 11,
        TD_Size = 24,
        TD_Name = 0,
        TD_SFieldsFirst = 16,
        TD_SFieldsNum = 20,
        FD_Size = 8,
        FD_Name = 0,
    };
}
