namespace TiendaMiriam.Domain;

public static class StockUnitConversion
{
    public static decimal Convert(decimal quantity, string fromUnit, string toUnit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);
        var from = Resolve(fromUnit);
        var to = Resolve(toUnit);
        if (from.Dimension != to.Dimension)
        {
            throw new InvalidOperationException(
                $"No se puede convertir de «{fromUnit}» a «{toUnit}»: las unidades no son compatibles.");
        }

        return quantity * from.FactorToBase / to.FactorToBase;
    }

    public static IReadOnlyList<string> GetCompatibleUnits(string stockUnit)
    {
        var resolved = Resolve(stockUnit);
        var units = resolved.Dimension switch
        {
            "mass" => new[] { "kg", "g" },
            "volume" => new[] { "l", "ml" },
            _ => new[] { stockUnit.Trim() }
        };
        return units.Contains(stockUnit.Trim(), StringComparer.OrdinalIgnoreCase)
            ? units
            : [stockUnit.Trim(), .. units];
    }

    private static (string Dimension, decimal FactorToBase) Resolve(string unit)
    {
        var normalized = unit.Trim().ToLowerInvariant();
        return normalized switch
        {
            "kg" or "kgs" or "kilo" or "kilos" or "kilogramo" or "kilogramos" => ("mass", 1000m),
            "g" or "gr" or "grs" or "gramo" or "gramos" => ("mass", 1m),
            "l" or "lt" or "lts" or "litro" or "litros" => ("volume", 1000m),
            "ml" or "mililitro" or "mililitros" => ("volume", 1m),
            "unidad" or "unidades" or "u" or "und" or "unid" or "unids" or "pieza" or "piezas"
                => ("count", 1m),
            _ => ($"custom:{normalized}", 1m)
        };
    }
}
