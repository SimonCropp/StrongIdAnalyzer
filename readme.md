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
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L9-L42' title='Snippet source file'>snippet source</a> | <a href='#snippet-BuggyExample' title='Start of snippet'>anchor</a></sup>
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
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L44-L75' title='Snippet source file'>snippet source</a> | <a href='#snippet-SilentMismatch' title='Start of snippet'>anchor</a></sup>
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
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L77-L115' title='Snippet source file'>snippet source</a> | <a href='#snippet-FixedExample' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `IdAttribute` is source-generated into your compilation — you don't take a runtime dependency on any attributes assembly. Just install the analyzer package and start tagging.


## Diagnostics

| ID     | Severity | Code fix | Summary                                          |
|--------|----------|----------|--------------------------------------------------|
| SIA001 | Warning  | —        | Both sides tagged with different `[Id]` values   |
| SIA002 | Warning  | Yes      | Source missing `[Id]`; target has one            |
| SIA003 | Warning  | Yes      | Source has `[Id]`; target missing one            |


### SIA001 — Id mismatch

Fires when both operands carry `[Id]` and the tag values differ. The analyzer sees an unambiguous cross-domain flow (e.g. a `Customer` id passed where an `Order` id is expected) and refuses it.

<!-- snippet: SIA001Example -->
<a id='snippet-SIA001Example'></a>
```cs
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
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L117-L133' title='Snippet source file'>snippet source</a> | <a href='#snippet-SIA001Example' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


#### Why SIA001 has no code fix

SIA001 is ambiguous by design. Given `OrderId == CustomerId`, the analyzer can see the two tags don't match, but it has no way to know whether the bug is:

 * the left operand (wrong property picked on one side)
 * the right operand (same, other side)
 * one of the `[Id]` annotations itself being wrong
 * or the comparison is intentional cross-domain logic that just happens to fail safely

Any auto-fix would be picking a side at random and silently rewriting logic. For equality specifically there's one mechanical option — "replace with `false`" (for `==`) or "`true`" (for `!=`), since cross-domain equality is always false — but that's a behavior change dressed as a fix, and if the user wanted that they'd delete the line. Manual resolution is the only safe path.


### SIA002 — Source missing `[Id]`

Fires when the source (argument, right-hand side of an assignment, initializer, or one operand of an equality check) has no `[Id]` but the target carries one. The fix adds `[Id("<target value>")]` to the source symbol's declaration.

<!-- snippet: SIA002Example -->
<a id='snippet-SIA002Example'></a>
```cs
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
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L135-L151' title='Snippet source file'>snippet source</a> | <a href='#snippet-SIA002Example' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Suppressed when the untagged source lives in referenced metadata (e.g. `Guid.Empty`, a third-party property) — library authors can't apply `[Id]`, so the warning would offer no actionable fix.


### SIA003 — Target missing `[Id]`

Fires when the source carries `[Id]` but the target (parameter, assignment left-hand side, initializer) does not. The fix adds `[Id("<source value>")]` to the target symbol's declaration.

<!-- snippet: SIA003Example -->
<a id='snippet-SIA003Example'></a>
```cs
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
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L153-L170' title='Snippet source file'>snippet source</a> | <a href='#snippet-SIA003Example' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Suppressed when the target is declared in referenced metadata (BCL, third-party libraries — e.g. `Dictionary<Guid, T>.this[Guid]`, `Guid.Equals(Guid)`, `object.Equals(object)`). SIA003 is not raised on equality comparisons (`==`, `!=`) — the two operands are symmetric, so it's SIA001 or SIA002 or nothing.



## Analyzed sites

Diagnostics fire on:

 * Method, constructor, indexer, and delegate arguments
 * Simple assignments (`=`), including inside object initializers
 * Property and field inline initializers
 * Equality comparisons (`==`, `!=`) — SIA001 when both sides carry different `[Id]` values; SIA002 (with fix) when only one side is tagged and the other is a user-owned untagged member. Comparisons against `Guid.Empty`, literals, locals, and method results stay silent.

Targets declared in referenced metadata (BCL, third-party libraries — e.g. `Dictionary<Guid, T>.this[Guid]`, `Guid.Equals(Guid)`, `object.Equals(object)`) are treated as boundary APIs: SIA003 is suppressed for them because the library author has no way to apply `[Id]`.


## Sources the analyzer can resolve

The source of a value is resolved when it is one of:

 * A property reference (`obj.Prop`)
 * A field reference (`obj._field`)
 * A parameter reference (method argument, lambda parameter, etc.)

Other sources — literals, local variables, method invocations, `await`, expressions — are treated as **unknown** and suppress all three diagnostics. This avoids noise on every `Guid.NewGuid()`, every local variable, and every primitive passed to an `[Id]` parameter.


## Icon

[Escher Triangle](https://thenounproject.com/icon/escher-triangle-358766/)
