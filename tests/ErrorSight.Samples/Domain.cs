using ErrorSight;

namespace SampleApp;

public sealed class Order
{
    public int Id { get; set; }
    public Customer Customer { get; set; }
    public List<OrderLine> Lines { get; set; } = new();
}

public sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; }
    public string Password { get; set; }
    public string Notes { get; set; }
    public Address Address { get; set; }
}

public sealed class Address
{
    public string Street { get; set; } = "";
    public City City { get; set; }
}

public sealed class City
{
    public string Name { get; set; } = "";
}

public sealed record OrderLine(string Sku, int Quantity);

public sealed class PaymentCard
{
    public string CardNumber { get; set; } = "";
    public string Holder { get; set; } = "";
}

public struct Point
{
    public int X;
    public string Label;

    public int LabelLength()
    {
        var offset = X * 2;
        return Label.Length + offset;
    }
}

public static class Build
{
    public static Order OrderWithoutAddress() => new()
    {
        Id = 1837,
        Customer = new Customer { Id = 42, Name = "Jane Doe", Email = "jane@example.com", Password = "hunter2", Notes = "VIP" },
        Lines = { new OrderLine("A-1", 2), new OrderLine("B-7", 1) },
    };

    public static Order OrderWithoutCity() => new()
    {
        Id = 7,
        Customer = new Customer { Id = 1, Name = "John", Address = new Address { Street = "Main St 1" } },
    };

    public static Order CompleteOrder() => new()
    {
        Id = 1,
        Customer = new Customer { Id = 1, Name = "Ok", Address = new Address { Street = "x", City = new City { Name = "Oslo" } } },
    };
}
