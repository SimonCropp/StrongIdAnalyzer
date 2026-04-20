// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedParameter.Local
// ReSharper disable ClassNeverInstantiated.Local
// ReSharper disable MemberCanBePrivate.Local
// ReSharper disable CollectionNeverUpdated.Local
// ReSharper disable UnusedParameter.Global
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable MemberCanBePrivate.Global
#pragma warning disable CS0414
#pragma warning disable CA1822
#pragma warning disable CA1002
#pragma warning disable SIA001
#pragma warning disable SIA002
#pragma warning disable SIA003
#pragma warning disable SIA004
#pragma warning disable SIA005

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
    // [Id("Order")] / [Id("Customer")] are inferred by naming convention — no attributes needed.
    public Guid Id { get; set; }

    public Guid CustomerId { get; set; }

    public decimal Amount { get; set; }
}

public class TypedOrderService
{
    Dictionary<Guid, TypedOrder> orders = [];

    public decimal GetOrderAmount(Guid orderId) =>
        orders[orderId].Amount;
}

public static class FixedUsage
{
    public static decimal Run(TypedOrderService service, TypedOrder order) =>
        // Compile-time SIA001: passing a [Id("Customer")] Guid to an [Id("Order")] parameter.
        service.GetOrderAmount(order.CustomerId);
}

#endregion

#region SIA001Example

public class SIA001Sample
{
    // CustomerId is tagged "Customer" by naming convention.
    public Guid CustomerId { get; set; }

    public static void ProcessOrder(Guid orderId) { }

    public void Trigger() =>
        // SIA001: argument tagged [Id("Customer")] passed to parameter tagged [Id("Order")].
        ProcessOrder(CustomerId);
}

#endregion

#region SIA002Example

public class SIA002Sample
{
    // Name doesn't match the `Id`/`XxxId` convention, so no automatic tag.
    public Guid Raw { get; set; }

    public static void ProcessOrder(Guid orderId) { }

    public void Trigger() =>
        // SIA002: Raw has no [Id] but is passed to an [Id("Order")] parameter.
        // Code fix: add [Id("Order")] to Raw's declaration.
        ProcessOrder(Raw);
}

#endregion

#region SIA003Example

public class SIA003Sample
{
    // OrderId is tagged "Order" by naming convention.
    public Guid OrderId { get; set; }

    public static void Consume(Guid value) { }

    public void Trigger() =>
        // SIA003: OrderId is [Id("Order")] but Consume's parameter has no [Id].
        // Code fix: add [Id("Order")] to Consume's value parameter.
        Consume(OrderId);
}

#endregion

#region InheritanceAbstractClassExplicit

namespace InheritanceAbstractClassExplicit
{
    // Explicit [Id(...)] values match what the convention would infer, so SIA005
    // would warn on each one.
    public abstract class Base
    {
        [Id("Base")]
        public abstract Guid Id { get; set; }
    }

    public class Child1 : Base
    {
        [Id("Child1")]
        public override Guid Id { get; set; }
    }

    public class Child2 : Base
    {
        [Id("Child2")]
        public override Guid Id { get; set; }
    }

    public static class Usage
    {
        public static void Foo(Guid child1Id, Guid baseId) { }

        public static void Run()
        {
            var child1 = new Child1();
            // OK: child1.Id is tagged {"Child1","Base"}
            Foo(child1.Id, child1.Id);

            var child2 = new Child2();
            // SIA001 on arg 1: {"Child2","Base"} is missing "Child1"
            Foo(child2.Id, child2.Id);
        }
    }
}

#endregion

#region InheritanceAbstractClassConvention

namespace InheritanceAbstractClassConvention
{
    // SIA004 fires because Base/Child1/Child2 also exist in the interface-convention
    // scenario below; in real code this would never happen because there's only one
    // definition per name.
    public abstract class Base
    {
        public abstract Guid Id { get; set; }
    }

    public class Child1 : Base
    {
        public override Guid Id { get; set; }
    }

    public class Child2 : Base
    {
        public override Guid Id { get; set; }
    }

    public static class Usage
    {
        public static void Foo(Guid child1Id, Guid baseId) { }

        public static void Run()
        {
            var child1 = new Child1();
            // OK: override chain gives {"Child1","Base"}
            Foo(child1.Id, child1.Id);

            var child2 = new Child2();
            // SIA001 on arg 1: convention gives {"Child2","Base"}
            Foo(child2.Id, child2.Id);
        }
    }
}

#endregion

#region InheritanceInterfaceExplicit

namespace InheritanceInterfaceExplicit
{
    public interface Base
    {
        [Id("Base")]
        Guid Id { get; set; }
    }

    public class Child1 : Base
    {
        [Id("Child1")]
        public Guid Id { get; set; }
    }

    public class Child2 : Base
    {
        [Id("Child2")]
        public Guid Id { get; set; }
    }

    public static class Usage
    {
        public static void Foo(Guid child1Id, Guid baseId) { }

        public static void Run()
        {
            var child1 = new Child1();
            // OK: interface walk adds "Base" next to "Child1"
            Foo(child1.Id, child1.Id);

            var child2 = new Child2();
            // SIA001 on arg 1: {"Child2","Base"} is missing "Child1"
            Foo(child2.Id, child2.Id);
        }
    }
}

#endregion

#region InheritanceInterfaceConvention

namespace InheritanceInterfaceConvention
{
    // See the note on InheritanceAbstractClassConvention — same name-collision
    // suppression rationale.
    public interface Base
    {
        Guid Id { get; set; }
    }

    public class Child1 : Base
    {
        public Guid Id { get; set; }
    }

    public class Child2 : Base
    {
        public Guid Id { get; set; }
    }

    public static class Usage
    {
        public static void Foo(Guid child1Id, Guid baseId) { }

        public static void Run()
        {
            var child1 = new Child1();
            // OK: convention tags {"Child1","Base"}
            Foo(child1.Id, child1.Id);

            var child2 = new Child2();
            // SIA001 on arg 1: {"Child2","Base"} is missing "Child1"
            Foo(child2.Id, child2.Id);
        }
    }
}

#endregion

#region RecordPrimaryCtorParameter

public record Holder([Id("Order")] Guid Value);

public static class RecordUsage
{
    public static void Consume([Id("Order")] Guid value) { }

    public static void Use(Holder holder) =>
        // no diagnostic — attribute flows to property
        Consume(holder.Value);
}

#endregion

namespace TaggedCollectionSamples
{

#region TaggedCollectionLinqLambda

public class CustomerList
{
    // [Id] on a single-T collection describes its elements. The tag flows into any
    // site that extracts an element: lambda parameters, foreach variables, .First()
    // results, and through chains of LINQ-shape element-preserving calls.
    [Id("Customer")]
    public IEnumerable<Guid> Ids { get; set; } = [];
}

public class OrderWriter
{
    public void Consume([Id("Order")] Guid value) { }

    public void Go(CustomerList list) =>
        // SIA001 on the argument: `id` inherits "Customer" from list.Ids, which is
        // then passed into a parameter tagged "Order".
        list.Ids.Select(id => { Consume(id); return id; }).ToList();
}

#endregion

#region TaggedCollectionForEach

public class CustomerScan
{
    [Id("Customer")]
    public IEnumerable<Guid> Ids { get; set; } = [];

    public void ConsumeOrder([Id("Order")] Guid value) { }

    public void Go()
    {
        foreach (var id in Ids)
        {
            // SIA001: `id` carries the Customer tag inherited from the collection.
            ConsumeOrder(id);
        }
    }
}

#endregion

#region TaggedCollectionUserExtension

public static class Paged
{
    // An extension with shape `IEnumerable<T> → IEnumerable<T>` is treated as
    // element-preserving, so element tags flow through it just like through
    // `Where`, `Take`, and `OrderBy`.
    public static IEnumerable<T> TakePage<T>(this IEnumerable<T> source, int page, int size) =>
        source.Skip(page * size).Take(size);
}

public class PagedReader
{
    [Id("Customer")]
    public IEnumerable<Guid> Ids { get; set; } = [];

    [Id("Order")]
    public Guid LatestId { get; set; }

    // SIA001 on the assignment: .First() returns a Customer-tagged Guid after
    // passing through the user-defined element-preserving extension.
    public void Copy() => LatestId = Ids.TakePage(0, 10).First();
}

#endregion

#region UnsupportedMultiTCollection

public class CustomerOrderMap
{
    // [Id] on a Dictionary/KeyValuePair/tuple/grouping carries no element tag —
    // the analyzer can't tell whether the tag applies to K, V, or both. Flows
    // through these containers stay "unknown" and produce no diagnostics.
    [Id("Customer")]
    public Dictionary<Guid, string> OrdersByCustomer { get; set; } = [];
}

#endregion

}
