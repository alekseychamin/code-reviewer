using Microsoft.AspNetCore.Mvc;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Contracts.Providers;

namespace TfsReviewPlatform.Api.Controllers;

[ApiController]
[Route("api/provider-profiles")]
public sealed class ProviderProfilesController(IProviderCatalogService providerCatalogService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ProviderProfileDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ProviderProfileDto>>> Get(CancellationToken cancellationToken)
    {
        var profiles = await providerCatalogService.ListAsync(cancellationToken);
        return Ok(profiles);
    }
}
