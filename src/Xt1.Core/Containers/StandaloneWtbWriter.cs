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
        bank.IsStandalone = true;
        bank.PayloadBase = PayloadBase;
        bank.SyncTable1WithImageSize = false;
        bank.RecalculatePayloadOffsets(PayloadBase);
        bank.StructureDirty = true;
        return bank.ToBytes();
    }
}
