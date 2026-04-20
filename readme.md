# <img src="/src/icon.png" height="30px"> StrongIdAnalyzer

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
        service.GetOrderAmount(order.CustomerId);
}
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L14-L46' title='Snippet source file'>snippet source</a> | <a href='#snippet-BuggyExample' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The bug is the call to `service.GetOrderAmount(order.CustomerId)` — a customer's `Guid` is passed into a method expecting an order's `Guid`. Both are `Guid`, so the compiler is happy; at runtime the caller gets a `KeyNotFoundException`, or worse, if the `Guid` coincidentally hits a populated dictionary, silently wrong data.


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
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L49-L80' title='Snippet source file'>snippet source</a> | <a href='#snippet-SilentMismatch' title='Start of snippet'>anchor</a></sup>
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
        // disabled so the sample code will build
#pragma warning disable SIA001
        service.GetOrderAmount(order.CustomerId);
#pragma warning restore SIA001
}
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L82-L120' title='Snippet source file'>snippet source</a> | <a href='#snippet-FixedExample' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `IdAttribute` is source-generated into the consuming compilation — no runtime dependency on any attributes assembly. Install the analyzer package and start tagging.


## Alternatives, and why this one

The "cross-wired IDs" problem has three established families of solutions in .NET. Each makes a different trade between enforcement strength, runtime cost, and integration effort.

### 1. Hand-rolled wrapper types

A `readonly record struct CustomerId(Guid Value)` per domain type. Conversions are explicit; the compiler enforces type separation with no analyzer needed.

* **Pros:** strongest guarantee — a `Guid` cannot flow into a `CustomerId` slot without an explicit cast. Works uniformly at runtime and compile time (deserialization that produces the wrong wrapper is a serialization bug, not a silent identity mixup).
* **Cons:**
   * **Every library in the stack has to learn the type.** EF Core won't map `CustomerId` to a column without a `ValueConverter`. System.Text.Json won't read or write it without a `JsonConverter`. ASP.NET Core route/query/form binding needs a `TypeConverter` or custom `IModelBinder`. Dapper needs an `ITypeHandler`. gRPC/Protobuf needs a custom serializer or a `.proto` message. Logging producers, cache serializers, hash keys, OpenTelemetry tags, Swagger schemas, and message-bus contracts each need their own adapter. The cost is paid *per wrapper type × per integration*.
   * **Primitives can no longer cross domain boundaries.** The wrapper definitions have to live somewhere every consumer can reference — typically a shared `Domain.Core` / `SharedKernel` assembly. Independent bounded contexts that previously exchanged `Guid`s across a boundary now share a type dependency; services that serialize to a wire format outside the team's control (legacy SOAP, a partner's REST API, a third-party queue) still have to round-trip through the primitive and re-wrap, so the guarantee disappears at the edges anyway.
   * **High cost to retrofit.** Introducing a wrapper into an existing codebase touches every call site that reads or passes the ID. Equality, `ToString()`, comparison, and debugger display all change shape; a column type change may require a database migration or at minimum a regenerated EF model snapshot.
* **Best fit:** greenfield domain models where the team controls the full stack end-to-end and the ceremony is worth the permanent guarantee.

### 2. Source-generated wrapper types

Libraries like [`StronglyTypedId`](https://github.com/andrewlock/StronglyTypedId) (Andrew Lock) and [`Vogen`](https://github.com/SteveDunn/Vogen) (Steve Dunn) automate family #1. Declaring `[StronglyTypedId] public partial record struct CustomerId;` triggers the generator to emit the struct, equality, converters, and integration glue.

* **Pros:** same runtime guarantee as hand-rolling with a fraction of the boilerplate. Generators ship converters for the *common* stacks out of the box — EF Core, Dapper, System.Text.Json, Newtonsoft, Swashbuckle.
* **Cons:**
   * **The wrapper type is still a custom type at runtime.** The integration surface is smaller than hand-rolling (the generator writes the common converters) but not zero — any stack the generator doesn't target needs a hand-written adapter. "Does the serializer / ORM / binder / cache / bus know about `CustomerId`?" becomes a checklist item for every new dependency.
   * **Same domain-coupling problem as #1.** The generated wrapper types have to be visible to every project that touches the ID. Two services that were previously decoupled via a primitive `Guid` on a message contract now need a shared types assembly — or they marshal through the primitive at the boundary and lose the guarantee on the wire.
   * **Big-bang migration.** Converting a primitive-ID codebase to wrappers is an all-or-nothing change across every call site — half-adoption is impossible because a `CustomerId` and a `Guid` don't implicitly convert.
* **Best fit:** new projects, or projects already paying the migration cost to get the runtime-level guarantee where the integration stack falls inside the generator's supported surface.

### 3. Nominal type aliases / phantom types

C# 12 `using CustomerId = System.Guid;` at file scope, or generic `Id<Customer>` phantom types. Both *look* like type safety but aren't — the aliases are structural (a `CustomerId` is still exactly a `Guid` to the compiler), and phantom types degrade to primitives across API boundaries that aren't generic-aware.

* **Pros:** zero runtime cost, zero integration friction.
* **Cons:** no actual enforcement. The compiler treats `CustomerId` and `OrderId` as the same type. Useful as documentation, not as a guarantee.
* **Best fit:** readability, not correctness.

### 4. This project — compile-time tagging

`[Id("Customer")]` on the primitive itself. The analyzer enforces separation at build time; the runtime type stays `Guid`/`int`/`string`.

* **Pros:**
   * **No new runtime type — the primitive stays a primitive.** EF Core columns, JSON payloads, ASP.NET binders, Dapper parameters, `Dictionary<Guid, …>`, and every serializer, logger, cache, and message bus already in the stack keep working unchanged. No `ValueConverter`s, no `JsonConverter`s, no `IModelBinder`s, no per-wrapper-type adapters.
   * **No shared-type coupling between domains.** Two bounded contexts (or two services) can each tag a `Guid Id` with a local `[Id("...")]` independently and still exchange the value as a plain `Guid` across any boundary — no shared `Domain.Core` assembly, no wire-format adapter. The tag is metadata on the declaration; the wire sees only the primitive.
   * **Incremental adoption.** Tag the declarations that matter, ignore the rest. No big-bang refactor — a codebase can go from zero tagged IDs to partially tagged to fully tagged without ever being in a broken state. Naming convention covers the common `Id` / `XxxId` case so most declarations need no attribute at all.
   * **Inheritance and interface hierarchies are modeled** (see [Inheritance and covariant Id tagging](#inheritance-and-covariant-id-tagging)) — covariant tag sets mean `child.Id` satisfies parameters tagged for either the derived or base type.
* **Cons:** enforcement is **compile-time only**. A `Guid` deserialized from an untrusted source isn't checked; flow through `object` / `dynamic` / reflection isn't tracked; expressions like ternaries, casts, and locals are intentionally `Unknown` (see [Sources the analyzer can resolve](#sources-the-analyzer-can-resolve)) to keep noise low, which means a few code shapes slip through. Where a runtime guarantee is required regardless of the code path, use family #1 or #2 instead.
* **Best fit:** existing codebases where wrapper types would require a sprawling migration, or teams that want the compile-time catch without changing their serialization, ORM, or transport stack.

### Picking between them

| Need | Pick |
| --- | --- |
| Runtime guarantee that survives deserialization, reflection, and `object`-typed flows | #1 or #2 |
| New project, willing to pay integration cost up front | #2 |
| Existing project, want compile-time catch without touching serialization/ORM/transport | **this** |
| Documentation only, no real enforcement | #3 |

This analyzer is deliberately complementary to families #1 and #2 — not a replacement. Projects already running `StronglyTypedId` or `Vogen` don't need it. For teams that have evaluated the cost of introducing wrapper types across a large existing codebase and decided it isn't worth paying, this is the alternative that provides most of the catch without the migration.


## Naming conventions

Most declarations don't need an explicit `[Id("...")]` — the analyzer infers a tag from two naming rules. Whatever falls through the rules stays untagged (no tag, no diagnostic) until an explicit attribute is added.

### The two rules

1. **`Id` on a type** — a property or field named exactly `Id` gets the **containing type's name** as its tag.

    ```cs
    public class Customer
    {
        public Guid Id { get; set; } // tag: "Customer"
    }
    ```

2. **`<Xxx>Id`** — a property, field, **or** parameter whose name ends in `Id` gets the prefix as its tag. The first character is upper-cased so camelCase parameters line up with PascalCase members.

    ```cs
    public Guid CustomerId { get; set; } // tag: "Customer"
    public void Handle(Guid orderId) { } // tag: "Order" (first char upper-cased)
    public Guid ShipmentId;              // tag: "Shipment"
    ```

    The prefix is the whole identifier minus the trailing `Id` — `OldCustomerId` tags as `"OldCustomer"`, not `"Customer"`.

### What does *not* get a convention tag

- **Parameters named exactly `id`** — rule 1 doesn't apply to parameters (a bare `id` has no containing-type equivalent, and parameters should be name-driven so method signatures read cleanly). Write `orderId`, or add `[Id("Order")]` explicitly.
- **Names that are exactly `Id`** (on parameters) or shorter than 3 characters under rule 2 — so a property literally named `Id` only matches rule 1, never rule 2.
- **Anonymous-type properties** — `new { CustomerId = x }`. They can't carry `[Id]`, so tagging them would produce diagnostics the user has no way to silence (matters for EF `HasIndex`, LINQ projections, etc.).
- **Indexers** — `this[Guid id]` never participates.
- **Implicitly-declared fields** — backing fields, primary-constructor capture fields, and similar compiler-synthesized members.
- **Members declared in referenced metadata** — BCL and third-party members (`Diagnostic.Id`, `EventArgs`, …) never receive convention tags. If it were otherwise, any library property named `Id` would suddenly carry a tag the user can't change.

### Precedence

When resolving a symbol's tag set, the analyzer consults these sources in order and stops at the first that produces a tag:

1. **Explicit `[Id]` / `[UnionId]`** directly on the symbol.
2. **Inherited explicit attribute** via the property's override / interface-implementation chain, or the parameter's matching slot on overridden / implemented methods.
3. **Record primary-constructor parameter attribute** bridged onto the synthesized property (see "Record primary-constructor parameters" below).
4. **Naming convention** (rules 1 and 2 above).

At access sites (`child.Id`), covariant receiver-type walking unions the current level's tags with every parent-type tag between the receiver type and the declaring type — see "Inheritance and covariant Id tagging".

### Interaction with diagnostics

- **SIA004** only fires for rule 1 collisions — two `public Guid Id` declarations on different types both claiming the same type name. Rule 2 collisions across types are the *intended* matching behavior (`Order.CustomerId` and `Invoice.CustomerId` both referring to "Customer").
- **SIA005** (redundant `[Id]`) fires only when an explicit `[Id("X")]` exactly equals what the convention would infer. It ships a fixer that removes the attribute.

### Overriding the convention

Any `[Id("...")]` / `[UnionId("...")]` on the symbol wins over the convention, so the tag can be broadened, narrowed, or renamed at will:

```cs
public class Customer
{
    [Id("Person")] // overrides the "Customer" convention tag
    public Guid Id { get; set; }
}
```


## Diagnostics

| ID     | Severity | Code fix | Summary                                                                 |
|--------|----------|----------|-------------------------------------------------------------------------|
| SIA001 | Warning  | Yes      | Both sides tagged with different `[Id]` values                          |
| SIA002 | Warning  | Yes      | Source missing `[Id]`; target has one                                   |
| SIA003 | Warning  | Yes      | Source has `[Id]`; target missing one                                   |
| SIA004 | Error    | —        | Two `public Guid Id` declarations collide under the naming convention   |
| SIA005 | Warning  | Yes      | `[Id("x")]` is redundant — the naming convention already infers `"x"`   |
| SIA006 | Warning  | Yes      | `[UnionId("x")]` with a single option should be `[Id("x")]`             |


### SIA001 — Id mismatch

Fires when both operands carry `[Id]` and the tag values differ. The analyzer sees an unambiguous cross-domain flow (e.g. a `Customer` id passed where an `Order` id is expected) and refuses it.

<!-- snippet: SIA001Example -->
<a id='snippet-SIA001Example'></a>
```cs
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
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L122-L138' title='Snippet source file'>snippet source</a> | <a href='#snippet-SIA001Example' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


#### Code fixes

For flow-style mismatches (argument, assignment, property/field initializer) the analyzer attaches the **target** declaration as the fix site and offers:

 * **Change attribute on `<kind> '<name>'` to `[Id("<source tag>")]`** — when the target already carries an explicit `[Id]` / `[UnionId]`, replaces it with the source's tag.
 * **Add `[Id("<source tag>")]` to `<kind> '<name>'`** — when the target is untagged (its current tag came from naming convention), adds the attribute.
 * **Rename `<kind> '<name>'` to `<sourceTag>Id`** — when the target has no explicit attribute and its name matches the `XxxId` convention. First-character case is preserved (`bidId` → `treasuryBidId`, `BidId` → `TreasuryBidId`). Works for parameters, properties, and single-declarator fields.

The `<source tag>` is the receiver's static type, not the declaring type of the `Id` member. For `treasuryBid.Id` where `Id` is inherited from `BaseEntity`, the fix suggests `TreasuryBid` — what reads locally at the call site — rather than `BaseEntity`.

The fixer always changes the *target* side because the analyzer picks a direction by fix site, not by blaming. If the source annotation is the one that's actually wrong, fix it by hand — a silent cross-domain rewrite would be a behavior change dressed as a fix.

No fix is offered for equality-operand mismatches (`a == b`): both sides are symmetric and there's no distinguished "target" to blame. The mechanical options — replace with `false` / `true`, since cross-domain equality is always false — would be behavior changes, not corrections. Manual resolution is the only safe path there.


### SIA002 — Source missing `[Id]`

Fires when the source (argument, right-hand side of an assignment, initializer, or one operand of an equality check) has no `[Id]` but the target carries one. The fix adds `[Id("<target value>")]` to the source symbol's declaration.

<!-- snippet: SIA002Example -->
<a id='snippet-SIA002Example'></a>
```cs
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
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L140-L157' title='Snippet source file'>snippet source</a> | <a href='#snippet-SIA002Example' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Suppressed when the untagged source lives in referenced metadata (e.g. `Guid.Empty`, a third-party property) — library authors can't apply `[Id]`, so the warning would offer no actionable fix.


### SIA003 — Target missing `[Id]`

Fires when the source carries `[Id]` but the target (parameter, assignment left-hand side, initializer) does not. The fix adds `[Id("<source value>")]` to the target symbol's declaration.

<!-- snippet: SIA003Example -->
<a id='snippet-SIA003Example'></a>
```cs
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
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L159-L176' title='Snippet source file'>snippet source</a> | <a href='#snippet-SIA003Example' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

SIA003 is suppressed when the tag can't meaningfully survive:

 * **Library metadata targets** — BCL and third-party members (`Dictionary<Guid, T>.this[Guid]`, `Guid.Equals(Guid)`, `object.Equals(object)`). Library authors can't apply `[Id]`.
 * **`object` parameters / properties / fields** — logging, serialization, message buses. The tag is erased through `object` anyway.
 * **Unconstrained generic type parameters (`T`)** — identity methods, container helpers. Generics carry no domain intent.
 * **Targets in a suppressed namespace** — by default `System*` and `Microsoft*` (see below).
 * **Equality comparisons** — `==` / `!=` operands are symmetric, so only SIA001 / SIA002 apply.


## `[UnionId(...)]`

Sometimes a member legitimately accepts any one of several domain types — a generic lookup helper, a cache key that can be either a Customer or a Product. The package ships `[UnionId("Customer", "Product")]` for that case.

Two values are **compatible** when their tag sets overlap on at least one entry:

| Source                           | Target                                  | Compatible?     |
|----------------------------------|------------------------------------------|-----------------|
| `[UnionId("Customer","Product")]`| `[UnionId("Customer","Product")]`        | yes (full)      |
| `[UnionId("Customer","Product")]`| `[UnionId("Customer","Order")]`          | yes (`Customer`)|
| `[UnionId("Customer","Product")]`| `[Id("Product")]`                        | yes             |
| `[Id("Product")]`                | `[UnionId("Customer","Product")]`        | yes             |
| `[UnionId("Customer","Product")]`| `[UnionId("Order","Supplier")]`          | **no** → SIA001 |

A `[UnionId("x")]` with a single option is always a mistake — use `[Id("x")]`. SIA006 flags it and ships a fixer that rewrites the attribute in place.


## Inheritance and covariant Id tagging

A property (or field) named `Id` inherited from a base type carries tags from **every** level of the chain its receiver walks — the base type's tag **and** the derived type's tag. At an access site like `child1.Id`, the tag set is the union of:

 * Every explicit `[Id("...")]` found on the property, its `override` chain, and its interface impls.
 * Every naming-convention tag for types in the receiver's static-type chain between the receiver type and the member's declaring type (inclusive), where the member was not redeclared.

Matching rules use set containment — a single-tag parameter is satisfied if its tag appears anywhere in the source's set. So given `public static void Foo(Guid child1Id, Guid baseId)`:

 * `Foo(child1.Id, child1.Id)` is **OK** — `child1.Id`'s set `{"Child1", "Base"}` covers both `"Child1"` and `"Base"`.
 * `Foo(child2.Id, child2.Id)` fires **SIA001 on the first argument only** — `{"Child2", "Base"}` covers `"Base"` but not `"Child1"`.

The same semantics work across four common shapes:

### Abstract class + explicit `[Id]` on every level

<!-- snippet: InheritanceAbstractClassExplicit -->
<a id='snippet-InheritanceAbstractClassExplicit'></a>
```cs
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
            // OK: child1.Id is tagged {"Child1","Base"}
            Foo(child1.Id, child1.Id);

            var child2 = new Child2();
#pragma warning disable SIA001
            // SIA001 on arg 1: {"Child2","Base"} is missing "Child1"
            Foo(child2.Id, child2.Id);
#pragma warning restore SIA001
        }
    }
}
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L178-L224' title='Snippet source file'>snippet source</a> | <a href='#snippet-InheritanceAbstractClassExplicit' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Abstract class + naming convention only

<!-- snippet: InheritanceAbstractClassConvention -->
<a id='snippet-InheritanceAbstractClassConvention'></a>
```cs
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
            // OK: override chain gives {"Child1","Base"}
            Foo(child1.Id, child1.Id);

            var child2 = new Child2();
#pragma warning disable SIA001
            // SIA001 on arg 1: convention gives {"Child2","Base"}
            Foo(child2.Id, child2.Id);
#pragma warning restore SIA001
        }
    }
}
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L226-L269' title='Snippet source file'>snippet source</a> | <a href='#snippet-InheritanceAbstractClassConvention' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Interface + explicit `[Id]` on every level

<!-- snippet: InheritanceInterfaceExplicit -->
<a id='snippet-InheritanceInterfaceExplicit'></a>
```cs
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
            // OK: interface walk adds "Base" next to "Child1"
            Foo(child1.Id, child1.Id);

            var child2 = new Child2();
#pragma warning disable SIA001
            // SIA001 on arg 1: {"Child2","Base"} is missing "Child1"
            Foo(child2.Id, child2.Id);
#pragma warning restore SIA001
        }
    }
}
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L271-L314' title='Snippet source file'>snippet source</a> | <a href='#snippet-InheritanceInterfaceExplicit' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Interface + naming convention only

<!-- snippet: InheritanceInterfaceConvention -->
<a id='snippet-InheritanceInterfaceConvention'></a>
```cs
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
            // OK: convention tags {"Child1","Base"}
            Foo(child1.Id, child1.Id);

            var child2 = new Child2();
#pragma warning disable SIA001
            // SIA001 on arg 1: {"Child2","Base"} is missing "Child1"
            Foo(child2.Id, child2.Id);
#pragma warning restore SIA001
        }
    }
}
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L316-L358' title='Snippet source file'>snippet source</a> | <a href='#snippet-InheritanceInterfaceConvention' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Suppressing namespaces

Diagnostics SIA002 and SIA003 are suppressed when the fix-target lives in a namespace matching the configured list. Defaults:

 * `System*`
 * `Microsoft*`

Patterns:

 * A trailing `*` makes it a prefix match — `System*` matches the `System` namespace itself *and* any `System.<anything>` child namespace, but not unrelated roots like `SystemX`.
 * Without `*`, the pattern is an exact namespace match.

Override via `.editorconfig` — user value fully replaces the defaults:

```editorconfig
[*.cs]
strongidanalyzer.suppressed_namespaces = System*,Microsoft*,MyCompany.Logging*
```

Set the value to empty to disable namespace suppression entirely (the metadata-target, `object`, and generic-`T` suppressions still apply):

```editorconfig
[*.cs]
strongidanalyzer.suppressed_namespaces =
```



## Analyzed sites

Diagnostics fire on:

 * Method, constructor, indexer, and delegate arguments
 * Direct assignments (`=`), including inside object initializers
 * Property and field inline initializers
 * Equality comparisons (`==`, `!=`) — SIA001 when both sides carry different `[Id]` values; SIA002 (with fix) when only one side is tagged and the other is a user-owned untagged member. Comparisons against `Guid.Empty`, literals, locals, and method results stay silent.

Targets declared in referenced metadata (BCL, third-party libraries — e.g. `Dictionary<Guid, T>.this[Guid]`, `Guid.Equals(Guid)`, `object.Equals(object)`) are treated as boundary APIs: SIA003 is suppressed for them because the library author has no way to apply `[Id]`.


## Sources the analyzer can resolve

The source of a value is resolved when it is one of:

 * A property reference (`obj.Prop`)
 * A field reference (`obj._field`)
 * A parameter reference (method argument, lambda parameter, etc.)

Other sources are treated as **unknown** and suppress all three diagnostics. This avoids noise on every `Guid.NewGuid()`, every local variable, and every primitive that happens to pass through an expression.

### Literals

No `[Id]` can be attached to a literal, so there is nothing to compare against.

```cs
void Consume([Id("Order")] Guid value) { }

Consume(Guid.Empty);                                 // unknown — literal-like
Consume(new Guid("00000000-0000-0000-0000-000000000000")); // unknown — constructor
```

### Local variables

Locals don't support attributes in C#, so the analyzer can't resolve a tag for them — even if the value that flowed into the local was originally tagged.

```cs
[Id("Customer")] Guid source = default;

var copy = source;      // tag does not flow through the local
Consume(copy);          // unknown — local reference
```

### Method invocations

Untagged return values stay **unknown** — no noise on `Guid.NewGuid()` and friends. To flow a tag through a return value, annotate the method with `[return: Id("...")]` or `[return: UnionId("...", "...")]`. The tag is read from the method's own return attributes, plus any method it overrides or interface member it implements.

```cs
Guid GetOrderId() => Guid.NewGuid();

Consume(GetOrderId());   // unknown — untagged return
Consume(Guid.NewGuid()); // unknown — untagged return

[return: Id("Order")]
Guid LoadOrderId() => Guid.NewGuid();

Consume(LoadOrderId());  // OK — tag matches
```

### `await` expressions

The analyzer unwraps `await` to the underlying operation, so `[return: Id]` on an async method flows to the awaited value at the call site.

```cs
Task<Guid> LoadOrderIdAsync() => Task.FromResult(Guid.NewGuid());

Consume(await LoadOrderIdAsync()); // unknown — untagged async return

[return: Id("Order")]
Task<Guid> LoadTaggedOrderIdAsync() => Task.FromResult(Guid.NewGuid());

Consume(await LoadTaggedOrderIdAsync()); // OK — tag flows through await
```

### Compound expressions

Conditionals, casts, pattern results, null-coalescing, and any other expression shape collapse to "unknown" — even when every operand would individually resolve.

```cs
[Id("Order")]    Guid a = default;
[Id("Customer")] Guid b = default;

Consume(condition ? a : b); // unknown — ternary
Consume((Guid)(object)a);   // unknown — cast chain
Consume(a == Guid.Empty ? b : a); // unknown — conditional result
```


## Record primary-constructor parameters

When a record is declared with a primary constructor, `[Id(...)]` / `[UnionId(...)]` written on a parameter applies to both the parameter and the auto-generated property. The C# compiler leaves the attribute physically on the parameter (its default target), so reading `record.Member` would otherwise look unattributed. The analyzer bridges this gap: a property synthesized from a primary-constructor parameter inherits the parameter's Id-family attributes for analysis purposes.

<!-- snippet: RecordPrimaryCtorParameter -->
<a id='snippet-RecordPrimaryCtorParameter'></a>
```cs
public record Holder([Id("Order")] Guid Value);

public static class RecordUsage
{
    public static void Consume([Id("Order")] Guid value) { }

    public static void Use(Holder holder) =>
        // no diagnostic — attribute flows to property
        Consume(holder.Value);
}
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L360-L373' title='Snippet source file'>snippet source</a> | <a href='#snippet-RecordPrimaryCtorParameter' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

An explicit `[property: Id(...)]` on the property still wins — if both targets are attributed, the property's own attribute is used. Naming-convention inference (for properties named `Id` or `XxxId`) is only consulted after both the property's and the parameter's explicit attributes come up empty.


## Icon

[Escher Triangle](https://thenounproject.com/icon/escher-triangle-358766/)
