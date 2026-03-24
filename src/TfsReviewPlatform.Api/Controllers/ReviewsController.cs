using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Contracts.Reviews;
using TfsReviewPlatform.Domain.Entities;

namespace TfsReviewPlatform.Api.Controllers;

[ApiController]
[Route("api/reviews")]
public sealed class ReviewsController(
    IReviewOrchestrator reviewOrchestrator,
    IReviewProgressStore reviewProgressStore,
    IOptions<JsonOptions> jsonOptions)
    : ControllerBase
{
    [HttpPost("pull-requests")]
    [ProducesResponseType<ReviewRunDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<ReviewRunDto>> StartPullRequestReview(
        [FromBody] StartPullRequestReviewRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var run = await reviewOrchestrator.StartPullRequestReviewAsync(request, cancellationToken);
            return AcceptedAtAction(nameof(GetById), new { runId = run.Id }, run);
        }
        catch (InvalidOperationException exception)
        {
            return ValidationProblem(detail: exception.Message);
        }
    }

    [HttpPost("branch-comparisons")]
    [ProducesResponseType<ReviewRunDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<ReviewRunDto>> StartBranchReview(
        [FromBody] StartBranchReviewRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var run = await reviewOrchestrator.StartBranchReviewAsync(request, cancellationToken);
            return AcceptedAtAction(nameof(GetById), new { runId = run.Id }, run);
        }
        catch (InvalidOperationException exception)
        {
            return ValidationProblem(detail: exception.Message);
        }
    }

    [HttpGet("{runId:guid}")]
    [ProducesResponseType<ReviewRunDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReviewRunDto>> GetById(Guid runId, CancellationToken cancellationToken)
    {
        var run = await reviewOrchestrator.GetAsync(runId, cancellationToken);
        return run is null ? NotFound() : Ok(run);
    }

    [HttpGet("{runId:guid}/events")]
    [Produces("text/event-stream")]
    public async Task Stream(Guid runId, CancellationToken cancellationToken)
    {
        var run = await reviewOrchestrator.GetAsync(runId, cancellationToken);
        if (run is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        Response.Headers.Append("X-Accel-Buffering", "no");

        await WriteEventAsync("snapshot", run, cancellationToken);

        await foreach (var update in reviewProgressStore.Subscribe(runId).ReadAllAsync(cancellationToken))
        {
            var dto = new ReviewProgressEventDto
            {
                RunId = update.RunId,
                Status = update.Status,
                Stage = update.Stage,
                ProgressPercent = update.ProgressPercent,
                Message = update.Message,
                Timestamp = update.Timestamp,
                IsTerminal = update.IsTerminal
            };

            await WriteEventAsync(update.IsTerminal ? "completed" : "progress", dto, cancellationToken);
        }
    }

    private async Task WriteEventAsync(string eventName, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, jsonOptions.Value.JsonSerializerOptions);
        await Response.WriteAsync($"event: {eventName}\n", cancellationToken);
        await Response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}
