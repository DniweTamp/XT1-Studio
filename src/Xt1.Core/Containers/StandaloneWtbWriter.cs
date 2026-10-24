using Xt1.Core.Editing;

namespace Xt1.Core.Containers;

/// <summary>
/// Builds a standalone `.wtb`: the WTB header/tables/XT1 region is padded to 0x1000, the payload
/// stream is appended inline, and every <c>table0</c> entry is rebased to an absolute file offset.
/// </summary>
public static class StandaloneWtbWriter
{
    public const int PayloadBase = 0x1000;

    public static byte[] Build(WtbBank bank, WriteOptions? options = null)
    {
        options ??= new WriteOptions();
        bank.IsStandalone = true;
        bank.PayloadBase = PayloadBase;
        if (options.RecomputePayloadOffsets || bank.StructureDirty || bank.Textures.Any(t => t.OffsetsDirty))
        {
            bank.RecalculatePayloadOffsets(PayloadBase);
        }
        bank.StructureDirty = true;
        return bank.ToBytes();
    }
}
