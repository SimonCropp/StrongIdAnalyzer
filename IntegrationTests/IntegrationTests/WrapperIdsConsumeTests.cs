// Validates that `strongidanalyzer.infer_wrapper_ids = true` in .editorconfig reaches
// the analyzer when consumed as a packaged NuGet. The csproj raises SIA001/SIA002/SIA003
// to errors, so if the flag failed to propagate the `momUserId = dadUserId` assignment
// below would be tagged "MomUser" / "DadUser" by the whole-name rule and fail the build.
namespace WrapperIds.Consume;

public readonly record struct UserId(System.Guid Value);

public class Order
{
    // Tag "User" via naming convention (rule 2) — matches the wrapper's tag.
    public System.Guid UserId { get; set; }
}

public class Parents
{
    public UserId momUserId;
    public UserId dadUserId;

    public Parents(UserId momUserId, UserId dadUserId)
    {
        this.momUserId = momUserId;
        this.dadUserId = dadUserId;
    }

    public void Swap() =>
        momUserId = dadUserId;
}

public class WrapperIdsConsumeTests
{
    // The unwrap seam: `.Value` carries the wrapper's tag into a conventional parameter.
    static void Handle(System.Guid userId)
    {
    }

    [Test]
    public void WrapperTypedMembers_AssignAcrossRoleNames_BuildsClean()
    {
        var parents = new Parents(new(System.Guid.NewGuid()), new(System.Guid.NewGuid()));
        parents.Swap();
        Handle(parents.momUserId.Value);
    }

    [Test]
    public void WrapSeam_AcceptsMatchingConventionalId_BuildsClean()
    {
        var order = new Order { UserId = System.Guid.NewGuid() };
        var wrapped = new UserId(order.UserId);
        Handle(wrapped.Value);
    }
}
