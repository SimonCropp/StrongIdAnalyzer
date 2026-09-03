// Opt-in wrapper-type support (`strongidanalyzer.infer_wrapper_ids`). A recognised
// wrapper type is a tag: members and expressions typed as the wrapper carry it, the
// `.Value` unwrap carries it, and the constructor / factory parameter carries it. The
// harness here compiles every snippet and fails on compiler errors first — a snippet
// that does not compile would otherwise resolve to Unknown and pass silently.
public class WrapperTests
{
    static readonly Dictionary<string, string> wrapperOn = new()
    {
        ["strongidanalyzer.infer_wrapper_ids"] = "true"
    };

    static readonly Dictionary<string, string> wrapperAndSuffixOn = new()
    {
        ["strongidanalyzer.infer_wrapper_ids"] = "true",
        ["strongidanalyzer.infer_suffix_ids"] = "true"
    };

    static readonly Dictionary<string, string> flagsOff = new();

    // The exact shape from the StrongGuidExample repro.
    const string strongGuidExample =
        """
        using System;

        public readonly record struct UserId(Guid Value);

        public class StrongGuid
        {
            public UserId momUserId;
            public UserId dadUserId;

            public StrongGuid(UserId momUserId, UserId dadUserId)
            {
                this.momUserId = momUserId;
                this.dadUserId = dadUserId;
            }

            void F()
            {
                momUserId = dadUserId;
            }
        }
        """;

    // Shared scaffolding: a hand-rolled wrapper, a domain type with conventional ids, a
    // holder exposing the wrapper, and sinks with tagged / untagged primitive parameters.
    const string scaffolding =
        """
        using System;
        using System.Collections.Generic;
        using System.Linq;

        public readonly record struct UserId(Guid Value)
        {
            public static UserId New() => new(Guid.NewGuid());
            public static readonly UserId Empty = new(Guid.Empty);
            public static UserId From(Guid value) => new(value);
            public static UserId Parse(string input) => new(Guid.Parse(input));
        }

        public readonly record struct OrderId(Guid Value);

        public class Order
        {
            public Guid Id { get; set; }
            public Guid CustomerId { get; set; }
            [Id("Order")]
            public string Reference { get; set; } = "";
        }

        public class Dto
        {
            public Guid Raw { get; set; }
        }

        public class Holder
        {
            public UserId User { get; set; }
            public UserId Other { get; set; }
            public UserId? Maybe { get; set; }
            public List<UserId> Ids { get; set; } = new();
        }

        public static class Sink
        {
            public static void TakeOrder([Id("Order")] Guid value)
            {
            }

            public static void TakeUser([Id("User")] Guid value)
            {
            }

            public static void TakeConvention(Guid userId)
            {
            }

            public static void TakeRaw(Guid raw)
            {
            }
        }
        """;

    [Test]
    public async Task WrapperIds_Disabled_RecordStructFields_StillReportsSIA001()
    {
        var diagnostics = await Analyze(strongGuidExample, flagsOff);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        var message = diagnostics[0].GetMessage();
        await Assert.That(message.Contains("DadUser")).IsTrue();
        await Assert.That(message.Contains("MomUser")).IsTrue();
    }

    [Test]
    public async Task WrapperIds_Enabled_RecordStructFields_NoDiagnostic()
    {
        var diagnostics = await Analyze(strongGuidExample, wrapperOn);

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task WrapperIds_TypeBeatsMemberName()
    {
        var source = scaffolding +
            """

            public class Parent
            {
                public OrderId ParentId { get; set; }
            }

            public static class ParentSink
            {
                public static void TakeParent([Id("Parent")] Guid value)
                {
                }
            }

            public class Use
            {
                public void Run(Parent parent)
                {
                    Sink.TakeOrder(parent.ParentId.Value);
                    ParentSink.TakeParent(parent.ParentId.Value);
                }
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        await Assert.That(diagnostics[0].GetMessage().Contains("Parent")).IsTrue();
    }

    [Test]
    public async Task WrapperIds_TypeBeatsSuffixInference()
    {
        var source = scaffolding +
            """

            public class Command
            {
                public OrderId SourceCustomerId { get; set; }
            }

            public class Use
            {
                public void Run(Command command) =>
                    Sink.TakeOrder(command.SourceCustomerId.Value);
            }
            """;

        var diagnostics = await Analyze(source, wrapperAndSuffixOn);

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task WrapperIds_WrapperTypedMember_NamedId_OnUnrelatedType()
    {
        var source = scaffolding +
            """

            public class Invoice
            {
                public UserId Id { get; set; }
            }

            public static class InvoiceSink
            {
                public static void TakeInvoice([Id("Invoice")] Guid value)
                {
                }
            }

            public class Use
            {
                public void Run(Invoice invoice)
                {
                    InvoiceSink.TakeInvoice(invoice.Id.Value);
                    Sink.TakeUser(invoice.Id.Value);
                }
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        await Assert.That(diagnostics[0].GetMessage().Contains("User")).IsTrue();
    }

    [Test]
    public async Task WrapperIds_ValueMember_IntoMismatchedTaggedParameter()
    {
        var source = scaffolding +
            """

            public class Use
            {
                public void Run(Holder holder) =>
                    Sink.TakeOrder(holder.User.Value);
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        var diagnostic = diagnostics[0];
        await Assert.That(diagnostic.GetMessage().Contains("User")).IsTrue();
        await Assert.That(diagnostic.GetMessage().Contains("Order")).IsTrue();
        // The target parameter is a fix site; the wrapper's Value member is not.
        await Assert.That(diagnostic.AdditionalLocations[0].IsInSource).IsTrue();
        await Assert.That(diagnostic.AdditionalLocations[1].IsInSource).IsFalse();
    }

    [Test]
    public async Task WrapperIds_ValueMember_IntoMatchingTargets_NoDiagnostic()
    {
        var source = scaffolding +
            """

            public class Use
            {
                public void Run(Holder holder)
                {
                    Sink.TakeUser(holder.User.Value);
                    Sink.TakeConvention(holder.User.Value);
                }
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task WrapperIds_ValueMember_IntoUntaggedUserParameter_SIA003()
    {
        var source = scaffolding +
            """

            public class Use
            {
                public void Run(Holder holder) =>
                    Sink.TakeRaw(holder.User.Value);
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA003"]);
        await Assert.That(diagnostics[0].GetMessage().Contains("User")).IsTrue();
    }

    [Test]
    public async Task WrapperIds_Constructor_MismatchedArgument_SIA001()
    {
        var source = scaffolding +
            """

            public class Use
            {
                public UserId Run(Order order) =>
                    new UserId(order.CustomerId);
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        var diagnostic = diagnostics[0];
        await Assert.That(diagnostic.GetMessage().Contains("Customer")).IsTrue();
        // The wrapper's constructor parameter is not a fix site.
        await Assert.That(diagnostic.AdditionalLocations[0].IsInSource).IsFalse();
        await Assert.That(diagnostic.AdditionalLocations[1].IsInSource).IsTrue();
    }

    [Test]
    public async Task WrapperIds_Constructor_UntaggedSourceProperty_SIA002()
    {
        var source = scaffolding +
            """

            public class Use
            {
                public UserId Run(Dto dto) =>
                    new UserId(dto.Raw);
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA002"]);
        await Assert.That(diagnostics[0].Properties["IdValue"]).IsEqualTo("User");
    }

    [Test]
    public async Task WrapperIds_Constructor_MatchingArgument_NoDiagnostic()
    {
        var source = scaffolding +
            """

            public class Membership
            {
                public Guid UserId { get; set; }
            }

            public class Use
            {
                public UserId Run(Membership membership) =>
                    new UserId(membership.UserId);
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task WrapperIds_StaticFactory_MismatchedArgument_SIA001()
    {
        var source = scaffolding +
            """

            public class Use
            {
                public UserId Run(Order order) =>
                    UserId.From(order.CustomerId);
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
    }

    // `Parse(string input)` is declared on the wrapper but is not the wrap boundary.
    // Its parameter is Unknown rather than untagged, so a tagged string flowing in does
    // not raise SIA003 with a fix site inside the wrapper.
    [Test]
    public async Task WrapperIds_WrapperOwnedParameter_Parse_NoDiagnostic()
    {
        var source = scaffolding +
            """

            public class Use
            {
                public UserId Run(Order order) =>
                    UserId.Parse(order.Reference);
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics).IsEmpty();
    }

    // A factory that returns the wrapper but is not declared on it keeps the naming
    // convention on its parameters.
    [Test]
    public async Task WrapperIds_RepositoryFactory_KeepsConvention()
    {
        var source = scaffolding +
            """

            public static class Repository
            {
                public static UserId FindOwner(Guid orderId) => default;
            }

            public class Use
            {
                public void Run(Order order)
                {
                    Repository.FindOwner(order.Id);
                    Repository.FindOwner(order.CustomerId);
                }
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        await Assert.That(diagnostics[0].GetMessage().Contains("Customer")).IsTrue();
    }

    const string convertible =
        """
        using System;

        public readonly record struct UserId(Guid Value)
        {
            public static UserId New() => new(Guid.NewGuid());
            public static implicit operator Guid(UserId id) => id.Value;
            public static implicit operator UserId(Guid value) => new(value);
        }

        public class Order
        {
            public Guid Id { get; set; }
        }

        public class Holder
        {
            public UserId User { get; set; }
        }

        public static class Sink
        {
            public static void TakeOrder([Id("Order")] Guid value)
            {
            }

            public static void TakeRaw(Guid raw)
            {
            }
        }
        """;

    [Test]
    public async Task WrapperIds_ImplicitConversion_MemberIntoUntaggedGuid_SIA003()
    {
        var source = convertible +
            """

            public class Use
            {
                public void Run(Holder holder) =>
                    Sink.TakeRaw(holder.User);
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA003"]);
    }

    [Test]
    public async Task WrapperIds_ImplicitConversion_IntoMismatchedTag_SIA001()
    {
        var source = convertible +
            """

            public class Use
            {
                public void Run(Holder holder) =>
                    Sink.TakeOrder(holder.User);
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
    }

    // Expressions of wrapper type — factory results, object creation, locals — carry the
    // tag purely because of their static type.
    [Test]
    public async Task WrapperIds_ImplicitConversion_ExpressionsCarryTheTag()
    {
        var source = convertible +
            """

            public class Use
            {
                public void Run()
                {
                    Sink.TakeOrder(UserId.New());
                    Sink.TakeOrder(new UserId(Guid.NewGuid()));
                    var local = UserId.New();
                    Sink.TakeOrder(local);
                }
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001", "SIA001", "SIA001"]);
    }

    [Test]
    public async Task WrapperIds_ImplicitConversion_FromTaggedGuid_SIA001()
    {
        var source = convertible +
            """

            public class Use
            {
                public void Run(Holder holder, Order order) =>
                    holder.User = order.Id;
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
    }

    // A wrapper flowing as itself into an interface, object, a generic T, a base wrapper
    // or a collection leaks no primitive — nothing to report, whatever the target's name.
    [Test]
    public async Task WrapperIds_WrapperFlowingIntact_NoDiagnostic()
    {
        var source =
            """
            using System;
            using System.Collections.Generic;

            public interface IMarker
            {
            }

            public abstract class EntityId
            {
                public Guid Value { get; protected set; }
            }

            public class UserId : EntityId, IMarker
            {
                public UserId(Guid value) => Value = value;
            }

            public static class Sink
            {
                public static void Log(IMarker marker)
                {
                }

                public static void Box(object orderId)
                {
                }

                public static void Keep<T>(T value)
                {
                }

                public static void TakeBase(EntityId entityId)
                {
                }

                public static void TakeNullable(UserId? maybe)
                {
                }
            }

            public class Holder
            {
                public UserId User { get; set; } = new(Guid.Empty);
                public UserId? Maybe { get; set; }
            }

            public class Use
            {
                public void Run(Holder holder, List<UserId> ids, Dictionary<UserId, int> counts)
                {
                    Sink.Log(holder.User);
                    Sink.Box(holder.User);
                    Sink.Keep(holder.User);
                    Sink.TakeBase(holder.User);
                    Sink.TakeNullable(holder.User);
                    ids.Add(holder.User);
                    counts[holder.User] = 1;
                    holder.Maybe = holder.User;
                }
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task WrapperIds_NullableWrapper_ValueOfValue_SIA001()
    {
        var source = scaffolding +
            """

            public class Use
            {
                public void Run(Holder holder) =>
                    Sink.TakeOrder(holder.Maybe.Value.Value);
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
    }

    [Test]
    public async Task WrapperIds_Collections_ValueMemberFlows()
    {
        var source = scaffolding +
            """

            public class Use
            {
                public void Run(Holder holder, Order order)
                {
                    Sink.TakeOrder(holder.Ids[0].Value);
                    Sink.TakeOrder(holder.Ids.First().Value);
                    foreach (var id in holder.Ids)
                    {
                        Sink.TakeOrder(id.Value);
                    }

                    var any = holder.Ids.Any(_ => _.Value == order.Id);
                }
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001", "SIA001", "SIA001", "SIA001"]);
    }

    [Test]
    public async Task WrapperIds_Equality()
    {
        var source = scaffolding +
            """

            public class Use
            {
                public bool Run(Holder holder, Order order)
                {
                    var same = holder.User == holder.Other;
                    var empty = holder.User == UserId.Empty;
                    return holder.User.Value == order.Id;
                }
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
    }

    [Test]
    public async Task WrapperIds_WithInitializer()
    {
        var source = scaffolding +
            """

            public class Use
            {
                public void Run(Holder holder, Order order, Dto dto)
                {
                    var fromOrder = holder.User with { Value = order.Id };
                    var fromRaw = holder.User with { Value = dto.Raw };
                }
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id).OrderBy(_ => _)).IsEquivalentTo(["SIA001", "SIA002"]);
    }

    [Test]
    public async Task WrapperIds_ExplicitMatchingAttributes_SIA005()
    {
        var source =
            """
            using System;

            public readonly record struct AccountId([Id("Account")] Guid Value);

            public class TokenId
            {
                public TokenId([Id("Token")] Guid value) => Value = value;
                public Guid Value { get; }
            }

            public class Holder
            {
                [Id("Account")]
                public AccountId Current;
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA005", "SIA005", "SIA005"]);
    }

    // Reported today because the name convention agrees with the attribute; with the
    // flag on the member resolves to "User" without it, so the attribute is load-bearing.
    [Test]
    public async Task WrapperIds_ExplicitDifferentFromWrapper_NoSIA005()
    {
        var source = scaffolding +
            """

            public class Membership
            {
                [Id("Customer")]
                public UserId CustomerId { get; set; }
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task WrapperIds_ExplicitConflictingAttributeOnTarget_SIA001()
    {
        var source = scaffolding +
            """

            public class Membership
            {
                [Id("Product")]
                public UserId Product;
            }

            public class Use
            {
                public void Run(Membership membership, Holder holder) =>
                    membership.Product = holder.User;
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        var diagnostic = diagnostics[0];
        // The explicit attribute is changeable; the wrapper-typed source is not a fix site.
        await Assert.That(diagnostic.AdditionalLocations[0].IsInSource).IsTrue();
        await Assert.That(diagnostic.AdditionalLocations[1].IsInSource).IsFalse();
    }

    [Test]
    public async Task WrapperIds_ExplicitTagOnReceiver_WinsForValue()
    {
        var source = scaffolding +
            """

            public class Payment
            {
                [Id("Payer")]
                public UserId Payer;
            }

            public class Use
            {
                public void Run(Payment payment) =>
                    Sink.TakeUser(payment.Payer.Value);
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        await Assert.That(diagnostics[0].GetMessage().Contains("Payer")).IsTrue();
    }

    [Test]
    public async Task WrapperIds_ReturnAttribute_WinsOverWrapper()
    {
        var source = convertible +
            """

            public class Loader
            {
                [return: Id("Customer")]
                public UserId Load() => default;
            }

            public static class UserSink
            {
                public static void TakeUser([Id("User")] Guid value)
                {
                }
            }

            public class Use
            {
                public void Run(Loader loader) =>
                    UserSink.TakeUser(loader.Load());
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        await Assert.That(diagnostics[0].GetMessage().Contains("Customer")).IsTrue();
    }

    [Test]
    public async Task WrapperIds_NonGenericAbstractBase_BaseCtorArgument_NoDiagnostic()
    {
        var source =
            """
            using System;

            public abstract class EntityId
            {
                protected EntityId(Guid value) => Value = value;
                public Guid Value { get; }
            }

            public class UserId : EntityId
            {
                public UserId(Guid value) : base(value)
                {
                }
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics).IsEmpty();
    }

    // `record struct CustomerId(Guid Id)`: rule 1 used to tag the `Id` member with the
    // containing type's name, "CustomerId". The wrapper rule reads it as "Customer".
    [Test]
    public async Task WrapperIds_RecordWithIdValueMember_TagIsWrapperTag()
    {
        var source =
            """
            using System;

            public readonly record struct CustomerId(Guid Id);

            public static class Sink
            {
                public static void TakeCustomer([Id("Customer")] Guid value)
                {
                }
            }

            public class Use
            {
                public void Run(CustomerId customerId) =>
                    Sink.TakeCustomer(customerId.Id);
            }
            """;

        var withFlag = await Analyze(source, wrapperOn);
        await Assert.That(withFlag).IsEmpty();

        var withoutFlag = await Analyze(source, flagsOff);
        await Assert.That(withoutFlag.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
    }

    [Test]
    public async Task WrapperIds_WrapperTypedMember_DoesNotFeedSIA004()
    {
        var source =
            """
            using System;

            public readonly record struct OrderId(Guid Value);

            namespace Sales
            {
                public class Order
                {
                    public OrderId Id { get; set; }
                }
            }

            namespace Billing
            {
                public class Order
                {
                    public Guid Id { get; set; }
                }
            }
            """;

        var withFlag = await Analyze(source, wrapperOn);
        await Assert.That(withFlag).IsEmpty();

        var withoutFlag = await Analyze(source, flagsOff);
        await Assert.That(withoutFlag.Select(_ => _.Id).Distinct()).IsEquivalentTo(["SIA004"]);
    }

    // The StrongTypedId (Steffen Skov) shape: reference types built on a CRTP base
    // chain, the value member declared as `TPrimitive PrimitiveValue` on the root base,
    // marker interfaces, and a static Create factory on the base.
    const string skovHierarchy =
        """
        using System;

        namespace StrongTypedId
        {
            public interface IStrongTypedValue
            {
                object? PrimitiveValue { get; }
            }

            public interface IStrongTypedValue<out TPrimitive> : IStrongTypedValue
            {
                new TPrimitive PrimitiveValue { get; }
            }

            public interface IStrongTypedId
            {
            }

            public interface IStrongTypedId<out TPrimitive> : IStrongTypedValue<TPrimitive>, IStrongTypedId
            {
            }

            public interface IStrongTypedGuid : IStrongTypedId<Guid>
            {
            }

            public abstract class StrongTypedValue<TSelf, TPrimitive> : IStrongTypedValue<TPrimitive>
                where TSelf : StrongTypedValue<TSelf, TPrimitive>
            {
                protected StrongTypedValue(TPrimitive primitiveValue) => PrimitiveValue = primitiveValue;

                public TPrimitive PrimitiveValue { get; }

                object? IStrongTypedValue.PrimitiveValue => PrimitiveValue;

                public static TSelf Create(TPrimitive value) =>
                    (TSelf)Activator.CreateInstance(typeof(TSelf), value)!;

                public static bool operator ==(StrongTypedValue<TSelf, TPrimitive> left, TPrimitive right) =>
                    Equals(left.PrimitiveValue, right);

                public static bool operator !=(StrongTypedValue<TSelf, TPrimitive> left, TPrimitive right) =>
                    !(left == right);

                public override bool Equals(object? obj) =>
                    obj is StrongTypedValue<TSelf, TPrimitive> other &&
                    Equals(other.PrimitiveValue, PrimitiveValue);

                public override int GetHashCode() =>
                    PrimitiveValue?.GetHashCode() ?? 0;
            }

            public abstract class StrongTypedId<TSelf, TPrimitive> : StrongTypedValue<TSelf, TPrimitive>, IStrongTypedId<TPrimitive>
                where TSelf : StrongTypedId<TSelf, TPrimitive>
                where TPrimitive : struct
            {
                protected StrongTypedId(TPrimitive value) : base(value)
                {
                }
            }

            public abstract class StrongTypedGuid<TSelf> : StrongTypedId<TSelf, Guid>, IStrongTypedGuid
                where TSelf : StrongTypedGuid<TSelf>
            {
                protected StrongTypedGuid(Guid value) : base(value)
                {
                }
            }
        }

        public class CustomerId : StrongTypedId.StrongTypedGuid<CustomerId>
        {
            public CustomerId(Guid value) : base(value)
            {
            }
        }

        public class EmailAddress : StrongTypedId.StrongTypedValue<EmailAddress, string>
        {
            public EmailAddress(string value) : base(value)
            {
            }
        }
        """;

    [Test]
    public async Task WrapperIds_Skov_Shape()
    {
        var source = skovHierarchy +
            """

            public class Order
            {
                public Guid Id { get; set; }
            }

            public static class Sink
            {
                public static void TakeOrder([Id("Order")] Guid value)
                {
                }

                public static void TakeBase(StrongTypedId.StrongTypedGuid<CustomerId> customerId)
                {
                }

                public static void Send(string address)
                {
                }
            }

            public class Use
            {
                public void Run(CustomerId customerId, Order order, EmailAddress email)
                {
                    Sink.TakeOrder(customerId.PrimitiveValue);
                    var created = new CustomerId(order.Id);
                    var factory = CustomerId.Create(order.Id);
                    Sink.TakeBase(customerId);
                    var equal = customerId == order.Id;
                    Sink.Send(email.PrimitiveValue);
                }
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001", "SIA001", "SIA001", "SIA001"]);
        foreach (var diagnostic in diagnostics)
        {
            await Assert.That(diagnostic.GetMessage().Contains("Customer")).IsTrue();
        }
    }

    [Test]
    public async Task WrapperIds_Skov_ValueObjectIsNotAnId()
    {
        var source = skovHierarchy +
            """

            public class Holder
            {
                [Id("Email")]
                public EmailAddress Email = new("");
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics).IsEmpty();
    }

    // StronglyTypedId (Andrew Lock): the source-visible [StronglyTypedId] attribute, and
    // the [GeneratedCode] stamp that survives into metadata, admit names without the
    // `Id` suffix. The tag is then the whole type name.
    [Test]
    public async Task WrapperIds_Lock_Markers()
    {
        var source =
            """
            using System;

            namespace StronglyTypedIds
            {
                [AttributeUsage(AttributeTargets.Struct)]
                [System.Diagnostics.Conditional("STRONGLY_TYPED_ID_USAGES")]
                public sealed class StronglyTypedIdAttribute : Attribute
                {
                }
            }

            [StronglyTypedIds.StronglyTypedId]
            public readonly partial struct Sku
            {
                public Guid Value { get; }
                public Sku(Guid value) => Value = value;
            }

            [System.CodeDom.Compiler.GeneratedCode("StronglyTypedId", "1.0.0-beta08")]
            public readonly partial struct CustomerKey
            {
                public Guid Value { get; }
                public int Other { get; }
                public CustomerKey(Guid value) => Value = value;
            }

            public static class Sink
            {
                public static void TakeOrder([Id("Order")] Guid value)
                {
                }
            }

            public class Use
            {
                public void Run(Sku sku, CustomerKey key)
                {
                    Sink.TakeOrder(sku.Value);
                    Sink.TakeOrder(key.Value);
                }
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001", "SIA001"]);
        await Assert.That(diagnostics.Any(_ => _.GetMessage().Contains("Sku"))).IsTrue();
        await Assert.That(diagnostics.Any(_ => _.GetMessage().Contains("CustomerKey"))).IsTrue();
    }

    [Test]
    public async Task WrapperIds_Vogen_Shape()
    {
        var source =
            """
            using System;

            namespace Vogen
            {
                [AttributeUsage(AttributeTargets.Struct)]
                public sealed class ValueObjectAttribute<T> : Attribute
                {
                }
            }

            [Vogen.ValueObject<Guid>]
            public readonly partial struct ProductCode
            {
                public Guid Value { get; }
                ProductCode(Guid value) => Value = value;
                public static ProductCode From(Guid value) => new(value);
            }

            public class Order
            {
                public Guid Id { get; set; }
            }

            public class Use
            {
                public ProductCode Run(Order order) =>
                    ProductCode.From(order.Id);
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        await Assert.That(diagnostics[0].GetMessage().Contains("ProductCode")).IsTrue();
    }

    [Test]
    public async Task WrapperIds_Generic_IdOfT_TagIsTypeArgument()
    {
        var source =
            """
            using System;

            public readonly record struct Id<T>(Guid Value);
            public readonly record struct EntityId<T>(Guid Value);

            public class User
            {
            }

            public class Order
            {
            }

            public class Holder
            {
                public Id<User> User { get; set; }
                public Id<Order> Order { get; set; }
                public EntityId<User> Entity { get; set; }
            }

            public static class Sink
            {
                public static void TakeOrder([Id("Order")] Guid value)
                {
                }

                public static void TakeRaw(Guid raw)
                {
                }

                public static void Keep<T>(Id<T> id) =>
                    TakeRaw(id.Value);
            }

            public class Use
            {
                public void Run(Holder holder)
                {
                    Sink.TakeOrder(holder.User.Value);
                    Sink.TakeOrder(holder.Order.Value);
                    Sink.TakeOrder(holder.Entity.Value);
                }
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001", "SIA001"]);
        foreach (var diagnostic in diagnostics)
        {
            await Assert.That(diagnostic.GetMessage().Contains("User")).IsTrue();
        }
    }

    [Test]
    public async Task WrapperIds_Generic_OverPrimitive_NotRecognised()
    {
        var source =
            """
            using System;

            public readonly record struct Id<TValue>(TValue Value);

            public class Holder
            {
                public Id<Guid> Key { get; set; }
            }

            public static class Sink
            {
                public static void TakeRaw(Guid raw)
                {
                }
            }

            public class Use
            {
                public void Run(Holder holder) =>
                    Sink.TakeRaw(holder.Key.Value);
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics).IsEmpty();
    }

    // Shapes that are deliberately not wrappers keep today's name-based behaviour: a
    // nested type named `Id`, a struct with several primitive members and no `Value`,
    // and a struct wrapping another wrapper.
    [Test]
    public async Task WrapperIds_Recognition_Rejects()
    {
        var source =
            """
            using System;

            public class Customer
            {
                public readonly record struct Id(Guid Value);
            }

            public readonly record struct Point(int X, int Y);

            public readonly record struct InnerId(Guid Value);

            public readonly record struct OuterId(InnerId Value);

            public class Holder
            {
                public Customer.Id sourceCustomerId;
                public Customer.Id targetCustomerId;
                public Point sourcePointId;
                public Point targetPointId;
                public OuterId sourceOuterId;
                public OuterId targetOuterId;

                public void Run()
                {
                    sourceCustomerId = targetCustomerId;
                    sourcePointId = targetPointId;
                    sourceOuterId = targetOuterId;
                }
            }
            """;

        var diagnostics = await Analyze(source, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001", "SIA001", "SIA001"]);
    }

    [Test]
    public async Task WrapperIds_SuffixInference_SeesWrapperTags()
    {
        var source = scaffolding +
            """

            public class Command
            {
                public Guid SourceUserId { get; set; }
            }

            public class Use
            {
                public void Run(Command command, Holder holder) =>
                    command.SourceUserId = holder.User.Value;
            }
            """;

        var bothFlags = await Analyze(source, wrapperAndSuffixOn);
        await Assert.That(bothFlags).IsEmpty();

        var wrapperOnly = await Analyze(source, wrapperOn);
        await Assert.That(wrapperOnly.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        await Assert.That(wrapperOnly[0].GetMessage().Contains("SourceUser")).IsTrue();
    }

    [Test]
    public async Task WrapperIds_SuffixInference_ExplicitTagVouchedByWrapper_SIA005()
    {
        var source =
            """
            using System;

            public readonly record struct UserId(Guid Value);

            public class Command
            {
                [Id("User")]
                public Guid SourceUserId { get; set; }
            }
            """;

        var diagnostics = await Analyze(source, wrapperAndSuffixOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA005"]);
    }

    const string crossAssemblyLibrary =
        """
        using System;

        public readonly record struct UserId(Guid Value);

        public static class Library
        {
            public static void Take(Guid raw)
            {
            }
        }
        """;

    const string crossAssemblyConsumer =
        """
        using System;

        public class Order
        {
            public Guid Id { get; set; }
        }

        public class Holder
        {
            public UserId User { get; set; }
            public UserId momUserId;
            public UserId dadUserId;
            public Guid Raw { get; set; }
        }

        public static class Sink
        {
            public static void TakeOrder([Id("Order")] Guid value)
            {
            }

            public static void TakeRaw(Guid raw)
            {
            }
        }

        public class Use
        {
            public void Run(Holder holder)
            {
                Sink.TakeOrder(holder.User.Value);
                Sink.TakeRaw(holder.User.Value);
                var wrapped = new UserId(holder.Raw);
                Library.Take(holder.User.Value);
                holder.momUserId = holder.dadUserId;
            }
        }
        """;

    // The "cannot be migrated" story: the wrapper lives in a referenced assembly with no
    // attribute of any kind, and the consumer's seams are still checked.
    [Test]
    public async Task WrapperIds_CrossAssembly_HandRolled()
    {
        var diagnostics = await AnalyzeCrossAssembly(crossAssemblyLibrary, crossAssemblyConsumer, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id).OrderBy(_ => _)).IsEquivalentTo(["SIA001", "SIA002", "SIA003"]);
        var missing = diagnostics.Single(_ => _.Id == "SIA002");
        await Assert.That(missing.AdditionalLocations[0].IsInSource).IsTrue();
    }

    [Test]
    public async Task WrapperIds_CrossAssembly_Disabled_StillUsesNames()
    {
        var diagnostics = await AnalyzeCrossAssembly(crossAssemblyLibrary, crossAssemblyConsumer, flagsOff);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        await Assert.That(diagnostics[0].GetMessage().Contains("MomUser")).IsTrue();
    }

    [Test]
    public async Task WrapperIds_CrossAssembly_Skov()
    {
        var consumer =
            """
            using System;

            public class Order
            {
                public Guid Id { get; set; }
            }

            public static class Sink
            {
                public static void TakeOrder([Id("Order")] Guid value)
                {
                }
            }

            public class Use
            {
                public void Run(CustomerId customerId, Order order)
                {
                    Sink.TakeOrder(customerId.PrimitiveValue);
                    var created = new CustomerId(order.Id);
                    var factory = CustomerId.Create(order.Id);
                }
            }
            """;

        var diagnostics = await AnalyzeCrossAssembly(skovHierarchy, consumer, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001", "SIA001", "SIA001"]);
    }

    [Test]
    public async Task WrapperIds_CrossAssembly_GeneratedCodeMarker()
    {
        var library =
            """
            using System;

            [System.CodeDom.Compiler.GeneratedCode("StronglyTypedId", "1.0.0-beta08")]
            public readonly partial struct CustomerKey
            {
                public Guid Value { get; }
                public CustomerKey(Guid value) => Value = value;
            }
            """;

        var consumer =
            """
            using System;

            public static class Sink
            {
                public static void TakeOrder([Id("Order")] Guid value)
                {
                }
            }

            public class Use
            {
                public void Run(CustomerKey key) =>
                    Sink.TakeOrder(key.Value);
            }
            """;

        var diagnostics = await AnalyzeCrossAssembly(library, consumer, wrapperOn);

        await Assert.That(diagnostics.Select(_ => _.Id)).IsEquivalentTo(["SIA001"]);
        await Assert.That(diagnostics[0].GetMessage().Contains("CustomerKey")).IsTrue();
    }

    static readonly IReadOnlyList<MetadataReference> references = TrustedReferences.Where(_ =>
        !_.EndsWith("StrongIdAnalyzer.Tests.dll", StringComparison.OrdinalIgnoreCase));

    static Task<ImmutableArray<Diagnostic>> Analyze(string source, IDictionary<string, string> options)
    {
        var compilation = CompileWithGenerator("Tests", source, references);
        return RunAnalyzer(compilation, options);
    }

    static Task<ImmutableArray<Diagnostic>> AnalyzeCrossAssembly(
        string library,
        string consumer,
        IDictionary<string, string> options)
    {
        var libraryCompilation = CompileWithGenerator("Library", library, references);
        using var stream = new MemoryStream();
        var emit = libraryCompilation.Emit(stream);
        if (!emit.Success)
        {
            throw new InvalidOperationException(
                "Library compilation failed: " +
                string.Join(Environment.NewLine, emit.Diagnostics.Where(_ => _.Severity == DiagnosticSeverity.Error)));
        }

        var libraryReference = MetadataReference.CreateFromImage(stream.ToArray());
        var consumerCompilation = CompileWithGenerator("Consumer", consumer, [.. references, libraryReference]);
        return RunAnalyzer(consumerCompilation, options);
    }

    static Compilation CompileWithGenerator(string name, string source, IEnumerable<MetadataReference> metadataReferences)
    {
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source)],
            metadataReferences,
            new(OutputKind.DynamicallyLinkedLibrary));
        CSharpGeneratorDriver
            .Create(new IdAttributeGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);

        var errors = updated.GetDiagnostics()
            .Where(_ => _.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length > 0)
        {
            throw new InvalidOperationException(
                $"{name} compilation has errors: " +
                string.Join(Environment.NewLine, errors.Select(_ => _.ToString())));
        }

        return updated;
    }

    static Task<ImmutableArray<Diagnostic>> RunAnalyzer(Compilation compilation, IDictionary<string, string> options)
    {
        var analyzerOptions = new AnalyzerOptions([], new TestAnalyzerConfigOptionsProvider(options));
        return compilation
            .WithAnalyzers([new IdMismatchAnalyzer()], analyzerOptions)
            .GetAnalyzerDiagnosticsAsync();
    }
}
