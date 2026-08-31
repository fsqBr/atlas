using System;
using System.Collections.Generic;

namespace Shop.Core
{
    public class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Cpf { get; set; }
        public string Rg { get; set; }
        public string Email { get; set; }
        public string Telefone { get; set; }
        public string Cep { get; set; }
        public string CardNumber { get; set; }
        public string Cvv { get; set; }
        public DateTime DataNascimento { get; set; }
        public string PasswordHash { get; set; }
    }

    public class Order
    {
        public List<OrderLine> Lines { get; set; } = new List<OrderLine>();
        public decimal TaxRate { get; set; }
        public decimal Weight { get; set; }
        public decimal? Coupon { get; set; }
        public decimal Total { get; set; }
    }

    public class OrderLine
    {
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal Discount { get; set; }
    }
}
