using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services;

/// <summary>
/// Universal review service for marketplace resources (templates, nodes, etc.).
/// Enforces eligibility (proof of usage) and manages rating aggregates.
/// </summary>
public interface IReviewService
{
    Task<MarketplaceReview> SubmitReviewAsync(MarketplaceReview review);
    Task<MarketplaceReview?> UpdateReviewAsync(string reviewId, string reviewerId, int rating, string? title, string? comment);
    Task<bool> DeleteReviewAsync(string reviewId, string reviewerId);
    Task<List<MarketplaceReview>> GetReviewsAsync(string resourceType, string resourceId, int limit = 50, int skip = 0);
    Task<MarketplaceReview?> GetUserReviewAsync(string resourceType, string resourceId, string reviewerId);
}

public class ReviewService : IReviewService
{
    private readonly DataStore _dataStore;
    private readonly ITemplateService _templateService;
    private readonly ILogger<ReviewService> _logger;

    public ReviewService(
        DataStore dataStore,
        ITemplateService templateService,
        ILogger<ReviewService> logger)
    {
        _dataStore = dataStore;
        _templateService = templateService;
        _logger = logger;
    }

    public async Task<MarketplaceReview> SubmitReviewAsync(MarketplaceReview review)
    {
        // Validate rating
        if (review.Rating < 1 || review.Rating > 5)
            throw new ArgumentException("Rating must be between 1 and 5");

        // Validate required fields
        if (string.IsNullOrWhiteSpace(review.ResourceType))
            throw new ArgumentException("Resource type is required");

        if (string.IsNullOrWhiteSpace(review.ResourceId))
            throw new ArgumentException("Resource ID is required");

        if (string.IsNullOrWhiteSpace(review.ReviewerId))
            throw new ArgumentException("Reviewer ID is required");

        // Check for existing review (one review per user per resource)
        var existing = await _dataStore.GetReviewByReviewerAsync(
            review.ResourceType, review.ResourceId, review.ReviewerId);

        if (existing != null)
            throw new InvalidOperationException("You have already reviewed this resource. Use update instead.");

        // Validate eligibility proof
        await ValidateEligibilityAsync(review);

        // Mark as verified and save
        review.IsVerified = true;
        review.EligibilityProof.VerifiedAt = DateTime.UtcNow;
        review.CreatedAt = DateTime.UtcNow;
        review.UpdatedAt = DateTime.UtcNow;
        review.Status = ReviewStatus.Active;

        var saved = await _dataStore.SaveReviewAsync(review);

        // Update denormalized aggregates on the resource
        await UpdateResourceRatingsAsync(review.ResourceType, review.ResourceId);

        _logger.LogInformation(
            "Review submitted: {ReviewId} for {ResourceType}/{ResourceId} by {ReviewerId} ({Rating}/5)",
            saved.Id, saved.ResourceType, saved.ResourceId, saved.ReviewerId, saved.Rating);

        return saved;
    }

    public async Task<MarketplaceReview?> UpdateReviewAsync(
        string reviewId, string reviewerId, int rating, string? title, string? comment)
    {
        if (rating < 1 || rating > 5)
            throw new ArgumentException("Rating must be between 1 and 5");

        var reviews = await _dataStore.GetReviewsAsync("template", ""); // We need the review by ID
        // Fetch the specific review - we'll look it up via the reviewer's existing review
        // Since we don't have GetReviewByIdAsync, find it through the resource
        // For now, we search by reviewer
        // TODO: Add GetReviewByIdAsync to DataStore if needed

        // Use a simpler approach: find the user's review for the resource
        // The controller will pass resourceType/resourceId
        throw new NotImplementedException("Use the resource-scoped update path via controller");
    }

    public async Task<bool> DeleteReviewAsync(string reviewId, string reviewerId)
    {
        // For now, only allow deletion through moderation or by the reviewer
        // The controller enforces ownership
        var deleted = await _dataStore.DeleteReviewAsync(reviewId);

        if (deleted)
        {
            _logger.LogInformation("Review deleted: {ReviewId} by {ReviewerId}", reviewId, reviewerId);
        }

        return deleted;
    }

    public async Task<List<MarketplaceReview>> GetReviewsAsync(
        string resourceType, string resourceId, int limit = 50, int skip = 0)
    {
        return await _dataStore.GetReviewsAsync(resourceType, resourceId, limit, skip);
    }

    public async Task<MarketplaceReview?> GetUserReviewAsync(
        string resourceType, string resourceId, string reviewerId)
    {
        return await _dataStore.GetReviewByReviewerAsync(resourceType, resourceId, reviewerId);
    }

    /// <summary>
    /// Validate that the reviewer has actually used the resource they're reviewing.
    /// </summary>
    private async Task ValidateEligibilityAsync(MarketplaceReview review)
    {
        switch (review.ResourceType)
        {
            case "template":
                await ValidateTemplateReviewEligibilityAsync(review);
                break;

            case "node":
                await ValidateNodeReviewEligibilityAsync(review);
                break;

            default:
                throw new ArgumentException($"Unknown resource type: {review.ResourceType}");
        }
    }

    /// <summary>
    /// Template review: reviewer must have a VM deployed from this template.
    /// </summary>
    private async Task ValidateTemplateReviewEligibilityAsync(MarketplaceReview review)
    {
        if (review.EligibilityProof?.Type != "deployment")
            throw new ArgumentException("Template reviews require a deployment proof (VM ID)");

        if (string.IsNullOrWhiteSpace(review.EligibilityProof.ReferenceId))
            throw new ArgumentException("Deployment proof must include a VM ID");

        var vmId = review.EligibilityProof.ReferenceId;

        // Verify the VM exists, belongs to the reviewer, and was created from this template
        if (!_dataStore.ActiveVMs.TryGetValue(vmId, out var vm))
        {
            throw new ArgumentException($"VM {vmId} not found. Only VMs you deployed can be used as proof.");
        }

        if (vm.OwnerId != review.ReviewerId)
        {
            throw new UnauthorizedAccessException("You can only use your own VMs as review proof");
        }

        if (vm.TemplateId != review.ResourceId)
        {
            throw new ArgumentException(
                $"VM {vmId} was not deployed from template {review.ResourceId}");
        }
    }

    /// <summary>
    /// Node review: reviewer must have had a VM run on this node.
    /// </summary>
    private Task ValidateNodeReviewEligibilityAsync(MarketplaceReview review)
    {
        if (review.EligibilityProof?.Type != "vm_usage")
            throw new ArgumentException("Node reviews require a vm_usage proof (VM ID)");

        if (string.IsNullOrWhiteSpace(review.EligibilityProof.ReferenceId))
            throw new ArgumentException("VM usage proof must include a VM ID");

        var vmId = review.EligibilityProof.ReferenceId;

        // Verify the VM exists, belongs to the reviewer, and ran on this node
        if (!_dataStore.ActiveVMs.TryGetValue(vmId, out var vm))
        {
            throw new ArgumentException($"VM {vmId} not found");
        }

        if (vm.OwnerId != review.ReviewerId)
        {
            throw new UnauthorizedAccessException("You can only use your own VMs as review proof");
        }

        if (vm.NodeId != review.ResourceId)
        {
            throw new ArgumentException($"VM {vmId} is not running on node {review.ResourceId}");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Update the denormalized rating aggregates on the reviewed resource.
    /// </summary>
    private async Task UpdateResourceRatingsAsync(string resourceType, string resourceId)
    {
        switch (resourceType)
        {
            case "template":
                await _templateService.UpdateTemplateRatingsAsync(resourceId);
                break;

            case "node":
                // Future: update node rating aggregates
                _logger.LogInformation("Node rating aggregates not yet implemented for node {NodeId}", resourceId);
                break;
        }
    }
}
