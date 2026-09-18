# Bug backlog — all fixed

Found in a code review on 2026-09-17 against commit `6273646` (the analyzer source is unchanged at `d4f1371`). Each item was reproduced with a throwaway test that was not committed. The existing 356 tests pass.

**Status 2026-09-18:** every item below was reproduced against the analyzer at HEAD, fixed, and covered by a committed regression test. The suite is now 410 tests, all passing. Two existing tests changed with the behaviour they pinned:

 * `IdTag_NoAttribute_ProducesNoTag` probed "no tag leaked" through an SIA002 on a lambda parameter, which item 8 removes. It now probes the same thing through the SIA001 that a leak would produce.
 * `ReceiverWalk_NamespaceSuppressionDisabled_MetadataTypeTagged` now expects `Entity` in the tag set: item 19's fix means a metadata level that contributes nothing no longer marks its type as covered, so the declaring type names itself exactly as a source-declared one does.

Line numbers drift, so each item also names the method.


## Crashes

Items 1 and 2 overflow the stack, which kills whatever process hosts the analyzer: `csc` / `VBCSCompiler` on the command line, or the analyzer process in an IDE.

- [x] **1. A local whose initializer refers back to itself overflows the stack.**
  - Repro: `Guid a = a; Use(a);` or `Guid a = b; Guid b = a; Use(a);`. Both are compile errors, but analyzers run on half-typed code, and a command-line build crashes instead of reporting CS0165.
  - Cause: `TryResolveLocalInitializer` ([IdMismatchAnalyzer.cs:1155](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L1155)) resolves the initializer through `GetAccessInfo` with no cycle guard. `FindAnonymousCreationFromLocal` ([IdMismatchAnalyzer.cs:1525](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L1525)) has the same shape.
  - Fix: track the locals currently being resolved (a `[ThreadStatic]` set, or a set passed down) and return `Unknown` on re-entry.

- [x] **2. Wrapper recognition overflows the stack on valid code** (only with `strongidanalyzer.infer_wrapper_ids = true`).
  - Repro:
    ```cs
    public readonly record struct Id<T>(Guid Value);
    public readonly record struct UserId(Id<UserId> Value);
    ```
    The same code with the option off, or with `Id<T>` declared as a class, is fine.
  - Cause: `WrapperTypes.TryGet` ([WrapperTypes.cs:52](src/StrongIdAnalyzer/WrapperTypes.cs#L52)) caches a type only after `Recognize` returns. `Recognize(UserId)` reaches `TryGet(UserId)` again through the value member's type ([WrapperTypes.cs:135](src/StrongIdAnalyzer/WrapperTypes.cs#L135)) and the type-argument check in `ResolveTag` ([WrapperTypes.cs:240](src/StrongIdAnalyzer/WrapperTypes.cs#L240)).
  - Fix: mark a type as in progress before recognising it (thread-local, so other threads never read a provisional result) and treat re-entry as "not a wrapper".

- [x] **3. A `null` params array throws `NullReferenceException`** (reported as AD0001).
  - Repro: `[UnionId(null)]` on a property, or `[assembly: ExternalId(typeof(C), "Value", null)]`.
  - Cause: for a null array argument, `TypedConstant.Values` is an uninitialised `ImmutableArray`, and reading `.Length` throws. Affected: `ExternalIds.ReadTags` ([ExternalIds.cs:138](src/StrongIdAnalyzer/ExternalIds.cs#L138)), `ExtractUnionOptions` ([IdAttributeExtensions.cs:308](src/StrongIdAnalyzer/IdAttributeExtensions.cs#L308)), `CheckEmptyTag` ([IdMismatchAnalyzer.cs:152](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L152)).
  - Impact: the `ExternalId` case throws at compilation start, which turns off every rule for that project. A referenced assembly that contains such an attribute does the same to every consumer.
  - Fix: check `TypedConstant.IsNull` before reading `.Values`, and treat a null array as empty so SIA007 / SIA008 report it.


## Code fixes that produce wrong or broken code

- [x] **4. The rename fix renames the wrong symbol when the declaration is in another file.**
  - Repro: SIA003 on `order.Value = Source;` in `Test.cs`, where `Order.Value` is declared in `Order.cs`. "Rename property 'Value' to 'CustomerId'" renamed an unrelated `Holdr.Name` in `Test.cs`. When the span lies past the end of the current file, `FindNode` throws instead.
  - Cause: `RenameAsync` ([AddIdCodeFixProvider.cs:213](src/StrongIdAnalyzer.CodeFixes/AddIdCodeFixProvider.cs#L213)) runs `FindNode` with the other file's span against the root and semantic model of `context.Document`.
  - Fix: resolve the declaring document from the location (with the fallback from item 5) and use that document's root and semantic model.

- [x] **5. The add and change fixes do nothing when the diagnostic's syntax tree is stale.**
  - Repro: the setup of `SIA002_PrefersGenericForm_WhenDiagnosticTreeIsStale` (the Rider case described in `ShouldUseGenericAsync`). Applying "Add [Id<Election>] to field 'Election2022'" changes no document. The test only checks the title.
  - Cause: `ChangeOrAddAttributeAsync`, `AddAttributeAsync` and `AddUnionAttributeAsync` call `solution.GetDocument(declarationLocation.SourceTree)` ([AddIdCodeFixProvider.cs:178](src/StrongIdAnalyzer.CodeFixes/AddIdCodeFixProvider.cs#L178), [AddIdCodeFixProvider.cs:503](src/StrongIdAnalyzer.CodeFixes/AddIdCodeFixProvider.cs#L503), [AddIdCodeFixProvider.cs:553](src/StrongIdAnalyzer.CodeFixes/AddIdCodeFixProvider.cs#L553)). That returns null for a stale tree, and the action returns the solution unchanged. The rename works in this case only because it uses `context.Document`, which is what breaks it in item 4.
  - Fix: give every action (rename included) one document lookup: by tree identity first, then by file path, as `ShouldUseGenericAsync` already does. Extend the test to apply the fix.

- [x] **6. The SIA005 fix deletes comments above the attribute.**
  - Repro: a `/// <summary>` doc comment and a `//` comment directly above `[Id("Order")]` on `Order.Id` both disappear. `#if` and `#region` lines in that position would be removed too.
  - Cause: `RemoveAttributeAsync` removes the node with `SyntaxRemoveOptions.KeepNoTrivia` ([AddIdCodeFixProvider.cs:644](src/StrongIdAnalyzer.CodeFixes/AddIdCodeFixProvider.cs#L644), [AddIdCodeFixProvider.cs:648](src/StrongIdAnalyzer.CodeFixes/AddIdCodeFixProvider.cs#L648)).
  - Fix: keep the attribute list's leading trivia (move it onto the next token), then format.

- [x] **7. The generic-form fix ignores arity.**
  - Repro: a tag inferred from `Ledger<TKey>.Id` produces `[Id<Ledger>]`, which fails with CS0305. Generic `Entity<TKey>` base classes make this common.
  - Cause: `ShouldUseGenericAsync` ([AddIdCodeFixProvider.cs:300](src/StrongIdAnalyzer.CodeFixes/AddIdCodeFixProvider.cs#L300)) accepts any named type with a matching name.
  - Fix: require `Arity == 0`. Fall back to the string form when the lookup finds more than one type (CS0104) or a static class (CS0718).

- [x] **8. SIA002 fires on lambda parameters, and its fix does not compile.**
  - Repro: `Ids.Any(x => x == order.Id)` over an untagged collection. The fix writes `Ids.Any([Id<Order>] x => ...)`, which fails with CS8916 and CS0592 because the attribute binds to the lambda. With parentheses, an `IQueryable` lambda fails with CS8972 instead.
  - Also: `CustomerIds.ForEach(id => Ship(id))` on a tagged `List<Guid>` reports this SIA002 and misses the real Customer → Order mismatch. `List<T>.ForEach` is an instance method, and `GetLinqLambdaReceiver` ([LinqExtensions.cs:295](src/StrongIdAnalyzer/LinqExtensions.cs#L295)) only binds extension methods.
  - Fix: don't report SIA002 on lambda parameters (`MethodKind.LambdaMethod`), or at least don't offer the attribute fix there; tagging the collection is the useful fix. Bind instance methods on an enumerable receiver the same way as extensions.

- [x] **9. The rename fix is wrong for tags that start with a lower-case letter.**
  - Repro: for `[Id("customer")]` the fix offers `customerId`. The naming rule reads that as `"Customer"`, so SIA001 follows.
  - Cause: `TryGetRenameTarget` ([AttributeHost.cs:145](src/StrongIdAnalyzer.CodeFixes/AttributeHost.cs#L145)) re-cases the tag's first letter to match the identifier.
  - Fix: only offer the rename when the naming rule applied to the new name gives back the tag.

- [x] **10. Tuple elements get the wrong fix target.**
  - Repro: SIA001 with `pair.CustomerId` as the source, where `pair` is a `(Guid CustomerId, Guid Other)` parameter. The fixes offered are "Add [Id("Order")] to parameter 'pair'", which tags the whole tuple and leaves the diagnostic, and "Rename parameter 'pair' to 'orderId'", which renames the tuple element.
  - Cause: `AttributeHost.Find` ([AttributeHost.cs:22](src/StrongIdAnalyzer.CodeFixes/AttributeHost.cs#L22)) climbs from the `TupleElementSyntax` to the enclosing parameter.
  - Fix: return null for tuple elements, and treat tuple-element fields as non-editable fix sites in the analyzer.


## Wrong diagnostics

- [x] **11. Nested lambdas bind the outer parameter to the inner collection.**
  - Repro, with `[Id("Customer")] List<Guid> CustomerIds` and `[Id("Order")] List<Guid> OrderIds`:
    ```cs
    // false SIA001
    CustomerIds.Any(c => OrderIds.Any(o => IsCustomer(c)));
    // missed SIA001
    CustomerIds.Any(c => OrderIds.Any(o => o == c));
    ```
    This only happens when both collections share an element type, which is the id-list case.
  - Cause: `GetLinqLambdaReceiver` ([LinqExtensions.cs:283](src/StrongIdAnalyzer/LinqExtensions.cs#L283)) takes the nearest lambda around the parameter *reference*, not the lambda that declares the parameter. `FindAnonymousCreationFromLambdaParameter` inherits this.
  - Fix: walk up to the anonymous function whose `Symbol` equals `param.Parameter.ContainingSymbol`.

- [x] **12. A local keeps its initializer's tag after reassignment.**
  - Repro: `var id = order.CustomerId; id = product.Id; UseProduct(id);` reports a false SIA001.
  - Cause: `TryResolveLocalInitializer` ([IdMismatchAnalyzer.cs:1155](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L1155)) reads only the declarator's initializer. `FindAnonymousCreationFromLocal` does the same for anonymous types.
  - Fix: trust the initializer only when the local is never written again (assignment, `ref` / `out` argument, deconstruction target).

- [x] **13. SIA004 (an error) fires for private fields.**
  - Repro: `Billing.Handler` and `Shipping.Handler`, each with `private readonly Guid _id`, fail the build with two SIA004 errors.
  - Cause: `TryGetConventionName` ([IdMismatchAnalyzer.cs:435](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L435)) never checks accessibility, and `CollectConvention` feeds every match into the ambiguity map ([IdMismatchAnalyzer.cs:252](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L252)). [docs/SIA004.md](docs/SIA004.md) describes the rule as applying to public members. The `_id` field rule makes collisions far more likely.
  - Fix: decide whether non-public members belong in SIA004. Leaving them out of the ambiguity map (while keeping their naming-rule tag) is the smallest change. Update SIA004.md either way.

- [x] **14. SIA005 asks to remove the `[Id]` that prevents SIA004.**
  - Repro: `A.Customer.Id` has `[Id("Customer")]` and `B.Customer.Id` has none. SIA005 fires on `A.Customer.Id`; applying its fix produces two SIA004 errors.
  - Cause: tagged members are left out of the ambiguity map ([IdMismatchAnalyzer.cs:252](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L252)), but the SIA005 check ([IdMismatchAnalyzer.cs:275](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L275)) doesn't account for removal putting the member back in.
  - Fix: at compilation end, skip SIA005 on an `Id` member named after its type when the ambiguity map holds a same-named member from a different type.

- [x] **15. SIA005 ignores tags inherited from a base member.**
  - Repro: base `Handle([Id("Client")] Guid customerId)`, override `Handle([Id("Customer")] Guid customerId)`. SIA005 calls the override's attribute redundant. Removing it makes the parameter inherit `"Client"`, and SIA001 appears at the call site.
  - Cause: `CollectConvention` ([IdMismatchAnalyzer.cs:275](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L275)) compares against the wrapper, suffix and naming-rule tags only, while `GetIdWithInheritance` checks inherited attributes first.
  - Fix: compute the "without this attribute" tag in the same order as `GetIdWithInheritance`: inherited attributes, record parameter, wrapper, suffix, naming rule.

- [x] **16. SIA003 fires on members of a generic type used with a concrete type argument.**
  - Repro: `new Box<Guid> { Content = CustomerId }` and `box.Slot = CustomerId` ask for `[Id]` on `Box<T>.Content` and `Box<T>.Slot`. `box.Put(CustomerId)` is correctly quiet.
  - Cause: `IsBoundaryTarget` ([IdMismatchAnalyzer.cs:2560](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L2560)) uses `OriginalDefinition.Type` only for parameters. For properties and fields it reads the substituted type (`Guid`), although the comment there says no extra step is needed.
  - Fix: use `OriginalDefinition.Type` for properties and fields too, and correct the comment.

- [x] **17. The same-element-type rule overrides an explicit `[return: Id]`.**
  - Repro:
    ```cs
    [return: Id("Order")]
    public static IEnumerable<Guid> OrdersOf([Id("Customer")] this IEnumerable<Guid> customerIds) => ...;

    // false SIA001 on both lines
    foreach (var orderId in CustomerIds.OrdersOf()) { Ship(orderId); }
    Ship(CustomerIds.OrdersOf().First());
    ```
    A non-extension version called as `Lookups.OrdersOfPlain(CustomerIds)` resolves correctly.
  - Cause: `GetReceiverElementTags` ([IdMismatchAnalyzer.cs:1294](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L1294)) treats any `IEnumerable<X> → IEnumerable<X>` extension as element-preserving ([LinqExtensions.cs:132](src/StrongIdAnalyzer/LinqExtensions.cs#L132)) before looking at the method's own attribute. Without an attribute, a non-generic helper that maps customer ids to order ids is also assumed to keep the customer tag.
  - Fix: check `GetReturnInfo` first. Consider limiting the shape rule to generic methods whose element type is the method's own type parameter.

- [x] **18. Generic methods lose tags declared on the interface they implement.**
  - Repro:
    ```cs
    public interface IRepo
    {
        [return: Id("Order")] Guid Get<T>();
        void Put<T>([Id("Order")] Guid key);
    }
    // Repo implements IRepo without attributes.
    Use(repo.Get<int>());      // missed SIA001 (the non-generic equivalent reports it)
    repo.Put<int>(productId);  // SIA003 asking to tag Repo.Put's parameter, instead of SIA001
    ```
  - Cause: `GetReturnInfo` ([IdMismatchAnalyzer.cs:1696](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L1696)) and `GetParameterIdFromHierarchy` ([IdMismatchAnalyzer.cs:2217](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L2217)) compare the result of `FindImplementationForInterfaceMember` (the definition `Get<T>`) with the constructed `Get<int>`.
  - Fix: compare with `method.ConstructedFrom`.

- [x] **19. `product.Id` gets no tag when `Id` is declared on a `Product` in a referenced assembly.**
  - Repro: a library with `namespace Shop { public class Product { public Guid Id { get; set; } } }`. In the consumer, `UseOrder(product.Id)` into `[Id("Order")] Guid orderId` reports nothing. With `Product : Entity` (the only tested shape), SIA001 fires.
  - Cause: `GetMemberAccessInfo` adds each level's type to `coveredTypes` ([IdMismatchAnalyzer.cs:1760](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L1760)) before skipping metadata levels, so the receiver-type walk skips `Product` as already covered. CLAUDE.md says this case must keep tagging.
  - Fix: only mark a level's type as covered when that level contributed a tag.
  - Related, not verified: IDEs usually load project references as source, where the `DeclaringSyntaxReferences` checks behave differently, so IDE and command-line results may disagree for members of other projects.

- [x] **20. A `[StrongIdIndex]` hit drops receiver tags from derived types in the consuming project.**
  - Repro: a library with `Entity { Guid Id }` and index `P:Entity.Id=Entity`. In the consumer, `Invoice : Entity` and `UseInvoice(invoice.Id)` into `[Id("Invoice")]`. With the index: false SIA001. Without it: no diagnostic.
  - Cause: the index branch of `GetMemberAccessInfo` ([IdMismatchAnalyzer.cs:1727](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L1727)) returns before the receiver-type walk. The comment above it says consumer subclasses fall through, but they don't. Nothing generates this attribute yet, so the impact is low for now.
  - Fix: run the receiver-type walk after an index hit, at least for receiver types outside the indexed assembly.

- [x] **21. Tag widening ignores the suppression lists and scans every reference.**
  - Repro: domain classes `Process` and `Component`, each with an `Id`. `UseComponent(process.Id)` into `[Id("Component")]` reports nothing, because `System.Diagnostics.Process : Component` widens `"Process"` to include `"Component"`. With the classes renamed `Proc` and `Comp`, SIA001 fires. SDK types such as `Microsoft.Graph.Models.User : DirectoryObject : Entity` widen domain tags the same way.
  - Cause: `ComputeAncestorTags` ([IdMismatchAnalyzer.cs:692](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L692)) uses `TypeEnumeration.FindByName`, which never consults `Config.Suppression`.
  - Performance: `Report` ([IdMismatchAnalyzer.cs:2420](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L2420)) and `AnalyzeBinaryOperator` ([IdMismatchAnalyzer.cs:753](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L753)) widen before the cheap overlap check, and the first use of each tag enumerates every type in every referenced assembly.
  - Fix: skip suppressed types in the widening walk, check overlap before widening, and build a name-to-types map once per compilation.

- [x] **22. `_id = id` in a constructor reports SIA002; `Id = id` does not.**
  - Repro: `public Tenant(string id) { _id = id; }` reports `add [Id("Tenant")] to parameter 'id'`.
  - Cause: the name-matching exemption in `Report` ([IdMismatchAnalyzer.cs:2469](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L2469)) only covers property targets, and the `_id` rule now tags the field.
  - Fix: accept field targets too, comparing the parameter name with `field.ConventionName()`.


## Smaller issues

- [x] **23. The generated attributes need C# 12.** Primary constructors ([IdAttributeGenerator.cs:35](src/StrongIdAnalyzer/IdAttributeGenerator.cs#L35)), a file-scoped namespace and `global using` make projects on LangVersion 11 or lower fail to compile. That includes netstandard2.0 and .NET Framework projects on their default C# 7.3. The generator's pre-C# 11 path ([IdAttributeGenerator.cs:156](src/StrongIdAnalyzer/IdAttributeGenerator.cs#L156)) can never produce valid code. Fix: document C# 12 as the minimum and drop that path, or emit syntax older compilers accept.
- [x] **24. `suppressed_namespaces = Ext.*` silently matches nothing;** only `Ext*` works. `Suppression.Parse` ([Suppression.cs:76](src/StrongIdAnalyzer/Suppression.cs#L76)) keeps an empty last segment. Fix: treat `X.*` as `X*`, or reject the pattern.
- [x] **25. `[Id(null)]` gets no SIA007,** and `[UnionId(null, "Order")]` is reported as SIA006 instead of SIA007 (`CheckEmptyTag`, [IdMismatchAnalyzer.cs:127](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L127)). Fix: treat null like an empty tag.
- [x] **26. SIA006 isn't reported for `[return: UnionId("X")]`.** `AnalyzeSingletonUnion` is registered for properties, fields and parameters only ([IdMismatchAnalyzer.cs:78](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L78)), while `AnalyzeEmptyTag` also covers return attributes. Fix: register it for methods and check `GetReturnTypeAttributes()`.
- [x] **27. The docs and the code disagree about casts.** [readme.md](readme.md#L886), docs/SIA001.md, docs/SIA002.md and CLAUDE.md say casts are Unknown, citing `Consume((Guid)(object)a)`. `Unwrap` ([Extensions.cs:13](src/StrongIdAnalyzer/Extensions.cs#L13)) strips every conversion, so that call reports SIA001, as does `Use((int)longCustomerId)`. Fix: decide which is intended, then either peel only implicit conversions or update the docs.
- [x] **28. `SelectMany` never passes element tags through,** although the comment on `GetReceiverElementTags` says it is handled. `GetSelectElementTags` resolves the selector body with `GetAccessInfo` ([IdMismatchAnalyzer.cs:1434](src/StrongIdAnalyzer/IdMismatchAnalyzer.cs#L1434)), which treats a collection-typed body as Unknown. For the three-argument overload, `FindSelectorArgument` ([LinqExtensions.cs:55](src/StrongIdAnalyzer/LinqExtensions.cs#L55)) picks the collection selector instead of the result selector. Fix: use `GetReceiverElementTags` on the collection selector's body, and use the result selector for the three-argument overload.
