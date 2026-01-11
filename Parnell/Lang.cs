using ECommons;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace Parnell;

public class Lang
{
    internal static string[] BellName => [Svc.Data.GetExcelSheet<EObjName>().GetRow(2000401).Singular.GetText(), "リテイナーベル"];
    public const char HqSymbol = '\uE03C';
}
