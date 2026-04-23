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
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L18-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-BuggyExample' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L52-L83' title='Snippet source file'>snippet source</a> | <a href='#snippet-SilentMismatch' title='Start of snippet'>anchor</a></sup>
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
        service.GetOrderAmount(order.CustomerId);
}
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L85-L120' title='Snippet source file'>snippet source</a> | <a href='#snippet-FixedExample' title='Start of snippet'>anchor</a></sup>
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
   * **No shared-type coupling between domains.** Two bounded contexts (or two services) can each tag a `Guid Id` with a local `[Id("...")]` independently and still exchange the value as a plain `Guid` across any boundary — no shared `Domain.Core` assembly, no wire-format adapter. The id is metadata on the declaration; the wire sees only the primitive.
   * **Incremental adoption.** Tag the declarations that matter, ignore the rest. No big-bang refactor — a codebase can go from zero tagged IDs to partially tagged to fully tagged without ever being in a broken state. Naming convention covers the common `Id` / `XxxId` case so most declarations need no attribute at all.
   * **Inheritance and interface hierarchies are modeled** (see [Inheritance and covariant Id tagging](#inheritance-and-covariant-id-tagging)) — covariant id sets mean `child.Id` satisfies parameters tagged for either the derived or base type.
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

Most declarations don't need an explicit `[Id("...")]` — the analyzer infers an id from two naming rules. Whatever falls through the rules stays untagged (no id, no diagnostic) until an explicit attribute is added.


### The two rules

1. **`Id` on a type** — a property or field named exactly `Id` gets the **containing type's name** as its id.

    ```cs
    public class Customer
    {
        // id: "Customer"
        public Guid Id { get; set; }
    }
    ```

2. **`<Xxx>Id`** — a property, field, **or** parameter whose name ends in `Id` gets the prefix as its id. The first character is upper-cased so camelCase parameters line up with PascalCase members.

    ```cs
    // id: "Customer"
    public Guid CustomerId { get; set; }

    // id: "Order" (first char upper-cased)
    public void Handle(Guid orderId) { }

    // id: "Shipment"
    public Guid ShipmentId;
    ```

    The prefix is the whole identifier minus the trailing `Id` — `OldCustomerId` resolves to `"OldCustomer"`, not `"Customer"`.

### What does *not* get a convention id

- **Parameters named exactly `id`** — rule 1 doesn't apply to parameters (a bare `id` has no containing-type equivalent, and parameters should be name-driven so method signatures read cleanly). Write `orderId`, or add `[Id("Order")]` explicitly.
- **Names that are exactly `Id`** (on parameters) or shorter than 3 characters under rule 2 — so a property literally named `Id` only matches rule 1, never rule 2.
- **Anonymous-type properties under rule 1** — a bare `Id` on `new { Id = x }` would map to a synthesized `<>f__AnonymousType*` name, which is meaningless as an id. Rule 2 still applies (`new { CustomerId = x }` reads as `"Customer"`), so values projected through anonymous types in LINQ pipelines or EF `HasIndex` expressions keep flowing the right id downstream. Writes **into** anonymous-type properties never produce a diagnostic — there's no fix site, since anon members can't carry `[Id]`.
- **Indexers** — `this[Guid id]` never participates.
- **Implicitly-declared fields** — backing fields, primary-constructor capture fields, and similar compiler-synthesized members.
- **Members declared in referenced metadata** — BCL and third-party members (`Diagnostic.Id`, `EventArgs`, …) never receive a convention id. If it were otherwise, any library property named `Id` would suddenly carry an id the user can't change.


### Precedence

When resolving a symbol's id set, the analyzer consults these sources in order and stops at the first that produces an id:

1. **Explicit `[Id]` / `[UnionId]`** directly on the symbol.
2. **Inherited explicit attribute** via the property's override / interface-implementation chain, or the parameter's matching slot on overridden / implemented methods.
3. **Record primary-constructor parameter attribute** bridged onto the synthesized property (see "Record primary-constructor parameters" below).
4. **Naming convention** (rules 1 and 2 above).

At access sites (`child.Id`), covariant receiver-type walking unions the current level's ids with every parent-type id between the receiver type and the declaring type — see "Inheritance and covariant Id tagging".


### Interaction with diagnostics

- **SIA004** only fires for rule 1 collisions — two `public Guid Id` declarations on different types both claiming the same type name. Rule 2 collisions across types are the *intended* matching behavior (`Order.CustomerId` and `Invoice.CustomerId` both referring to "Customer").
- **SIA005** (redundant `[Id]`) fires only when an explicit `[Id("X")]` exactly equals what the convention would infer. It ships a fixer that removes the attribute.

### Overriding the convention

Any `[Id("...")]` / `[UnionId("...")]` on the symbol wins over the convention, so the id can be broadened, narrowed, or renamed at will:

```cs
public class Customer
{
    // overrides the "Customer" convention id
    [Id("Person")]
    public Guid Id { get; set; }
}
```

### Suffix inference (opt-in)

Member names frequently carry a qualifier prefix — parameters like `sourceProductId`, `targetProductId`, `oldOrderId`, `newOrderId`, or properties on command / DTO types like:

```cs
public class DuplicateProductCommand
{
    public Guid SourceProductId { get; set; }
    public Guid TargetProductId { get; set; }
    public string NewName { get; set; }
}
```

Under rule 2 the whole prefix becomes the id (`SourceProductId` → `"SourceProduct"`), which is almost never what the user means: the intent is usually `"Product"`, with `Source` / `Target` purely disambiguating two members of the same domain.

Enable the opt-in suffix rule in `.editorconfig` to have the analyzer pick the **last upper-case-delimited word before `Id`** as the id, *but only if that word is a known id in the compilation*:

```editorconfig
[*.cs]
strongidanalyzer.infer_suffix_ids = true
```

With the flag on:

```cs
public class Product
{
    // id: "Product" (rule 1)
    public Guid Id { get; set; }
}

// Both properties resolve to "Product"; assigning a Product.Id to either is clean.
public class DuplicateProductCommand
{
    public Guid SourceProductId { get; set; }
    public Guid TargetProductId { get; set; }
    public string NewName { get; set; }
}

// Same rule applies to parameters.
public void DuplicateProduct(Guid sourceProductId, Guid targetProductId, string newName) { }

var product = new Product();
DuplicateProduct(product.Id, product.Id, "n"); // OK
```

#### How the match works

For any property, field, or parameter whose name ends in `Id`:

1. Walk back from the trailing `Id` to the last upper-case letter — that span is the first candidate word.
2. If the candidate word is in the compilation's **known-id set** (any id produced by rule 1, rule 2, or an explicit `[Id]` / `[UnionId]` anywhere in the source), accept it.
3. Otherwise, step back one character and walk to the **next** upper-case boundary — each step lengthens the candidate by one more upper-case-delimited word. Accept the first candidate that's in the known-id set.
4. If no candidate short of the whole prefix matches, fall through to rule 2 (the whole-name rule) unchanged.

Shortest-first means "last word wins": `productOrderId` with both `Product` and `Order` known resolves to `"Order"`. Multi-word tails are picked up when the shortest candidate isn't known but a longer one is: `templateExternalObjectId` with `ExternalObject` known (but no `Object`) resolves to `"ExternalObject"`.

The known-id constraint is deliberate — without it, every `hashedId`, `rawId`, `validId` in the codebase would start getting tagged on the last word, producing noise. Restricting to words that are *already* ids in the project means the rule only fires where the intent is unambiguous.

#### Examples

| Member name                | Flag off (rule 2)        | Flag on, `Product` known         | Flag on, nothing known  |
|----------------------------|--------------------------|----------------------------------|-------------------------|
| `ProductId` / `productId`  | `"Product"`              | `"Product"` (rule 2)             | `"Product"` (rule 2)    |
| `SourceProductId`          | `"SourceProduct"`        | `"Product"`                      | `"SourceProduct"`       |
| `templateExternalObjectId` (with `ExternalObject` known) | `"TemplateExternalObject"` | `"ExternalObject"` — `"Object"` unknown, walks back to match | `"TemplateExternalObject"` |
| `rawProductBytesId`        | `"RawProductBytes"`      | `"RawProductBytes"` — no known candidate tail | `"RawProductBytes"` |
| `productOrderId` (both known) | `"ProductOrder"`      | `"Order"` — shortest match wins  | `"ProductOrder"`        |
| `HashedId`                 | `"Hashed"`               | `"Hashed"` (no prefix; rule 2)   | `"Hashed"`              |

Explicit `[Id("...")]` / `[UnionId(...)]` on the member still wins over the suffix rule — same precedence as the other naming-convention rules.

#### What the flag does not change

- Local variables — `var sourceProductId = ...;` is still `Unknown`. Locals are temporary containers whose real identity comes from the right-hand side (`Guid.NewGuid()`, an untagged input, a tagged property…); tagging by local name would invent identities the RHS can't back up and produce false SIA001 noise. Flow an id through a local by assigning from a tagged source (properties, parameters, `[return: Id]` methods, or `foreach` over a tagged collection) — the existing source-resolution rules already propagate the id.
- Method names — `GetSourceProductId()` and similar don't get suffix-inferred. Method names describe behavior (`Get`, `Find`, `Build`), not data, so treating the leading word as a qualifier would be noisy. Apply `[return: Id("Product")]` to tag the return type explicitly.
- Codefixes — SIA001/SIA002/SIA003 still offer the add-`[Id]` and rename fixes; the rename fix may propose stripping the qualifier (e.g. `SourceProductId` → `ProductId`), which is a lossy rename — decline the rename fix when the qualifier is meaningful.
- Referenced metadata — members from BCL / third-party libraries never receive a convention id, suffix-inferred or otherwise.


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

Fires when both operands carry `[Id]` and the id values differ. The analyzer sees an unambiguous cross-domain flow (e.g. a `Customer` id passed where an `Order` id is expected) and refuses it.

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
        ProcessOrder(CustomerId);
}
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L122-L136' title='Snippet source file'>snippet source</a> | <a href='#snippet-SIA001Example' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


#### Code fixes

For flow-style mismatches (argument, assignment, property/field initializer) the analyzer attaches the **target** declaration as the fix site and offers:

 * **Change attribute on `<kind> '<name>'` to `[Id("<source id>")]`** — when the target already carries an explicit `[Id]` / `[UnionId]`, replaces it with the source's id.
 * **Add `[Id("<source id>")]` to `<kind> '<name>'`** — when the target is untagged (its current id came from naming convention), adds the attribute.
 * **Rename `<kind> '<name>'` to `<sourceTag>Id`** — when the target has no explicit attribute and its name matches the `XxxId` convention. First-character case is preserved (`bidId` → `treasuryBidId`, `BidId` → `TreasuryBidId`). Works for parameters, properties, and single-declarator fields.

The `<source id>` is the receiver's static type, not the declaring type of the `Id` member. For `treasuryBid.Id` where `Id` is inherited from `BaseEntity`, the fix suggests `TreasuryBid` — what reads locally at the call site — rather than `BaseEntity`.

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
        ProcessOrder(Raw);
}
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L138-L153' title='Snippet source file'>snippet source</a> | <a href='#snippet-SIA002Example' title='Start of snippet'>anchor</a></sup>
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
        Consume(OrderId);
}
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L155-L170' title='Snippet source file'>snippet source</a> | <a href='#snippet-SIA003Example' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

SIA003 is suppressed when the id can't meaningfully survive:

 * **Library metadata targets** — BCL and third-party members (`Dictionary<Guid, T>.this[Guid]`, `Guid.Equals(Guid)`, `object.Equals(object)`). Library authors can't apply `[Id]`.
 * **`object` parameters / properties / fields** — logging, serialization, message buses. The id is erased through `object` anyway.
 * **Unconstrained generic type parameters (`T`)** — identity methods, container helpers. Generics carry no domain intent.
 * **Targets in a suppressed namespace** — by default `System*` and `Microsoft*` (see below).
 * **Equality comparisons** — `==` / `!=` operands are symmetric, so only SIA001 / SIA002 apply.


## `[UnionId(...)]`

Sometimes a member legitimately accepts any one of several domain types — a generic lookup helper, a cache key that can be either a Customer or a Product. The package ships `[UnionId("Customer", "Product")]` for that case.

Two values are **compatible** when their id sets overlap on at least one entry:

| Source                           | Target                                  | Compatible?     |
|----------------------------------|------------------------------------------|-----------------|
| `[UnionId("Customer","Product")]`| `[UnionId("Customer","Product")]`        | yes (full)      |
| `[UnionId("Customer","Product")]`| `[UnionId("Customer","Order")]`          | yes (`Customer`)|
| `[UnionId("Customer","Product")]`| `[Id("Product")]`                        | yes             |
| `[Id("Product")]`                | `[UnionId("Customer","Product")]`        | yes             |
| `[UnionId("Customer","Product")]`| `[UnionId("Order","Supplier")]`          | **no** → SIA001 |

A `[UnionId("x")]` with a single option is always a mistake — use `[Id("x")]`. SIA006 flags it and ships a fixer that rewrites the attribute in place.


## Inheritance and covariant Id tagging

A property (or field) named `Id` inherited from a base type carries ids from **every** level of the chain its receiver walks — the base type's id **and** the derived type's id. At an access site like `child1.Id`, the id set is the union of:

 * Every explicit `[Id("...")]` found on the property, its `override` chain, and its interface impls.
 * Every naming-convention id for types in the receiver's static-type chain between the receiver type and the member's declaring type (inclusive), where the member was not redeclared.

Matching rules use set containment — a single-id parameter is satisfied if its id appears anywhere in the source's set. So given `public static void Foo(Guid child1Id, Guid baseId)`:

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
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L172-L213' title='Snippet source file'>snippet source</a> | <a href='#snippet-InheritanceAbstractClassExplicit' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Abstract class + naming convention only

<!-- snippet: InheritanceAbstractClassConvention -->
<a id='snippet-InheritanceAbstractClassConvention'></a>
```cs
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
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L215-L254' title='Snippet source file'>snippet source</a> | <a href='#snippet-InheritanceAbstractClassConvention' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Interface + explicit `[Id]` on every level

<!-- snippet: InheritanceInterfaceExplicit -->
<a id='snippet-InheritanceInterfaceExplicit'></a>
```cs
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
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L256-L295' title='Snippet source file'>snippet source</a> | <a href='#snippet-InheritanceInterfaceExplicit' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Interface + naming convention only

<!-- snippet: InheritanceInterfaceConvention -->
<a id='snippet-InheritanceInterfaceConvention'></a>
```cs
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
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L297-L335' title='Snippet source file'>snippet source</a> | <a href='#snippet-InheritanceInterfaceConvention' title='Start of snippet'>anchor</a></sup>
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

// unknown — literal-like
Consume(Guid.Empty);

// unknown — constructor
Consume(new Guid("00000000-0000-0000-0000-000000000000"));
```

### Local variables

Locals don't support attributes in C#, but the analyzer resolves the initializer expression and propagates a Present id through the local when one is found. A local initialised from a tagged field / property / parameter / return value / element-returning LINQ chain carries that id forward; a local initialised from a literal, an untagged call, or any other expression that resolves to Unknown stays Unknown.

```cs
[Id("Customer")] Guid source = default;

// id flows — copy is Customer
var copy = source;

// SIA001 if Consume expects a non-Customer id
Consume(copy);

var fresh = Guid.NewGuid();

// unknown — initializer resolves to Unknown
Consume(fresh);
```

Reassignments aren't tracked — only the declarator's initializer is inspected. `var x = tagged; x = other;` still reports `x` as the initializer's id on every read.

### Method invocations

Untagged return values stay **unknown** — no noise on `Guid.NewGuid()` and friends. To flow an id through a return value, annotate the method with `[return: Id("...")]` or `[return: UnionId("...", "...")]`. The id is read from the method's own return attributes, plus any method it overrides or interface member it implements.

```cs
Guid GetOrderId() => Guid.NewGuid();

// unknown — untagged return
Consume(GetOrderId());

// unknown — untagged return
Consume(Guid.NewGuid());

[return: Id("Order")]
Guid LoadOrderId() => Guid.NewGuid();

// OK — id matches
Consume(LoadOrderId());
```

### `await` expressions

The analyzer unwraps `await` to the underlying operation, so `[return: Id]` on an async method flows to the awaited value at the call site.

```cs
Task<Guid> LoadOrderIdAsync() => Task.FromResult(Guid.NewGuid());

// unknown — untagged async return
Consume(await LoadOrderIdAsync());

[return: Id("Order")]
Task<Guid> LoadTaggedOrderIdAsync() => Task.FromResult(Guid.NewGuid());

// OK — id flows through await
Consume(await LoadTaggedOrderIdAsync());
```

### Compound expressions

Conditionals, casts, pattern results, null-coalescing, and any other expression shape collapse to "unknown" — even when every operand would individually resolve.

```cs
[Id("Order")]    Guid a = default;
[Id("Customer")] Guid b = default;

// unknown — ternary
Consume(condition ? a : b);

// unknown — cast chain
Consume((Guid)(object)a);

// unknown — conditional result
Consume(a == Guid.Empty ? b : a);
```


## Collections

An `[Id(...)]` or `[UnionId(...)]` on a collection-typed member describes the elements inside the collection, not the collection itself. The analyzer threads those element ids through LINQ queries, `foreach` loops, and user-defined extensions — so id flow works without attributes on every lambda parameter (which are illegal inside `IQueryable` expression trees anyway, per CS8972).

The collection must be a **single-T enumerable**: an array, or a type that implements exactly one `IEnumerable<T>` construction. That covers `T[]`, `IEnumerable<T>`, `IReadOnlyList<T>`, `List<T>`, `HashSet<T>`, `IQueryable<T>`, and the various immutable/concurrent flavours.

<!-- snippet: TaggedCollectionLinqLambda -->
<a id='snippet-TaggedCollectionLinqLambda'></a>
```cs
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
        // ReSharper disable once ReturnValueOfPureMethodIsNotUsed
        list.Ids.Select(id => { Consume(id); return id; }).ToList();
}
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L355-L377' title='Snippet source file'>snippet source</a> | <a href='#snippet-TaggedCollectionLinqLambda' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Only **explicit** `[Id]` / `[UnionId]` attributes on a collection-typed declaration participate in element-id flow. Naming-convention inference (`Id` / `XxxId`) is not applied to collection-typed members — the common case of a `List<Guid>` happening to be named `CustomerIds` would otherwise spuriously acquire an id that no caller can change.


### LINQ

Ids flow through three categories of call, classified by signature rather than by name:

 * **Element-returning** — the following methods on `System.Linq.Enumerable`/`Queryable` surface the receiver's element id as the result's scalar id:
   * `First` / `FirstOrDefault`
   * `Single` / `SingleOrDefault`
   * `Last` / `LastOrDefault`
   * `ElementAt` / `ElementAtOrDefault`
   * `Min`
   * `Max`
   * `Aggregate`

   The `*Async` counterparts from EF Core (`FirstAsync`, `SingleAsync`, …) are recognised by shape: any element-returning name + `Async` whose return type is `Task<T>` / `ValueTask<T>` over the receiver's element type flows the same way, so `await q.Select(_ => _.Tagged).SingleAsync()` is treated as a tagged scalar.
 * **Element-preserving** — the following methods pass the element id through unchanged, so chains like `ids.Where(x => x != Guid.Empty).First()` work:
   * `Where`
   * `OrderBy` / `OrderByDescending`
   * `ThenBy` / `ThenByDescending`
   * `Reverse`
   * `Take` / `TakeWhile` / `TakeLast`
   * `Skip` / `SkipWhile` / `SkipLast`
   * `Distinct` / `DistinctBy`
   * `Concat`
   * `Union` / `UnionBy`
   * `Intersect` / `IntersectBy`
   * `Except` / `ExceptBy`
   * `AsEnumerable` / `AsQueryable`
   * `ToArray` / `ToList` / `ToHashSet`
   * `Append` / `Prepend`
 * **`Select` / `SelectMany`** transform the element id according to the selector:
   * Identity lambda `x => x` keeps the receiver's element id.
   * Method group `Select(Converter)` reads `[return: Id(...)]` on the target method.
   * Expression-bodied lambda with a tagged body (`Select(x => GetTagged(x))`) adopts the body's resolved id.
   * Any other selector shape drops the id.

Lambda parameters in those calls inherit the receiver's element id without an attribute, which is the mechanism that makes `IQueryable` predicates analyzable.


### `foreach`

The loop variable inherits the collection's element id for the body of the loop. Nested `foreach` works the same way — the inner loop sees the inner collection's element id.

<!-- snippet: TaggedCollectionForEach -->
<a id='snippet-TaggedCollectionForEach'></a>
```cs
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
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L379-L398' title='Snippet source file'>snippet source</a> | <a href='#snippet-TaggedCollectionForEach' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


### User-defined LINQ-shape extensions

An extension method whose first parameter is `IEnumerable<T>` or `T[]`, and whose return is an `IEnumerable<T>` over the same `T`, is treated as element-preserving by shape. MoreLINQ helpers, EF Core `IQueryable` extensions like `.Include`, and project-local paging helpers all propagate element ids without being on an allowlist.

<!-- snippet: TaggedCollectionUserExtension -->
<a id='snippet-TaggedCollectionUserExtension'></a>
```cs
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
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L400-L424' title='Snippet source file'>snippet source</a> | <a href='#snippet-TaggedCollectionUserExtension' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Lambda-parameter binding applies to any extension method on `IEnumerable<T>` regardless of its return type — so `Action<T>` callbacks and void-returning helpers flow ids the same way.

Element-returning inference (`.First()` and friends) stays closed to the `System.Linq.Enumerable`/`Queryable` allowlist — a third-party method named `First` could have different semantics, and the element-returning category depends on the semantics, not the signature.


### Tagging from a generic type parameter

Generic container types — lookup tables, well-known-id registries, per-domain helpers — can declare the Id at the type-parameter level via `[IdTag]`. Collection-typed members of such a container pick up the substituted type argument's short name as an implicit element id at each use site, so LINQ chains and foreach loops bind their element variables to the right id without a per-member attribute.

<!-- snippet: IdTagTypeParameter -->
<a id='snippet-IdTagTypeParameter'></a>
```cs
public class Operation;

public static class WellKnownId<[IdTag] T>
{
    // [IdTag] on the type parameter marks it as an Id source. Members of the
    // containing type implicitly carry the substituted type argument's short name
    // as an Id at every use site — so WellKnownId<Customer>.Guids is treated
    // as a Customer-tagged collection without a per-member attribute.
    public static IEnumerable<Guid> Guids { get; } = [];
}

public class OperationIndex
{
    [Id("Operation")]
    static Guid[] blocked = [];

    [Id("Customer")]
    public Guid LatestCustomerId { get; set; }

    // SIA001: .Except is element-preserving, so the walk terminates at
    // WellKnownId<Operation>.Guids whose implicit tag is "Operation". That tag
    // rides through .First() and collides with the "Customer"-tagged target.
    public void Copy() =>
        LatestCustomerId = WellKnownId<Operation>.Guids.Except(blocked).First();
}
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L426-L454' title='Snippet source file'>snippet source</a> | <a href='#snippet-IdTagTypeParameter' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The implicit flow is deliberately scoped to collection elements. Scalar members — method returns, properties, parameters — still need explicit `[Id]` / `[UnionId]`; otherwise a factory like `WellKnownId<T>.MakeGuid(int)` would silently id every call site, forcing every receiving field and variable onto the attribute to avoid SIA003. Open-generic references (where the type parameter is still unsubstituted, e.g. member accesses from inside `WellKnownId<T>` itself) produce no implicit id. Multiple `[IdTag]` parameters on the same type contribute a union: a collection declared on `Cross<[IdTag] T1, [IdTag] T2>` carries both id names. Nested types inherit the outer type's `[IdTag]` parameters.


### What is not supported

Multi-type-parameter containers — `Dictionary<K,V>`, `KeyValuePair<K,V>`, `ILookup<K,V>`, `IGrouping<K,T>`, `ValueTuple<…>` — are deliberately excluded in this release. A bare `[Id("Customer")]` attribute on a `Dictionary<Guid,Guid>` has no unambiguous target (key? value? both?), so the analyzer ignores the attribute on these shapes and produces no diagnostics for reads through them.

<!-- snippet: UnsupportedMultiTCollection -->
<a id='snippet-UnsupportedMultiTCollection'></a>
```cs
public class CustomerOrderMap
{
    // [Id] on a Dictionary/KeyValuePair/tuple/grouping carries no element id —
    // the analyzer can't tell whether the id applies to K, V, or both. Flows
    // through these containers stay "unknown" and produce no diagnostics.
    [Id("Customer")]
    public Dictionary<Guid, string> OrdersByCustomer { get; set; } = [];
}
```
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L456-L467' title='Snippet source file'>snippet source</a> | <a href='#snippet-UnsupportedMultiTCollection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Distinct attributes for the key and value positions, plus tuple-field-level tagging, are on the roadmap but will require a dedicated design pass.


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
<sup><a href='/src/StrongIdAnalyzer.Tests/Samples.cs#L337-L350' title='Snippet source file'>snippet source</a> | <a href='#snippet-RecordPrimaryCtorParameter' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

An explicit `[property: Id(...)]` on the property still wins — if both targets are attributed, the property's own attribute is used. Naming-convention inference (for properties named `Id` or `XxxId`) is only consulted after both the property's and the parameter's explicit attributes come up empty.


## Icon

[Escher Triangle](https://thenounproject.com/icon/escher-triangle-358766/)
