# StrongIdAnalyzer

[![Build status](https://img.shields.io/appveyor/build/SimonCropp/StrongIdAnalyzer)](https://ci.appveyor.com/project/SimonCropp/StrongIdAnalyzer)
[![NuGet Status](https://img.shields.io/nuget/v/StrongIdAnalyzer.svg?label=StrongIdAnalyzer)](https://www.nuget.org/packages/StrongIdAnalyzer/)

Roslyn analyzer that prevents primitive ID values (`Guid`, `int`, `string`, etc.) from being crossed between domain types at compile time. Tag each ID declaration with `[Id("Customer")]`, `[Id("Order")]`, ... and the analyzer flags any assignment or argument that mixes them up.

This targets the same problem as [`StronglyTypedId`](https://github.com/andrewlock/StronglyTypedId) but without generating wrapper struct types — the runtime type stays as the primitive, so serializers, ORMs, and transport layers need no changes. The whole enforcement lives in a compile-time attribute.

**See [Milestones](../../milestones?state=closed) for release notes.**


## The problem

Primitive ID types give the compiler nothing to check. Two `Guid`s from two different entities look identical to it:

<!-- snippet: BuggyExample -->
<a id='snippet-BuggyExample'></a>
```cs
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
    readonly Dictionary<Guid, Order> orders = [];

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
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L10-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-BuggyExample' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The bug is the call to `service.GetOrderAmount(order.CustomerId)` — a customer's `Guid` is passed into a method expecting an order's `Guid`. Both are `Guid`, so the compiler is happy; at runtime you get a `KeyNotFoundException`, or worse, if the `Guid` coincidentally hits a populated dictionary, silently wrong data.


### A more insidious variant

<!-- snippet: SilentMismatch -->
<a id='snippet-SilentMismatch'></a>
```cs
public class Product
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public class EntityLookup
{
    readonly Dictionary<Guid, Customer> customers = [];
    readonly Dictionary<Guid, Product> products = [];

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
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L45-L76' title='Snippet source file'>snippet source</a> | <a href='#snippet-SilentMismatch' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## The fix

Tag every primitive ID with `[Id("<type>")]`. The analyzer then refuses to cross the streams:

<!-- snippet: FixedExample -->
<a id='snippet-FixedExample'></a>
```cs
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
    readonly Dictionary<Guid, TypedOrder> orders = [];

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
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L78-L116' title='Snippet source file'>snippet source</a> | <a href='#snippet-FixedExample' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `IdAttribute` is source-generated into your compilation — you don't take a runtime dependency on any attributes assembly. Just install the analyzer package and start tagging.


## Diagnostics

| ID     | Severity | Code fix | Description                                                   |
|--------|----------|----------|---------------------------------------------------------------|
| SIA001 | Warning  | —        | Id mismatch — both sides have `[Id]` but values differ        |
| SIA002 | Warning  | Yes      | Source has no `[Id]` while the target requires one            |
| SIA003 | Warning  | Yes      | Source has `[Id]` while the target has none                   |

SIA002 and SIA003 ship a code fix that adds `[Id("<value>")]` to the relevant declaration — the source symbol for SIA002, the target symbol for SIA003. SIA001 has no fix because picking which side to change requires human judgement.


## Analyzed sites

Diagnostics fire on:

 * Method, constructor, indexer, and delegate arguments
 * Simple assignments (`=`), including inside object initializers
 * Property and field inline initializers


## Sources the analyzer can resolve

The source of a value is resolved when it is one of:

 * A property reference (`obj.Prop`)
 * A field reference (`obj._field`)
 * A parameter reference (method argument, lambda parameter, etc.)

Other sources — literals, local variables, method invocations, `await`, expressions — are treated as **unknown** and suppress all three diagnostics. This avoids noise on every `Guid.NewGuid()`, every local variable, and every primitive passed to an `[Id]` parameter.


## Icon

[Escher Triangle](https://thenounproject.com/icon/escher-triangle-358766/)
