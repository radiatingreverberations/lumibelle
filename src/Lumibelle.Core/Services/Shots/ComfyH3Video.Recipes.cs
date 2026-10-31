using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public sealed partial class ComfyH3Video
{
    public Task<H3GenerationRecipe> CaptureRecipeAsync(AiVideoJobRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(H3GenerationRecipes.Capture(request));
    }

    internal Task ValidateRecipeAsync(AiVideoJobRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        H3GenerationRecipes.Validate(request);
        H3GenerationRecipes.CheckNativeRequest(request.Recipe);
        return Task.CompletedTask;
    }
}
