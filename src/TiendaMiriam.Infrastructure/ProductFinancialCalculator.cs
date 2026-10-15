using TiendaMiriam.Domain;

namespace TiendaMiriam.Infrastructure;

public static class ProductFinancialCalculator
{
    public static decimal? GetUnitCost(
        Product product,
        IReadOnlyDictionary<Guid, StockItem> stockItems,
        IReadOnlyDictionary<Guid, RecipeComponent[]> recipeComponents,
        decimal? serviceUnitCost = null)
    {
        if (product.IsRecipe)
        {
            if (!recipeComponents.TryGetValue(product.Id, out var components) || components.Length == 0
                || components.Any(component => !stockItems.ContainsKey(component.StockItemId)))
            {
                return null;
            }
            return components.Sum(component =>
                component.Quantity * stockItems[component.StockItemId].UnitCost);
        }

        if (product.StockItemId is Guid stockItemId && stockItems.TryGetValue(stockItemId, out var stockItem))
        {
            return stockItem.UnitCost;
        }
        return serviceUnitCost;
    }

    public static decimal? GetGrossMarginPercent(decimal? unitCost, decimal salePrice)
    {
        return unitCost is null || salePrice <= 0
            ? null
            : decimal.Round((salePrice - unitCost.Value) / salePrice * 100m, 6, MidpointRounding.AwayFromZero);
    }
}
