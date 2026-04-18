// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedParameter.Local
// ReSharper disable ClassNeverInstantiated.Local
// ReSharper disable MemberCanBePrivate.Local
#pragma warning disable CS0414
#pragma warning disable CA1822
#pragma warning disable CA1002

#region BuggyExample

public class Customer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public class Order
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public decimal Amount { get; set; }
}

public class OrderService
{
    Dictionary<Guid, Order> orders = [];

    // BUG: Parameter is named 'orderId' but caller passes a CustomerId.
    // The compiler can't catch this because both are just Guid.
    public decimal GetOrderAmount(Guid orderId) =>
        orders[orderId].Amount;
}

public static class BuggyUsage
{
    public static decimal Run(OrderService service, Order order) =>
        // BUG: Passing CustomerId where OrderId is expected.
        // Compiles fine, throws KeyNotFoundException at runtime.
        service.GetOrderAmount(order.CustomerId);
}

#endregion

#region SilentMismatch

public class Product
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public class EntityLookup
{
    Dictionary<Guid, Customer> customers = [];
    Dictionary<Guid, Product> products = [];

    // Caller intends to look up an order, but this method silently
    // reports it as something else or "Unknown" — no exception, just wrong behavior.
    public string DescribeEntity(Guid id)
    {
        if (customers.TryGetValue(id, out var c))
        {
            return $"Customer: {c.Name}";
        }

        if (products.TryGetValue(id, out var p))
        {
            return $"Product: {p.Name}";
        }

        return "Unknown";
    }
}

#endregion

#region FixedExample

public class TypedCustomer
{
    [Id("Customer")]
    public Guid Id { get; set; }

    public string Name { get; set; } = "";
}

public class TypedOrder
{
    [Id("Order")]
    public Guid Id { get; set; }

    [Id("Customer")]
    public Guid CustomerId { get; set; }

    public decimal Amount { get; set; }
}

public class TypedOrderService
{
    Dictionary<Guid, TypedOrder> orders = [];

    public decimal GetOrderAmount([Id("Order")] Guid orderId) =>
        orders[orderId].Amount;
}

public static class FixedUsage
{
    public static decimal Run(TypedOrderService service, TypedOrder order) =>
        // Compile-time SIA001: passing a [Id("Customer")] Guid to an [Id("Order")] parameter.
#pragma warning disable SIA001
        service.GetOrderAmount(order.CustomerId);
#pragma warning restore SIA001
}

#endregion

#region SIA001Example

public class SIA001Sample
{
    [Id("Customer")]
    public Guid CustomerId { get; set; }

    public static void ProcessOrder([Id("Order")] Guid orderId) { }

    public void Trigger() =>
        // SIA001: argument tagged [Id("Customer")] passed to parameter tagged [Id("Order")].
#pragma warning disable SIA001
        ProcessOrder(CustomerId);
#pragma warning restore SIA001
}

#endregion

#region SIA002Example

public class SIA002Sample
{
    public Guid RawId { get; set; }

    public static void ProcessOrder([Id("Order")] Guid orderId) { }

    public void Trigger() =>
        // SIA002: RawId has no [Id] but is passed to an [Id("Order")] parameter.
        // Code fix: add [Id("Order")] to RawId's declaration.
#pragma warning disable SIA002
        ProcessOrder(RawId);
#pragma warning restore SIA002
}

#endregion

#region SIA003Example

public class SIA003Sample
{
    [Id("Order")]
    public Guid OrderId { get; set; }

    public static void Consume(Guid value) { }

    public void Trigger() =>
        // SIA003: OrderId is [Id("Order")] but Consume's parameter has no [Id].
        // Code fix: add [Id("Order")] to Consume's value parameter.
#pragma warning disable SIA003
        Consume(OrderId);
#pragma warning restore SIA003
}

#endregion
