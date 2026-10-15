using TiendaMiriam.Domain;

namespace TiendaMiriam.Infrastructure;

public static class ProductStockCalculator
{
    public static decimal? GetAvailableQuantity(
        Product product,
        IReadOnlyDictionary<Guid, StockItem> stockItems,
        IReadOnlyDictionary<Guid, RecipeComponent[]> recipeComponents)
    {
        if (product.IsRecipe)
        {
            if (!recipeComponents.TryGetValue(product.Id, out var components)
                || components.Length == 0
                || components.Any(component => component.Quantity <= 0
                    || !stockItems.ContainsKey(component.StockItemId)))
            {
                return null;
            }

            return components.Min(component =>
                stockItems[component.StockItemId].OnHand / component.Quantity);
        }

        return product.StockItemId is Guid stockItemId && stockItems.TryGetValue(stockItemId, out var stockItem)
            ? stockItem.OnHand
            : null;
    }
}
