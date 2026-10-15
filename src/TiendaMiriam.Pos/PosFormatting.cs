using System.Globalization;

namespace TiendaMiriam.Pos;

internal static class PosFormatting
{
    private static readonly CultureInfo Bolivia = CultureInfo.GetCultureInfo("es-BO");

    public static string Money(decimal amount) => $"{amount.ToString("N2", Bolivia)} Bs";
}
