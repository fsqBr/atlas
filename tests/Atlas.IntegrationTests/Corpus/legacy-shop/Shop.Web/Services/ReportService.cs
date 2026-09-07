using System;
using Shop.Core;

namespace Shop.Web.Services
{
    public class ReportService
    {
        // Copy-pasted from OrderService.Total
        public decimal Total(Order order)
        {
            var subtotal = 0m;
            foreach (var line in order.Lines)
            {
                if (line.Quantity <= 0) continue;
                subtotal += line.Quantity * line.UnitPrice;
                if (line.Discount > 0) subtotal -= line.Discount;
            }
            var tax = subtotal * order.TaxRate;
            var shipping = order.Weight > 10 ? 25m : 10m;
            var total = subtotal + tax + shipping;
            if (order.Coupon != null) total -= order.Coupon.Value;
            if (total < 0) total = 0;
            order.Total = total;
            return total;
        }

        public int Complexity(int a, int b, int c, int d)
        {
            var r = 0;
            if (a > 0) r++; else if (a < 0) r--;
            if (b > 0) r++; else if (b < 0) r--;
            if (c > 0) r++; else if (c < 0) r--;
            if (d > 0) r++; else if (d < 0) r--;
            for (var i = 0; i < a; i++) { if (i % 2 == 0) r++; else r--; }
            for (var i = 0; i < b; i++) { if (i % 3 == 0) r++; else if (i % 5 == 0) r--; }
            while (r > 100) { r -= 7; if (r % 2 == 0) r--; }
            switch (r) { case 1: return 10; case 2: return 20; case 3: return 30; case 4: return 40; default: break; }
            return a > b ? (c > d ? 1 : 2) : (c > d ? 3 : 4);
        }
    }
}
