namespace StrongIdAnalyzer.Benchmarks;

// Generates the library + consumer source used by the cross-assembly benchmarks.
//
// The library defines a moderate inheritance tree so per-call-site lookups have
// real work to do: each concrete class implements one tagged marker interface plus
// four noise interfaces, and the tag lives on the interface's property rather than
// the concrete class. Every access to `customer.Id` forces
// GetMemberAccessInfo → EnumerateMemberChain to iterate AllInterfaces and call
// FindImplementationForInterfaceMember for each — which is what the index would
// let us skip.
static class SourceBuilder
{
    public const string LibrarySource =
        """
        using System;

        namespace StrongIdAnalyzer
        {
            [AttributeUsage(
                AttributeTargets.Property |
                AttributeTargets.Field |
                AttributeTargets.Parameter |
                AttributeTargets.ReturnValue,
                Inherited = false)]
            internal sealed class IdAttribute(string type) : Attribute
            {
                public string Type { get; } = type;
            }
        }

        public interface IHasAudit { string AuditedBy { get; } }
        public interface IHasTimestamp { DateTime Created { get; } }
        public interface IHasVersion { int Version { get; } }
        public interface IHasName { string Name { get; } }

        public interface ICustomer : IHasAudit, IHasTimestamp, IHasVersion, IHasName
        {
            [StrongIdAnalyzer.Id("Customer")] Guid Id { get; }
        }

        public interface IOrder : IHasAudit, IHasTimestamp, IHasVersion, IHasName
        {
            [StrongIdAnalyzer.Id("Order")] Guid Id { get; }
        }

        public class Customer : ICustomer
        {
            public Guid Id { get; init; }
            public string AuditedBy { get; init; } = "";
            public DateTime Created { get; init; }
            public int Version { get; init; }
            public string Name { get; init; } = "";
        }

        public class Order : IOrder
        {
            public Guid Id { get; init; }
            public string AuditedBy { get; init; } = "";
            public DateTime Created { get; init; }
            public int Version { get; init; }
            public string Name { get; init; } = "";
        }

        public static class Methods
        {
            public static void TakeCustomerId([StrongIdAnalyzer.Id("Customer")] Guid id) { }
            public static void TakeOrderId([StrongIdAnalyzer.Id("Order")] Guid id) { }
        }
        """;

    public static string BuildConsumer(int callSites)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            """
            using System;
            public class CallSites
            {
                public void Run(Customer customer, Order order, Guid untagged)
                {
            """);
        for (var i = 0; i < callSites; i++)
        {
            switch (i % 4)
            {
                case 0:
                    builder.AppendLine($"        Methods.TakeCustomerId(customer.Id); // match, i={i}");
                    break;
                case 1:
                    builder.AppendLine($"        Methods.TakeCustomerId(order.Id);    // SIA001, i={i}");
                    break;
                case 2:
                    builder.AppendLine($"        Methods.TakeOrderId(customer.Id);    // SIA001, i={i}");
                    break;
                default:
                    builder.AppendLine($"        Methods.TakeOrderId(untagged);       // SIA002, i={i}");
                    break;
            }
        }

        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }
}
