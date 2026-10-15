namespace TiendaMiriam.Domain;

public sealed record ProductPriceQuote(decimal Price, Guid? PromotionId);

public static class ProductPricingCalculator
{
    public static ProductPriceQuote GetQuote(
        decimal catalogPrice,
        decimal? listPrice,
        IEnumerable<Promotion> promotions,
        DateTime occurredAtUtc)
    {
        var basePrice = listPrice ?? catalogPrice;
        var quote = new ProductPriceQuote(basePrice, null);
        foreach (var promotion in promotions
                     .Where(item => item.IsActive && item.StartsAtUtc <= occurredAtUtc
                         && occurredAtUtc < item.EndsAtUtc)
                     .OrderBy(item => item.Id))
        {
            var promotionalPrice = promotion.Kind switch
            {
                PromotionKind.PercentOff => decimal.Round(
                    basePrice * (100m - promotion.Value) / 100m, 2, MidpointRounding.AwayFromZero),
                PromotionKind.FixedPrice => decimal.Round(promotion.Value, 2, MidpointRounding.AwayFromZero),
                _ => basePrice
            };
            if (promotionalPrice >= 0 && promotionalPrice < quote.Price)
            {
                quote = new ProductPriceQuote(promotionalPrice, promotion.Id);
            }
        }
        return quote;
    }
}
