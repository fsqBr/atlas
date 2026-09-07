using System;

namespace Shop.Core
{
    // Left behind by an old promotion campaign; nothing references it anymore.
    internal class LegacyDiscountCalculator
    {
        public decimal Apply(decimal total)
        {
            if (total > 100m)
            {
                return total * 0.9m;
            }

            return total;
        }
    }
}
