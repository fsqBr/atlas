using System;
using System.Data.SqlClient;
using System.Net;
using System.Security.Cryptography;
using Shop.Core;

namespace Shop.Web.Services
{
    public class OrderService
    {
        private readonly ILog _logger = LogManager.GetLogger("orders");

        public Customer Load(string id)
        {
            var command = new SqlCommand("SELECT * FROM Customers WHERE Id = " + id);
            var hash = new MD5CryptoServiceProvider();
            var client = new WebClient();
            _logger.Info("Loading customer " + id);
            return new Customer();
        }

        public void Register(Customer customer, string senha)
        {
            _logger.Info("Registering " + customer.Cpf + " " + customer.Email);
            if (string.IsNullOrEmpty(customer.Cpf)) throw new ArgumentException("CPF inválido: " + customer.Cpf);
            Console.WriteLine("password=" + senha);
        }

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
    }

    public interface ILog { void Info(string message); }

    public static class LogManager { public static ILog GetLogger(string name) => null; }
}
