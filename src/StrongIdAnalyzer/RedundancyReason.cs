// What would still supply an explicit [Id]'s tag if the attribute were deleted. SIA005 names it,
// because "the naming convention already infers" is only true of one of them: a record property
// written `[Id("User")][property: Id("User")] Guid PerformedById` is redundant because of the
// parameter's attribute, while its name would infer "PerformedBy".
enum RedundancyReason
{
    Convention,
    Inherited,
    RecordParameter,
    Wrapper,
    External
}
