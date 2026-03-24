namespace TfsReviewPlatform.Application.Models;

public sealed class AzureDevOpsOptions
{
    public const string SectionName = "AzureDevOps";

    public bool SkipCertificateValidation { get; init; }
}
