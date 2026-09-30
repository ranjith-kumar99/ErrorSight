using ErrorSight;

namespace SampleApp;

/// <summary>Plain application code: no try/catch, no ErrorSight calls.</summary>
public class OrderService
{
    private readonly Dictionary<string, decimal> _prices = new() { ["A-1"] = 9.99m };

    public int Counter { get; private set; }

    public string GetCity(Order order)
    {
        var city = order.Customer.Address.City.Name;
        return city.ToUpperInvariant();
    }

    public string ProcessOrder(Order order)
    {
        var orderId = order.Id;
        var label = $"order-{orderId}";
        return label + ":" + GetCity(order);
    }

    public async Task<string> GetCityAsync(Order order)
    {
        await Task.Yield();
        var customerName = order.Customer.Name;
        var city = order.Customer.Address.City.Name;
        return customerName + city;
    }

    public async Task<string> ProcessOrderAsync(Order order)
    {
        var attempt = 3;
        var result = await GetCityAsync(order);
        return result + attempt;
    }

    public string Login(string userName, string password)
    {
        string sessionToken = null;
        var normalized = userName.Trim();
        return normalized + sessionToken.Trim();
    }

    public decimal PriceOf(string sku)
    {
        var key = sku.ToUpperInvariant();
        return _prices[key];
    }

    public int LineQuantity(Order order, int index)
    {
        var lines = order.Lines.ToArray();
        return lines[index].Quantity;
    }

    public int LineQuantityFromList(Order order, int position)
    {
        var lines = order.Lines;
        return lines[position].Quantity;
    }

    public string ViaLambda(Order order)
    {
        var prefix = "city:";
        Func<string> read = () => prefix + order.Customer.Address.City.Name;
        return read();
    }

    public string ViaLocalFunction(Order order)
    {
        var suffix = "!";
        return Read() + suffix;

        string Read() => order.Customer.Address.City.Name.Trim() + suffix;
    }

    public IEnumerable<string> CityNames(IEnumerable<Customer> customers)
    {
        foreach (var customer in customers)
        {
            var name = customer.Address.City.Name;
            yield return name;
        }
    }

    public async IAsyncEnumerable<string> CityNamesAsync(IEnumerable<Customer> customers)
    {
        foreach (var customer in customers)
        {
            await Task.Yield();
            var name = customer.Address.City.Name;
            yield return name;
        }
    }

    public string Charge(PaymentCard card, string apiKey, Customer customer)
    {
        var amount = 12.5m;
        var receipt = amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return receipt + customer.Address.Street;
    }

    public int Parse(ReadOnlySpan<char> text, ref int counter, out string result)
    {
        result = null;
        counter++;
        Span<int> buffer = stackalloc int[2];
        return result.Length + buffer.Length + text.Length;
    }

    public int TryFinally(int x)
    {
        try
        {
            if (x > 0) return x * 2;
            if (x == 0) throw new InvalidOperationException("zero");
        }
        finally
        {
            Counter++;
        }

        return -1;
    }

    public string Classify(int n)
    {
        if (n < 0) return "negative";
        if (n == 0) return "zero";
        if (n > 1000) return "large";
        return "positive";
    }

    public string CatchesOwn(Order order)
    {
        try
        {
            return GetCity(order);
        }
        catch (NullReferenceException) when (order.Id > 0)
        {
            return "handled";
        }
    }

    public string StateBeforeFinally(Order order)
    {
        var stage = "started";
        try
        {
            stage = "reading";
            return order.Customer.Address.City.Name + stage;
        }
        finally
        {
            stage = "finished";
            Counter += stage.Length;
        }
    }

    public string ShippingCity(Customer customer)
    {
        var address = customer.Address;
        Counter += address is null ? 0 : 1;
        return address.City.Name;
    }

    [ErrorSightIgnore]
    public string Ignored(Order order)
    {
        var city = order.Customer.Address.City.Name;
        return city.ToUpperInvariant();
    }

    public static string Upper(string text)
    {
        var trimmed = text.Trim();
        var upper = trimmed.ToUpperInvariant();
        return upper.Length > 0 ? upper : "(empty)";
    }

    /// <summary>Tiny (≤ 16 IL bytes): left uninstrumented so the JIT can keep inlining it.</summary>
    public static int LengthOf(string text) => text.Length;

    public int NameLength(Customer customer)
    {
        var name = customer.Name;
        var length = LengthOf(name);
        return length + Counter;
    }

    public string Recurse(Order order, int depth)
    {
        if (depth > 0) return Recurse(order, depth - 1) + depth;
        return order.Customer.Address.City.Name;
    }
}

public sealed class Invoice
{
    public Invoice(Order order, int copies)
    {
        var header = "INVOICE";
        Total = header.Length + order.Customer.Name.Length * copies;
    }

    public int Total { get; }
}

public class Repository<T> where T : class
{
    private readonly List<T> _items = new();

    public void Add(T item) => _items.Add(item);

    public T Get(int index)
    {
        var items = _items;
        var item = items[index];
        return item ?? throw new InvalidOperationException($"Item {index} is missing.");
    }

    public string Describe<TKey>(T item, TKey key, Func<T, string> describe)
    {
        var text = describe(item);
        return key + ":" + text.Trim();
    }
}
