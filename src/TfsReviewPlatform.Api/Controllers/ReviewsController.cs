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

    [HttpGet("history/pull-requests")]
    [ProducesResponseType<ReviewHistoryDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ReviewHistoryDto>> GetPullRequestHistory(
        [FromQuery] string pullRequestUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await reviewOrchestrator.GetPullRequestHistoryAsync(pullRequestUrl, cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return ValidationProblem(detail: exception.Message);
        }
    }

    [HttpDelete("history/pull-requests")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeletePullRequestHistory(
        [FromQuery] string pullRequestUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            await reviewOrchestrator.DeletePullRequestHistoryAsync(pullRequestUrl, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException exception)
        {
            return ValidationProblem(detail: exception.Message);
        }
    }

    [HttpPost("{runId:guid}/inline-comments/{commentId:guid}/publish")]
    [ProducesResponseType<ReviewRunDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ReviewRunDto>> PublishInlineComment(
        Guid runId,
        Guid commentId,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await reviewOrchestrator.PublishInlineCommentAsync(runId, commentId, cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return ValidationProblem(detail: exception.Message);
        }
    }

    [HttpPost("{runId:guid}/report/publish")]
    [ProducesResponseType<ReviewRunDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ReviewRunDto>> PublishReport(
        Guid runId,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await reviewOrchestrator.PublishReportAsync(runId, cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return ValidationProblem(detail: exception.Message);
        }
    }

    [HttpPost("{runId:guid}/inline-comments/{commentId:guid}/discussion")]
    [ProducesResponseType<ReviewRunDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ReviewRunDto>> ContinueInlineDiscussion(
        Guid runId,
        Guid commentId,
        [FromBody] ContinueInlineDiscussionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await reviewOrchestrator.ContinueInlineDiscussionAsync(runId, commentId, request, cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return ValidationProblem(detail: exception.Message);
        }
    }

    [HttpGet("{runId:guid}/artifacts/diff")]
    public async Task<IActionResult> DownloadDiff(Guid runId, CancellationToken cancellationToken)
    {
        var artifact = await reviewOrchestrator.GetDiffDownloadAsync(runId, cancellationToken);
        return artifact is null
            ? NotFound()
            : File(artifact.Content, artifact.ContentType, artifact.FileName);
    }

    [HttpGet("{runId:guid}/artifacts/report")]
    public async Task<IActionResult> DownloadReport(Guid runId, CancellationToken cancellationToken)
    {
        var artifact = await reviewOrchestrator.GetMarkdownReportDownloadAsync(runId, cancellationToken);
        return artifact is null
            ? NotFound()
            : File(artifact.Content, artifact.ContentType, artifact.FileName);
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
