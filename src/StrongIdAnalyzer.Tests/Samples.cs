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
        // With conventions both sides carry inferred tags, so this is SIA001.
#pragma warning disable SIA001
        service.GetOrderAmount(order.CustomerId);
#pragma warning restore SIA001
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
#pragma warning disable SIA001
        service.GetOrderAmount(order.CustomerId);
#pragma warning restore SIA001
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
#pragma warning disable SIA001
        ProcessOrder(CustomerId);
#pragma warning restore SIA001
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
#pragma warning disable SIA002
        ProcessOrder(Raw);
#pragma warning restore SIA002
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
#pragma warning disable SIA003
        Consume(OrderId);
#pragma warning restore SIA003
}

#endregion

#region InheritanceAbstractClassExplicit

namespace InheritanceAbstractClassExplicit
{
    // Explicit [Id(...)] values match what the convention would infer, so SIA005
    // would warn on each one. Suppressed here because the whole point of this
    // snippet is to spell out each tag by hand.
#pragma warning disable SIA005
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
#pragma warning restore SIA005

    public static class Usage
    {
        public static void Foo(Guid child1Id, Guid baseId) { }

        public static void Run()
        {
            var child1 = new Child1();
            Foo(child1.Id, child1.Id); // OK: child1.Id is tagged {"Child1","Base"}

            var child2 = new Child2();
#pragma warning disable SIA001
            Foo(child2.Id, child2.Id); // SIA001 on arg 1: {"Child2","Base"} is missing "Child1"
#pragma warning restore SIA001
        }
    }
}

#endregion

#region InheritanceAbstractClassConvention

namespace InheritanceAbstractClassConvention
{
    // SIA004 fires because Base/Child1/Child2 also exist in the interface-convention
    // scenario below; in real code this would never happen because there's only one
    // definition per name. Suppressed here so the snippets can showcase every shape.
#pragma warning disable SIA004
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
#pragma warning restore SIA004

    public static class Usage
    {
        public static void Foo(Guid child1Id, Guid baseId) { }

        public static void Run()
        {
            var child1 = new Child1();
            Foo(child1.Id, child1.Id); // OK: override chain gives {"Child1","Base"}

            var child2 = new Child2();
#pragma warning disable SIA001
            Foo(child2.Id, child2.Id); // SIA001 on arg 1: convention gives {"Child2","Base"}
#pragma warning restore SIA001
        }
    }
}

#endregion

#region InheritanceInterfaceExplicit

namespace InheritanceInterfaceExplicit
{
#pragma warning disable SIA005
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
#pragma warning restore SIA005

    public static class Usage
    {
        public static void Foo(Guid child1Id, Guid baseId) { }

        public static void Run()
        {
            var child1 = new Child1();
            Foo(child1.Id, child1.Id); // OK: interface walk adds "Base" next to "Child1"

            var child2 = new Child2();
#pragma warning disable SIA001
            Foo(child2.Id, child2.Id); // SIA001 on arg 1: {"Child2","Base"} is missing "Child1"
#pragma warning restore SIA001
        }
    }
}

#endregion

#region InheritanceInterfaceConvention

namespace InheritanceInterfaceConvention
{
    // See the note on InheritanceAbstractClassConvention — same name-collision
    // suppression rationale.
#pragma warning disable SIA004
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
#pragma warning restore SIA004

    public static class Usage
    {
        public static void Foo(Guid child1Id, Guid baseId) { }

        public static void Run()
        {
            var child1 = new Child1();
            Foo(child1.Id, child1.Id); // OK: convention tags {"Child1","Base"}

            var child2 = new Child2();
#pragma warning disable SIA001
            Foo(child2.Id, child2.Id); // SIA001 on arg 1: {"Child2","Base"} is missing "Child1"
#pragma warning restore SIA001
        }
    }
}

#endregion
