namespace TiendaMiriam.Domain;

public static class ScaleBarcodeParser
{
    public static bool TryParseEan13(string? barcode, out string itemCode, out decimal quantityKg)
    {
        itemCode = string.Empty;
        quantityKg = 0m;
        if (barcode is null || barcode.Length != 13 || barcode.Any(character => !char.IsAsciiDigit(character))
            || barcode[0] != '2' || barcode[1] is < '0' or > '9')
        {
            return false;
        }

        var weightedSum = 0;
        for (var index = 0; index < 12; index++)
        {
            var digit = barcode[index] - '0';
            weightedSum += digit * (index % 2 == 0 ? 1 : 3);
        }
        var expectedCheckDigit = (10 - weightedSum % 10) % 10;
        if (expectedCheckDigit != barcode[12] - '0')
        {
            return false;
        }

        itemCode = barcode[2..7];
        if (!decimal.TryParse(barcode[7..12], out var grams) || grams <= 0)
        {
            itemCode = string.Empty;
            return false;
        }
        quantityKg = grams / 1000m;
        return true;
    }
}
