// Validates that `strongidanalyzer.infer_suffix_ids = true` in .editorconfig reaches
// the analyzer when consumed as a packaged NuGet. The csproj raises SIA001 to an
// error, so if the flag fails to propagate, the `SourceProductId` / `sourceProductId`
// slots would be tagged "SourceProduct" by the whole-name rule and the assignments
// below would fail the build with Product vs SourceProduct.
namespace SuffixInference.Consume;

public class Product
{
    // Tag "Product" via naming convention (rule 1).
    public System.Guid Id { get; set; }
}

public class DuplicateProductCommand
{
    // Both properties infer "Product" via suffix inference when the flag is on.
    public System.Guid SourceProductId { get; set; }
    public System.Guid TargetProductId { get; set; }
    public string NewName { get; set; } = "";
}

public class SuffixInferenceConsumeTests
{
    // Same rule on parameters.
    public static void DuplicateProduct(System.Guid sourceProductId, System.Guid targetProductId, string newName)
    {
    }

    [Test]
    public void SuffixInferredParameters_AcceptMatchingDomainId_BuildsClean()
    {
        var product = new Product();
        DuplicateProduct(product.Id, product.Id, "n");
    }

    [Test]
    public void SuffixInferredProperties_AcceptMatchingDomainId_BuildsClean()
    {
        var product = new Product();
        var command = new DuplicateProductCommand
        {
            SourceProductId = product.Id,
            TargetProductId = product.Id,
            NewName = "n"
        };

        System.Console.WriteLine(command.NewName);
    }
}
